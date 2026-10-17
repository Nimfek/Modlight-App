#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Integration;
using ShareX.AvaloniaUI.Theming;
using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX.HelpersLib;

public partial class UpdateMessageWindow : Window
{
    private DialogResult _result;

    /// <summary>Where "view changelog" goes. The feed decides; it is not a fixed address.</summary>
    private readonly string changelogUrl;

    public static bool IsOpen { get; private set; }

    public bool ActivateWindow { get; }
    public string MessageText { get; }

    /// <summary>What the feed says is in the release, or empty when it said nothing.</summary>
    public string NotesText { get; }

    public bool HasNotes => NotesText.Length > 0;
    public bool ShowChangelog { get; }

    public string HeaderText => Localization.Strings.UpdateMessageWindow_Header;
    public string DialogTitle => Localization.Strings.UpdateMessageBox_UpdateMessageBox_update_is_available;
    public string YesText => Localization.Strings.MyMessageBox_MyMessageBox_Yes;
    public string NoText => Localization.Strings.MyMessageBox_MyMessageBox_No;

    public UpdateMessageWindow()
    {
        ActivateWindow = true;
        MessageText = string.Empty;
        NotesText = string.Empty;
        changelogUrl = Links.Changelog;
        ShowChangelog = true;
        InitializeWindow();
    }

    private UpdateMessageWindow(UpdateChecker updateChecker, bool activateWindow)
    {
        ActivateWindow = activateWindow;
        MessageText = BuildMessage(updateChecker);
        NotesText = updateChecker.ReleaseNotes ?? string.Empty;
        changelogUrl = string.IsNullOrEmpty(updateChecker.ChangelogURL) ? Links.Changelog : updateChecker.ChangelogURL;
        ShowChangelog = !updateChecker.IsDev && !string.IsNullOrEmpty(changelogUrl);

        InitializeWindow();
    }

    private void InitializeWindow()
    {
        InitializeComponent();
        DataContext = this;
        Title = DialogTitle;
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        ShowActivated = ActivateWindow;

        Opened += OnOpened;
        Closing += (_, _) =>
        {
            if (_result == DialogResult.None) _result = DialogResult.No;
        };
    }

    public static async Task<DialogResult> StartAsync(UpdateChecker? updateChecker, bool activateWindow = true)
    {
        if (updateChecker == null || updateChecker.Status != UpdateStatus.UpdateAvailable)
        {
            return DialogResult.None;
        }

        IsOpen = true;

        try
        {
            DialogResult result = await ShowWindowAsync(updateChecker, activateWindow);
            if (result == DialogResult.Yes)
            {
                await updateChecker.DownloadUpdateAsync();
            }

            return result;
        }
        finally
        {
            IsOpen = false;
        }
    }

    private static Task<DialogResult> ShowWindowAsync(UpdateChecker updateChecker, bool activateWindow)
    {
        AvaloniaBootstrapper.EnsureInitialized();
        TaskCompletionSource<DialogResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                UpdateMessageWindow window = new(updateChecker, activateWindow);
                window.Closed += (_, _) => completion.TrySetResult(window._result);
                window.Show();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        return completion.Task;
    }

    private static string BuildMessage(UpdateChecker updateChecker)
    {
        StringBuilder text = new();
        text.AppendLine(updateChecker.IsPortable
            ? Localization.Strings.UpdateMessageBox_UpdateMessageBox_Portable
            : Localization.Strings.UpdateMessageBox_UpdateMessageBox_);
        text.AppendLine();
        text.Append(Localization.Strings.UpdateMessageBox_UpdateMessageBox_CurrentVersion);
        text.Append(": ");
        text.Append(updateChecker.CurrentVersion);
        text.AppendLine();
        text.Append(Localization.Strings.UpdateMessageBox_UpdateMessageBox_LatestVersion);
        text.Append(": ");
        // What the server published, verbatim, rather than the parse of it. The two are not
        // the same string: a feed may publish "v0.8.0" or "0.8.0-rc1", and the parsed Version
        // has dropped the parts it cannot hold. The user is being asked to accept a specific
        // release, so the name on the dialog should be the name on the release.
        text.Append(string.IsNullOrEmpty(updateChecker.LatestVersionText)
            ? updateChecker.LatestVersion?.ToString()
            : updateChecker.LatestVersionText);
        if (updateChecker.IsDev) text.Append(" Dev");
        if (updateChecker is GitHubUpdateChecker { IsPreRelease: true })
            text.Append($" ({Localization.Strings.UpdateChannel_PreRelease})");

        // The size, because consent to a download is consent to a download of some size, and
        // eighty megabytes on a metered connection is a different answer to eight.
        if (updateChecker.ReleaseSize > 0)
        {
            text.AppendLine();
            text.Append(Localization.Strings.UpdateMessageWindow_DownloadSize);
            text.Append(": ");
            text.Append(updateChecker.ReleaseSize.ToSizeString());
        }

        return text.ToString();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (ActivateWindow)
        {
            Activate();
            return;
        }

        WindowState = Avalonia.Controls.WindowState.Minimized;
        IntPtr handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero) NativeMethods.FlashWindowEx(handle, 10);
    }

    private void OnViewChangelogClick(object? sender, RoutedEventArgs e) => URLHelpers.OpenURL(changelogUrl);

    private void OnYesClick(object? sender, RoutedEventArgs e)
    {
        _result = DialogResult.Yes;
        Close();
    }

    private void OnNoClick(object? sender, RoutedEventArgs e)
    {
        _result = DialogResult.No;
        Close();
    }
}
