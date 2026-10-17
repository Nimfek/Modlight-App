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
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using Canvas = Avalonia.Controls.Canvas;

namespace ShareX;

/// <summary>
/// Dragging in the library: what a drag MEANS, and what the pointer carries while it runs.
/// </summary>
/// <remarks>
/// Two gestures land on the same folder tile and they are not the same request.
///
/// Cards dragged out of this window's own grid are a MOVE - the file already exists on the
/// server and the only thing changing is which folder it is filed under, which is one
/// PATCH /api/uploads. Files dragged in from Explorer are an UPLOAD into that folder.
/// Answering the first as if it were the second would leave the original where it was and
/// put a second copy in the target, which is not "move" by any reading.
///
/// The two used to be told apart by what the transfer was carrying, and that is exactly how
/// this went wrong: a drag has to put SOMETHING in the transfer or the platform will not run
/// one at all, ours puts the links in as text, and a window-wide handler that saw text and
/// concluded "an upload of some text" then announced "release to upload text" over an image
/// the account already owns - and, worse, would have carried that out on release, writing a
/// new paste onto the server for a gesture that was meant to move a file three inches.
///
/// So the test is <see cref="IsCloudCardDrag"/>: the list of cards being dragged, held in a
/// field for the life of the gesture. It cannot be misread, it cannot be forged by another
/// application, and a second copy of Modlight running beside this one sees null - which is
/// correct, because from its point of view the drag really did come from outside.
/// </remarks>
public partial class MainWindow
{
    /// <summary>How far from the pointer the ghost sits, so it never covers the drop target.</summary>
    private const double DragGhostOffset = 16;

    /// <summary>The layer the ghost is parented to, found once.</summary>
    private Canvas? _dragGhostHost;

    /// <summary>The floating card that follows the pointer while a drag runs.</summary>
    private Border? _dragGhost;

    /// <summary>The thumbnail half of it, whose data context is the card being dragged.</summary>
    private Panel? _dragGhostPicture;

    private Border? _dragGhostFrame;
    private TextBlock? _dragGhostGlyph;
    private Border? _dragGhostBadge;
    private TextBlock? _dragGhostBadgeText;
    private TextBlock? _dragGhostHint;

    /// <summary>Whether the window-wide handlers that keep the ghost under the pointer are on.</summary>
    private bool _dragGhostTracking;

    /// <summary>Whether the drag now running started on this window's own file cards.</summary>
    private bool IsCloudCardDrag => _cloudDrag is { Count: > 0 };

