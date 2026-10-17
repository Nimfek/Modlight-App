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

using System.Threading;

namespace ShareX;

/// <summary>
/// Where an upload goes when nobody said otherwise: the folder My space is standing in.
/// </summary>
/// <remarks>
/// One ambient answer for the whole app, because the alternative was one answer per
/// entry point and that is exactly how the bug this fixes came about. The folder tile
/// and the "Upload here" button aimed their uploads explicitly and landed correctly;
/// dropping a file on the window, pasting from the clipboard, using the tray or taking
/// a capture never named a folder at all, so every one of them went to the account's
/// default while the window plainly said the user was inside "CV".
///
/// The rule is deliberately one sentence: <b>if My space is showing a folder, that is
/// where new uploads go</b>. Not a preference hidden in a settings page - the folder is
/// named in the breadcrumb across the top of the tab, it is remembered between runs the
/// same way the tab is, and the toast after every upload says which folder took the
/// file. Anything more conditional ("only while the window is visible", "only while that
/// tab is selected") would make the same gesture do two different things for reasons the
/// user cannot see: a capture hides the main window before it fires, and the Capture tab
/// is by definition not the My space tab.
///
/// Null means "expressed no preference", which the server answers with the ACCOUNT'S own
/// default folder - the behaviour every ShareX upload has always had. It is what this
/// holds while the library is showing everything, and while nobody is signed in.
///
/// Read on a task's worker thread and written on the UI thread, hence the volatile
/// access. A string reference is written atomically anyway; the barrier is there so a
/// task started immediately after a folder change cannot read the previous value.
/// </remarks>
public static class ModlightUploadTarget
{
    private static string? folderId;

    /// <summary>The folder new uploads land in, or null for the account's default.</summary>
    public static string? Current => Volatile.Read(ref folderId);

    /// <summary>Say where uploads go from now on. Empty and null both mean "the account decides".</summary>
    public static void Set(string? id) =>
        Volatile.Write(ref folderId, string.IsNullOrEmpty(id) ? null : id);
}
