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
using Avalonia.Interactivity;
using ShareX.HelpersLib;
using System;

namespace ShareX;

/// <summary>
/// The Upload tab's way in for text: "Paste text", beside "Choose files".
/// </summary>
/// <remarks>
/// A partial of its own rather than four more lines in <c>UploadView.axaml.cs</c>. Three
/// people are working in this folder; a new file cannot collide with anything, while the
/// same four lines appended to a shared code-behind collide with whatever else was
/// appended to it.
///
/// The owner's request was "when I go into Send, let there be a paste-text option there,
/// to pick syntax and so on" - so the action lives in the header of this tab, next to the
/// file picker, and everything it collects is <see cref="PasteWindow"/>'s business.
/// </remarks>
public partial class UploadView
{
    /// <summary>
    /// Open the paste window over the main window.
    /// </summary>
    /// <remarks>
    /// A modal needs an owner, and this control does not know its window at compile time -
    /// it is hosted in one, and in the designer it is hosted in nothing. If there is no
    /// window above it there is nothing to be modal to, and doing nothing is better than
    /// an unparented dialog that can end up behind the app.
    ///
    /// The catch is here because this is an <c>async void</c> handler: an exception
    /// escaping one of those is not caught by anything and takes the process with it.
    /// </remarks>
    private async void OnPasteTextClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await PasteWindow.ShowAsync(owner);
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }
}
