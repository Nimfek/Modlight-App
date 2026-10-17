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
using System;
using System.Runtime.InteropServices;

namespace ShareX;

/// <summary>
/// Gives the main window back the one thing a hand-drawn title bar silently costs: the
/// window menu.
/// </summary>
/// <remarks>
/// A window with its own title bar asks Avalonia for BorderOnly decorations, and on Win32
/// that means the window is created without WS_CAPTION - correct, that is the system title
/// bar we are replacing - but also without WS_SYSMENU, which is a different thing wearing
/// the same word. WS_SYSMENU is not a drawn control; it is the window's menu, and losing
/// it takes away the right-click menu on the title bar, Alt+Space, and the Restore / Move
/// / Size / Minimize / Maximize / Close entries on the taskbar button. Measured, not
/// assumed: GetSystemMenu returned NULL on the window before this ran.
///
/// The bit is put back by hand because Avalonia has no property for it. Nothing is drawn
/// as a result - Windows only paints a menu box when there is a caption to paint it on,
/// and there is none - and the frame is left otherwise exactly as Avalonia set it.
///
/// Re-asserted rather than set once: the backend rewrites the window style when the
/// decorations or the resize policy change, and a bit that has to be there is cheaper to
/// check than to debug.
/// </remarks>
internal static class WindowSystemMenu
{
    private const int GWL_STYLE = -16;
    private const int WS_SYSMENU = 0x00080000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>Returns whether the window ended up with a window menu.</summary>
    public static bool Ensure(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not IntPtr handle || handle == IntPtr.Zero)
        {
            return false;
        }

        int style = GetWindowLong(handle, GWL_STYLE);
        if (style == 0)
        {
            return false;
        }

        if ((style & WS_SYSMENU) == WS_SYSMENU)
        {
            return true;
        }

        SetWindowLong(handle, GWL_STYLE, style | WS_SYSMENU);

        // Read it back rather than trusting the return value: SetWindowLong answers with
        // the PREVIOUS style, and a previous style of zero is indistinguishable from a
        // failure without going through GetLastError for a call that is allowed to fail.
        if ((GetWindowLong(handle, GWL_STYLE) & WS_SYSMENU) != WS_SYSMENU)
        {
            return false;
        }

        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        return true;
    }

    // The 32-bit entry points on purpose: GWL_STYLE is a 32-bit field on every
    // architecture, and GetWindowLongPtrW is not exported at all in a 32-bit process.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
        int cx, int cy, uint flags);
}
