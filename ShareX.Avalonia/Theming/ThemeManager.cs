#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using System.ComponentModel;

namespace ShareX.AvaloniaUI.Theming
{
    public static class ThemeManager
    {
        public static readonly ThemeVariant ShareXDark = new ThemeVariant("ShareXDark", ThemeVariant.Dark);
        public static readonly ThemeVariant ShareXLight = new ThemeVariant("ShareXLight", ThemeVariant.Light);
        private static ThemeVariant _currentTheme = ShareXDark;
        private static SkinDefinition _currentSkin = SkinVariants.Resolve(null);
        private static bool _currentIsDark = true;
        private static ApplicationThemeOptions? _options;
        private static IPlatformSettings? _platformSettings;

        public static event EventHandler<ThemeVariant>? ThemeChanged;
        public static event EventHandler<Color>? AccentColorChanged;

        /// <summary>The skin now on screen. Its <c>Id</c> is the value the website stores.</summary>
        public static SkinDefinition CurrentSkin => _currentSkin;

        public static void Configure(ApplicationThemeOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (!ReferenceEquals(_options, options))
            {
                if (_options != null)
                {
                    _options.PropertyChanged -= OnOptionsPropertyChanged;
                }

                _options = options;
                _options.PropertyChanged += OnOptionsPropertyChanged;
            }

            RunOnUIThread(ApplyConfiguredSettings);
        }

        public static void SetTheme(ThemeVariant theme, object? target = null)
        {
            bool themeChanged = !Equals(_currentTheme, theme);
            _currentTheme = theme;

            if (target is Application app)
            {
                app.RequestedThemeVariant = theme;

                if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    foreach (Window window in desktop.Windows)
                    {
                        window.RequestedThemeVariant = theme;
                    }
                }
            }
            else if (target is Window window)
            {
                window.RequestedThemeVariant = theme;
            }
            else if (target is ThemeVariantScope scope)
            {
                scope.RequestedThemeVariant = theme;
            }

            if (themeChanged)
            {
                ThemeChanged?.Invoke(null, theme);
            }
        }

        public static ThemeVariant GetCurrentTheme()
        {
            return _currentTheme;
        }

        /// <remarks>
        /// A tracked flag rather than a string test on the variant's name. There are
        /// twenty-eight variants now, all named after a skin, and deciding "is this
        /// dark?" by looking for the word "Light" in an identifier is a trap waiting
        /// for the first skin someone calls Daylight.
        /// </remarks>
        public static bool IsDarkTheme => _currentIsDark;

        public static void Refresh()
        {
            RunOnUIThread(ApplyConfiguredSettings);
        }

