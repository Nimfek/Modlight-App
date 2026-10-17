#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The window's half of the details drawer: who it is, and what it can ask for.
/// </summary>
/// <remarks>
/// A partial of its own rather than more lines in MainWindow.MySpace.Actions.cs. The
/// drawer needs six of that file's private methods and none of its state, and the file
/// is one several people are editing - so the wiring lives beside the control it wires,
/// and the only thing left in the shared file is the single line that opens it.
///
/// Every callback below hands the work straight back to the action the CONTEXT MENU
/// already runs. That is the point of doing it this way: publishing from the drawer and
/// publishing from the menu are one request with one confirmation, one toast and one
/// error path, so the two surfaces cannot drift into behaving differently.
/// </remarks>
public partial class MainWindow
{
    private ModlightDetailsModel? _details;

    /// <summary>
    /// The drawer's state, made on first use.
    /// </summary>
    /// <remarks>
    /// Lazily rather than as a field initializer, and that is not a micro-optimisation:
    /// field initializers across partial classes run in whatever order the compiler was
    /// handed the files, so one that reached for <see cref="MySpace"/> would be relying
    /// on a build detail nobody can see. By the time a binding or a menu asks for this,
    /// the window is constructed and there is nothing left to order.
    /// </remarks>
    internal ModlightDetailsModel Details => _details ??= CreateDetails();

    private ModlightDetailsModel CreateDetails()
    {
        ModlightDetailsModel model = new();

        model.Attach(MySpace);

        model.VisibilityRequested = file => _ = SetPublishedAsync(new[] { file }, !file.Published);
        model.FolderRequested = OfferFolders;
        model.LinkSettingsRequested = file => _ = EditLinkSettingsAsync(file);
        model.DownloadRequested = file => _ = DownloadAsync(file);
        model.TrashRequested = file => _ = TrashFilesAsync(new[] { file });
        model.RestoreRequested = file => _ = RestoreFilesAsync(new[] { file });
        model.DeleteForeverRequested = file => _ = DeleteForeverAsync(new[] { file });

        return model;
    }

    /// <summary>
    /// Open the drawer on one file, and keep it on whatever is picked next.
    /// </summary>
    /// <remarks>
    /// It answers with a Task it has already finished, and the name keeps the shape the
    /// dialog it replaces had. Both are deliberate: this used to open a modal window, so
    /// its callers await it - one of them around a re-entry guard that existed to stop a
    /// second dialog. A drawer is not modal and there is nothing to wait for, but a
    /// caller awaiting a completed Task is correct, whereas changing the signature would
    /// mean editing every one of them.
    /// </remarks>
    private Task ShowDetailsAsync(CloudFileViewModel file)
    {
        Details.Open(file);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Where this file could go, offered under the drawer's own button.
    /// </summary>
    /// <remarks>
    /// The same entries the card's context menu builds, indentation and tick included -
    /// not a second folder picker. A dialog listing seventeen folders is a dialog too
    /// many, and two lists of folders that could disagree is worse than either.
    /// </remarks>
    private void OfferFolders(CloudFileViewModel file, Control anchor)
    {
        // A menu already up has to go first; the opener refuses while one is open, and
        // the second click would look like a dead button.
        CloseActiveContextMenu();

        ContextMenu menu = BuildContextMenu(BuildMoveToFolderMenu(new[] { file }));
        TryOpenContextMenu(menu, anchor, PlacementMode.BottomEdgeAlignedRight);
    }
}
