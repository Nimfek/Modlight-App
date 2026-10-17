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
using Avalonia.Controls.Primitives;

namespace ShareX;

/// <summary>
/// The one thing a file card asks the window to do for it.
/// </summary>
/// <remarks>
/// Its own file, and a partial rather than another method in MainWindow.MySpace.cs,
/// because that file belongs to the grid's gestures and this belongs to the tile - and
/// because several people are editing this window at once, so a new file is a change
/// that cannot collide with theirs.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Open the file menu from the handle in a card's corner.
    /// </summary>
    /// <remarks>
    /// The same menu the right button opens, built the same way, and it treats the
    /// selection the same way Explorer does: a card that is not part of the current
    /// selection replaces it, so the menu is about the thing you pointed at rather than
    /// about something selected elsewhere and forgotten. A card that IS part of a
    /// selection leaves it alone, which is what makes the handle usable on a group.
    ///
    /// Anchored under the handle and aligned to its right edge, not at the pointer: this
    /// one was opened from a control, and a menu that appears somewhere other than the
    /// control that opened it looks like it belongs to something else.
    /// </remarks>
    internal void OpenCloudFileMenu(CloudFileViewModel file, Control handle)
    {
        // A menu already up has to go before the next one is asked for; the opener
        // refuses while one is open, and the second click would do nothing.
        CloseActiveContextMenu();

        if (!file.IsSelected)
        {
            MySpace.SelectFile(file);
        }

        ContextMenu menu = BuildContextMenu(BuildCloudFileMenu(file));
        TryOpenContextMenu(menu, handle, PlacementMode.BottomEdgeAlignedRight);
    }
}
