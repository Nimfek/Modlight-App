#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using ShareX.HelpersLib;

namespace ShareX;

/// <summary>
/// The Upload tab: a window onto what this app is sending and what it has sent.
/// </summary>
/// <remarks>
/// Its own control rather than another slab of the main window's layout, because the
/// two have nothing to say to each other: the window hands it a visibility flag and the
/// tab reads <see cref="TaskManager"/> for itself. The only thing that has to be shared
/// is the drop handling, and that is shared by NOT being duplicated - the window
/// already accepts files dropped anywhere on itself, this tab included, and adding a
/// second drop target inside it would mean two overlays fighting over one drag.
///
/// Nothing here decides anything. Every handler is one line to the view model or to
/// <see cref="UploadManager"/>, which is the app's single entry point for uploading and
/// therefore the only one that reaches the streaming uploader.
/// </remarks>
public partial class UploadView : UserControl
{
    public UploadView()
    {
        InitializeComponent();

        // On the grid, not on this control: the window binds IsVisible on the
        // <local:UploadView/> element itself, and that binding resolves against
        // whatever DataContext the control has. Overriding it here would point that
        // binding at the view model, where the property does not exist, and the tab
        // would sit on top of the others for ever.
        Root.DataContext = Model;
    }

    internal UploadViewModel Model { get; } = new();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Model.Attach();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // TaskManager's events are static. A control that keeps them after its window
        // has gone keeps the window with it, and every upload for the rest of the
        // process would still be posting updates to a dead visual tree.
        Model.Detach();
    }

    /// <summary>
    /// The picker, which is the app's own file-upload entry point.
    /// </summary>
    /// <remarks>
    /// Not a second implementation of anything: this is the same call the upload menu
    /// and the tray make, so the multi-file warning, the remembered directory and the
    /// streaming uploader behind it are all whatever the rest of the app already does.
    /// </remarks>
    private void OnChooseFilesClick(object? sender, RoutedEventArgs e) => UploadManager.UploadFile();

    private void OnSignInClick(object? sender, RoutedEventArgs e) => SignInWindowIntegration.Show();

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => Model.Refresh();

    /// <summary>
    /// Stop one transfer.
    /// </summary>
    /// <remarks>
    /// <c>Stop</c> rather than <c>TaskManager.Remove</c>: stopping leaves the task on
    /// the list, so it lands in the history below as a cancellation with the file it
    /// came from still named. Removing it would make the row vanish, which reads as
    /// "it went" rather than "you stopped it".
    /// </remarks>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TransferViewModel transfer })
        {
            transfer.Task.Stop();
            transfer.Refresh();
        }
    }

    /// <summary>
    /// Stop waiting for the clock and try this one now.
    /// </summary>
    /// <remarks>
    /// The button exists because the queue's timing is necessarily a guess about the
    /// world, and the person sitting at the machine is not guessing: they have just
    /// plugged the cable back in, signed in again, or made room. Waiting a quarter of an
    /// hour for a schedule to catch up with something they can see is the kind of
    /// helplessness that makes people give up and do it by hand.
    /// </remarks>
    private void OnQueueSendNowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueueRowViewModel row })
        {
            ModlightUploadQueue.SendNow(row.Id);
        }
    }

    /// <summary>
    /// Drop one queued upload for good.
    /// </summary>
    /// <remarks>
    /// It leaves a cancelled row in the history below rather than simply disappearing -
    /// the same rule <see cref="OnCancelClick"/> follows one method up, and for the same
    /// reason: a row that vanishes from a list of uploads reads as one that went.
    /// </remarks>
    private void OnQueueCancelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueueRowViewModel row })
        {
            ModlightUploadQueue.Cancel(row.Id);
        }
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UploadHistoryViewModel row })
        {
            row.Copy();
        }
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UploadHistoryViewModel row } && row.HasLink)
        {
            URLHelpers.OpenURL(row.Link);
        }
    }

    /// <summary>
    /// Send the same file again.
    /// </summary>
    /// <remarks>
    /// A fresh upload of the file on disk rather than a restart of the old task: a
    /// <see cref="WorkerTask"/> runs once and holds a stream that a failure has already
    /// closed. The button is only offered when that file is still there, which
    /// <see cref="UploadHistoryViewModel.CanRetry"/> answers.
    /// </remarks>
    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UploadHistoryViewModel row } && row.CanRetry)
        {
            UploadManager.UploadFile(row.RetryPath);
        }
    }
}
