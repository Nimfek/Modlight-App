#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Platform;
using ShareX.AvaloniaUI.Controls;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using AvaloniaColor = Avalonia.Media.Color;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;
using MessageBoxResult = ShareX.AvaloniaUI.DialogResult;

namespace ShareX;

public sealed class ApplicationSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private string _selectedPageId = "general";
    private ClipboardFormatItem? _selectedClipboardFormat;
    private string _personalFolderPath = string.Empty;
    private string _personalFolderPreview = string.Empty;
    private string _screenshotsFolderPreview = string.Empty;
    private bool _startWithWindows;
    private bool _startWithWindowsEnabled;
    private string _startWithWindowsText = string.Empty;
    private bool _shellContextMenu;
    private bool _editWithShareX;
    private bool _sendToMenu;
    private bool _exportSettings = true;
    private bool _exportHistory = true;
    private bool _personalPathDirty;
    private bool _isBusy;
    private bool _restartRequired;
    private string _statusMessage = string.Empty;
    private bool _disposed;

    private ApplicationConfig Settings => Program.Settings;

    /// <summary>
    /// The external components list - the same one the installer asked about and the same
    /// one a failed recording opens. Created once and kept, because its rows carry the state
    /// of a download that may still be running.
    /// </summary>
    public ComponentsViewModel Components { get; } = new();
    public ObservableCollection<ClipboardFormatItem> ClipboardFormats { get; private set; } = [];
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = CreateLanguageOptions();
    public IReadOnlyList<EnumOption<HotkeyType>> HotkeyTypeOptions { get; } = CreateEnumOptions<HotkeyType>();
    public IReadOnlyList<EnumOption<ThumbnailTitleLocation>> ThumbnailTitleLocationOptions { get; } = CreateEnumOptions<ThumbnailTitleLocation>();
    public IReadOnlyList<EnumOption<ThumbnailViewClickAction>> ThumbnailClickActionOptions { get; } = CreateEnumOptions<ThumbnailViewClickAction>();
    public IReadOnlyList<EnumOption<ProxyMethod>> ProxyMethodOptions { get; } = CreateEnumOptions<ProxyMethod>();
    public IReadOnlyList<EnumOption<ContentAlignment>> DropAlignmentOptions { get; } =
    [
        new(ContentAlignment.TopLeft, Strings.ApplicationSettingsWindow_TopLeft),
        new(ContentAlignment.TopCenter, Strings.ApplicationSettingsWindow_TopCenter),
        new(ContentAlignment.TopRight, Strings.ApplicationSettingsWindow_TopRight),
        new(ContentAlignment.MiddleLeft, Strings.ApplicationSettingsWindow_MiddleLeft),
        new(ContentAlignment.MiddleCenter, Strings.ApplicationSettingsWindow_MiddleCenter),
        new(ContentAlignment.MiddleRight, Strings.ApplicationSettingsWindow_MiddleRight),
        new(ContentAlignment.BottomLeft, Strings.ApplicationSettingsWindow_BottomLeft),
        new(ContentAlignment.BottomCenter, Strings.ApplicationSettingsWindow_BottomCenter),
        new(ContentAlignment.BottomRight, Strings.ApplicationSettingsWindow_BottomRight)
    ];
    public IReadOnlyList<EnumOption<int>> BufferSizeOptions { get; private set; } = [];
    public IReadOnlyList<EnumOption<string>> ThemeOptions { get; } =
    [
        new("Dark", Strings.ApplicationSettingsWindow_Dark),
        new("Light", Strings.ApplicationSettingsWindow_Light)
    ];

    /// <summary>
    /// The fourteen skins, named the way the website names them.
    /// </summary>
    /// <remarks>
    /// The names come from <see cref="SkinVariants"/>, which is generated from the panel's
    /// own message catalogues, rather than from resx. They are labels for the panel's
    /// skins, the panel adds skins regularly, and restating fourteen of them by hand in two
    /// more files would be out of date the week after it was done. The strings this window
    /// itself authors - the heading, the note under the picker - are in resx as usual.
    /// </remarks>
    public IReadOnlyList<EnumOption<string>> SkinOptions { get; } =
        SkinVariants.All.Select(skin => new EnumOption<string>(skin.Id, SkinName(skin))).ToList();

    private static string SkinName(SkinDefinition skin) =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "pl", StringComparison.OrdinalIgnoreCase)
            ? skin.NamePolish
            : skin.NameEnglish;

    /// <summary>Which of these pages the shell's section list is pointing at.</summary>
    /// <remarks>
    /// A page id and not a navigation item: the list of sections is built once by
    /// <see cref="SettingsSections"/> for the whole window, and this view model only needs to
    /// know which of its own pages to show. Holding the item as well would mean two lists that
    /// have to agree on the same ids anyway.
    /// </remarks>
    public void SelectPage(string pageId)
    {
        if (!SetField(ref _selectedPageId, pageId))
        {
            return;
        }

        OnPageChanged();

        if (pageId == "integration")
        {
            RefreshStartWithWindows();
        }
        else if (pageId == "components")
        {
            Components.RefreshAll();
        }
    }

    public bool IsGeneralPage => IsPage("general");
    public bool IsComponentsPage => IsPage("components");
    public bool IsThemePage => IsPage("theme");
    public bool IsIntegrationPage => IsPage("integration");
    public bool IsPathsPage => IsPage("paths");
    public bool IsSettingsPage => IsPage("settings");
    public bool IsMainWindowPage => IsPage("main-window");
    public bool IsClipboardFormatsPage => IsPage("clipboard-formats");
    public bool IsUploadPage => IsPage("upload");
    public bool IsHistoryPage => IsPage("history");
    public bool IsPrintPage => IsPage("print");
    public bool IsProxyPage => IsPage("proxy");
    public bool IsAdvancedPage => IsPage("advanced");

    public bool UpdatesVisible
    {
        get
        {
            return !SystemOptions.DisableUpdateCheck && Program.UpdateManager.HasReleaseChannel;
        }
    }

    public bool WindowsIntegrationVisible
    {
        get
        {
            return true;
        }
    }

    public LanguageOption? SelectedLanguage
    {
        get => Find(LanguageOptions, Settings.Language);
        set
        {
            if (value == null || Settings.Language == value.Value)
            {
                return;
            }

            Settings.Language = value.Value;

            if (LanguageHelper.ChangeLanguage(value.Value))
            {
                RestartRequired = true;
            }
        }
    }

    public bool ShowTray
    {
        get => Settings.ShowTray;
        set
        {
            if (SetSetting(Settings.ShowTray, value, x => Settings.ShowTray = x))
            {
                MainWindowIntegration.SetTrayVisible(value);
                OnPropertyChanged(nameof(SilentRunEnabled));
            }
        }
    }

    public bool SilentRunEnabled => ShowTray;
    public bool SilentRun { get => Settings.SilentRun; set => SetSetting(Settings.SilentRun, value, x => Settings.SilentRun = x); }
    public bool TrayIconProgressEnabled { get => Settings.TrayIconProgressEnabled; set => SetSetting(Settings.TrayIconProgressEnabled, value, x => Settings.TrayIconProgressEnabled = x); }

    public bool TaskbarProgressEnabled
    {
        get => Settings.TaskbarProgressEnabled;
        set
        {
            if (SetSetting(Settings.TaskbarProgressEnabled, value, x => Settings.TaskbarProgressEnabled = x))
            {
                TaskbarManager.Enabled = value;
            }
        }
    }

    public bool TaskbarProgressSupported => TaskbarManager.IsPlatformSupported;

    public bool UseWhiteShareXIcon
    {
        get => Settings.UseWhiteShareXIcon;
        set
        {
            if (SetSetting(Settings.UseWhiteShareXIcon, value, x => Settings.UseWhiteShareXIcon = x))
            {
                InvokeOnMainThread(Program.MainForm.UpdateTheme);
            }
        }
    }

    public bool RememberMainFormPosition { get => Settings.RememberMainFormPosition; set => SetSetting(Settings.RememberMainFormPosition, value, x => Settings.RememberMainFormPosition = x); }
    public bool RememberMainFormSize { get => Settings.RememberMainFormSize; set => SetSetting(Settings.RememberMainFormSize, value, x => Settings.RememberMainFormSize = x); }

    public bool UseSystemTheme
    {
        get => Settings.ThemeOptions.UseSystemTheme;
        set
        {
            if (SetSetting(Settings.ThemeOptions.UseSystemTheme, value, x => Settings.ThemeOptions.UseSystemTheme = x))
            {
                OnPropertyChanged(nameof(CanEditTheme));
            }
        }
    }

    public bool CanEditTheme => !UseSystemTheme;

    public EnumOption<string>? SelectedTheme
    {
        get => Find(ThemeOptions, NormalizeTheme(Settings.ThemeOptions.Theme));
        set
        {
            if (value != null)
            {
                SetSetting(Settings.ThemeOptions.Theme, value.Value, x => Settings.ThemeOptions.Theme = x);
            }
        }
    }

    /// <summary>
    /// Which skin the app wears - and, through the sync, which one the website wears.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="SelectedTheme"/> on purpose. A skin brings a light and a
    /// dark form of itself, so choosing one says nothing about whether the app should be on
    /// a light or a dark ground - that stays the system's business, or the user's.
    ///
    /// Writing to <c>ThemeOptions.Skin</c> repaints the app at once, because ThemeManager
    /// is subscribed to that object. The write to the SERVER deliberately does not happen
    /// here: this setter runs on every click of a fourteen-item list, and this window's
    /// house rule is that nothing is persisted until it closes. <see cref="Dispose"/> sends
    /// it, once, and only if it ended up different from what the server last confirmed.
    /// </remarks>
    public EnumOption<string>? SelectedSkin
    {
        get => Find(SkinOptions, SkinVariants.Resolve(Settings.ThemeOptions.Skin).Id);
        set
        {
            if (value != null)
            {
                SetSetting(Settings.ThemeOptions.Skin, value.Value, x => Settings.ThemeOptions.Skin = x);
            }
        }
    }

    public bool UseSystemAccentColor
    {
        get => Settings.ThemeOptions.UseSystemAccentColor;
        set
        {
            if (SetSetting(Settings.ThemeOptions.UseSystemAccentColor, value, x => Settings.ThemeOptions.UseSystemAccentColor = x))
            {
                OnPropertyChanged(nameof(CanEditAccentColor));
            }
        }
    }

    public bool CanEditAccentColor => !UseSystemAccentColor;

    public AvaloniaColor AccentColor
    {
        get => Settings.ThemeOptions.AccentColor;
        set => SetSetting(Settings.ThemeOptions.AccentColor, value, x => Settings.ThemeOptions.AccentColor = x);
    }

    public EnumOption<HotkeyType>? SelectedTrayLeftDoubleClickAction
    {
        get => Find(HotkeyTypeOptions, Settings.TrayLeftDoubleClickAction);
        set { if (value != null) SetSetting(Settings.TrayLeftDoubleClickAction, value.Value, x => Settings.TrayLeftDoubleClickAction = x); }
    }

    public EnumOption<HotkeyType>? SelectedTrayLeftClickAction
    {
        get => Find(HotkeyTypeOptions, Settings.TrayLeftClickAction);
        set { if (value != null) SetSetting(Settings.TrayLeftClickAction, value.Value, x => Settings.TrayLeftClickAction = x); }
    }

    public EnumOption<HotkeyType>? SelectedTrayMiddleClickAction
    {
        get => Find(HotkeyTypeOptions, Settings.TrayMiddleClickAction);
        set { if (value != null) SetSetting(Settings.TrayMiddleClickAction, value.Value, x => Settings.TrayMiddleClickAction = x); }
    }

    /// <summary>
    /// Whether the app may look for a newer release on its own.
    /// </summary>
    /// <remarks>
    /// Off means off: MainForm gates the timer on this before it is ever
    /// armed, so nothing is asked and nothing is fetched. It has no say over the About
    /// window, which checks because a person opened it and asked.
    /// </remarks>
    public bool AutoCheckUpdate
    {
        get => Settings.AutoCheckUpdate;
        set
        {
            SetSetting(Settings.AutoCheckUpdate, value, x => Settings.AutoCheckUpdate = x);
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!_startWithWindowsEnabled || _startWithWindows == value)
            {
                return;
            }

            try
            {
                InvokeOnMainThread(() => StartupManager.State = value ? StartupState.Enabled : StartupState.Disabled);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                StatusMessage = e.Message;
            }

            RefreshStartWithWindows();
        }
    }

    public bool StartWithWindowsEnabled { get => _startWithWindowsEnabled; private set => SetField(ref _startWithWindowsEnabled, value); }
    public string StartWithWindowsText { get => _startWithWindowsText; private set => SetField(ref _startWithWindowsText, value); }

    public bool ShellContextMenu
    {
        get => _shellContextMenu;
        set
        {
            if (SetField(ref _shellContextMenu, value))
            {
                InvokeOnMainThread(() => IntegrationHelpers.CreateShellContextMenuButton(value));
            }
        }
    }

    public bool EditWithShareX
    {
        get => _editWithShareX;
        set
        {
            if (SetField(ref _editWithShareX, value))
            {
                InvokeOnMainThread(() => IntegrationHelpers.CreateEditShellContextMenuButton(value));
            }
        }
    }

    public bool SendToMenu
    {
        get => _sendToMenu;
        set
        {
            if (SetField(ref _sendToMenu, value))
            {
                InvokeOnMainThread(() => IntegrationHelpers.CreateSendToMenuButton(value));
            }
        }
    }

    public string PersonalFolderPath
    {
        get => _personalFolderPath;
        set
        {
            if (!SetField(ref _personalFolderPath, value))
            {
                return;
            }

            UpdatePersonalFolderPreview();
            _personalPathDirty = true;
        }
    }

    public string PersonalFolderPreview { get => _personalFolderPreview; private set => SetField(ref _personalFolderPreview, value); }

    public bool UseCustomScreenshotsPath
    {
        get => Settings.UseCustomScreenshotsPath;
        set
        {
            if (SetSetting(Settings.UseCustomScreenshotsPath, value, x => Settings.UseCustomScreenshotsPath = x))
            {
                UpdateScreenshotsFolderPreview();
            }
        }
    }

    public string CustomScreenshotsPath
    {
        get => Settings.CustomScreenshotsPath;
        set
        {
            string sanitized = FileHelpers.SanitizePath(value);
            if (SetSetting(Settings.CustomScreenshotsPath, sanitized, x => Settings.CustomScreenshotsPath = x))
            {
                UpdateScreenshotsFolderPreview();
            }
        }
    }

    public string SaveImageSubFolderPattern
    {
        get => Settings.SaveImageSubFolderPattern;
        set
        {
            string sanitized = FileHelpers.SanitizePath(value);
            if (SetSetting(Settings.SaveImageSubFolderPattern, sanitized, x => Settings.SaveImageSubFolderPattern = x))
            {
                UpdateScreenshotsFolderPreview();
            }
        }
    }

    public string SaveImageSubFolderPatternWindow
    {
        get => Settings.SaveImageSubFolderPatternWindow;
        set => SetSetting(Settings.SaveImageSubFolderPatternWindow, FileHelpers.SanitizePath(value), x => Settings.SaveImageSubFolderPatternWindow = x);
    }

    public string ScreenshotsFolderPreview { get => _screenshotsFolderPreview; private set => SetField(ref _screenshotsFolderPreview, value); }

    public bool ExportSettings { get => _exportSettings; set { if (SetField(ref _exportSettings, value)) OnPropertyChanged(nameof(CanExport)); } }
    public bool ExportHistory { get => _exportHistory; set { if (SetField(ref _exportHistory, value)) OnPropertyChanged(nameof(CanExport)); } }
    public bool CanExport => !IsBusy && (ExportSettings || ExportHistory);

    public bool AutoCleanupBackupFiles { get => Settings.AutoCleanupBackupFiles; set => SetSetting(Settings.AutoCleanupBackupFiles, value, x => Settings.AutoCleanupBackupFiles = x); }
    public bool AutoCleanupLogFiles { get => Settings.AutoCleanupLogFiles; set => SetSetting(Settings.AutoCleanupLogFiles, value, x => Settings.AutoCleanupLogFiles = x); }
    public decimal CleanupKeepFileCount { get => Settings.CleanupKeepFileCount; set => SetSetting(Settings.CleanupKeepFileCount, decimal.ToInt32(value), x => Settings.CleanupKeepFileCount = x); }

    public bool ShowThumbnailTitle { get => Settings.ShowThumbnailTitle; set => SetSetting(Settings.ShowThumbnailTitle, value, x => Settings.ShowThumbnailTitle = x); }

    public EnumOption<ThumbnailTitleLocation>? SelectedThumbnailTitleLocation
    {
        get => Find(ThumbnailTitleLocationOptions, Settings.ThumbnailTitleLocation);
        set { if (value != null) SetSetting(Settings.ThumbnailTitleLocation, value.Value, x => Settings.ThumbnailTitleLocation = x); }
    }

    public decimal ThumbnailWidth
    {
        get => Settings.ThumbnailSize.Width;
        set => SetSetting(Settings.ThumbnailSize.Width, decimal.ToInt32(value), x => Settings.ThumbnailSize = new Size(x, Settings.ThumbnailSize.Height));
    }

    public decimal ThumbnailHeight
    {
        get => Settings.ThumbnailSize.Height;
        set => SetSetting(Settings.ThumbnailSize.Height, decimal.ToInt32(value), x => Settings.ThumbnailSize = new Size(Settings.ThumbnailSize.Width, x));
    }

    public EnumOption<ThumbnailViewClickAction>? SelectedThumbnailClickAction
    {
        get => Find(ThumbnailClickActionOptions, Settings.ThumbnailClickAction);
        set { if (value != null) SetSetting(Settings.ThumbnailClickAction, value.Value, x => Settings.ThumbnailClickAction = x); }
    }

    public ClipboardFormatItem? SelectedClipboardFormat
    {
        get => _selectedClipboardFormat;
        set
        {
            if (SetField(ref _selectedClipboardFormat, value))
            {
                OnPropertyChanged(nameof(HasSelectedClipboardFormat));
            }
        }
    }

    public bool HasSelectedClipboardFormat => SelectedClipboardFormat != null;

    /// <summary>Whether the copy-formats list has nothing in it - a fresh profile has not.</summary>
    public bool HasNoClipboardFormats => ClipboardFormats.Count == 0;

    public decimal UploadLimit { get => Settings.UploadLimit; set => SetSetting(Settings.UploadLimit, decimal.ToInt32(value), x => Settings.UploadLimit = x); }

    public EnumOption<int>? SelectedBufferSize
    {
        get => Find(BufferSizeOptions, Settings.BufferSizePower);
        set { if (value != null) SetSetting(Settings.BufferSizePower, value.Value, x => Settings.BufferSizePower = x); }
    }

    public decimal MaxUploadFailRetry { get => Settings.MaxUploadFailRetry; set => SetSetting(Settings.MaxUploadFailRetry, decimal.ToInt32(value), x => Settings.MaxUploadFailRetry = x); }

    public bool HistorySaveTasks { get => Settings.HistorySaveTasks; set => SetSetting(Settings.HistorySaveTasks, value, x => Settings.HistorySaveTasks = x); }
    public bool HistoryCheckURL { get => Settings.HistoryCheckURL; set => SetSetting(Settings.HistoryCheckURL, value, x => Settings.HistoryCheckURL = x); }
    public bool RecentTasksSave { get => Settings.RecentTasksSave; set => SetSetting(Settings.RecentTasksSave, value, x => Settings.RecentTasksSave = x); }
    public decimal RecentTasksMaxCount { get => Settings.RecentTasksMaxCount; set => SetSetting(Settings.RecentTasksMaxCount, decimal.ToInt32(value), x => Settings.RecentTasksMaxCount = x); }
    public bool RecentTasksShowInMainWindow { get => Settings.RecentTasksShowInMainWindow; set => SetSetting(Settings.RecentTasksShowInMainWindow, value, x => Settings.RecentTasksShowInMainWindow = x); }
    public bool RecentTasksShowInTrayMenu { get => Settings.RecentTasksShowInTrayMenu; set => SetSetting(Settings.RecentTasksShowInTrayMenu, value, x => Settings.RecentTasksShowInTrayMenu = x); }
    public bool RecentTasksTrayMenuMostRecentFirst { get => Settings.RecentTasksTrayMenuMostRecentFirst; set => SetSetting(Settings.RecentTasksTrayMenuMostRecentFirst, value, x => Settings.RecentTasksTrayMenuMostRecentFirst = x); }

    public bool DontShowPrintSettingsDialog { get => Settings.DontShowPrintSettingsDialog; set => SetSetting(Settings.DontShowPrintSettingsDialog, value, x => Settings.DontShowPrintSettingsDialog = x); }

    public bool DontShowWindowsPrintDialog
    {
        get => !Settings.PrintSettings.ShowPrintDialog;
        set
        {
            if (SetSetting(Settings.PrintSettings.ShowPrintDialog, !value, x => Settings.PrintSettings.ShowPrintDialog = x))
            {
                OnPropertyChanged(nameof(DefaultPrinterOverrideVisible));
            }
        }
    }

    public bool DefaultPrinterOverrideVisible => !Settings.PrintSettings.ShowPrintDialog;
    public string DefaultPrinterOverride { get => Settings.PrintSettings.DefaultPrinterOverride; set => SetSetting(Settings.PrintSettings.DefaultPrinterOverride, value, x => Settings.PrintSettings.DefaultPrinterOverride = x); }

    public EnumOption<ProxyMethod>? SelectedProxyMethod
    {
        get => Find(ProxyMethodOptions, Settings.ProxySettings.ProxyMethod);
        set
        {
            if (value == null || !SetSetting(Settings.ProxySettings.ProxyMethod, value.Value, x => Settings.ProxySettings.ProxyMethod = x))
            {
                return;
            }

            if (value.Value == ProxyMethod.Automatic)
            {
                Settings.ProxySettings.IsValidProxy();
                OnPropertyChanged(nameof(ProxyHost));
                OnPropertyChanged(nameof(ProxyPort));
            }

            OnPropertyChanged(nameof(ProxyCredentialsEnabled));
            OnPropertyChanged(nameof(ManualProxyEnabled));
        }
    }

    public bool ProxyCredentialsEnabled => Settings.ProxySettings.ProxyMethod != ProxyMethod.None;
    public bool ManualProxyEnabled => Settings.ProxySettings.ProxyMethod == ProxyMethod.Manual;
    public string ProxyUsername { get => Settings.ProxySettings.Username ?? string.Empty; set => SetSetting(Settings.ProxySettings.Username, value, x => Settings.ProxySettings.Username = x); }
    public string ProxyPassword { get => Settings.ProxySettings.Password ?? string.Empty; set => SetSetting(Settings.ProxySettings.Password, value, x => Settings.ProxySettings.Password = x); }
    public string ProxyHost { get => Settings.ProxySettings.Host ?? string.Empty; set => SetSetting(Settings.ProxySettings.Host, value, x => Settings.ProxySettings.Host = x); }
    public decimal ProxyPort { get => Settings.ProxySettings.Port; set => SetSetting(Settings.ProxySettings.Port, decimal.ToInt32(value), x => Settings.ProxySettings.Port = x); }

    public bool BinaryUnits
    {
        get => Settings.BinaryUnits;
        set
        {
            if (SetSetting(Settings.BinaryUnits, value, x => Settings.BinaryUnits = x))
            {
                RefreshBufferSizeOptions();
            }
        }
    }

    public bool ShowMostRecentTaskFirst { get => Settings.ShowMostRecentTaskFirst; set => SetSetting(Settings.ShowMostRecentTaskFirst, value, x => Settings.ShowMostRecentTaskFirst = x); }
    public bool WorkflowsOnlyShowEdited { get => Settings.WorkflowsOnlyShowEdited; set => SetSetting(Settings.WorkflowsOnlyShowEdited, value, x => Settings.WorkflowsOnlyShowEdited = x); }
    public bool TrayAutoExpandCaptureMenu { get => Settings.TrayAutoExpandCaptureMenu; set => SetSetting(Settings.TrayAutoExpandCaptureMenu, value, x => Settings.TrayAutoExpandCaptureMenu = x); }
    public string BrowserPath { get => Settings.BrowserPath ?? string.Empty; set => SetSetting(Settings.BrowserPath, value, x => Settings.BrowserPath = x); }
    public bool SaveSettingsAfterTaskCompleted { get => Settings.SaveSettingsAfterTaskCompleted; set => SetSetting(Settings.SaveSettingsAfterTaskCompleted, value, x => Settings.SaveSettingsAfterTaskCompleted = x); }
    public bool DevMode { get => Settings.DevMode; set => SetSetting(Settings.DevMode, value, x => Settings.DevMode = x); }

    public bool DisableHotkeys { get => Settings.DisableHotkeys; set => SetSetting(Settings.DisableHotkeys, value, x => Settings.DisableHotkeys = x); }
    public bool DisableHotkeysOnFullscreen { get => Settings.DisableHotkeysOnFullscreen; set => SetSetting(Settings.DisableHotkeysOnFullscreen, value, x => Settings.DisableHotkeysOnFullscreen = x); }
    public decimal HotkeyRepeatLimit { get => Settings.HotkeyRepeatLimit; set => SetSetting(Settings.HotkeyRepeatLimit, decimal.ToInt32(value), x => Settings.HotkeyRepeatLimit = x); }

    public bool ShowClipboardContentViewer { get => Settings.ShowClipboardContentViewer; set => SetSetting(Settings.ShowClipboardContentViewer, value, x => Settings.ShowClipboardContentViewer = x); }
    public bool DefaultClipboardCopyImageFillBackground { get => Settings.DefaultClipboardCopyImageFillBackground; set => SetSetting(Settings.DefaultClipboardCopyImageFillBackground, value, x => Settings.DefaultClipboardCopyImageFillBackground = x); }
    public bool UseAlternativeClipboardCopyImage { get => Settings.UseAlternativeClipboardCopyImage; set => SetSetting(Settings.UseAlternativeClipboardCopyImage, value, x => Settings.UseAlternativeClipboardCopyImage = x); }
    public bool UseAlternativeClipboardGetImage { get => Settings.UseAlternativeClipboardGetImage; set => SetSetting(Settings.UseAlternativeClipboardGetImage, value, x => Settings.UseAlternativeClipboardGetImage = x); }

    public bool RotateImageByExifOrientationData { get => Settings.RotateImageByExifOrientationData; set => SetSetting(Settings.RotateImageByExifOrientationData, value, x => Settings.RotateImageByExifOrientationData = x); }
    public bool PNGStripColorSpaceInformation { get => Settings.PNGStripColorSpaceInformation; set => SetSetting(Settings.PNGStripColorSpaceInformation, value, x => Settings.PNGStripColorSpaceInformation = x); }

    public bool DisableUpload { get => Settings.DisableUpload; set => SetSetting(Settings.DisableUpload, value, x => Settings.DisableUpload = x); }
    public bool URLEncodeIgnoreEmoji { get => Settings.URLEncodeIgnoreEmoji; set => SetSetting(Settings.URLEncodeIgnoreEmoji, value, x => Settings.URLEncodeIgnoreEmoji = x); }
    public bool ShowMultiUploadWarning { get => Settings.ShowMultiUploadWarning; set => SetSetting(Settings.ShowMultiUploadWarning, value, x => Settings.ShowMultiUploadWarning = x); }
    public decimal ShowLargeFileSizeWarning { get => Settings.ShowLargeFileSizeWarning; set => SetSetting(Settings.ShowLargeFileSizeWarning, decimal.ToInt32(value), x => Settings.ShowLargeFileSizeWarning = x); }

    public bool UseMachineSpecificUploadersConfig { get => Settings.UseMachineSpecificUploadersConfig; set => SetSetting(Settings.UseMachineSpecificUploadersConfig, value, x => Settings.UseMachineSpecificUploadersConfig = x); }
    public string CustomUploadersConfigPath { get => Settings.CustomUploadersConfigPath ?? string.Empty; set => SetSetting(Settings.CustomUploadersConfigPath, value, x => Settings.CustomUploadersConfigPath = x); }
    public string CustomHotkeysConfigPath { get => Settings.CustomHotkeysConfigPath ?? string.Empty; set => SetSetting(Settings.CustomHotkeysConfigPath, value, x => Settings.CustomHotkeysConfigPath = x); }
    public string CustomScreenshotsPath2 { get => Settings.CustomScreenshotsPath2 ?? string.Empty; set => SetSetting(Settings.CustomScreenshotsPath2, value, x => Settings.CustomScreenshotsPath2 = x); }

    public decimal DropSize { get => Settings.DropSize; set => SetSetting(Settings.DropSize, decimal.ToInt32(value), x => Settings.DropSize = x); }
    public decimal DropOffset { get => Settings.DropOffset; set => SetSetting(Settings.DropOffset, decimal.ToInt32(value), x => Settings.DropOffset = x); }

    public EnumOption<ContentAlignment>? SelectedDropAlignment
    {
        get => Find(DropAlignmentOptions, Settings.DropAlignment);
        set { if (value != null) SetSetting(Settings.DropAlignment, value.Value, x => Settings.DropAlignment = x); }
    }

    public decimal DropOpacity { get => Settings.DropOpacity; set => SetSetting(Settings.DropOpacity, decimal.ToInt32(value), x => Settings.DropOpacity = x); }
    public decimal DropHoverOpacity { get => Settings.DropHoverOpacity; set => SetSetting(Settings.DropHoverOpacity, decimal.ToInt32(value), x => Settings.DropHoverOpacity = x); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanExport));
            }
        }
    }

    public bool RestartRequired { get => _restartRequired; private set => SetField(ref _restartRequired, value); }
    public string StatusMessage { get => _statusMessage; private set { if (SetField(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatusMessage)); } }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public ApplicationSettingsViewModel()
    {
        Reload();
    }

    public void AddClipboardFormat(string description, string value)
    {
        ClipboardFormat format = new(description, value);
        Settings.ClipboardContentFormats.Add(format);
        ClipboardFormatItem item = new(format);
        ClipboardFormats.Add(item);
        SelectedClipboardFormat = item;
        OnPropertyChanged(nameof(HasNoClipboardFormats));
    }

    public void RemoveSelectedClipboardFormat()
    {
        if (SelectedClipboardFormat == null)
        {
            return;
        }

        int index = ClipboardFormats.IndexOf(SelectedClipboardFormat);
        Settings.ClipboardContentFormats.Remove(SelectedClipboardFormat.Model);
        ClipboardFormats.Remove(SelectedClipboardFormat);
        SelectedClipboardFormat = ClipboardFormats.Count == 0 ? null : ClipboardFormats[Math.Min(index, ClipboardFormats.Count - 1)];
        OnPropertyChanged(nameof(HasNoClipboardFormats));
    }

    public void ResetThumbnailSize()
    {
        Settings.ThumbnailSize = new Size(200, 150);
        OnPropertyChanged(nameof(ThumbnailWidth));
        OnPropertyChanged(nameof(ThumbnailHeight));
    }

    public void EditQuickTaskMenu() => QuickTaskMenuEditorIntegration.Show();

    public void OpenPersonalFolder() => FileHelpers.OpenFolder(PersonalFolderPreview);
    public void OpenScreenshotsFolder() => FileHelpers.OpenFolder(ScreenshotsFolderPreview);

    public void ShowImagePrintSettings(Avalonia.Controls.Window owner)
    {
        InvokeOnMainThread(() =>
        {
            using Image image = TaskHelpers.GetScreenshot().CaptureActiveMonitor();
            PrintWindowIntegration.Show(image, Settings.PrintSettings, true, owner);
        });
    }

    public async Task ExportAsync(string path)
    {
        IsBusy = true;
        StatusMessage = Strings.ApplicationSettingsWindow_ExportingBackup;

        try
        {
            bool exportSettings = ExportSettings;
            bool exportHistory = ExportHistory;
            bool result = await Task.Run(() =>
            {
                SettingManager.SaveAllSettings();
                return SettingManager.Export(path, exportSettings, exportHistory);
            });
            StatusMessage = result
                ? string.Format(Strings.ApplicationSettingsWindow_BackupExportedTo, path)
                : Strings.ApplicationSettingsWindow_BackupExportFailed;
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
            StatusMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportAsync(string path)
    {
        IsBusy = true;
        StatusMessage = Strings.ApplicationSettingsWindow_ImportingBackup;

        try
        {
            bool result = await Task.Run(() =>
            {
                if (!SettingManager.Import(path))
                {
                    return false;
                }

                SettingManager.LoadAllSettings();
                return true;
            });

            if (result)
            {
                LanguageHelper.ChangeLanguage(Settings.Language);
                Reload();
                await UpdateMainFormAsync();
                StatusMessage = string.Format(Strings.ApplicationSettingsWindow_BackupImportedFrom, path);
            }
            else
            {
                StatusMessage = Strings.ApplicationSettingsWindow_BackupImportFailed;
            }
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
            StatusMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ResetAsync()
    {
        bool confirmed = InvokeOnMainThread(() => MessageBox.Show(
            Strings.ApplicationSettingsForm_btnResetSettings_Click_WouldYouLikeToResetShareXSettings,
            "Modlight - " + Strings.Confirmation,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Exclamation) == MessageBoxResult.Yes);

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;

        try
        {
            InvokeOnMainThread(() =>
            {
                SettingManager.ResetSettings();
                SettingManager.SaveAllSettings();
            });
            LanguageHelper.ChangeLanguage(Settings.Language);
            Reload();
            await UpdateMainFormAsync();
            StatusMessage = Strings.ApplicationSettingsWindow_SettingsReset;
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
            StatusMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Restart() => InvokeOnMainThread(() => Program.Restart());

    public void Reload()
    {
        _personalFolderPath = Program.ReadPersonalPathConfig();
        UpdatePersonalFolderPreview();
        UpdateScreenshotsFolderPreview();
        RefreshIntegrations();
        RefreshStartWithWindows();

        ClipboardFormats = new ObservableCollection<ClipboardFormatItem>(Settings.ClipboardContentFormats
            .Select(x => new ClipboardFormatItem(x)));
        SelectedClipboardFormat = ClipboardFormats.FirstOrDefault();

        RefreshBufferSizeOptions();

        OnPropertyChanged(string.Empty);
    }

    private static string NormalizeTheme(string? theme)
    {
        return !string.IsNullOrWhiteSpace(theme) &&
            theme.Contains("Light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";
    }

    private void RefreshIntegrations()
    {
        _shellContextMenu = IntegrationHelpers.CheckShellContextMenuButton();
        _editWithShareX = IntegrationHelpers.CheckEditShellContextMenuButton();
        _sendToMenu = IntegrationHelpers.CheckSendToMenuButton();
    }

    private void RefreshStartWithWindows()
    {
        StartWithWindowsText = Strings.ApplicationSettingsForm_cbStartWithWindows_Text;
        StartWithWindowsEnabled = false;

        try
        {
            StartupState state = InvokeOnMainThread(() => StartupManager.State);
            _startWithWindows = state == StartupState.Enabled || state == StartupState.EnabledByPolicy;
            OnPropertyChanged(nameof(StartWithWindows));

            if (state == StartupState.DisabledByUser)
            {
                StartWithWindowsText = Strings.ApplicationSettingsForm_cbStartWithWindows_DisabledByUser_Text;
            }
            else if (state == StartupState.DisabledByPolicy)
            {
                StartWithWindowsText = Strings.ApplicationSettingsForm_cbStartWithWindows_DisabledByPolicy_Text;
            }
            else if (state == StartupState.EnabledByPolicy)
            {
                StartWithWindowsText = Strings.ApplicationSettingsForm_cbStartWithWindows_EnabledByPolicy_Text;
            }
            else
            {
                StartWithWindowsEnabled = true;

                // "Off" must never be able to mean "you are looking at a different
                // copy of Modlight". StartupManager.State compares the startup
                // shortcut against the RUNNING executable, so a build launched from
                // anywhere but the installed folder reports Disabled while Windows
                // still starts the installed copy at every sign-in. Unchecked is the
                // right answer for this copy and a wrong-looking one for the machine,
                // so when the two disagree the label says which copy Windows starts.
                string otherCopy = InvokeOnMainThread(() => StartupManager.OtherCopyStartupTargetPath);

                if (!string.IsNullOrEmpty(otherCopy))
                {
                    // The folder, not the full path: it is what tells the two copies
                    // apart, and it fits on the checkbox.
                    StartWithWindowsText = string.Format(Strings.ApplicationSettingsForm_cbStartWithWindows_OtherCopy_Text,
                        Path.GetDirectoryName(otherCopy));
                }
            }
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
            StatusMessage = e.Message;
        }
    }

    private void UpdatePersonalFolderPreview()
    {
        try
        {
            string path = FileHelpers.SanitizePath(_personalFolderPath);
            if (string.IsNullOrEmpty(path))
            {
                path = Program.Portable ? Program.PortablePersonalFolder : Program.DefaultPersonalFolder;
            }
            else
            {
                path = FileHelpers.GetAbsolutePath(path);
            }

            PersonalFolderPreview = path;
        }
        catch (Exception e)
        {
            PersonalFolderPreview = Strings.ApplicationSettingsWindow_ErrorPrefix + " " + e.Message;
        }
    }

    private void UpdateScreenshotsFolderPreview()
    {
        try
        {
            ScreenshotsFolderPreview = TaskHelpers.GetScreenshotsFolder();
        }
        catch (Exception e)
        {
            ScreenshotsFolderPreview = Strings.ApplicationSettingsWindow_ErrorPrefix + " " + e.Message;
        }
    }

    private bool IsPage(string id) => _selectedPageId == id;

    private void OnPageChanged()
    {
        OnPropertyChanged(nameof(IsGeneralPage));
        OnPropertyChanged(nameof(IsComponentsPage));
        OnPropertyChanged(nameof(IsThemePage));
        OnPropertyChanged(nameof(IsIntegrationPage));
        OnPropertyChanged(nameof(IsPathsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsMainWindowPage));
        OnPropertyChanged(nameof(IsClipboardFormatsPage));
        OnPropertyChanged(nameof(IsUploadPage));
        OnPropertyChanged(nameof(IsHistoryPage));
        OnPropertyChanged(nameof(IsPrintPage));
        OnPropertyChanged(nameof(IsProxyPage));
        OnPropertyChanged(nameof(IsAdvancedPage));
    }

    private bool SetSetting<T>(T current, T value, Action<T> setter, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return false;
        }

        setter(value);
        OnPropertyChanged(propertyName);
        return true;
    }

    private void RefreshBufferSizeOptions()
    {
        BufferSizeOptions = Enumerable.Range(0, 14)
            .Select(power => new EnumOption<int>(power, ((long)(Math.Pow(2, power) * 1024)).ToSizeString(Settings.BinaryUnits, 0)))
            .ToArray();
        OnPropertyChanged(nameof(BufferSizeOptions));
        OnPropertyChanged(nameof(SelectedBufferSize));
    }

    private void FlushPersonalPath()
    {
        if (!_personalPathDirty)
        {
            return;
        }

        _personalPathDirty = false;

        try
        {
            bool changed = InvokeOnMainThread(() => Program.WritePersonalPathConfig(FileHelpers.SanitizePath(_personalFolderPath)));
            if (changed)
            {
                RestartRequired = true;
            }
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
            StatusMessage = e.Message;
        }
    }

    private async Task UpdateMainFormAsync()
    {
        Task updateTask = InvokeOnMainThread(() => Program.MainForm.UpdateControls());
        await updateTask;
    }

    private static IReadOnlyList<EnumOption<T>> CreateEnumOptions<T>() where T : struct, Enum =>
        Helpers.GetEnums<T>().Select(x => new EnumOption<T>(x, x.GetLocalizedDescription())).ToArray();

    /// <summary>
    /// The language list, with a flag next to each language and a glyph next to anything
    /// that has not got one.
    /// </summary>
    /// <remarks>
    /// The glyph is decided by the flag that actually loaded, not by the enum member, so
    /// a row that lost its picture falls back to an icon rather than to a hole.
    /// </remarks>
    private static IReadOnlyList<LanguageOption> CreateLanguageOptions() =>
        Helpers.GetEnums<SupportedLanguage>()
            .Select(x =>
            {
                AvaloniaBitmap? flag = LoadLanguageFlag(x);

                return new LanguageOption(
                    x,
                    x.GetLocalizedDescription(),
                    flag,
                    flag == null ? LucideIcons.languages : null);
            })
            .ToArray();

    /// <summary>
    /// The flag beside a language, or null when there is not one to be had.
    /// </summary>
    /// <remarks>
    /// Two things here, and the second is the one that matters.
    ///
    /// The authority in an avares URI is the ASSEMBLY name, not the namespace - and this
    /// assembly is Modlight, while the namespaces are still ShareX.*. The literal
    /// "ShareX" was correct upstream and stopped being correct the moment the binary was
    /// renamed, so every language asked for a picture in an assembly that does not exist,
    /// and every one of them threw. Reading the name off the type means the next rename
    /// cannot bring this back.
    ///
    /// And the load is guarded, because this runs from a constructor: decoration that can
    /// throw there takes the whole settings window down with it, which is exactly what
    /// happened. A flag that will not load is a missing picture. It is never a reason for
    /// the window not to open.
    /// </remarks>
    private static AvaloniaBitmap? LoadLanguageFlag(SupportedLanguage language)
    {
        if (language == SupportedLanguage.Automatic)
        {
            return null;
        }

        string assembly = typeof(ApplicationSettingsViewModel).Assembly.GetName().Name ?? "Modlight";
        string resourceName = LanguageHelper.GetCultureName(language).Split('-')[1].ToLowerInvariant();

        try
        {
            using Stream stream = AssetLoader.Open(new Uri($"avares://{assembly}/Resources/Flags/{resourceName}.png"));
            return new AvaloniaBitmap(stream);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return null;
        }
    }

    private static LanguageOption? Find(IReadOnlyList<LanguageOption> options, SupportedLanguage value) =>
        options.FirstOrDefault(x => x.Value == value);

    private static EnumOption<T>? Find<T>(IReadOnlyList<EnumOption<T>> options, T value) =>
        options.FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, value));

    private static void InvokeOnMainThread(Action action)
    {
        if (Program.MainForm == null || Program.MainForm.IsDisposed)
        {
            return;
        }

        if (Program.MainForm.InvokeRequired)
        {
            Program.MainForm.Invoke(action);
        }
        else
        {
            action();
        }
    }

    private static T InvokeOnMainThread<T>(Func<T> action)
    {
        if (Program.MainForm == null || Program.MainForm.IsDisposed)
        {
            return action();
        }

        if (Program.MainForm.InvokeRequired)
        {
            return (T)Program.MainForm.Invoke(action);
        }

        return action();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (LanguageOption language in LanguageOptions)
        {
            language.Flag?.Dispose();
        }

        if (!Program.MainForm.IsClosing)
        {
            FlushPersonalPath();
            InvokeOnMainThread(Program.MainForm.ApplyApplicationSettings);
            SettingManager.SaveApplicationConfigAsync();

            // The other half of the two-way sync. Fire and forget: a skin is not worth
            // holding a window closed for, and a server that will not take it leaves the
            // choice marked unconfirmed so the next start tries again.
            _ = ModlightTheme.PushAsync(CancellationToken.None);
        }
    }
}
