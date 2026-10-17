#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The Download tab.
/// </summary>
/// <remarks>
/// Its own control rather than another block inside MainWindow, which is already eighteen
/// hundred lines: this is a self-contained thing with a queue, a process and a settings
/// strip of its own, and it appears in the window as one element.
///
/// Everything here is a click handler or a picker. What a download is, and what happens to
/// it, is in <see cref="DownloadViewModel"/> and <see cref="DownloadItemViewModel"/>.
/// </remarks>
public partial class DownloadView : UserControl
{
    internal DownloadViewModel Model { get; } = new();

    public DownloadView()
    {
        InitializeComponent();

        // On the Grid, not on this control: the host in MainWindow binds IsVisible on the
        // element itself and that binding has to keep resolving against the window.
        Root.DataContext = Model;

        PropertyChanged += OnViewPropertyChanged;
    }

    /// <summary>
    /// Stop anything still running when the window goes away for good.
    /// </summary>
    /// <remarks>
    /// Hiding to the tray does not detach, so a download started and then minimised away
    /// keeps going - which is the whole point of a queue. If the app is killed outright
    /// instead of closed, nothing managed gets to run at all and the job object in
    /// <see cref="ChildProcessJob"/> is what stops yt-dlp.
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Model.Shutdown();
    }

    /// <summary>
    /// Put the caret in the address box when the tab is opened.
    /// </summary>
    /// <remarks>
    /// This is what makes Ctrl+V work in the plain way people expect: the box has focus, so
    /// the paste is the TextBox's own and behaves like every other text field in Windows -
    /// it replaces a selection, it can be undone, it takes a second paste. Handling the key
    /// ourselves instead would have reimplemented all of that, worse.
    ///
    /// Posted rather than done inline because the control is being made visible as this
    /// runs, and something that is not laid out yet cannot take focus.
    /// </remarks>
    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true)
        {
            // Both of the facts this tab states can change while it is not on screen:
            // yt-dlp can be installed from the notice's own link, and the YouTube session
            // is rewritten at the end of every run and can be deleted from outside the app
            // entirely. Asked again here rather than remembered from whenever the tab was
            // last looked at.
            Model.Refresh();

            Dispatcher.UIThread.Post(() => UrlBox.Focus());
        }
    }

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = StartAsync();
        }
    }

    private void OnDownloadClick(object? sender, RoutedEventArgs e) => _ = StartAsync();

    /// <summary>
    /// Check the address, make sure the chosen quality is reachable, ask where the file
    /// goes if that is still open, and queue it.
    /// </summary>
    /// <remarks>
    /// In that order. A folder chooser that opens for an address which was never going to
    /// work spends the user's one decision and then throws it away - and the ffmpeg
    /// question comes before it for the same reason, since its answer can change which
    /// quality somebody wants to pick.
    /// </remarks>
    private async Task StartAsync()
    {
        if (!Model.CheckUrl())
        {
            return;
        }

        // Without ffmpeg the quality in the picker cannot be honoured at all: on YouTube
        // every entry in it collapses to the one pre-muxed 360p stream. That was the
        // reported bug, so this is the moment to offer the thing that fixes it.
        //
        // The answer is deliberately not acted on. Somebody who says no still gets their
        // download, at whatever quality is reachable, with a line on the row saying why it
        // is that - a Download button that refuses to do anything until a 68 MB component
        // has been fetched would be a worse answer than the bug it is guarding against.
        Model.EnsureFFmpeg();

        string? folder = null;

        if (Model.NeedsFolderChoice)
        {
            folder = await PickFolderAsync();

            if (string.IsNullOrEmpty(folder))
            {
                // Closed the chooser. Not an error and not worth a message; the address is
                // still in the box, so pressing Download again picks up where this left off.
                return;
            }
        }

        Model.Enqueue(folder);
    }

    private void OnDestinationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadDestination destination })
        {
            Model.Destination = destination;
        }
    }

    private void OnMediaKindClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadMediaKind kind })
        {
            Model.MediaKind = kind;
        }
    }

    /// <summary>
    /// Install ffmpeg from the notice that says it is missing.
    /// </summary>
    /// <remarks>
    /// The same door as the one a download opens for itself, so there is one place this
    /// gets installed from and one place that knows whether it worked. Nothing else is
    /// needed here: <see cref="DownloadViewModel.EnsureFFmpeg"/> re-reads the tool paths
    /// afterwards, which is what takes this notice off the screen.
    /// </remarks>
    private void OnFFmpegInstallClick(object? sender, RoutedEventArgs e) => Model.EnsureFFmpeg();

    private async void OnChooseFolderClick(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync();

        if (!string.IsNullOrEmpty(folder))
        {
            Model.SetFixedFolder(folder);
        }
    }

    private void OnForgetFolderClick(object? sender, RoutedEventArgs e) => Model.SetFixedFolder("");

    /// <summary>
    /// Install yt-dlp, and look again if it worked.
    /// </summary>
    /// <remarks>
    /// This used to open yt-dlp's installation page in a browser, because Modlight would not
    /// fetch an executable on anybody's behalf. It does now - over HTTPS, against a pinned
    /// checksum, and only for a component the user has left ticked - so the button that
    /// looks like the answer is the answer. The manual route is still there: install it any
    /// way you like and press "check again".
    /// </remarks>
    private void OnToolInstallClick(object? sender, RoutedEventArgs e)
    {
        if (ComponentsWindowIntegration.Require(ModlightComponentCatalog.YtDlp, Strings.Components_FeatureDownload))
        {
            Model.RefreshTool();
        }
    }

    /// <summary>
    /// Open or close the panel in the top right corner.
    /// </summary>
    /// <remarks>
    /// The disk is asked on the way in, every time. Whether there is a YouTube session is
    /// a fact about a file that a download rewrites, a sign-out deletes and anything else
    /// on the PC can remove; a panel that answered it from what was true when the tab was
    /// built would be the one place in the app confidently showing the wrong colour.
    ///
    /// Nothing here has to close the panel when the pointer lands on the button while it
    /// is open: the light-dismiss layer takes that click, so this handler is not reached.
    /// The toggle is what makes the keyboard path work anyway.
    /// </remarks>
    private void OnYouTubeConfigClick(object? sender, RoutedEventArgs e)
    {
        if (YouTubeConfigPopup.IsOpen)
        {
            YouTubeConfigPopup.IsOpen = false;
            return;
        }

        OpenYouTubeConfig();
    }

    private void OpenYouTubeConfig()
    {
        Model.RefreshSignIn();
        YouTubeConfigPopup.IsOpen = true;
    }

    /// <summary>
    /// Sign in, from either of the panel's two buttons.
    /// </summary>
    /// <remarks>
    /// Posted rather than run inline, and the close is inside the same post. Closing a
    /// popup while the click that caused it is still being dispatched through the popup's
    /// own window tears that window down underneath the event - the same reason every
    /// dropdown in the image editor defers its close. Doing both in one post also fixes
    /// the order: the panel is gone before the browser window arrives on top of where it
    /// was.
    ///
    /// Nothing is retried here. A row that failed for want of a session is put back in the
    /// queue by the view model when a session is actually captured, which covers the rows
    /// this click never mentioned and the sign-ins that did not start from a row at all.
    /// </remarks>
    private void OnSignInClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            YouTubeConfigPopup.IsOpen = false;
            _ = Model.SignInAsync();
        });

    /// <summary>
    /// Throw the session away, with the panel left open.
    /// </summary>
    /// <remarks>
    /// Deliberately left open: signing out has no window of its own to show for itself, so
    /// the dot going red and the words changing under it are the entire confirmation that
    /// anything happened.
    /// </remarks>
    private void OnSignOutClick(object? sender, RoutedEventArgs e) => Model.SignOut();

    private void OnSignInRuntimeClick(object? sender, RoutedEventArgs e) =>
        URLHelpers.OpenURL(YouTubeCookies.RuntimePage);

    /// <summary>
    /// The remedy offered on a row that failed for want of a signed-in account.
    /// </summary>
    /// <remarks>
    /// It opens the panel rather than the browser window, because the owner asked for the
    /// sign-in to live in one place and this is the same door as the one in the corner -
    /// with the state and the way back out beside it, which a window opened straight from
    /// a list row would not have shown him.
    /// </remarks>
    private void OnRowSignInClick(object? sender, RoutedEventArgs e) => OpenYouTubeConfig();

    private void OnToolRecheckClick(object? sender, RoutedEventArgs e) => Model.RefreshTool();

    private void OnClearFinishedClick(object? sender, RoutedEventArgs e) => Model.ClearFinished();

    private void OnPauseClick(object? sender, RoutedEventArgs e) => Item(sender)?.Pause();

    private void OnResumeClick(object? sender, RoutedEventArgs e) => Item(sender)?.Resume();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Item(sender)?.Cancel();

    private void OnRetryClick(object? sender, RoutedEventArgs e) => Item(sender)?.Retry();

    /// <summary>
    /// Fetch a newer yt-dlp and try this row again.
    /// </summary>
    /// <remarks>
    /// The row owns the whole sequence, including what to say when there is nothing newer
    /// to fetch, because the answer belongs on the line the person is already reading.
    /// </remarks>
    private void OnUpdateToolClick(object? sender, RoutedEventArgs e)
    {
        DownloadItemViewModel? item = Item(sender);

        if (item != null)
        {
            _ = item.UpdateToolAndRetryAsync();
        }
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        DownloadItemViewModel? item = Item(sender);

        if (item != null)
        {
            Model.Remove(item);
        }
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        string? path = Item(sender)?.FilePath;

        if (!string.IsNullOrEmpty(path))
        {
            FileHelpers.OpenFolderWithFile(path);
        }
    }

    private static DownloadItemViewModel? Item(object? sender) =>
        sender is Button { Tag: DownloadItemViewModel item } ? item : null;

    private async Task<string?> PickFolderAsync()
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;

        if (storage == null)
        {
            return null;
        }

        try
        {
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Strings.Download_FolderPickerTitle,
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return null;
        }
    }
}
