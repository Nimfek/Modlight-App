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

namespace ShareX;

/// <summary>
/// The places the main window can be.
/// </summary>
/// <remarks>
/// Declaration order is the order they appear in the tab strip, and the first one is
/// what the window opens on. Both of those are the owner's, not ours: he asked for
/// "My space, Upload, Download, Create and Tools" along the top, with his own files
/// in front of him when the window opens.
///
/// <see cref="Admin"/> is last and is the only one that is not always there: it exists
/// for an account the server says administers it, and for nobody else. See
/// MainWindow.Admin.cs - the tab is a door, not a permission.
/// </remarks>
internal enum MainWindowTab
{
    MySpace,
    Upload,
    Download,
    Create,
    Tools,
    Admin
}
