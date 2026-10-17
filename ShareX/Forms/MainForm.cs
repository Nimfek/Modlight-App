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

using ShareX.Localization;
using Avalonia.Styling;
using ShareX.AvaloniaUI.Integration;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Properties;
using ShareX.ScreenCaptureLib;
using ShareX.UploadersLib;
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;
using MessageBoxResult = ShareX.AvaloniaUI.DialogResult;

namespace ShareX;

/// <summary>
/// Hidden WinForms host for global hotkeys and the tray icon.
/// Avalonia owns the application lifetime, message loop, and visible windows.
/// </summary>
public sealed class MainForm : HotkeyForm
{
    private bool _forceClose;
    private bool _isClosing;

    public bool IsReady { get; private set; }
    internal bool IsClosing => _isClosing;
    internal ITrayIconService TrayIconService { get; }

    public MainForm()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Text = Program.Title;
        ShareXResources.UseWhiteIcon = Program.Settings.UseWhiteShareXIcon;
        Icon = ShareXResources.Icon;
        TrayIconService = new WinFormsTrayIconService(Icon, Program.TitleShort, Program.Settings.ShowTray);

        FormClosed += MainForm_FormClosed;
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
    }

    internal async Task InitializeAsync()
    {
        Show();

        NativeMethods.UseImmersiveDarkMode(Handle, ShareXResources.IsDarkTheme);

        await UpdateControls();

        bool showMainWindow = !(Program.SilentRun || Program.Settings.SilentRun) || !Program.Settings.ShowTray;
        MainWindowIntegration.Initialize(this, showMainWindow);

        if (showMainWindow)
        {
            await AfterShownJobs();
        }

        DebugHelper.WriteLine("Startup time: {0} ms", Program.StartTimer.ElapsedMilliseconds);

        await Program.CLI.UseCommandLineArgs();
    }

    public async Task UpdateControls()
    {
        IsReady = false;

        TaskManager.UpdateMainFormTip();
        TaskManager.RecentManager.InitItems();
        TaskbarManager.Enabled = Program.Settings.TaskbarProgressEnabled;

        ApplyApplicationSettings();
        await InitHotkeys();

        IsReady = true;
        MainWindowIntegration.RefreshMenus();
    }

    internal void ApplyApplicationSettings()
    {
        HotkeyRepeatLimit = Program.Settings.HotkeyRepeatLimit;

        HelpersOptions.CurrentProxy = Program.Settings.ProxySettings;
        HelpersOptions.URLEncodeIgnoreEmoji = Program.Settings.URLEncodeIgnoreEmoji;
        HelpersOptions.DefaultCopyImageFillBackground = Program.Settings.DefaultClipboardCopyImageFillBackground;
        HelpersOptions.UseAlternativeClipboardCopyImage = Program.Settings.UseAlternativeClipboardCopyImage;
        HelpersOptions.UseAlternativeClipboardGetImage = Program.Settings.UseAlternativeClipboardGetImage;
        HelpersOptions.RotateImageByExifOrientationData = Program.Settings.RotateImageByExifOrientationData;
        HelpersOptions.BrowserPath = Program.Settings.BrowserPath;
        HelpersOptions.RecentColors = Program.Settings.RecentColors;
        HelpersOptions.DevMode = Program.Settings.DevMode;
        Program.UpdateHelpersSpecialFolders();

        // After UpdateHelpersSpecialFolders, because it needs ExternalTools.ManagedFolder to
        // be set before it can tell an installed component from a missing one. Guarded
        // inside, so the settings window applying settings again does not re-run it.
        ModlightComponents.OnStartup();

        TaskManager.RecentManager.MaxCount = Program.Settings.RecentTasksMaxCount;

        ShareXResources.UseWhiteIcon = Program.Settings.UseWhiteShareXIcon;
        Icon = ShareXResources.Icon;
        Text = Program.Title;
        TrayIconService.ToolTipText = Program.TitleShort;
        TrayIconService.Visible = Program.Settings.ShowTray;

        UpdateTheme();
        ConfigureAutoUpdate();

        MainWindowIntegration.SetTitle(Program.Title);
        MainWindowIntegration.SetTrayVisible(Program.Settings.ShowTray);
        MainWindowIntegration.RefreshMenus();
    }

    public void UpdateTheme()
    {
        bool isDarkTheme = ThemeManager.IsDarkTheme;

        if (ShareXResources.IsDarkTheme != isDarkTheme)
        {
            ShareXResources.Theme = isDarkTheme ? ShareXTheme.DarkTheme : ShareXTheme.LightTheme;
        }

        if (IsHandleCreated)
        {
            NativeMethods.UseImmersiveDarkMode(Handle, ShareXResources.IsDarkTheme);
        }

#pragma warning disable WFO5001
        Application.SetColorMode(ShareXResources.IsDarkTheme ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001

        using Icon trayIcon = ShareXResources.Icon;
        TrayIconService.SetIcon(trayIcon);
        MainWindowIntegration.RefreshMenus();
    }

    private void ThemeManager_ThemeChanged(object? sender, ThemeVariant theme)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(UpdateTheme);
        }
        else
        {
            UpdateTheme();
        }
    }

    private void ConfigureAutoUpdate()
    {
        // HasReleaseChannel first, and it is not redundant with the user's own setting:
        // AutoCheckUpdate defaults to true and the timer fires at TimeSpan.Zero, so a
        // build with no feed would otherwise reach out on every single launch, before
        // the window is even up.
        Program.UpdateManager.AllowAutoUpdate = Program.UpdateManager.HasReleaseChannel &&
            !SystemOptions.DisableUpdateCheck && Program.Settings.AutoCheckUpdate;
        Program.UpdateManager.ConfigureAutoUpdate();
    }

    private async Task InitHotkeys()
    {
        await Task.Run(SettingManager.WaitHotkeysConfig);

        if (Program.HotkeyManager == null)
        {
            Program.HotkeyManager = new HotkeyManager(this);
            Program.HotkeyManager.HotkeyTrigger += HandleHotkeys;
        }

        // Deliberately false: never a modal at startup.
        //
        // Windows 11 gives the whole Print Screen family to the Snipping Tool out
        // of the box, and those four keys are our defaults - so a fresh install
        // greeted every new user with a warning dialog before they had seen the
        // app at all, and it sat in front of the main window until dismissed.
        // A dialog is the right answer to something the user just did; it is the
        // wrong answer to a condition that was true before they arrived.
        // The Capture tab says so instead, where the fix is one click away.
        Program.HotkeyManager.UpdateHotkeys(Program.HotkeysConfig.Hotkeys, false);
        DebugHelper.WriteLine("HotkeyManager started.");

        Program.WatchFolderManager ??= new WatchFolderManager();
        Program.WatchFolderManager.UpdateWatchFolders();
        DebugHelper.WriteLine("WatchFolderManager started.");

        MainWindowIntegration.RefreshMenus();
    }

    private async void HandleHotkeys(HotkeySettings hotkeySetting)
    {
        DebugHelper.WriteLine("Hotkey triggered. " + hotkeySetting);
        await TaskHelpers.ExecuteJob(hotkeySetting.TaskSettings);
    }

    private static Task AfterShownJobs()
    {
        MainWindowIntegration.Activate();
        return Task.CompletedTask;
    }

    public void ForceClose()
    {
        if (ScreenRecordManager.IsRecording)
        {
            if (MessageBox.Show(Strings.ShareXCannotBeClosedWhileScreenRecordingIsActive, "Modlight",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == MessageBoxResult.Yes)
            {
                ScreenRecordManager.AbortRecording();
            }

            return;
        }

        ExitApplication();
    }

    internal void ExitApplication()
    {
        _forceClose = true;
        MainWindowIntegration.Close();
        Close();
    }

    public new bool Visible => MainWindowIntegration.IsVisible;

    public new void Hide()
    {
        if (MainWindowIntegration.IsInitialized)
        {
            MainWindowIntegration.Hide();
        }
        else
        {
            base.Hide();
        }
    }

    public void ForceActivate()
    {
        if (MainWindowIntegration.IsInitialized)
        {
            MainWindowIntegration.Activate();
        }
        else
        {
            FormExtensions.ForceActivate(this);
        }
    }

    internal void SetAvaloniaScreenshotDelay(decimal delay)
    {
        Program.DefaultTaskSettings.CaptureSettings.ScreenshotDelay = delay;
        MainWindowIntegration.RefreshMenus();
    }

    internal void ExecuteAvaloniaMainFormCommand(MainFormCommand command)
    {
        switch (command)
        {
            case MainFormCommand.ApplicationSettings:
                OpenApplicationSettings();
                break;
            case MainFormCommand.TaskSettings:
                OpenTaskSettings();
                break;
            case MainFormCommand.HotkeySettings:
                OpenHotkeySettings();
                break;
            case MainFormCommand.ScreenshotsFolder:
                TaskHelpers.OpenScreenshotsFolder();
                break;
            case MainFormCommand.History:
                TaskHelpers.OpenHistory();
                break;
            case MainFormCommand.ImageHistory:
                TaskHelpers.OpenImageHistory();
                break;
            case MainFormCommand.DebugLog:
                TaskHelpers.OpenDebugLog();
                break;
            case MainFormCommand.TestImageUpload:
                UploadManager.UploadImage(ShareXResources.Logo);
                break;
            case MainFormCommand.TestTextUpload:
                UploadManager.UploadText(Strings.MainForm_tsmiTestTextUpload_Click_Text_upload_test);
                break;
            case MainFormCommand.TestFileUpload:
                UploadManager.UploadImage(ShareXResources.Logo, ImageDestination.FileUploader, Program.DefaultTaskSettings.FileDestination);
                break;
            case MainFormCommand.TestUrlShortener:
                UploadManager.ShortenURL(Links.Website);
                break;
            case MainFormCommand.TestUrlSharing:
                UploadManager.ShareURL(Links.Website);
                break;
            case MainFormCommand.Donate:
                URLHelpers.OpenURL(Links.Donate);
                break;
            case MainFormCommand.X:
                URLHelpers.OpenURL(Links.XFollow);
                break;
            case MainFormCommand.Discord:
                URLHelpers.OpenURL(Links.Discord);
                break;
            case MainFormCommand.About:
                AboutWindowIntegration.Show();
                break;
        }
    }

    private void OpenApplicationSettings()
    {
        ApplicationSettingsIntegration.Show();
    }

    private void OpenTaskSettings()
    {
        TaskSettingsIntegration.Show(Program.DefaultTaskSettings, true, () =>
        {
            if (!IsDisposed)
            {
                MainWindowIntegration.RefreshMenus();
                SettingManager.SaveApplicationConfigAsync();
            }
        });
    }

    private void OpenHotkeySettings()
    {
        if (Program.HotkeyManager == null)
        {
            return;
        }

        HotkeySettingsIntegration.Show(new HotkeySettingsAvaloniaService(
            Program.HotkeyManager,
            this,
            () =>
            {
                if (!IsDisposed)
                {
                    MainWindowIntegration.RefreshMenus();
                    SettingManager.SaveHotkeysConfigAsync();
                }
            }));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)WindowsMessages.QUERYENDSESSION)
        {
            EndSessionReasons reason = (EndSessionReasons)m.LParam.ToInt64();
            if (reason.HasFlag(EndSessionReasons.ENDSESSION_CLOSEAPP))
            {
                NativeMethods.RegisterApplicationRestart("-silent", 0);
            }

            m.Result = new IntPtr(1);
        }
        else if (m.Msg == (int)WindowsMessages.ENDSESSION)
        {
            if (m.WParam != IntPtr.Zero)
            {
                Program.CloseSequence();
            }

            m.Result = IntPtr.Zero;
        }
        else
        {
            base.WndProc(ref m);
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && !IsHandleCreated)
        {
            CreateHandle();
        }

        base.SetVisibleCore(false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && Program.Settings.ShowTray && !_forceClose)
        {
            e.Cancel = true;
            MainWindowIntegration.Hide();
            SettingManager.SaveAllSettingsAsync();
        }

        base.OnFormClosing(e);

        if (!e.Cancel)
        {
            _isClosing = true;
        }
    }

    private void MainForm_FormClosed(object? sender, FormClosedEventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        MainWindowIntegration.Close();
        TrayIconService.Dispose();
        TaskManager.StopAllTasks();
        AvaloniaBootstrapper.Shutdown();
    }
}
