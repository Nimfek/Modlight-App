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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// What a click, a key and the trash's one bulk button mean in the library.
/// </summary>
/// <remarks>
/// A separate file from <see cref="MainWindow"/>'s other library halves on purpose. The
/// pointer file answers "which card is this about", the actions file answers "and what
/// does the server do with it"; this one answers the question in between - what the
/// GESTURE meant - and it is the half the owner asked to change:
///
///   * a click opens the file's DETAILS, and only a double click opens the link,
///   * Delete acts on the selection instead of forcing a right-click per file,
///   * the trash can be emptied in one go instead of one file at a time.
///
/// The selection gestures themselves are untouched and stay Windows': right-click opens
/// the menu without toggling anything, Ctrl+click toggles, Shift+click takes a range,
/// Ctrl+A takes everything on screen, Escape clears. The web panel's right-button
/// multi-select is deliberately not copied here.
/// </remarks>
public partial class MainWindow
{
    /* ---------------------------------------------------------------------- */
    /* A click opens details, a double click opens the link                   */
    /* ---------------------------------------------------------------------- */

    /// <summary>The details window a click has asked for, while a double click is still possible.</summary>
    private IDisposable? _pendingDetails;

    /// <summary>Which modifiers were held when the pointer last went down on a card.</summary>
    /// <remarks>
    /// Read from the press rather than from the tap because the two gestures are the same
    /// gesture: Ctrl+click and Shift+click also raise Tapped, and they mean "change the
    /// selection", never "show me this one". Recorded by the press handler in
    /// MainWindow.MySpace.cs through <see cref="NoteCardPress"/>, which is one line there
    /// rather than a second press handler here - a routed event may be wired from XAML
    /// once.
    /// </remarks>
    private KeyModifiers _cardPressModifiers;

    /// <summary>Remember what the pointer was holding, and drop any details a previous click asked for.</summary>
    internal void NoteCardPress(KeyModifiers modifiers)
    {
        _cardPressModifiers = modifiers;

        // A new press means the last click's answer is no longer wanted. Without this,
        // clicking three cards in quick succession would stack three windows.
        CancelPendingDetails();
    }

    /// <summary>The double click won: whatever the single click was about to do, it does not.</summary>
    internal void CancelPendingDetails()
    {
        _pendingDetails?.Dispose();
        _pendingDetails = null;
    }

    /// <summary>
    /// One click on a card: show what this file is.
    /// </summary>
    /// <remarks>
    /// Delayed by the system's own double-click time, and that delay is not a hedge - it
    /// is the only way both gestures can exist. Every double click begins as a single
    /// one, so acting on the first release would put a window on screen that swallows the
    /// second press, and the double click would never arrive at all.
    ///
    /// The other half of the same trap is already paid for in this repo: marking the
    /// PRESS handled on a tile eats the double click outright. So nothing here marks
    /// anything handled, and the press handler in MainWindow.MySpace.cs still deliberately
    /// leaves the left button alone.
    ///
    /// The delay is only ever paid ONCE, on the click that brings the drawer out. After
    /// that it is already on screen and follows the selection by itself, so the answer to
    /// every further click is instant - and it has to be, because there is no layout to
    /// redo either. That asymmetry is the reason the delay is bearable at all.
    ///
    /// Details cost no request - every figure in that drawer comes from the row the grid
    /// was already drawn from - so a click cannot spend a view or burn a one-time link,
    /// which is what makes it safe to put on the cheapest gesture there is.
    /// </remarks>
    private void OnCloudCardTapped(object? sender, TappedEventArgs e)
    {
        CancelPendingDetails();

        if (sender is not Control { DataContext: CloudFileViewModel file })
        {
            return;
        }

        // Selection gestures, not "show me this". Ctrl+click on a lone card would
        // otherwise open details as a side effect of adding it to a selection.
        if (_cardPressModifiers.HasFlag(KeyModifiers.Control) || _cardPressModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        // Already out: the press just changed the selection and the drawer listens for
        // exactly that, so it has retargeted itself. Waiting here would only make a click
        // on the second card slower than the click that opened the first.
        if (_cloudDrag != null || Details.IsPresent)
        {
            return;
        }

        _pendingDetails = DispatcherTimer.RunOnce(() =>
        {
            _pendingDetails = null;

            // The world may have moved during the wait: a drag may have started from this
            // very press, or the card may have been deleted, restored or paged away.
            if (_cloudDrag != null || Details.IsPresent || !MySpace.Files.Contains(file))
            {
                return;
            }

            _ = ShowDetailsAsync(file);
        }, DoubleClickWindow());
    }

    /// <summary>How long a second click may still arrive, according to the platform.</summary>
    /// <remarks>
    /// Asked of the system rather than hard-coded, because this is the user's own setting
    /// and a fixed 500 ms would be wrong for anybody who has changed it - too slow to feel
    /// responsive for one person, too fast to double click for another.
    /// </remarks>
    private static TimeSpan DoubleClickWindow()
    {
        TimeSpan platform = Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse)
            ?? TimeSpan.Zero;

        return platform > TimeSpan.Zero ? platform : TimeSpan.FromMilliseconds(500);
    }

