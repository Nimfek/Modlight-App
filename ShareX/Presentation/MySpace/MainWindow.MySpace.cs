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
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace ShareX;

/// <summary>
/// The My space tab's half of the window: the folder shelf, the file grid and what a
/// right-click on either of them can do.
/// </summary>
/// <remarks>
/// A separate file from the rest of <see cref="MainWindow"/> only because that one is
/// already 1800 lines of session-task handling; nothing here is different in kind. The
/// state itself lives in <see cref="MySpaceViewModel"/> - these are the methods the
/// layout names, and none of them decides anything the view model does not already know.
///
/// The tab is a folder explorer, not a filtered list: folders sit side by side, you go
/// into one, and its files are inside. That is what the owner asked for in those words,
/// pointing at the web panel's own browser as the thing to match, which is why the
/// gestures here are Explorer's rather than the panel's - a double click opens, a click
/// on the icon opens at once, and the right button is a menu rather than a way to select
/// several things.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Where the pointer went down, so a drag off a tile does not count as a click.</summary>
    private Point _folderPressPoint;

    /// <summary>Which tile the pointer went down on, if any.</summary>
    private CloudFolderViewModel? _folderPressTarget;

    /// <summary>Whether that press was on the tile's icon, which opens on release.</summary>
    private bool _folderPressOnIcon;

    /// <summary>How far a press may travel and still be a click rather than a drag.</summary>
    private const double FolderClickSlop = 6;

    /// <summary>The press a card drag would be started from, while the button is still down.</summary>
    private PointerPressedEventArgs? _cardPressEvent;

    /// <summary>Where that press landed, so a drag can be told from a click.</summary>
    private Point _cardPressPoint;

    private bool _folderSidebarVisible;

    /// <summary>
    /// The library panel: everything, the classes of file, the trash, and the folder tree.
    /// </summary>
    /// <remarks>
    /// It used to be a folder list that was deliberately NOT remembered between runs,
    /// because the owner had allowed it only as an "in case you want it" extra and a
    /// preference that quietly persists would have turned an extra into the layout.
    /// He has since asked for the opposite in as many words - "I do miss a side menu... a
    /// hamburger that looks like the whole sidebar of the web app" - and asked for the
    /// state to survive a restart, so it does. The old note is kept here rather than
    /// deleted so that nobody re-derives the previous rule from first principles and takes
    /// the panel away again.
    ///
    /// Remembered through <see cref="ApplicationConfig.ModlightLibraryPanel"/>, which is
    /// read once when the tab is first built.
    /// </remarks>
    public bool FolderSidebarVisible
    {
        get => _folderSidebarVisible;
        private set
        {
            if (_folderSidebarVisible == value)
            {
                return;
            }

            _folderSidebarVisible = value;

            if (Program.Settings != null)
            {
                Program.Settings.ModlightLibraryPanel = value;
            }

            OnPropertyChanged(nameof(FolderSidebarVisible));
            OnPropertyChanged(nameof(FolderSidebarHidden));
            StartSidebarSlide(value);
        }
    }

    /// <summary>How long the panel takes to travel, matching Border.library-panel.</summary>
    private static readonly TimeSpan SidebarSlide = TimeSpan.FromMilliseconds(160);

    /// <summary>Which toggle the current slide belongs to, so a reversal cancels the last one.</summary>
    private int _sidebarStep;

    private bool _folderSidebarPresent;
    private bool _folderSidebarOpen;

    /// <summary>Whether the panel occupies a column in the layout.</summary>
    /// <remarks>
    /// Separate from <see cref="FolderSidebarVisible"/> because they answer different
    /// questions. Visible is what the user asked for; this is whether the grid beside the
    /// panel has to make room. They differ for exactly the length of one slide, which is
    /// the whole trick: the layout changes once, at the end the panel is furthest away,
    /// and the movement itself costs no measuring at all.
    /// </remarks>
    public bool FolderSidebarPresent
    {
        get => _folderSidebarPresent;
        private set
        {
            if (_folderSidebarPresent == value)
            {
                return;
            }

            _folderSidebarPresent = value;
            OnPropertyChanged(nameof(FolderSidebarPresent));
        }
    }

    /// <summary>Whether the panel is at its open position. Drives the render transform.</summary>
    public bool FolderSidebarOpen
    {
        get => _folderSidebarOpen;
        private set
        {
            if (_folderSidebarOpen == value)
            {
                return;
            }

            _folderSidebarOpen = value;
            OnPropertyChanged(nameof(FolderSidebarOpen));
        }
    }

    /// <summary>
    /// Put the panel in motion, in whichever direction it now has to go.
    /// </summary>
    /// <remarks>
    /// Opening posts the move at <see cref="DispatcherPriority.Background"/> rather than
    /// setting it here. Two reasons, and both are the difference between a slide and a
    /// jump: a Border realised into the tree already carrying the open class has no
    /// previous value to animate FROM, and the layout pass that makes room for the column
    /// is the expensive one in a library of several thousand cards - starting the movement
    /// underneath it would spend the first frames of a 160ms animation on measuring. By
    /// the time a Background callback runs, both are done.
    ///
    /// Closing is the mirror image, delayed by the length of the travel. A little longer
    /// than the travel, because a column that collapses on the same frame the transition
    /// ends occasionally clips its own last frame.
    ///
    /// The step counter is what makes double-clicking the toggle safe: each call claims a
    /// number, and a callback that no longer holds the current one has been overtaken and
    /// does nothing.
    /// </remarks>
    private void StartSidebarSlide(bool opening)
    {
        int step = ++_sidebarStep;

        if (opening)
        {
            FolderSidebarPresent = true;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_sidebarStep == step)
                    {
                        FolderSidebarOpen = true;
                    }
                },
                DispatcherPriority.Background);
            return;
        }

        FolderSidebarOpen = false;
        DispatcherTimer.RunOnce(
            () =>
            {
                if (_sidebarStep == step)
                {
                    FolderSidebarPresent = false;
                }
            },
            SidebarSlide + TimeSpan.FromMilliseconds(40));
    }

    /// <summary>
    /// The other face of <see cref="FolderSidebarVisible"/>, for the button that opens it.
    /// </summary>
    /// <remarks>
    /// A property rather than a <c>!</c> in the binding, for the reason the rest of this
    /// window states: the layout is not compile-checked, and a binding that silently failed
    /// would draw both halves of the toggle at once.
    /// </remarks>
    public bool FolderSidebarHidden => !_folderSidebarVisible;

    /// <summary>Take up the panel state saved from the last run. Called once, from the window.</summary>
    /// <remarks>
    /// All three fields directly, so a window that opens with the panel already out shows
    /// it in place rather than sliding it in. An animation on startup would be the app
    /// performing for someone who has not asked it for anything yet.
    /// </remarks>
    private void RestoreLibraryPanel()
    {
        _folderSidebarVisible = Program.Settings?.ModlightLibraryPanel ?? false;
        _folderSidebarPresent = _folderSidebarVisible;
        _folderSidebarOpen = _folderSidebarVisible;
    }

    private void OnCloudRefreshClick(object? sender, RoutedEventArgs e) => MySpace.Reload();

    /* ---------------------------------------------------------------------- */
    /* Getting about                                                          */
    /* ---------------------------------------------------------------------- */

    private void OnFolderUpClick(object? sender, RoutedEventArgs e) => MySpace.GoUp();

    private void OnFolderSidebarToggleClick(object? sender, RoutedEventArgs e) =>
        FolderSidebarVisible = !FolderSidebarVisible;

    private void OnLibraryViewClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LibraryNavViewModel view })
        {
            MySpace.SelectView(view);
        }
    }

    /// <summary>The panel's own "new folder" button, for when nothing is selected.</summary>
    /// <remarks>
    /// The owner asked for this by name - "there's also no way to create new folders" -
    /// and the background right-click alone would not have answered it: a library with no
    /// folders yet has no tile to right-click, and the empty state is exactly when
    /// somebody wants the first one.
    /// </remarks>
    private void OnNewFolderClick(object? sender, RoutedEventArgs e) => _ = NewFolderAsync(MySpace.UploadFolderId);

    private void OnFolderRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CloudFolderViewModel folder })
        {
            MySpace.NavigateTo(folder.Id);
        }
    }

    /// <summary>
    /// A right-click on a folder row in the panel offers what the tile offers.
    /// </summary>
    /// <remarks>
    /// The same menu, deliberately. A folder is a folder wherever it is drawn, and a panel
    /// whose rows answered the right button with nothing would teach people that the panel
    /// is decoration.
    /// </remarks>
    private void OnFolderRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            CloseActiveContextMenu();
            e.Handled = true;
        }
    }

    private void OnFolderRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased)
        {
            return;
        }

        if (sender is not Control { Tag: CloudFolderViewModel folder } row)
        {
            return;
        }

        ContextMenu menu = BuildContextMenu(BuildFolderMenu(folder));
        TryOpenContextMenu(menu, row, PlacementMode.Pointer);
        e.Handled = true;
    }

    private void OnCrumbClick(object? sender, RoutedEventArgs e)
    {
        // The segment you are already in is a label, not a link. Going "to" it would
        // reload the folder for no reason and make the trail feel like it does nothing.
        if (sender is Button { Tag: FolderCrumbViewModel crumb } && !crumb.IsCurrent)
        {
            MySpace.NavigateTo(crumb.Id);
        }
    }

    /// <summary>
    /// A press on a tile: mark it, and remember where the pointer was.
    /// </summary>
    /// <remarks>
    /// The mark happens on press rather than on release because that is when Explorer
    /// marks, and because a press that turns into a drag has to have already told the
    /// user what it picked up.
    /// </remarks>
    private void OnFolderTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: CloudFolderViewModel folder })
        {
            return;
        }

        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;

        if (kind == PointerUpdateKind.RightButtonPressed)
        {
            // A menu already up has to go before the next one is asked for; the opener
            // refuses while one is open, and the second right-click would do nothing.
            CloseActiveContextMenu();
            MySpace.SelectFolderTile(folder);
            e.Handled = true;
            return;
        }

        if (kind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        _folderPressTarget = folder;
        _folderPressOnIcon = false;
        _folderPressPoint = e.GetPosition(this);

        // NOT marked handled, deliberately. The double-tap gesture is recognised from the
        // pressed event, and a double click on a tile is how a folder is opened; marking
        // this handled to stop the background clearing the selection would pay for that
        // with the gesture the whole shelf is navigated by. The background handler asks
        // whether the press landed on an item instead.
        MySpace.SelectFolderTile(folder);
    }

    private void OnFolderTilePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased)
        {
            _folderPressTarget = null;
            return;
        }

        if (sender is not Control { DataContext: CloudFolderViewModel folder } tile)
        {
            return;
        }

        ContextMenu menu = BuildContextMenu(BuildFolderMenu(folder));
        TryOpenContextMenu(menu, tile, PlacementMode.Pointer);
        e.Handled = true;
    }

    private void OnFolderTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: CloudFolderViewModel folder })
        {
            MySpace.OpenFolder(folder);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The icon is its own hit target, and pressing it goes straight in.
    /// </summary>
    /// <remarks>
    /// An explicit request from the owner: "besides that I want the option of entering a
    /// folder by clicking its icon". Acted on at RELEASE rather than at press, and only
    /// when the pointer has not travelled - otherwise starting a drag from the icon would
    /// navigate out from under the drag.
    /// </remarks>
    private void OnFolderIconPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: CloudFolderViewModel folder })
        {
            return;
        }

        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;

        if (kind == PointerUpdateKind.RightButtonPressed)
        {
            // Right-clicking the icon is still right-clicking the tile; let it bubble so
            // the tile puts up its menu rather than the icon swallowing the gesture.
            return;
        }

        if (kind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        _folderPressTarget = folder;
        _folderPressOnIcon = true;
        _folderPressPoint = e.GetPosition(this);

        MySpace.SelectFolderTile(folder);

        // Marked handled here and NOT on the tile, because the tile's own handler runs
        // next and would overwrite the flag that says this press was on the icon - and
        // then a click on the icon would only select, which is the one thing the owner
        // asked for by name. Nothing is lost: the icon opens on the first click, so it
        // has no use for a double one.
        e.Handled = true;
    }

    private void OnFolderIconPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased)
        {
            return;
        }

        if (sender is not Control { Tag: CloudFolderViewModel folder })
        {
            return;
        }

        bool sameTile = _folderPressOnIcon && ReferenceEquals(_folderPressTarget, folder);
        Point released = e.GetPosition(this);
        bool moved = Math.Abs(released.X - _folderPressPoint.X) > FolderClickSlop ||
                     Math.Abs(released.Y - _folderPressPoint.Y) > FolderClickSlop;

        _folderPressTarget = null;
        _folderPressOnIcon = false;

        if (sameTile && !moved)
        {
            MySpace.OpenFolder(folder);
            e.Handled = true;
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Putting things in                                                      */
    /* ---------------------------------------------------------------------- */

    /// <summary>Send files into the folder that is open.</summary>
    private void OnUploadHereClick(object? sender, RoutedEventArgs e) => PickAndUpload(MySpace.UploadFolderId);

    /// <summary>Send files into the folder whose tile this button is on.</summary>
    private void OnFolderUploadClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CloudFolderViewModel folder })
        {
            MySpace.SelectFolderTile(folder);
            PickAndUpload(folder.Id);
        }
    }

    /// <summary>
    /// Ask for files and send them somewhere in particular.
    /// </summary>
    /// <remarks>
    /// The picker is <see cref="UploadManager"/>'s own, so the "last folder used" memory
    /// and the multi-file warning are the ones the rest of the app has. All that is added
    /// is where the result goes.
    /// </remarks>
    private static void PickAndUpload(string? folderId) => UploadManager.UploadFile(TargetedSettings(folderId));

    /// <summary>
    /// Upload settings aimed at one folder.
    /// </summary>
    /// <remarks>
    /// A fresh TaskSettings per call, never a shared one: two uploads started a second
    /// apart go to different places, and a mutated shared instance would send the first
    /// one wherever the second was aimed.
    /// </remarks>
    private static TaskSettings TargetedSettings(string? folderId)
    {
        TaskSettings settings = TaskSettings.GetDefaultTaskSettings();
        settings.ModlightFolderId = folderId;
        return settings;
    }

    /* ---------------------------------------------------------------------- */
    /* Menus                                                                  */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// A right-click on the background, the way every file manager answers one.
    /// </summary>
    /// <remarks>
    /// The pressed half only closes whatever is already open. Opening on release is the
    /// same pattern the file cards use, and it is what makes a second right-click behave
    /// like a second right-click rather than like nothing at all.
    /// </remarks>
    private void OnCloudBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;

        if (kind == PointerUpdateKind.RightButtonPressed)
        {
            CloseActiveContextMenu();
            e.Handled = true;
            return;
        }

        // A click on nothing clears the selection, which is the only way back to "nothing
        // is selected" with the mouse alone. On NOTHING: a press that landed on a card or
        // a tile bubbles up through here too, and clearing on that would undo the
        // selection the card just made.
        if (kind == PointerUpdateKind.LeftButtonPressed && !PressedOnItem(e.Source))
        {
            MySpace.ClearSelection();
        }
    }

    /// <summary>
    /// Whether an event started inside a file card or a folder tile.
    /// </summary>
    /// <remarks>
    /// By walking up to the first control whose data is one of the two view models,
    /// rather than by marking the card's own press handled. Both would stop the
    /// background acting on it, but marking it handled also takes the double-tap gesture
    /// away, and double-clicking is how a folder is entered and a file is opened.
    /// </remarks>
    private static bool PressedOnItem(object? source)
    {
        Visual? visual = source as Visual;

        while (visual != null)
        {
            if (visual is Control { DataContext: CloudFileViewModel or CloudFolderViewModel })
            {
                return true;
            }

            visual = visual.GetVisualParent();
        }

        return false;
    }

    private void OnCloudBackgroundPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased)
        {
            return;
        }

        if (sender is Control background)
        {
            ContextMenu menu = BuildContextMenu(BuildCloudBackgroundMenu());
            TryOpenContextMenu(menu, background, PlacementMode.Pointer);
            e.Handled = true;
        }
    }

    /// <summary>The account's short code, which its public links are built from.</summary>
    private static string AccountTag() => ModlightSignIn.Current?.Tag ?? "";

    /// <summary>Open a page on the server this app is signed in to, and nowhere else.</summary>
    private static void OpenOnServer(string path)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null || string.IsNullOrEmpty(account.Host))
        {
            SignInWindowIntegration.Show();
            return;
        }

        URLHelpers.OpenURL(account.Host.TrimEnd('/') + path);
    }

    private void OnCloudRetryClick(object? sender, RoutedEventArgs e) => MySpace.Reload();

    private void OnMySpaceSignInClick(object? sender, RoutedEventArgs e) => SignInWindowIntegration.Show();

    private void OnGoToCaptureClick(object? sender, RoutedEventArgs e) => SelectedTab = MainWindowTab.Create;

    /// <summary>
    /// Say which cards are on screen, so their pictures are fetched first and kept longest.
    /// </summary>
    /// <remarks>
    /// This used to fetch the next page as well, because the grid was an endless one. It
    /// is not: the listing is paged the way the web panel's is, the bar under the grid
    /// turns the pages, and a scroll now moves within a page rather than through the
    /// library. What is left is the half that was always the interesting half - WHERE the
    /// viewport is - and it feeds two things that both need it: the ceiling on decoded
    /// pictures, which must not evict a tile somebody is looking at, and the fetch queue,
    /// which serves the tiles nearest the viewport first and drops the rest.
    ///
    /// It fires many times a second during a flick, and everything it calls is written for
    /// that: the estimate is arithmetic on three doubles, and the view model reuses one
    /// buffer rather than allocating a list per event.
    /// </remarks>
    private void OnCloudScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        ReportVisibleCards(viewer.Offset.Y, viewer.Viewport.Height, viewer.Extent.Height);
    }

    /// <summary>
    /// Work out which cards are on screen, so the view model knows whose pictures to keep.
    /// </summary>
    /// <remarks>
    /// By proportion of the scrolled height rather than by asking the cards, which cannot
    /// be asked without either virtualising the panel - Avalonia has no virtualising
    /// wrapping panel - or hanging a viewport listener on several thousand controls. Every
    /// card is the same size, so the proportion is the index to within the padding and the
    /// footer inside the scrolled area: eighty pixels, against an extent that reaches tens
    /// of thousands. The band the view model keeps is forty cards wider than this on each
    /// side, which is a great deal more than that error can amount to.
    /// </remarks>
    private void ReportVisibleCards(double offset, double viewport, double extent)
    {
        int count = MySpace.Files.Count;

        if (count == 0 || extent <= 0 || viewport <= 0)
        {
            return;
        }

        // The folder shelf shares the scroller with the grid, so its height is part of the
        // extent and none of it is files. Subtracting it keeps the proportion honest: at
        // the root of the owner's account the shelf is five rows of tiles, and treating
        // four hundred pixels of folders as if they were cards would put the estimate out
        // by the better part of a screenful on a short listing.
        double shelf = Math.Max(0, CloudFolderShelf.IsVisible ? CloudFolderShelf.Bounds.Height : 0);
        double gridExtent = extent - shelf;

        if (gridExtent <= 0)
        {
            // Only folders on screen, no files below them. Nothing to pin.
            return;
        }

        double gridOffset = Math.Max(0, offset - shelf);

        double top = Math.Clamp(gridOffset / gridExtent, 0, 1);
        double bottom = Math.Clamp((gridOffset + viewport) / gridExtent, 0, 1);

        MySpace.SetVisibleRange((int)Math.Floor(top * count), (int)Math.Ceiling(bottom * count));
    }

    /// <summary>
    /// Selecting file cards, the way the platform does it.
    /// </summary>
    /// <remarks>
    /// Plain click replaces the selection, Ctrl+click toggles one card, Shift+click takes
    /// the range from the last card touched. The web panel put multi-select on the RIGHT
    /// button instead (Round 4); that is a habit of a surface with no menus, and a desktop
    /// app that swallows the context menu to do it reads as broken. Here the right button
    /// means what it means everywhere on Windows.
    /// </remarks>
    private void OnCloudCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;

        // A plain click now also opens the file's details, and this press is the gesture
        // that decides: the modifiers are recorded for the tap that follows, and details
        // an earlier click asked for are dropped. See MainWindow.MySpace.Input.cs.
        NoteCardPress(e.KeyModifiers);

        if (kind == PointerUpdateKind.RightButtonPressed)
        {
            // A menu already up has to go before the next one is asked for; the opener
            // refuses while one is open, and the second right-click would do nothing.
            CloseActiveContextMenu();

            // A right-click on a card that is NOT part of the selection replaces it, the
            // way Explorer does: the menu that follows has to be about the thing under
            // the pointer, never about something selected elsewhere and forgotten.
            if (sender is Control { DataContext: CloudFileViewModel target } && !target.IsSelected)
            {
                MySpace.SelectFile(target);
            }

            e.Handled = true;
            return;
        }

        if (kind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        if (sender is not Control { DataContext: CloudFileViewModel file })
        {
            return;
        }

        // Kept so that a press which travels can become a drag. DoDragDropAsync needs the
        // pressed event itself, not a copy of where it happened.
        _cardPressEvent = e;
        _cardPressPoint = e.GetPosition(this);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            MySpace.SelectFileRange(file);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            MySpace.ToggleFile(file);
        }
        else
        {
            MySpace.SelectFile(file);
        }

        // Left alone on purpose - a double click on a card opens the file, and that
        // gesture is recognised from this event. See the folder tile for the same note.
    }

    private void OnCloudCardPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        _cardPressEvent = null;

    /// <summary>
    /// The pointer is on a card, which is the only signal that asks for a moving preview.
    /// </summary>
    /// <remarks>
    /// Entered and Exited rather than PointerMoved, which this card also handles: moving
    /// fires dozens of times a second for as long as the pointer is anywhere on the tile,
    /// and the question being asked here is answered once on the way in and once on the
    /// way out. Neither is marked handled - the drag gesture, the selection and the double
    /// click all still need these events to reach them.
    /// </remarks>
    private void OnCloudCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: CloudFileViewModel file })
        {
            MySpace.HoverFile(file);
        }
    }

    private void OnCloudCardPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: CloudFileViewModel file })
        {
            MySpace.UnhoverFile(file);
        }
    }

    private void OnCloudCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased)
        {
            return;
        }

        _cardPressEvent = null;

        if (sender is not Control { DataContext: CloudFileViewModel file } card)
        {
            return;
        }

        ContextMenu menu = BuildContextMenu(BuildCloudFileMenu(file));
        TryOpenContextMenu(menu, card, PlacementMode.Pointer);
        e.Handled = true;
    }

    private void OnCloudCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The first of these two clicks asked for the details window. It does not get it:
        // this gesture is the other one, and both cannot be what the user meant.
        CancelPendingDetails();

        if (sender is Control { DataContext: CloudFileViewModel file } && file.CanOpen)
        {
            URLHelpers.OpenURL(file.OpenLink);
            e.Handled = true;
        }
    }

    /// <summary>The window's full-surface drop overlay, which a folder tile answers instead of.</summary>
    private void HideDropOverlay() => DropOverlay.IsVisible = false;

    /// <summary>
    /// The keys an explorer answers. Returns whether this tab took the key.
    /// </summary>
    /// <remarks>
    /// Escape is the one worth stating out loud: it clears the selection and NEVER changes
    /// folder. Somebody who presses it to dismiss a marked file must not find themselves
    /// somewhere else, and it reports back that it did nothing when there was nothing to
    /// clear, so the rest of the window still gets its turn.
    /// </remarks>
    private bool HandleMySpaceKey(KeyEventArgs e)
    {
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if ((e.Key == Key.Back && !control && !alt) || (e.Key == Key.Left && alt))
        {
            if (!MySpace.CanGoUp)
            {
                return false;
            }

            MySpace.GoUp();
            return true;
        }

        if (e.Key == Key.B && control)
        {
            FolderSidebarVisible = !FolderSidebarVisible;
            return true;
        }

        if (e.Key == Key.A && control)
        {
            if (!MySpace.HasFiles)
            {
                return false;
            }

            MySpace.SelectAllFiles();
            return true;
        }

        // Delete and Shift+Delete, on whatever is selected. Guarded against text fields
        // from the inside as well as out. See MainWindow.MySpace.Input.cs.
        if (HandleLibraryDeleteKey(e))
        {
            return true;
        }

        return e.Key == Key.Escape && MySpace.ClearSelection();
    }
}