    /* ---------------------------------------------------------------------- */
    /* Starting a drag                                                        */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// A press that travels far enough becomes a drag of the whole selection.
    /// </summary>
    /// <remarks>
    /// Started from POINTER MOVED rather than from the press, and only past a threshold,
    /// because every click begins as a press: firing a drag on the press itself would mean
    /// a single click on a card started one, and the platform would then swallow the click.
    ///
    /// The button state is checked as well as the distance. A left press is remembered in
    /// _cardPressEvent and nothing was clearing it on a left RELEASE - only a right one did
    /// - so after any ordinary click the next stroll of the mouse across that card met a
    /// remembered press and started a drag with no button held down.
    ///
    /// The payload is the selection, held in a field. The transfer gets the links as text
    /// instead, and that is all it is for: it is what makes the gesture useful if it ends
    /// in a chat window, and it is the something the platform insists on before it will run
    /// a drag at all. Nothing on the receiving side reads it - see the note on the class.
    /// </remarks>
    private async void OnCloudCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_cardPressEvent == null || _cloudDrag != null || MySpace.IsTrash)
        {
            return;
        }

        PointerPoint at = e.GetCurrentPoint(this);

        if (!at.Properties.IsLeftButtonPressed)
        {
            _cardPressEvent = null;
            return;
        }

        if (Math.Abs(at.Position.X - _cardPressPoint.X) <= FolderClickSlop &&
            Math.Abs(at.Position.Y - _cardPressPoint.Y) <= FolderClickSlop)
        {
            return;
        }

        PointerPressedEventArgs press = _cardPressEvent;
        _cardPressEvent = null;

        List<CloudFileViewModel> moving = MySpace.SelectedFiles();

        if (moving.Count == 0)
        {
            return;
        }

        _cloudDrag = moving;

        try
        {
            ShowDragGhost(moving, at.Position);

            string carried = string.Join(Environment.NewLine,
                moving.Where(x => x.HasLink).Select(x => x.CopyLink));

            if (carried.Length == 0)
            {
                // Every card has a link in practice. If one somehow does not, the names still
                // make the transfer non-empty, and an empty transfer is a drag the platform
                // never starts - which would show up as the gesture doing nothing at all.
                carried = string.Join(Environment.NewLine, moving.Select(x => x.Name));
            }

            DataTransfer payload = new();
            payload.Add(DataTransferItem.CreateText(carried));

            await DragDrop.DoDragDropAsync(press, payload, DragDropEffects.Move);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
        finally
        {
            // The selection is deliberately left alone. Whatever was dragged is still what
            // is selected afterwards, which is what lets a move be followed straight away
            // by a second one, or by a menu about the same files. Cards that left the
            // folder on screen are taken off it by MySpaceViewModel.NoteFilesMoved; the
            // ones still on screen keep their mark, because nothing in that path touches
            // CloudFileViewModel.IsSelected.
            _cloudDrag = null;
            ClearDropHighlights();
            HideDragGhost();
        }
    }

    /// <summary>
    /// Take the hover mark off every folder once a drag is over.
    /// </summary>
    /// <remarks>
    /// A drag that ends outside any tile raises no DragLeave on the last one the pointer
    /// crossed, which used to leave a folder looking like a live drop target forever.
    /// </remarks>
    private void ClearDropHighlights()
    {
        foreach (CloudFolderViewModel folder in MySpace.Folders)
        {
            folder.IsDropTarget = false;
        }

        foreach (CloudFolderViewModel folder in MySpace.VisibleFolders)
        {
            folder.IsDropTarget = false;
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Folder tiles as drop targets                                           */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Something is hovering over a folder tile or a folder row.
    /// </summary>
    /// <remarks>
    /// Marking the event handled is what keeps the window's own full-surface overlay off
    /// the screen while the pointer is over a tile: that overlay says "release to upload",
    /// and over a tile the true answer names the folder. Two answers at once would be one
    /// too many.
    ///
    /// It is deliberately NOT marked handled when this tile cannot take the drop. The
    /// window behind it can - an upload that names no folder lands in the one being shown -
    /// so standing aside turns what used to be a dead patch of the window into the ordinary
    /// answer. Refusing here and saying nothing was the older behaviour and it read as
    /// broken: the overlay vanished, the tile did not light up, and releasing did nothing.
    /// </remarks>
    private void OnFolderTileDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: CloudFolderViewModel folder })
        {
            return;
        }

        if (IsCloudCardDrag)
        {
            OnFolderTileMoveOver(folder, e);
            return;
        }

        // From outside. Only a real folder can be aimed at, and only by files that exist
        // on disk: text and images dragged out of a browser have no path, so there is
        // nothing to hand the uploader that names this folder.
        if (!folder.IsRealFolder || e.DataTransfer.TryGetFiles()?.Any() != true)
        {
            folder.IsDropTarget = false;
            HideDragGhost();
            return;
        }

        folder.IsDropTarget = true;
        e.DragEffects = DragDropEffects.Copy;
        HideDropOverlay();
        ShowDragHint(string.Format(Strings.MySpace_DragUploadTo, folder.Name));
        e.Handled = true;
    }

    /// <summary>Cards from this window's own grid, hovering over a folder.</summary>
    private void OnFolderTileMoveOver(CloudFolderViewModel folder, DragEventArgs e)
    {
        bool accepts = CanDropCloudFiles(folder);

        folder.IsDropTarget = accepts;
        e.DragEffects = accepts ? DragDropEffects.Move : DragDropEffects.None;
        HideDropOverlay();

        SetDragHint(accepts
            ? folder.IsRealFolder
                ? string.Format(Strings.MySpace_DragMoveTo, folder.Name)
                : Strings.MySpace_DragMoveOut
            : Strings.MySpace_DragMoveAlreadyHere);

        // Handled either way: a move this tile refuses must not fall through to the window,
        // which would offer to upload the links as a note.
        e.Handled = true;
    }

    /// <summary>
    /// Whether the cards being dragged could land here.
    /// </summary>
    /// <remarks>
    /// The loose-files shelf counts as somewhere: moving a file OUT of every folder is a
    /// real thing to want, and the server spells it as a null folder id. What is refused
    /// is a drop onto the folder the files are already in, which would be a request that
    /// changes nothing.
    /// </remarks>
    private bool CanDropCloudFiles(CloudFolderViewModel folder)
    {
        if (_cloudDrag == null)
        {
            return false;
        }

        string? target = folder.IsRealFolder ? folder.Id : null;

        foreach (CloudFileViewModel file in _cloudDrag)
        {
            if (file.FolderId != target)
            {
                return true;
            }
        }

        return false;
    }

    private void OnFolderTileDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Control { DataContext: CloudFolderViewModel folder })
        {
            folder.IsDropTarget = false;
        }

        if (IsCloudCardDrag)
        {
            SetDragHint(Strings.MySpace_DragMoveHint);
        }
        else
        {
            HideDragGhost();
        }
    }

    private void OnFolderTileDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: CloudFolderViewModel folder })
        {
            return;
        }

        folder.IsDropTarget = false;
        HideDropOverlay();
        HideDragGhost();

        // Cards from this window's own grid: a move, and one PATCH for however many of
        // them are selected. MoveFilesAsync drops the ones already in the target itself.
        if (IsCloudCardDrag)
        {
            List<CloudFileViewModel> moving = _cloudDrag!;
            bool accepts = CanDropCloudFiles(folder);

            e.DragEffects = accepts ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;

            if (accepts)
            {
                _ = MoveFilesAsync(moving, folder.IsRealFolder ? folder.Id : null);
            }

            return;
        }

        string[] files = e.DataTransfer.TryGetFiles()?
            .Select(x => x.TryGetLocalPath())
            .Where(x => !string.IsNullOrEmpty(x))
            .Cast<string>().ToArray() ?? Array.Empty<string>();

        if (!folder.IsRealFolder || files.Length == 0)
        {
            // Left for the window to answer, which is the same thing the hover said would
            // happen: it never lit this tile up and never hid the "release to upload"
            // overlay, so the release has to do what the overlay promised.
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        UploadManager.UploadFile(files, TargetedSettings(folder.Id));
    }

    /* ---------------------------------------------------------------------- */
    /* Keeping the window's upload handling out of an internal drag           */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Whether the window-wide upload handling must stand aside for a drag of our own.
    /// </summary>
    /// <remarks>
    /// Called from both halves of the window's own drag handling. Over the file grid, over
    /// the tab strip, over anything that is not a folder, a drag of this account's own
    /// files means one thing and it is not an upload - so the overlay comes down, the
    /// effect says no, and the release does nothing at all rather than posting the links
    /// back to the server as a new paste.
    /// </remarks>
    private bool CloudDragOwnsEvent(DragEventArgs e)
    {
        if (!IsCloudCardDrag)
        {
            return false;
        }

        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
        HideDropOverlay();
        SetDragHint(Strings.MySpace_DragMoveHint);
        return true;
    }

    /* ---------------------------------------------------------------------- */
    /* The ghost                                                              */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Build the floating card, once.
    /// </summary>
    /// <remarks>
    /// Avalonia 12.1.1 has no drag image: DoDragDropAsync takes the press, the transfer and
    /// the allowed effects and nothing else, and there is no interop here for the shell's
    /// own drag-image helper. So a real one carried by the platform is not on the table, and
    /// this is the closest thing that works - a card of ours, in the window, following the
    /// pointer. It stops at the window edge, which is the whole distance this gesture ever
    /// travels: the grid and the folders it is aimed at are both inside.
    ///
    /// Built in code rather than in the window's layout because it belongs to this file and
    /// to nothing else, and parented to the OVERLAY LAYER rather than to the window's own
    /// grid: that layer is a Canvas above every row of the window, so the ghost sits over
    /// the tab strip, the title bar and the drop overlay without any z-order arrangement of
    /// ours. The fallback exists because a window whose template carries no visual layer
    /// manager has no overlay layer, and a ghost that silently never appears would be worse
    /// than one drawn a little less cleanly.
    ///
    /// The picture is BOUND to the card rather than handed its bitmap, and that is a safety
    /// measure, not a style. CloudFileViewModel disposes the old bitmap when the thumbnail
    /// budget takes a picture back, one line after raising PropertyChanged - so a binding is
    /// already null by the time the surface is destroyed, while a reference stored here
    /// would still be pointing at it when the compositor came to draw.
    /// </remarks>
    private bool EnsureDragGhost()
    {
        if (_dragGhost != null)
        {
            return true;
        }

        Canvas? host = OverlayLayer.GetOverlayLayer(this);

        if (host == null)
        {
            if (Content is not Grid root)
            {
                return false;
            }

            Canvas fallback = new() { IsHitTestVisible = false, ZIndex = 1000 };
            Grid.SetRowSpan(fallback, Math.Max(1, root.RowDefinitions.Count));
            root.Children.Add(fallback);
            host = fallback;
        }

        _dragGhostHost = host;

        Image picture = new()
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        picture.Bind(Image.SourceProperty, new Binding(nameof(CloudFileViewModel.Thumbnail)));

        _dragGhostFrame = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = picture
        };

        _dragGhostGlyph = new TextBlock
        {
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _dragGhostGlyph.Classes.Add("icon");
        _dragGhostGlyph.Bind(TextBlock.TextProperty, new Binding(nameof(CloudFileViewModel.Icon)));
        _dragGhostGlyph.Bind(IsVisibleProperty, new Binding(nameof(CloudFileViewModel.HasNoThumbnail)));

        _dragGhostBadgeText = new TextBlock
        {
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _dragGhostBadge = new Border
        {
            MinWidth = 20,
            Height = 18,
            Padding = new Thickness(5, 0),
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -6, -5),
            IsVisible = false,
            Child = _dragGhostBadgeText
        };

        _dragGhostPicture = new Panel
        {
            Width = 40,
            Height = 40,
            Children = { _dragGhostFrame, _dragGhostGlyph, _dragGhostBadge }
        };

        _dragGhostHint = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _dragGhost = new Border
        {
            Padding = new Thickness(9),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Opacity = 0.96,
            IsHitTestVisible = false,
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _dragGhostPicture, _dragGhostHint }
            }
        };

        host.Children.Add(_dragGhost);
        StartTrackingDragGhost();
        return true;
    }

    /// <summary>
    /// Follow the pointer for the whole of a drag, wherever in the window it goes.
    /// </summary>
    /// <remarks>
    /// Added with handledEventsToo, which is the whole reason these exist rather than the
    /// handlers the layout already names: a folder tile marks its own DragOver handled so
    /// the window stops offering to upload, and a ghost that only moved on unhandled events
    /// would freeze the moment it crossed the tile it is being aimed at.
    /// </remarks>
    private void StartTrackingDragGhost()
    {
        if (_dragGhostTracking)
        {
            return;
        }

        _dragGhostTracking = true;

        AddHandler(DragDrop.DragEnterEvent, OnDragGhostMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragGhostMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDragGhostLeft, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDragGhostDropped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnDragGhostMoved(object? sender, DragEventArgs e)
    {
        if (_dragGhost == null || _dragGhostHost == null)
        {
            return;
        }

        // A drag of our own has a ghost for the whole gesture: the pointer leaving one
        // control for another puts it out, and arriving anywhere puts it back.
        if (IsCloudCardDrag)
        {
            _dragGhost.IsVisible = true;
        }

        if (_dragGhost.IsVisible)
        {
            PlaceDragGhost(e.GetPosition(_dragGhostHost));
        }
    }

    private void OnDragGhostLeft(object? sender, DragEventArgs e)
    {
        if (_dragGhost != null)
        {
            _dragGhost.IsVisible = false;
        }
    }

    private void OnDragGhostDropped(object? sender, DragEventArgs e) => HideDragGhost();

    /// <summary>Start showing what is being dragged, next to the pointer.</summary>
    private void ShowDragGhost(IReadOnlyList<CloudFileViewModel> files, Point at)
    {
        if (files.Count == 0 || !EnsureDragGhost())
        {
            return;
        }

        PaintDragGhost();

        _dragGhostPicture!.IsVisible = true;
        _dragGhostPicture.DataContext = files[0];

        _dragGhostBadge!.IsVisible = files.Count > 1;
        _dragGhostBadgeText!.Text = files.Count.ToString();

        SetDragHint(Strings.MySpace_DragMoveHint);

        _dragGhost!.IsVisible = true;

        // Translated because the pointer was measured against the window while the ghost is
        // laid out on the overlay layer, and the two origins differ by the window's border.
        // Every later position comes straight from the drag events in the layer's own space.
        PlaceDragGhost(this.TranslatePoint(at, _dragGhostHost!) ?? at);
    }

    /// <summary>
    /// A line of text beside the pointer and nothing else.
    /// </summary>
    /// <remarks>
    /// For files coming from Explorer, where Windows is already drawing its own picture of
    /// what is being carried. A second one of ours would be two pictures of the same files;
    /// what is missing there is only the sentence saying which folder they land in.
    /// </remarks>
    private void ShowDragHint(string text)
    {
        if (!EnsureDragGhost())
        {
            return;
        }

        // Only the text changes while the pointer is over the same tile, and DragOver runs
        // dozens of times a second: the rest is done once, on the way in.
        if (!_dragGhost!.IsVisible)
        {
            PaintDragGhost();

            _dragGhostPicture!.IsVisible = false;
            _dragGhostPicture.DataContext = null;
            _dragGhostBadge!.IsVisible = false;
            _dragGhost.IsVisible = true;
        }

        SetDragHint(text);
    }

    private void SetDragHint(string text)
    {
        if (_dragGhostHint != null)
        {
            _dragGhostHint.Text = text;
        }
    }

    private void PlaceDragGhost(Point at)
    {
        if (_dragGhost == null || _dragGhostHost == null)
        {
            return;
        }

        double width = Math.Max(_dragGhost.Bounds.Width, _dragGhost.DesiredSize.Width);
        double height = Math.Max(_dragGhost.Bounds.Height, _dragGhost.DesiredSize.Height);

        double x = Math.Min(at.X + DragGhostOffset, _dragGhostHost.Bounds.Width - width - 4);
        double y = Math.Min(at.Y + DragGhostOffset, _dragGhostHost.Bounds.Height - height - 4);

        Canvas.SetLeft(_dragGhost, Math.Max(4, x));
        Canvas.SetTop(_dragGhost, Math.Max(4, y));
    }

    /// <summary>
    /// Put the ghost away and let go of the picture it was showing.
    /// </summary>
    /// <remarks>
    /// Clearing the data context matters as much as hiding it: leaving it set would leave
    /// one card's decoded bitmap reachable from here for as long as the window lives, which
    /// is the one thing ThumbnailBudget exists to prevent.
    /// </remarks>
    private void HideDragGhost()
    {
        if (_dragGhost == null)
        {
            return;
        }

        _dragGhost.IsVisible = false;

        if (_dragGhostPicture != null)
        {
            _dragGhostPicture.DataContext = null;
        }
    }

    /// <summary>
    /// Take the ghost's colours from the skin in force right now.
    /// </summary>
    /// <remarks>
    /// Resolved each time it appears rather than bound once, which is enough: the ghost is
    /// only ever on screen for the length of one gesture, and nobody changes skin during
    /// one. Fourteen skins times two modes is the reason none of these are literals.
    /// </remarks>
    private void PaintDragGhost()
    {
        Paint("ShareX.Brush.Background.Popup", brush => _dragGhost!.Background = brush);
        Paint("ShareX.Brush.Accent.Start", brush => _dragGhost!.BorderBrush = brush);
        Paint("ShareX.Brush.Control.Background", brush => _dragGhostFrame!.Background = brush);
        Paint("ShareX.Brush.Text.Secondary", brush => _dragGhostGlyph!.Foreground = brush);
        Paint("ShareX.Brush.Text", brush => _dragGhostHint!.Foreground = brush);
        Paint("ShareX.Brush.Accent.Start", brush => _dragGhostBadge!.Background = brush);
        Paint("ShareX.Brush.Accent.Foreground", brush => _dragGhostBadgeText!.Foreground = brush);
    }

    private void Paint(string key, Action<IBrush> apply)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out object? resource) && resource is IBrush brush)
        {
            apply(brush);
        }
    }
}
