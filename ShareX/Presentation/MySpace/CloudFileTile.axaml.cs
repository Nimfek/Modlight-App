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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ShareX;

/// <summary>
/// What one file of the account's looks like in the My space grid.
/// </summary>
/// <remarks>
/// The card used to show a name, and a size and date that appeared only while the
/// pointer was on it, in place of the name. The owner's words were that the app "only
/// shows the file name - it is irritating", and he was right about the cause as well as
/// the symptom: everything else the card knew was one hover away, so on a screen of
/// forty tiles it was not there at all. This prints what the web panel's card prints -
/// size, how long ago, how many views, which folder - and adds the two things the panel
/// has that the app did not: a visible handle for the menu, and a type mark for files
/// that have no picture.
///
/// Deliberately dumb. Every string and every colour is decided in
/// <see cref="CloudFileViewModel"/>; the one piece of behaviour here is the menu button,
/// and that only forwards to the window, which owns the menu and the selection it acts
/// on. The pointer handling - selecting, dragging, double-click to open, right-click for
/// the same menu - stays on the Border in the main window's layout, one level above this,
/// because it is the grid's business rather than the tile's.
/// </remarks>
public partial class CloudFileTile : UserControl
{
    /// <summary>
    /// The sheet with a folded corner that the type mark is drawn on.
    /// </summary>
    /// <remarks>
    /// Parsed once for the whole process rather than declared in the control's resources,
    /// which are per instance: a folder of six hundred files would otherwise parse this
    /// path six hundred times. The coordinates are the panel's SVG viewBox exactly (64x80,
    /// components/dashboard/Thumbnail.tsx), so the two marks are the same shape.
    ///
    /// One geometry, drawn twice - filled with the family colour, then stroked for the rim.
    /// A Path with Stretch None leaves it in these coordinates, which is what lets the
    /// Canvas around it clip the rim the way the viewBox does.
    /// </remarks>
    public static readonly Geometry SheetGeometry =
        Geometry.Parse("M8 0h32l24 24v48a8 8 0 0 1-8 8H8a8 8 0 0 1-8-8V8a8 8 0 0 1 8-8z");

    /// <summary>The turned-back corner, laid over the sheet as a white wash.</summary>
    public static readonly Geometry FoldGeometry =
        Geometry.Parse("M40 0l24 24H48a8 8 0 0 1-8-8V0z");

    public CloudFileTile()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Open the file menu from the handle in the card's corner.
    /// </summary>
    /// <remarks>
    /// Straight to the window, which is where the menu is built and where the selection
    /// it operates on lives. Nothing is needed to stop this press from also selecting the
    /// card or starting a drag of it: Avalonia's Button marks PointerPressed handled, so
    /// the grid's handlers on the Border above never see it. That is the behaviour we
    /// want - a menu opened from a card that is part of a selection of five must be about
    /// all five, and a press that collapsed the selection first would make it about one.
    /// </remarks>
    private void OnMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control handle && DataContext is CloudFileViewModel file)
        {
            this.FindAncestorOfType<MainWindow>()?.OpenCloudFileMenu(file, handle);
        }
    }

    /// <summary>
    /// Stop a tap on the handle from also counting as a tap on the card.
    /// </summary>
    /// <remarks>
    /// The one gesture the button does NOT swallow on its own. Tapped and DoubleTapped
    /// are synthesised after the pointer route has finished and are raised at the element
    /// under the pointer, so they bubble past the button to the card - where a single tap
    /// opens the details panel and a double tap opens the file. Without this, asking for
    /// the menu would also do one of those, behind the menu that just opened.
    /// </remarks>
    private void OnMenuGesture(object? sender, TappedEventArgs e) => e.Handled = true;
}
