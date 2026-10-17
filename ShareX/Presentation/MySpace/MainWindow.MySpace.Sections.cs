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

using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ShareX;

/// <summary>
/// The two section headers in My space - Folders and Files - and the fold they own.
/// </summary>
/// <remarks>
/// Its own file rather than more of <see cref="MainWindow"/>, which is already carrying
/// this tab in four parts.
///
/// The header IS the control. There is no separate chevron button beside a label: a
/// 12 px target next to a word the pointer is already over is a smaller version of the
/// same gesture, and the owner asked to click the header. The chevron turns rather than
/// swapping glyph, so the state reads as one thing moving instead of two icons taking
/// turns.
///
/// Both flags are remembered in <see cref="ApplicationConfig.ModlightFoldersSection"/>
/// and <see cref="ApplicationConfig.ModlightFilesSection"/>, and they are read in FIELD
/// INITIALISERS on purpose. Those run before the constructor body, and therefore before
/// InitializeComponent: the bindings below are plain properties, so a value assigned
/// after the layout was built would draw the window once expanded and then fold it -
/// the same glitch RestoreLibraryPanel exists to avoid for the side panel.
/// </remarks>
public partial class MainWindow
{
    private bool _foldersSectionOpen = Program.Settings?.ModlightFoldersSection ?? true;
    private bool _filesSectionOpen = Program.Settings?.ModlightFilesSection ?? true;

    /// <summary>Whether the folder shelf is showing its tiles.</summary>
    public bool FoldersSectionOpen
    {
        get => _foldersSectionOpen;
        private set
        {
            if (_foldersSectionOpen == value)
            {
                return;
            }

            _foldersSectionOpen = value;

            if (Program.Settings != null)
            {
                Program.Settings.ModlightFoldersSection = value;
            }

            OnPropertyChanged(nameof(FoldersSectionOpen));
            OnPropertyChanged(nameof(FoldersSectionCollapsed));
            SettleSections();
        }
    }

    /// <summary>
    /// The other face of <see cref="FoldersSectionOpen"/>, for the header's own class.
    /// </summary>
    /// <remarks>
    /// A property rather than a <c>!</c> in the binding: this window is
    /// x:CompileBindings="False", so the negating form is checked by nothing, and a
    /// binding that failed silently would leave the chevron pointing the wrong way at a
    /// section that folded perfectly well - the hardest kind of wrong to notice.
    /// </remarks>
    public bool FoldersSectionCollapsed => !_foldersSectionOpen;

    /// <summary>Whether the file grid is showing its cards.</summary>
    public bool FilesSectionOpen
    {
        get => _filesSectionOpen;
        private set
        {
            if (_filesSectionOpen == value)
            {
                return;
            }

            _filesSectionOpen = value;

            if (Program.Settings != null)
            {
                Program.Settings.ModlightFilesSection = value;
            }

            OnPropertyChanged(nameof(FilesSectionOpen));
            OnPropertyChanged(nameof(FilesSectionCollapsed));
            SettleSections();
        }
    }

    /// <inheritdoc cref="FoldersSectionCollapsed"/>
    public bool FilesSectionCollapsed => !_filesSectionOpen;

    private void OnFoldersSectionClick(object? sender, RoutedEventArgs e) =>
        FoldersSectionOpen = !FoldersSectionOpen;

    private void OnFilesSectionClick(object? sender, RoutedEventArgs e) =>
        FilesSectionOpen = !FilesSectionOpen;

    /// <summary>
    /// Hand the pictures back to a grid that has just been unfolded.
    /// </summary>
    /// <remarks>
    /// Which cards get thumbnails is worked out from the scroll position, in the scroll
    /// handler - so a fold that leaves the content SHORTER than the window tells nobody
    /// anything. A ScrollViewer reports its extent as at least its viewport, so unfolding
    /// a page of six files changes no number the control can raise an event about, and
    /// those six tiles would sit there as grey rectangles until something else moved.
    ///
    /// When it fits, every card on the page is on screen, which is a range this can state
    /// exactly rather than estimate. Anything taller than the window did change the
    /// extent, raised the event, and has already been dealt with by the handler that owns
    /// this arithmetic - which is why the taller case is deliberately not repeated here.
    ///
    /// At Background priority because that is below the layout pass: by the time it runs
    /// the section has been measured, so the extent is the one on screen rather than the
    /// one from before the click.
    /// </remarks>
    private void SettleSections() =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (FilesSectionOpen && CloudScroller.Extent.Height <= CloudScroller.Viewport.Height)
                {
                    MySpace.SetVisibleRange(0, MySpace.Files.Count);
                }
            },
            DispatcherPriority.Background);
}
