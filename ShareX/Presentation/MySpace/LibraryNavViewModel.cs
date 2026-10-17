#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ShareX;

/// <summary>
/// One row of the library panel: everything, one class of file, or the trash.
/// </summary>
/// <remarks>
/// The class rows are FILTERS OVER THE WHOLE LIBRARY, not places, which is exactly what
/// they are in the web panel: its sidebar entries are query strings on the dashboard
/// rather than routes, and they deliberately drop the folder on the way. So choosing
/// "Images" leaves whatever folder was open and shows every image on the account - the
/// same answer the browser gives, which is the point of mirroring it.
///
/// The trash is the one row that is not a filter over the same set of rows: the server
/// lists soft-deleted files only when it is asked to, and it withholds their links
/// because every one of them would 404. See <see cref="CloudFileViewModel.IsTrashed"/>.
/// </remarks>
internal sealed class LibraryNavViewModel : INotifyPropertyChanged
{
    private bool isCurrent;

    private LibraryNavViewModel(string name, string icon, string? kind, bool isTrash)
    {
        Name = name;
        Icon = icon;
        Kind = kind;
        IsTrash = isTrash;
    }

    /// <summary>
    /// The rows, in the panel's own order.
    /// </summary>
    /// <remarks>
    /// The six classes are the server's <c>FILE_KINDS</c> minus "other", which the panel
    /// also leaves out of its rail: it has a translation but no row, because a bucket
    /// named "everything I could not classify" is not something anybody goes looking for.
    ///
    /// Unlike the panel, a class with nothing in it is still drawn. The panel hides an
    /// empty one, but it knows the counts - it renders them beside each row - and this
    /// endpoint does not offer them. Hiding rows on a count we do not have would mean
    /// hiding all six or guessing, and a rail that changes shape between accounts for
    /// reasons the user cannot see is worse than one row that happens to be empty.
    /// </remarks>
    public static List<LibraryNavViewModel> BuildAll() => new()
    {
        new LibraryNavViewModel(Strings.MySpace_AllFiles, LucideIcons.layers, null, false),
        new LibraryNavViewModel(Strings.MySpace_KindImage, LucideIcons.image, "image", false),
        new LibraryNavViewModel(Strings.MySpace_KindVideo, LucideIcons.film, "video", false),
        new LibraryNavViewModel(Strings.MySpace_KindAudio, LucideIcons.music, "audio", false),
        new LibraryNavViewModel(Strings.MySpace_KindDocument, LucideIcons.file_text, "document", false),
        new LibraryNavViewModel(Strings.MySpace_KindArchive, LucideIcons.archive, "archive", false),
        new LibraryNavViewModel(Strings.MySpace_KindPaste, LucideIcons.scroll_text, "paste", false),
        new LibraryNavViewModel(Strings.MySpace_Trash, LucideIcons.trash_2, null, true)
    };

    public string Name { get; }

    public string Icon { get; }

    /// <summary>The server's class name for <c>?kind=</c>, or null for "every kind".</summary>
    public string? Kind { get; }

    public bool IsTrash { get; }

    /// <summary>This is what the grid is showing. At most one row at a time, and often none.</summary>
    /// <remarks>
    /// None of them is marked while a FOLDER is open: the folder's own row in the tree
    /// below carries the mark then, and two marks in one list would be two answers to
    /// "where am I".
    /// </remarks>
    public bool IsCurrent
    {
        get => isCurrent;
        set
        {
            if (isCurrent == value)
            {
                return;
            }

            isCurrent = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