    /* ---------------------------------------------------------------------- */
    /* The Delete key                                                         */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Delete acts on what is selected. Returns whether the library took the key.
    /// </summary>
    /// <remarks>
    /// The owner's complaint was that deleting anything meant a right-click per file,
    /// which is unbearable with a screenful selected. So: Delete sends the selection to
    /// the server's thirty-day trash, and Shift+Delete deletes for good - both through
    /// the same two methods the context menu calls, so there is one wording, one
    /// confirmation and one place where the request is built.
    ///
    /// Inside the trash there is no lighter reading available: those rows are already
    /// trashed, so plain Delete means the permanent one, exactly as it does in the
    /// Recycle Bin. It still asks first, in the wording that says it cannot be undone.
    ///
    /// The focus guard is the point of the whole method. A shortcut that fires while
    /// somebody is typing turns a Delete meant for one character into a delete of their
    /// files, so the key is only ours when nothing that takes text has the focus - the
    /// search box, a rename field, anything with a TextBox inside it.
    /// </remarks>
    private bool HandleLibraryDeleteKey(KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return false;
        }

        // Ctrl+Delete and Alt+Delete are somebody else's shortcuts, not shorter ways to
        // reach ours.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            return false;
        }

        if (IsTextEntryFocused())
        {
            return false;
        }

        bool forever = MySpace.IsTrash || e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        List<CloudFileViewModel> files = MySpace.SelectedFiles();

        if (files.Count > 0)
        {
            Ask(() => forever ? DeleteForeverAsync(files) : TrashFilesAsync(files));
            return true;
        }

        // A folder tile can be the selection too, and the two are exclusive. Its own
        // dialog asks what should happen to what is inside it, so nothing extra is needed
        // here beyond routing the key.
        CloudFolderViewModel? selected = MySpace.SelectedFolder;

        if (selected != null && !MySpace.IsTrash)
        {
            // Copied into a variable the lambda can hold non-null: a narrowing test does
            // not travel into a closure, and the capture would be nullable again inside.
            CloudFolderViewModel folder = selected;

            Ask(() => DeleteFolderAsync(folder));
            return true;
        }

        // Nothing selected: say so by not taking the key, and let the rest of the window
        // have its turn.
        return false;
    }

    /// <summary>
    /// Run a question-then-act step after this key has finished being delivered.
    /// </summary>
    /// <remarks>
    /// Every one of these puts a modal up, and this application's message box runs its own
    /// dispatcher frame. Opening one from inside a key handler would stack that frame on
    /// top of the one still delivering the key, which is how a dialog ends up receiving
    /// the keystroke that opened it.
    /// </remarks>
    private static void Ask(Func<Task> work) => Dispatcher.UIThread.Post(() => _ = work());

    /// <summary>Whether the keyboard belongs to something being typed into.</summary>
    /// <remarks>
    /// Asked of the FOCUS rather than of the event's source, and walked up the tree from
    /// there. A key pressed inside a composite control - a search box with a clear button,
    /// an AutoCompleteBox, a rename cell - is raised by the inner presenter or by the
    /// wrapper, not reliably by the TextBox itself, so testing one element would let the
    /// shortcut through in exactly the cases it must not.
    /// </remarks>
    private bool IsTextEntryFocused()
    {
        object? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

        for (Visual? node = focused as Visual; node != null; node = node.GetVisualParent())
        {
            if (node is TextBox or AutoCompleteBox)
            {
                return true;
            }
        }

        return false;
    }

    /* ---------------------------------------------------------------------- */
    /* Emptying the trash                                                     */
    /* ---------------------------------------------------------------------- */

    /// <summary>Rows per listing request while counting the trash - the server's own ceiling.</summary>
    /// <remarks>
    /// Asking for more is not an error at the far end: the server clamps it and echoes the
    /// page size it actually used, which is the figure the loop below believes rather than
    /// the one it asked for.
    /// </remarks>
    private const int TrashScanPage = 120;

    /// <summary>Ids per delete request - the server refuses a longer list outright (MAX_IDS).</summary>
    private const int TrashDeleteBatch = 500;

    /// <summary>A stop on the walk, so a runaway listing cannot page for ever.</summary>
    private const int TrashScanLimit = 50000;

    private bool _emptyingTrash;

    /// <summary>False while a trash is being counted or emptied, so the button cannot be pressed twice.</summary>
    public bool CanEmptyTrash => !_emptyingTrash;

    private void OnEmptyTrashClick(object? sender, RoutedEventArgs e) => _ = EmptyTrashAsync();

    /// <summary>
    /// Delete everything in the trash, having counted it first.
    /// </summary>
    /// <remarks>
    /// The counting pass is not politeness. This is irreversible, so the question has to
    /// name what it is about - how many files and how much space - and those two numbers
    /// have to be REAL: "are you sure?" is the sentence people click through. The walk
    /// costs nothing extra either, because the delete endpoint takes ids and the ids can
    /// only come from the same listing.
    ///
    /// The button that starts this deliberately does not live next to Restore. Restore is
    /// in the context menu on the cards; this is in the strip at the top, in the slot the
    /// "upload here" button vacates whenever the trash is on screen. Nothing that undoes a
    /// deletion and the thing that makes it permanent should ever be adjacent.
    ///
    /// The confirmation defaults to No, like every other one here.
    /// </remarks>
    private async Task EmptyTrashAsync()
    {
        if (_emptyingTrash)
        {
            return;
        }

        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        _emptyingTrash = true;
        OnPropertyChanged(nameof(CanEmptyTrash));

        try
        {
            Notice(LucideIcons.trash_2, Strings.MySpace_EmptyTrashCounting);

            List<string> ids = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            long bytes = 0;

            for (int page = 1; ids.Count < TrashScanLimit; page++)
            {
                ModlightUploadPage listing = await ModlightLibrary.GetUploadsAsync(
                    account.Host, account.Token, page, TrashScanPage,
                    null, null, null, true, CancellationToken.None);

                List<ModlightUpload> items = listing.Items ?? new List<ModlightUpload>();

                if (items.Count == 0)
                {
                    break;
                }

                int added = 0;

                foreach (ModlightUpload row in items)
                {
                    if (string.IsNullOrEmpty(row.Id) || !seen.Add(row.Id!))
                    {
                        continue;
                    }

                    ids.Add(row.Id!);
                    bytes += Math.Max(0, row.Size);
                    added++;
                }

                // Two ways this walk can stop making progress, and neither of them is an
                // empty page: the server has a ceiling on the page NUMBER and answers a
                // request beyond it with the clamped page, and rows shift underneath a
                // listing while it is being paged. Either would loop for ever on a count
                // alone, so the loop watches for new ids rather than for new rows.
                if (added == 0)
                {
                    break;
                }

                int perPage = listing.PerPage > 0 ? listing.PerPage : TrashScanPage;

                if (items.Count < perPage)
                {
                    break;
                }
            }

            if (ids.Count == 0)
            {
                Notice(LucideIcons.trash_2, Strings.MySpace_EmptyTrashEmpty);
                return;
            }

            if (!Confirm(
                    string.Format(Strings.MySpace_EmptyTrashConfirm, ids.Count, ModlightFormat.CloudSize(bytes)),
                    Strings.MySpace_EmptyTrashTitle))
            {
                return;
            }

            int affected = 0;

            for (int from = 0; from < ids.Count; from += TrashDeleteBatch)
            {
                List<string> batch = ids.GetRange(from, Math.Min(TrashDeleteBatch, ids.Count - from));

                ModlightDeleteResult result = await ModlightLibrary.DeleteUploadsAsync(
                    account.Host, account.Token, batch, "permanent", CancellationToken.None);

                affected += result.Affected;
            }

            if (affected == 0)
            {
                // Same answer for "already gone" and "not yours", deliberately, so this
                // cannot be used to tell them apart.
                Notice(LucideIcons.triangle_alert, Strings.MySpace_DeleteNothingHappened, failed: true);
                return;
            }

            // A reload rather than taking the cards off one at a time: the whole listing
            // is what just went, and the trash also holds pages this window never loaded.
            MySpace.Reload();
            Account.Refresh();

            Notice(LucideIcons.trash_2,
                string.Format(Strings.MySpace_EmptyTrashDone, affected, ModlightFormat.CloudSize(bytes)));
        }
        catch (Exception exception)
        {
            ReportFailure(exception, account.Host);
        }
        finally
        {
            _emptyingTrash = false;
            OnPropertyChanged(nameof(CanEmptyTrash));
        }
    }
}
