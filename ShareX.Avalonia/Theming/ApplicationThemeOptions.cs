#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace ShareX.AvaloniaUI.Theming;

public sealed class ApplicationThemeOptions : INotifyPropertyChanged
{
    public const string DefaultTheme = "Dark";

    /// <summary>
    /// Modlight's accent, in its light-ground and dark-ground forms.
    /// </summary>
    /// <remarks>
    /// These are the panel's `--l-primary` / `--d-primary` converted from OKLCH,
    /// and they matter more than they look: ThemeManager.ApplyAccentColor
    /// OVERWRITES the ShareX.Color.Accent.* resources at startup with whatever
    /// value lands here, so the accent written into ShareXTheme.axaml never
    /// reaches the screen. Repainting the theme file and leaving this alone is
    /// why the app still did not match the panel after the palette work: it was
    /// running with ShareX's blue, or with whatever accent Windows happens to be
    /// set to.
    ///
    /// Two values rather than one because a single hex cannot serve both grounds
    /// - the light-mode purple is too dark to read as an accent on #0A0A0A, and
    /// the dark-mode one is too pale on white.
    /// </remarks>
    public const string DefaultAccentColorHex = "#5857E9";
    public const string DefaultAccentColorHexDark = "#7E88FB";

    private string _theme = DefaultTheme;
    private bool _useSystemTheme = true;
    private string _accentColorHex = DefaultAccentColorHex;
    private string _skin = SkinVariants.DefaultSkinId;

    /// <summary>
    /// Which of the panel's skins this installation wears.
    /// </summary>
    /// <remarks>
    /// The same value the website stores in <c>user.theme</c> and serves from
    /// <c>/api/theme</c> - one id, one meaning, both ends. Independent of
    /// <see cref="Theme"/>: a skin declares a light and a dark form of itself, and
    /// which of the two is on screen stays the user's business (or their system's).
    ///
    /// An unknown id is not corrected here. It is resolved to the default at the
    /// moment of use, so an installation that syncs a skin this build has never
    /// heard of still holds the right value once the build catches up, rather than
    /// having had it quietly rewritten by the older one.
    /// </remarks>
    public string Skin
    {
        get => _skin;
        set => SetField(ref _skin, string.IsNullOrWhiteSpace(value) ? SkinVariants.DefaultSkinId : value);
    }

    /// <summary>
    /// The last skin id this installation knows the server agreed to.
    /// </summary>
    /// <remarks>
    /// The whole conflict rule, in one field. When it matches <see cref="Skin"/> there is
    /// nothing local to push and the server's answer is adopted; when it does not, this
    /// installation is holding a choice the server never heard - made offline, or while it
    /// was down - and that choice is pushed instead of being quietly overwritten.
    ///
    /// Deliberately not a timestamp. Comparing a desktop clock to a server clock asks a
    /// question about two clocks; this asks the only question that has a reliable answer
    /// here, which is whether our last write ever landed.
    ///
    /// Starts equal to <see cref="Skin"/>, and that is load-bearing: a fresh installation
    /// has not chosen anything, so its first sync must ADOPT the website's skin. Starting
    /// this empty would make "I have never picked a skin" look identical to "I picked one
    /// and it never reached the server", and a brand-new install would push its default
    /// over whatever the account was already wearing. A settings file written by a build
    /// that predates this field lands on the same value for the same reason.
    /// </remarks>
    public string SkinConfirmed { get; set; } = SkinVariants.DefaultSkinId;

    /// <summary>
    /// Default OFF: the brand colour is the brand colour.
    /// </summary>
    /// <remarks>
    /// Upstream defaulted this ON, which meant the app wore whatever accent the
    /// user's Windows was set to. That is a reasonable default for a tool with no
    /// identity of its own; it is the wrong one for a client whose whole job is
    /// to look like one specific web panel. Still a setting - somebody who wants
    /// their system colour can turn it back on.
    /// </remarks>
    private bool _useSystemAccentColor;

    /// <summary>
    /// Which product's appearance defaults this block was last stamped with.
    /// </summary>
    /// <remarks>
    /// Changing a field initialiser only helps installations that have no saved
    /// settings, and both of the cases that matter here DO have saved settings:
    /// anyone who has already run this build, and - more importantly - anyone who
    /// ticks "copy my existing ShareX settings" during install, which brings
    /// ShareX's ApplicationConfig.json across with its blue accent and its
    /// "follow the Windows accent" flag. Without this, the headline feature of
    /// the installer would hand new users a Modlight wearing ShareX's colours.
    ///
    /// Stamped once. After that the appearance block belongs to the user, and a
    /// colour they picked is never touched again.
    /// </remarks>
    public int BrandVersion { get; set; }

    private const int CurrentBrandVersion = 1;

    /// <summary>
    /// Bring an unstamped appearance block up to Modlight's identity. Returns
    /// true when something was changed, so the caller can save.
    /// </summary>
    public bool MigrateBrand()
    {
        if (BrandVersion >= CurrentBrandVersion)
        {
            return false;
        }

        BrandVersion = CurrentBrandVersion;

        // Only the two settings that carry the OLD product's identity. Theme and
        // UseSystemTheme are preferences about the user's environment, not about
        // the brand, so an imported "I like light mode" survives.
        // Through SetField so anything already bound to these updates, and so the
        // AccentColor notification fires with them.
        SetField(ref _accentColorHex, DefaultAccentColorHex, nameof(AccentColorHex));
        SetField(ref _useSystemAccentColor, false, nameof(UseSystemAccentColor));
        return true;
    }

    /// <summary>
    /// True while the accent is still whichever default we chose, so the theme
    /// can pick the form that suits the current ground. Once the user picks a
    /// colour it is theirs, and nothing here second-guesses it.
    /// </summary>
    public bool IsAccentDefault =>
        string.Equals(_accentColorHex, DefaultAccentColorHex, System.StringComparison.OrdinalIgnoreCase)
        || string.Equals(_accentColorHex, DefaultAccentColorHexDark, System.StringComparison.OrdinalIgnoreCase);

    public string Theme
    {
        get => _theme;
        set => SetField(ref _theme, value);
    }

    public bool UseSystemTheme
    {
        get => _useSystemTheme;
        set => SetField(ref _useSystemTheme, value);
    }

    public string AccentColorHex
    {
        get => _accentColorHex;
        set => SetField(ref _accentColorHex, value);
    }

    public Color AccentColor
    {
        get => Color.TryParse(AccentColorHex, out Color color) && color.A > 0
            ? color
            : Color.Parse(DefaultAccentColorHex);
        set => AccentColorHex = $"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    // Newtonsoft.Json convention used by ApplicationConfig without coupling this project to Newtonsoft.Json.
    public bool ShouldSerializeAccentColor() => false;

    public bool UseSystemAccentColor
    {
        get => _useSystemAccentColor;
        set => SetField(ref _useSystemAccentColor, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

            if (propertyName == nameof(AccentColorHex))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentColor)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