        private static void OnOptionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            RunOnUIThread(ApplyConfiguredSettings);
        }

        private static void ApplyConfiguredSettings()
        {
            if (_options == null)
            {
                return;
            }

            Application? application = Application.Current;
            SetPlatformSettings((_options.UseSystemTheme || _options.UseSystemAccentColor)
                ? application?.PlatformSettings
                : null);

            PlatformColorValues? colorValues = _platformSettings?.GetColorValues();

            // Two independent axes, exactly as the website has them: the SKIN decides
            // what the app looks like, the MODE decides whether it is on a light or a
            // dark ground, and the mode still follows the system unless the user said
            // otherwise. Every skin ships both forms of itself, so the two never argue.
            bool dark = _options.UseSystemTheme
                ? IsSystemDark(colorValues, _options.Theme)
                : !IsLightTheme(_options.Theme);

            SkinDefinition skin = SkinVariants.Resolve(_options.Skin);
            ThemeVariant theme = dark ? skin.Dark : skin.Light;

            _currentSkin = skin;
            _currentIsDark = dark;
            SetTheme(theme, application);

            if (application == null)
            {
                return;
            }

            // --- the accent ---------------------------------------------------
            //
            // This method OVERWRITES ShareX.Color.Accent.* in Application.Resources,
            // which shadows anything a theme dictionary declares under the same names.
            // That is why repainting the palette alone never changed the accent on
            // screen, and it is why the skin's accent cannot simply be written into
            // Skins.axaml and left there.
            //
            // So the skin's accent is read back out - from ShareX.Color.Skin.Accent,
            // a name this method never writes to, resolved for the variant just
            // selected - and fed in as the value to apply. The overwrite still
            // happens; it now writes the right colour.
            //
            // Precedence, highest first: the system accent if the user asked for it,
            // then a colour the user picked, then the skin's own. A colour someone
            // chose is never second-guessed - that rule is older than skins and stays.
            Color accentColor;
            bool accentFromSkin = false;

            if (_options.UseSystemAccentColor && colorValues is { AccentColor1.A: > 0 })
            {
                accentColor = colorValues.AccentColor1;
            }
            else if (_options.IsAccentDefault)
            {
                accentColor = ResourceColor(
                    application,
                    theme,
                    "ShareX.Color.Skin.Accent",
                    Color.Parse(dark
                        ? ApplicationThemeOptions.DefaultAccentColorHexDark
                        : ApplicationThemeOptions.DefaultAccentColorHex));
                accentFromSkin = true;
            }
            else
            {
                accentColor = _options.AccentColor;
            }

            ApplyAccentColor(application, theme, accentColor, accentFromSkin);
        }

        private static void SetPlatformSettings(IPlatformSettings? platformSettings)
        {
            if (ReferenceEquals(_platformSettings, platformSettings))
            {
                return;
            }

            if (_platformSettings != null)
            {
                _platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
            }

            _platformSettings = platformSettings;

            if (_platformSettings != null)
            {
                _platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
            }
        }

        private static void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e)
        {
            RunOnUIThread(ApplyConfiguredSettings);
        }

        private static bool IsSystemDark(PlatformColorValues? colorValues, string configuredTheme)
        {
            if (colorValues != null)
            {
                return !IsLightTheme(colorValues.ThemeVariant.ToString());
            }

            return !IsLightTheme(configuredTheme);
        }

        private static bool IsLightTheme(string? theme)
        {
            return !string.IsNullOrWhiteSpace(theme) &&
                theme.Contains("Light", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyAccentColor(Application? application, ThemeVariant theme, Color startColor, bool fromSkin)
        {
            if (application == null)
            {
                return;
            }

            Color endColor = DarkenColor(startColor, 0.10);
            Color foregroundColor = fromSkin
                // The skin states which of its own colours it writes on its accent, and
                // that pairing is measured against 4.5:1 by Tools/generate-skins.py before
                // it is ever emitted. Re-deriving it here from luminance alone would throw
                // away a verified answer in order to guess at one.
                ? ResourceColor(application, theme, "ShareX.Color.Skin.Accent.Foreground",
                    GetAccentForegroundColor(application, startColor, endColor))
                : GetAccentForegroundColor(application, startColor, endColor);

            application.Resources["SystemAccentColor"] = startColor;
            application.Resources["ShareX.Color.Accent.Start"] = startColor;
            application.Resources["ShareX.Color.Accent.End"] = endColor;
            application.Resources["ShareX.Color.Accent.Foreground"] = foregroundColor;
            application.Resources["ShareX.Brush.Accent.Start"] = new SolidColorBrush(startColor);
            application.Resources["ShareX.Brush.Accent.Foreground"] = new SolidColorBrush(foregroundColor);
            application.Resources["ShareX.Brush.Accent"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(startColor, 0),
                    new GradientStop(endColor, 1)
                }
            };

            AccentColorChanged?.Invoke(null, startColor);
        }

        private static Color DarkenColor(Color color, double amount)
        {
            double factor = Math.Clamp(1 - amount, 0, 1);
            return Color.FromArgb(color.A,
                (byte)Math.Round(color.R * factor),
                (byte)Math.Round(color.G * factor),
                (byte)Math.Round(color.B * factor));
        }

        /// <summary>
        /// The colour to write text in on top of the accent just applied.
        /// </summary>
        /// <remarks>
        /// The candidates are read from the palette rather than written here, so the two
        /// values live in one place - the theme file - alongside the per-theme
        /// ShareX.Color.Accent.Foreground they are drawn from. The fallbacks are only
        /// reachable if the theme dictionary failed to load at all, in which case there
        /// is no token left to read and the extremes are the safest guess available.
        /// </remarks>
        private static Color GetAccentForegroundColor(Application application, Color startColor, Color endColor)
        {
            Color light = ResourceColor(application, "ShareX.Color.Accent.Foreground.Light", Colors.White);
            Color dark = ResourceColor(application, "ShareX.Color.Accent.Foreground.Dark", Colors.Black);

            return AccentForeground.Choose(startColor, endColor, light, dark);
        }

        private static Color ResourceColor(Application application, string key, Color fallback) =>
            application.TryFindResource(key, out object? value) && value is Color color ? color : fallback;

        /// <summary>
        /// A colour from a named theme variant, whichever variant is being applied.
        /// </summary>
        /// <remarks>
        /// The variant is passed rather than read from the application, because this is
        /// called while the theme is being switched: asking the application which theme
        /// it is on, in the middle of telling it, is how you paint one frame in the old
        /// skin's accent.
        /// </remarks>
        private static Color ResourceColor(Application application, ThemeVariant theme, string key, Color fallback) =>
            application.TryFindResource(key, theme, out object? value) && value is Color color ? color : fallback;

        private static void RunOnUIThread(Action action)
        {
            if (Application.Current == null)
            {
                throw new InvalidOperationException("Avalonia must be initialized before applying theme changes.");
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        }
    }
}
