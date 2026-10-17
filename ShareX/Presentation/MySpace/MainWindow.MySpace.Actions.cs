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

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// What the library tab can actually DO: the three context menus and the work behind them.
/// </summary>
/// <remarks>
/// Split from the tab's pointer handling because the two are different kinds of code and
/// were beginning to hide each other. This half is the answer to the owner's complaint
/// that right-clicking anything in this app offered three items while the web panel
/// offered a dozen: the menus below are the panel's menus, in the panel's order, with the
/// panel's wording - and every one of them reaches the same server endpoint the panel's
/// own button reaches.
///
/// Everything here runs on the UI thread and awaits; nothing blocks it. A failure is said
/// once, in the corner, rather than in a message box per file - twenty modal dialogs in a
/// row is not a report, it is a punishment.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The files a drag started from, while it is in the air.
    /// </summary>
    /// <remarks>
    /// Held here rather than carried inside the drag payload, and that is the safer of the
    /// two. A platform drag round trip is free to drop a format it does not recognise, so
    /// a drop handler that read the selection back out of the transfer would silently do
    /// nothing on the one platform that lost it. This field can only be non-null while
    /// THIS window is the source of a drag that is currently happening, which is exactly
    /// the fact the drop targets need.
    /// </remarks>
    private List<CloudFileViewModel>? _cloudDrag;

    /* ---------------------------------------------------------------------- */
    /* Menus                                                                  */
    /* ---------------------------------------------------------------------- */

    /// <summary>A right-click on the empty part of the grid.</summary>
    private IReadOnlyList<MainMenuEntry> BuildCloudBackgroundMenu()
    {
        List<MainMenuEntry> entries = new();

        if (MySpace.CanUpload)
        {
            entries.Add(Item(Strings.MySpace_UploadHere, LucideIcons.upload,
                () => PickAndUpload(MySpace.UploadFolderId)));

            // Named for where it would land, like the panel's own background menu: inside
            // a folder the thing you are making is a subfolder, and calling it "New
            // folder" there leaves the reader to guess where it goes.
            entries.Add(Item(
                MySpace.CanGoUp ? Strings.MySpace_NewSubfolder : Strings.MySpace_NewFolder,
                LucideIcons.folder_plus,
                () => NewFolderAsync(MySpace.UploadFolderId)));
        }

        if (MySpace.HasFiles)
        {
            entries.Add(MainMenuEntry.Separator());
            entries.Add(Item(Strings.MySpace_SelectAll, LucideIcons.check, MySpace.SelectAllFiles));
        }

        entries.Add(MainMenuEntry.Separator());
        entries.Add(Item(Strings.MySpace_Refresh, LucideIcons.refresh_cw, MySpace.Reload));

        return entries;
    }

    /// <summary>
    /// A right-click on a folder, on the shelf or in the library panel.
    /// </summary>
    /// <remarks>
    /// The loose-files shelf gets almost none of it, and that is not an oversight: it is
    /// not a folder. It cannot be renamed, shared, deleted or nested into, because there
    /// is no row behind it - it is the server's way of saying "everything filed nowhere".
    /// </remarks>
    private IReadOnlyList<MainMenuEntry> BuildFolderMenu(CloudFolderViewModel folder)
    {
        List<MainMenuEntry> entries = new()
        {
            Item(Strings.MySpace_OpenFolder, LucideIcons.folder_open, () => MySpace.OpenFolder(folder))
        };

        if (!folder.IsRealFolder)
        {
            return entries;
        }

        entries.Add(Item(Strings.MySpace_UploadToFolder, LucideIcons.upload, () => PickAndUpload(folder.Id)));
        entries.Add(MainMenuEntry.Separator());

        entries.Add(Item(Strings.MySpace_NewSubfolder, LucideIcons.folder_plus, () => NewFolderAsync(folder.Id)));
        entries.Add(Item(Strings.MySpace_RenameFolder, LucideIcons.folder_pen, () => RenameFolderAsync(folder)));

        // A tick rather than the panel's "Share publicly..." dialog. The dialog exists in
        // the browser because it also has to hand over a link to copy; here the two
        // entries below do that, and a menu that opens a window to flip one switch is a
        // window nobody wanted.
        entries.Add(new MainMenuEntry(Strings.MySpace_FolderPublic, LucideIcons.globe,
            () => SetFolderPublicAsync(folder, !folder.IsPublic),
            isChecked: folder.IsPublic, toggleType: MainMenuToggleType.CheckBox));

        if (folder.IsPublic && folder.Slug.Length > 0 && AccountTag().Length > 0)
        {
            entries.Add(Item(Strings.MySpace_OpenGallery, LucideIcons.external_link,
                () => OpenOnServer(GalleryPath(folder))));
            entries.Add(Item(Strings.MySpace_CopyGalleryLink, LucideIcons.link, () => CopyGalleryLink(folder)));
        }

        entries.Add(MainMenuEntry.Separator());
        entries.Add(Item(Strings.MySpace_ShowInPanel, LucideIcons.external_link,
            () => OpenOnServer("/dashboard?folder=" + Uri.EscapeDataString(folder.Id))));

        entries.Add(MainMenuEntry.Separator());
        entries.Add(Item(Strings.MySpace_DeleteFolder, LucideIcons.trash_2, () => DeleteFolderAsync(folder)));

        return entries;
    }

    /// <summary>
    /// What a right-click on a file card offers, for one card or for a selection.
    /// </summary>
    /// <remarks>
    /// The menu speaks about the SELECTION when there is more than one card in it and the
    /// card under the pointer is one of them - which the press handler has just made
    /// certain of, by replacing the selection when it was not. Anything else would let a
    /// menu quietly act on files that are not on screen.
    ///
    /// Five of the entries stay single-file even inside a selection: details, download as,
    /// a custom address, the link settings and the visibility switch. That is the panel's
    /// own division and it is the right one - a custom address is unique by definition, and
    /// "download as" has to know what kind of file it is looking at.
    /// </remarks>
    private IReadOnlyList<MainMenuEntry> BuildCloudFileMenu(CloudFileViewModel file)
    {
        List<CloudFileViewModel> selection = MySpace.SelectedFiles();
        bool many = selection.Count > 1 && file.IsSelected;
        List<CloudFileViewModel> targets = many ? selection : new List<CloudFileViewModel> { file };

        return MySpace.IsTrash ? TrashMenu(targets, many) : LibraryMenu(file, targets, many);
    }

    private IReadOnlyList<MainMenuEntry> LibraryMenu(CloudFileViewModel file,
        List<CloudFileViewModel> targets, bool many)
    {
        List<MainMenuEntry> entries = new();

        if (many)
        {
            // A disabled line saying how many, exactly as the panel puts a count at the
            // head of its own multi-select menu. Without it a menu full of plurals is the
            // only clue that this is about more than the card under the pointer.
            entries.Add(new MainMenuEntry(string.Format(Strings.MySpace_SelectionCount, targets.Count),
                LucideIcons.check_check, isEnabled: false));
            entries.Add(MainMenuEntry.Separator());
        }
        else
        {
            entries.Add(Item(Strings.MySpace_Details, LucideIcons.info, () => ShowDetailsAsync(file)));
            entries.Add(MainMenuEntry.Separator());
        }

        int copyable = targets.Count(x => x.HasLink);

        entries.Add(Item(
            many ? string.Format(Strings.MySpace_CopyLinkMany, copyable) : Strings.MySpace_CopyLink,
            LucideIcons.link, () => CopyCloudLinks(targets), copyable > 0));

        entries.Add(Submenu(Strings.MySpace_CopyAs, LucideIcons.copy, () => BuildCopyAsMenu(targets), copyable > 0));

        entries.Add(MainMenuEntry.Separator());

        if (!many)
        {
            entries.Add(Item(Strings.MySpace_OpenInBrowser, LucideIcons.external_link,
                () => URLHelpers.OpenURL(file.OpenLink), file.CanOpen));
            entries.Add(Item(Strings.MySpace_Download, LucideIcons.download, () => DownloadAsync(file), file.HasMediaBase));

            IReadOnlyList<ModlightDownloadOption> formats =
                ModlightDownloadMenu.For(file.Row.Kind, file.Row.MimeType, file.Row.Ext);

            entries.Add(Submenu(Strings.MySpace_DownloadAs, LucideIcons.file_down,
                () => BuildDownloadAsMenu(file, formats), formats.Count > 0 && file.HasMediaBase));

            entries.Add(MainMenuEntry.Separator());
        }

        entries.Add(Submenu(
            many ? string.Format(Strings.MySpace_MoveManyToFolder, targets.Count) : Strings.MySpace_MoveToFolder,
            LucideIcons.folder_input, () => BuildMoveToFolderMenu(targets)));

        if (many)
        {
            // Both directions, because a selection is a mixture: the panel puts Publish
            // and Unpublish side by side in its bulk bar for the same reason.
            entries.Add(Item(string.Format(Strings.MySpace_MakePublicMany, targets.Count), LucideIcons.globe,
                () => SetPublishedAsync(targets, true)));
            entries.Add(Item(string.Format(Strings.MySpace_MakePrivateMany, targets.Count), LucideIcons.globe_lock,
                () => SetPublishedAsync(targets, false)));
        }
        else
        {
            entries.Add(Item(
                file.Published ? Strings.MySpace_MakePrivate : Strings.MySpace_MakePublic,
                file.Published ? LucideIcons.globe_lock : LucideIcons.globe,
                () => SetPublishedAsync(targets, !file.Published)));

            entries.Add(Item(Strings.MySpace_CustomUrl, LucideIcons.square_pen, () => SetCustomUrlAsync(file)));
            entries.Add(Item(Strings.MySpace_LinkSettingsMenu, LucideIcons.settings_2, () => EditLinkSettingsAsync(file)));
        }

        entries.Add(MainMenuEntry.Separator());

        int deletable = targets.Count(x => x.CanTrash);

        entries.Add(Item(
            many ? string.Format(Strings.MySpace_DeleteMany, deletable) : Strings.MySpace_Delete,
            LucideIcons.trash_2, () => TrashFilesAsync(targets), deletable > 0));

        return entries;
    }

    /// <summary>
    /// The trash offers two things, and neither of them is a link.
    /// </summary>
    /// <remarks>
    /// The server withholds every URL for a soft-deleted row because all of them would
    /// 404, so Copy link, Open and Download have nothing to act on and are absent rather
    /// than present and dead. The panel made the same correction: its trash used to render
    /// ordinary file cards, links and all.
    /// </remarks>
    private IReadOnlyList<MainMenuEntry> TrashMenu(List<CloudFileViewModel> targets, bool many)
    {
        List<MainMenuEntry> entries = new();

        if (many)
        {
            entries.Add(new MainMenuEntry(string.Format(Strings.MySpace_SelectionCount, targets.Count),
                LucideIcons.check_check, isEnabled: false));
            entries.Add(MainMenuEntry.Separator());
        }
        else
        {
            entries.Add(Item(Strings.MySpace_Details, LucideIcons.info, () => ShowDetailsAsync(targets[0])));
            entries.Add(MainMenuEntry.Separator());
        }

        entries.Add(Item(
            many ? string.Format(Strings.MySpace_RestoreMany, targets.Count) : Strings.MySpace_Restore,
            LucideIcons.undo_2, () => RestoreFilesAsync(targets)));

        entries.Add(MainMenuEntry.Separator());

        entries.Add(Item(
            many ? string.Format(Strings.MySpace_DeleteForeverMany, targets.Count) : Strings.MySpace_DeleteForever,
            LucideIcons.trash_2, () => DeleteForeverAsync(targets)));

        return entries;
    }

    /// <summary>The five shapes of link, in the panel's order.</summary>
    private IReadOnlyList<MainMenuEntry> BuildCopyAsMenu(IReadOnlyList<CloudFileViewModel> files) => new[]
    {
        CopyAsItem(files, CloudLinkFormat.Direct, Strings.MySpace_LinkFormatDirect),
        CopyAsItem(files, CloudLinkFormat.Raw, Strings.MySpace_LinkFormatRaw),
        CopyAsItem(files, CloudLinkFormat.Markdown, Strings.MySpace_LinkFormatMarkdown),
        CopyAsItem(files, CloudLinkFormat.BBCode, Strings.MySpace_LinkFormatBBCode),
        CopyAsItem(files, CloudLinkFormat.Html, Strings.MySpace_LinkFormatHtml)
    };

    private MainMenuEntry CopyAsItem(IReadOnlyList<CloudFileViewModel> files, CloudLinkFormat format, string label) =>
        Item(label, LucideIcons.copy, () => CopyAs(files, format));

    /// <summary>
    /// Every format this file can become, with what it costs the file said underneath.
    /// </summary>
    /// <remarks>
    /// The entries that throw the motion away sit under a heading of their own, below
    /// everything that keeps it. Taking a still out of a GIF is a real thing to want; being
    /// handed one when you asked for the animation is not.
    /// </remarks>
    private IReadOnlyList<MainMenuEntry> BuildDownloadAsMenu(CloudFileViewModel file,
        IReadOnlyList<ModlightDownloadOption> formats)
    {
        List<MainMenuEntry> entries = new();
        bool headed = false;

        foreach (ModlightDownloadOption option in formats)
        {
            if (option.LosesMotion && !headed)
            {
                headed = true;
                entries.Add(MainMenuEntry.Separator());
                entries.Add(new MainMenuEntry(Strings.MySpace_ConvertFrameSection, "", isEnabled: false));
            }

            string hint = ModlightConvertText.Hint(option.Hint);
            string header = hint.Length > 0
                ? option.To.ToUpperInvariant() + "   -   " + hint
                : option.To.ToUpperInvariant();

            string to = option.To;
            entries.Add(Item(header, LucideIcons.file_down, () => DownloadAsAsync(file, to)));
        }

        return entries;
    }

    /// <summary>
    /// Where these files could go: the loose shelf, then every folder, indented.
    /// </summary>
    /// <remarks>
    /// A submenu rather than the panel's search dialog. Seventeen folders fit in a menu,
    /// and a dialog to pick one of seventeen is a dialog too many; the indentation carries
    /// the tree, exactly as the library panel's own list does. The folder the files are
    /// already in is drawn ticked and does nothing, which is cheaper than working out
    /// whether to hide it - a mixed selection has no single answer to "where is it now".
    /// </remarks>
    private IReadOnlyList<MainMenuEntry> BuildMoveToFolderMenu(IReadOnlyList<CloudFileViewModel> files)
    {
        string? shared = SharedFolderOf(files);

        List<MainMenuEntry> entries = new()
        {
            new MainMenuEntry(Strings.MySpace_Unfiled, LucideIcons.inbox,
                () => MoveFilesAsync(files, null),
                isChecked: shared == null, toggleType: MainMenuToggleType.CheckBox),
            MainMenuEntry.Separator()
        };

        foreach (CloudFolderViewModel folder in MySpace.Folders)
        {
            if (!folder.IsRealFolder)
            {
                continue;
            }

            string id = folder.Id;

            entries.Add(new MainMenuEntry(new string(' ', folder.Depth * 3) + folder.Name, LucideIcons.folder,
                () => MoveFilesAsync(files, id),
                isChecked: shared == id, toggleType: MainMenuToggleType.CheckBox));
        }

        entries.Add(MainMenuEntry.Separator());
        entries.Add(Item(Strings.MySpace_NewFolder, LucideIcons.folder_plus, () => MoveToNewFolderAsync(files)));

        return entries;
    }

    /// <summary>The folder all of these are in, or null when they disagree or are loose.</summary>
    private static string? SharedFolderOf(IReadOnlyList<CloudFileViewModel> files)
    {
        string? first = files.Count > 0 ? files[0].FolderId : null;

        foreach (CloudFileViewModel file in files)
        {
            if (file.FolderId != first)
            {
                return null;
            }
        }

        return first;
    }

    /* ---------------------------------------------------------------------- */
    /* Clipboard                                                              */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Every selected file's link, one per line.
    /// </summary>
    /// <remarks>
    /// Newline-separated because that is what every field, chat box and text editor a
    /// person would paste a list of links into treats as a list. Comma-separating them
    /// would produce one string nothing can use.
    /// </remarks>
    private void CopyCloudLinks(IReadOnlyList<CloudFileViewModel> files)
    {
        string text = string.Join(Environment.NewLine, files.Where(x => x.HasLink).Select(x => x.CopyLink));

        if (text.Length > 0)
        {
            ClipboardHelpers.CopyText(text);
        }
    }

    private void CopyAs(IReadOnlyList<CloudFileViewModel> files, CloudLinkFormat format)
    {
        string text = string.Join(Environment.NewLine,
            files.Where(x => x.HasLink).Select(x => x.LinkFormat(format)).Where(x => x.Length > 0));

        if (text.Length > 0)
        {
            ClipboardHelpers.CopyText(text);
        }
    }

    private void CopyGalleryLink(CloudFolderViewModel folder)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null || string.IsNullOrEmpty(account.Host))
        {
            SignInWindowIntegration.Show();
            return;
        }

        ClipboardHelpers.CopyText(account.Host.TrimEnd('/') + GalleryPath(folder));
    }

    private static string GalleryPath(CloudFolderViewModel folder) =>
        "/a/" + Uri.EscapeDataString(AccountTag()) + "/" + Uri.EscapeDataString(folder.Slug);

    /* ---------------------------------------------------------------------- */
    /* Files                                                                  */
    /* ---------------------------------------------------------------------- */

    private async Task MoveFilesAsync(IReadOnlyList<CloudFileViewModel> files, string? folderId)
    {
        List<CloudFileViewModel> targets = files.Where(x => x.FolderId != folderId).ToList();

        if (targets.Count == 0)
        {
            return;
        }

        await EditAsync(targets, new ModlightUploadEdit().MoveTo(folderId), rows =>
        {
            MySpace.NoteFilesMoved(targets, folderId, rows);

            string where = folderId == null ? Strings.MySpace_Unfiled : MySpace.FolderNameFor(folderId);
            Notice(LucideIcons.folder_input, string.Format(Strings.MySpace_MovedToast, targets.Count, where));
        });
    }

    /// <summary>Make a folder and move the files into it in one gesture.</summary>
    private async Task MoveToNewFolderAsync(IReadOnlyList<CloudFileViewModel> files)
    {
        string? id = await CreateFolderAsync(MySpace.UploadFolderId);

        if (id != null)
        {
            await MoveFilesAsync(files, id);
        }
    }

    private async Task SetPublishedAsync(IReadOnlyList<CloudFileViewModel> files, bool published)
    {
        await EditAsync(files, new ModlightUploadEdit().SetPublished(published), rows =>
        {
            MySpace.ApplyRows(rows);
            Notice(published ? LucideIcons.globe : LucideIcons.globe_lock,
                string.Format(published ? Strings.MySpace_PublishedToast : Strings.MySpace_HiddenToast, files.Count));
        });
    }

    private async Task SetCustomUrlAsync(CloudFileViewModel file)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        // The part of the address the person does not choose, shown beside the box so the
        // thing they are typing is obviously the tail of a URL and not a file name.
        string prefix = account.Host.TrimEnd('/') + "/" + AccountTag() + "/";

        string? slug = await ModlightPromptWindow.AskAsync(this, Strings.MySpace_CustomUrlTitle,
            Strings.MySpace_CustomUrlDescription, Strings.MySpace_CustomUrlLabel, Strings.MySpace_Save,
            file.Code, prefix, maxLength: 64);

        if (slug == null || slug == file.Code)
        {
            return;
        }

        List<CloudFileViewModel> one = new() { file };

        await EditAsync(one, new ModlightUploadEdit().SetCode(slug), rows =>
        {
            MySpace.ApplyRows(rows);
            Notice(LucideIcons.square_pen, string.Format(Strings.MySpace_CustomUrlToast, file.Code));
        });
    }

    private async Task EditLinkSettingsAsync(CloudFileViewModel file)
    {
        ModlightUploadEdit? edit = await ModlightLinkSettingsWindow.AskAsync(this, file);

        if (edit == null)
        {
            return;
        }

        List<CloudFileViewModel> one = new() { file };

        await EditAsync(one, edit, rows =>
        {
            MySpace.ApplyRows(rows);
            Notice(LucideIcons.settings_2, Strings.MySpace_LinkSettingsToast);
        });
    }

    /// <summary>
    /// One PATCH, one place that reports what happened to it.
    /// </summary>
    /// <remarks>
    /// The server answers with the affected rows in the listing's own shape, so
    /// <paramref name="onDone"/> can redraw the cards from them rather than reloading the
    /// folder - which would empty the grid, drop every decoded thumbnail and scroll back
    /// to the top after every small edit.
    /// </remarks>
    private async Task EditAsync(IReadOnlyList<CloudFileViewModel> files, ModlightUploadEdit edit,
        Action<IReadOnlyList<ModlightUpload>?> onDone)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        List<string> ids = files.Where(x => x.CanTrash).Select(x => x.Id).ToList();

        if (ids.Count == 0 || edit.IsEmpty)
        {
            return;
        }

        try
        {
            ModlightUploadPatchResult result = await ModlightLibrary.EditUploadsAsync(
                account.Host, account.Token, ids, edit, CancellationToken.None);

            onDone(result.Items);
        }
        catch (Exception exception)
        {
            ReportFailure(exception, account.Host);
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Deleting and restoring                                                 */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Move files to the server's thirty-day trash, having said which.
    /// </summary>
    /// <remarks>
    /// It used to be a permanent delete over <c>POST /{tag}/{code}/delete/{token}</c>,
    /// because that was the only deletion route an API token could reach - and it took the
    /// token in the URL PATH, which wrote the account's credential into the reverse proxy's
    /// access log on every use. <c>DELETE /api/uploads</c> takes it in a header and
    /// defaults to the trash, so the safer route is also the one that stops leaking.
    ///
    /// The confirmation still defaults to No even though this can be undone: a link that
    /// stops working is worth a deliberate answer.
    /// </remarks>
    private Task TrashFilesAsync(IReadOnlyList<CloudFileViewModel> files) =>
        RemoveAsync(files, "trash",
            files.Count == 1
                ? string.Format(Strings.MySpace_DeleteConfirm, files[0].Name)
                : string.Format(Strings.MySpace_DeleteManyConfirm, files.Count),
            Strings.MySpace_DeleteTitle);

    /// <summary>
    /// Delete for good. Confirmed in its own words, because this one cannot be undone.
    /// </summary>
    /// <remarks>
    /// Reachable only from inside the trash, where every file is already one deliberate
    /// step away from the library. The confirmation says "cannot be undone" rather than
    /// reusing the trash wording, which promises the opposite.
    /// </remarks>
    private Task DeleteForeverAsync(IReadOnlyList<CloudFileViewModel> files) =>
        RemoveAsync(files, "permanent",
            files.Count == 1
                ? string.Format(Strings.MySpace_DeleteForeverConfirm, files[0].Name)
                : string.Format(Strings.MySpace_DeleteForeverManyConfirm, files.Count),
            Strings.MySpace_DeleteForeverTitle);

    private async Task RemoveAsync(IReadOnlyList<CloudFileViewModel> files, string mode, string question, string title)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        List<CloudFileViewModel> targets = files.Where(x => x.CanTrash).ToList();

        if (targets.Count == 0)
        {
            return;
        }

        if (!Confirm(question, title))
        {
            return;
        }

        try
        {
            ModlightDeleteResult result = await ModlightLibrary.DeleteUploadsAsync(
                account.Host, account.Token, targets.Select(x => x.Id).ToList(), mode, CancellationToken.None);

            if (result.Affected == 0)
            {
                // The server answers the same "nothing affected" for "already gone" and
                // "not yours", deliberately, so this cannot be used to find out which. The
                // message can only say that nothing happened.
                Notice(LucideIcons.triangle_alert, Strings.MySpace_DeleteNothingHappened, failed: true);
                return;
            }

            foreach (CloudFileViewModel file in targets)
            {
                MySpace.NoteFileRemoved(file);
            }

            // A trashed file stops counting against the quota, and the figure in the strip
            // is the one place that would otherwise keep the old number.
            Account.Refresh();

            Notice(LucideIcons.trash_2, string.Format(
                mode == "permanent" ? Strings.MySpace_DeletedForeverToast : Strings.MySpace_TrashedToast,
                result.Affected));
        }
        catch (Exception exception)
        {
            ReportFailure(exception, account.Host);
        }
    }

    /// <summary>Take files back out of the trash. Never asks - putting something back is not destructive.</summary>
    private async Task RestoreFilesAsync(IReadOnlyList<CloudFileViewModel> files)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        List<CloudFileViewModel> targets = files.Where(x => x.CanTrash).ToList();

        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            ModlightDeleteResult result = await ModlightLibrary.DeleteUploadsAsync(
                account.Host, account.Token, targets.Select(x => x.Id).ToList(), "restore", CancellationToken.None);

            if (result.Affected == 0)
            {
                Notice(LucideIcons.triangle_alert, Strings.MySpace_DeleteNothingHappened, failed: true);
                return;
            }

            // Off the screen rather than redrawn: this list is the trash, and a restored
            // file is no longer in it. The folder it is going back to gets its count back
            // at the same moment, rather than waiting for a reload nobody asked for.
            foreach (CloudFileViewModel file in targets)
            {
                MySpace.NoteFileRestored(file);
            }

            Account.Refresh();
            Notice(LucideIcons.undo_2, string.Format(Strings.MySpace_RestoredToast, result.Affected));
        }
        catch (Exception exception)
        {
            ReportFailure(exception, account.Host);
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Downloading                                                            */
    /* ---------------------------------------------------------------------- */

    private Task DownloadAsync(CloudFileViewModel file) =>
        SaveAsync(file, file.DownloadLink, file.Name, checkFirst: false);

    /// <summary>
    /// Ask the server for the file in another format, and save what comes back.
    /// </summary>
    /// <remarks>
    /// The dry run happens FIRST, and that is the whole reason this is not a link. A
    /// refusal arrives as a header on a response the client has already committed to
    /// treating as a download, so a video with no sound track answers "Failed" with no
    /// reason - which is exactly the complaint that made the web panel grow the same dry
    /// run. Here it costs one request that reads no bytes.
    /// </remarks>
    private async Task DownloadAsAsync(CloudFileViewModel file, string to)
    {
        string name = Path.GetFileNameWithoutExtension(file.Name);

        if (name.Length == 0)
        {
            name = file.Code;
        }

        await SaveAsync(file, file.ConvertLink(to), name + "." + to, checkFirst: true);
    }

    private async Task SaveAsync(CloudFileViewModel file, string link, string suggestedName, bool checkFirst)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        if (link.Length == 0)
        {
            return;
        }

        if (checkFirst)
        {
            ModlightConvertVerdict verdict = await ModlightLibrary.CheckConversionAsync(
                account.Host, account.Token, link, CancellationToken.None);

            if (!verdict.Ok)
            {
                Notice(LucideIcons.triangle_alert,
                    string.Format(Strings.MySpace_ConvertRefusedTitle, Path.GetExtension(suggestedName).TrimStart('.').ToUpperInvariant()),
                    ModlightConvertText.Refusal(verdict.Reason), failed: true);
                return;
            }
        }

        // Asked AFTER the dry run, so a refusal never gets as far as a save dialog. The
        // picker is the platform's, so the "last folder used" memory is the one the rest
        // of Windows has.
        IStorageFile? destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.MySpace_Download,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            ShowOverwritePrompt = true
        });

        string? path = destination?.TryGetLocalPath();

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Notice(LucideIcons.download, string.Format(Strings.MySpace_DownloadStarted, file.Name));

        try
        {
            await ModlightLibrary.DownloadToFileAsync(account.Host, account.Token, link, path!, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // A part-written file with a plausible name is worse than no file, so it goes.
            try
            {
                File.Delete(path!);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            ReportFailure(exception, account.Host);
            return;
        }

        Notice(LucideIcons.circle_check, string.Format(Strings.MySpace_DownloadSaved, Path.GetFileName(path!)));
    }

    /* ---------------------------------------------------------------------- */
    /* Folders                                                                */
    /* ---------------------------------------------------------------------- */

    private Task NewFolderAsync(string? parentId) => CreateFolderAsync(parentId);

    /// <summary>Ask for a name, make the folder, and answer with its id.</summary>
    private async Task<string?> CreateFolderAsync(string? parentId)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return null;
        }

        string parentName = parentId == null ? "" : MySpace.FolderNameFor(parentId);
        bool nested = parentName.Length > 0;

        string? name = await ModlightPromptWindow.AskAsync(this,
            nested ? Strings.MySpace_NewSubfolder : Strings.MySpace_NewFolder,
            nested ? string.Format(Strings.MySpace_NewFolderInside, parentName) : Strings.MySpace_NewFolderAtRoot,
            Strings.MySpace_FolderNameLabel, Strings.MySpace_Create);

        if (name == null)
        {
            return null;
        }

        try
        {
            ModlightFolder? folder = await ModlightLibrary.CreateFolderAsync(
                account.Host, account.Token, name, parentId, CancellationToken.None);

            MySpace.RefreshFolders();
            Notice(LucideIcons.folder_plus, string.Format(Strings.MySpace_FolderCreatedToast, name));
            return folder?.Id;
        }
        catch (Exception exception)
        {
            ReportFailure(exception, account.Host);
            return null;
        }
    }

    private async Task RenameFolderAsync(CloudFolderViewModel folder)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        string? name = await ModlightPromptWindow.AskAsync(this, Strings.MySpace_RenameFolder,
            Strings.MySpace_RenameFolderDescription, Strings.MySpace_FolderNameLabel, Strings.MySpace_Save,
            folder.Name);

        if (name == null || name == folder.Name)
        {
            return;
        }

        try
        {
            await ModlightLibrary.RenameFolderAsync(account.Host, account.Token, folder.Id, name, CancellationToken.None);
            MySpace.RefreshFolders();
            Notice(LucideIcons.folder_pen, string.Format(Strings.MySpace_FolderRenamedToast, name));
        }
        catch (Exception exception)
        {
            ReportFailure(exception, account.Host);
        }
    }

    private async Task SetFolderPublicAsync(CloudFolderViewModel folder, bool isPublic)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        try
        {
            await ModlightLibrary.SetFolderVisibilityAsync(account.Host, account.Token, folder.Id, isPublic,
                CancellationToken.None);

            MySpace.RefreshFolders();

            Notice(isPublic ? LucideIcons.globe : LucideIcons.globe_lock,
                isPublic ? Strings.MySpace_FolderMadePublic : Strings.MySpace_FolderMadePrivate);
        }
        catch (Exception exception)
        {
            ReportFailure(exception, account.Host);
        }
    }

    /// <summary>
    /// Delete a folder, having asked what should happen to what is inside it.
    /// </summary>
    /// <remarks>
    /// The dialog is the safeguard, not this method: it offers two labelled paths rather
    /// than one button with a tick beside it, and the destructive path cannot be pressed
    /// for the first five seconds. See <see cref="ModlightFolderDeleteWindow"/>.
    /// </remarks>
    private async Task DeleteFolderAsync(CloudFolderViewModel folder)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        FolderDeleteChoice choice = await ModlightFolderDeleteWindow.AskAsync(this, folder.Name,
            folder.FileCount, folder.DescendantCount);

        if (choice == FolderDeleteChoice.Cancel)
        {
            return;
        }

        bool withContents = choice == FolderDeleteChoice.WithContents;

        try
        {
            ModlightFolderDeleteResult result = await ModlightLibrary.DeleteFolderAsync(
                account.Host, account.Token, folder.Id, withContents, CancellationToken.None);

            // The folder that was open may be the one that has just gone, and the shelf
            // sends the view back to the top when it cannot find the id it is holding.
            MySpace.RefreshFolders();

            if (MySpace.UploadFolderId == folder.Id)
            {
                MySpace.NavigateTo(null);
            }

            Account.Refresh();

            Notice(LucideIcons.trash_2, result.Trashed > 0
                ? string.Format(Strings.MySpace_FolderDeletedWithContents, result.Trashed)
                : Strings.MySpace_FolderDeleted);
        }
        catch (Exception exception)
        {
            ReportFailure(exception, account.Host);
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Saying what happened                                                   */
    /* ---------------------------------------------------------------------- */

    /// <summary>A yes/no question, defaulting to No.</summary>
    private bool Confirm(string question, string title) =>
        AvaloniaUI.MessageBox.Show(this, question, Program.Title + " - " + title,
            AvaloniaUI.MessageBoxButtons.YesNo, AvaloniaUI.MessageBoxIcon.Warning,
            AvaloniaUI.MessageBoxDefaultButton.Button2) == AvaloniaUI.DialogResult.Yes;

    /// <summary>One line in the corner, which leaves on its own.</summary>
    private void Notice(string icon, string title, string detail = "", bool failed = false) =>
        ShowToast(UploadToastViewModel.Notice(icon, title, detail, failed));

    /// <summary>
    /// Turn whatever went wrong into one sentence somebody can act on.
    /// </summary>
    /// <remarks>
    /// Three failures with three different remedies, and telling them apart is the whole
    /// point: a rejected token asks the person to sign in, a refusal from the server is
    /// already a sentence in their own language, and anything else asks them to check the
    /// connection. Reporting all three as "something went wrong" would send somebody to
    /// look at their network because they picked a name that was taken.
    /// </remarks>
    private void ReportFailure(Exception exception, string host)
    {
        if (exception is ModlightUnauthorizedException)
        {
            Notice(LucideIcons.log_in, Strings.MySpace_TokenRejected, failed: true);
            return;
        }

        if (exception is ModlightApiException refused)
        {
            Notice(LucideIcons.triangle_alert, Strings.MySpace_ServerRefused,
                refused.Detail.Length > 0 ? refused.Detail : ModlightConvertText.Refusal(refused.Reason ?? ""),
                failed: true);
            return;
        }

        DebugHelper.WriteException(exception);
        Notice(LucideIcons.cloud_off, string.Format(Strings.ModlightSignIn_Unreachable, host, exception.Message),
            failed: true);
    }
}
