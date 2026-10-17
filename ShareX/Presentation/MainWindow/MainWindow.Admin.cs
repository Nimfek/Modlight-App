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

using Avalonia.Threading;
using ShareX.HelpersLib;
using System;

namespace ShareX;

/// <summary>
/// The Server tab, and the rule that it is only there for an administrator.
/// </summary>
/// <remarks>
/// <para>
/// A partial of the main window rather than more lines in its code-behind: that file is
/// 1500 lines with several hands in it, and everything the administration surface needs
/// fits in one place that nobody else is editing.
/// </para>
/// <para>
/// ADVISORY, NOT A PERMISSION. <see cref="ModlightAdmin"/> only answers whether the
/// server said this account administers it, and the sole use for that answer is
/// deciding whether to draw a door. The server checks again on every request behind it -
/// a build that got this wrong would show an administration screen full of refusals,
/// not an administration screen that works.
/// </para>
/// <para>
/// The answer is not persisted and starts false on every launch, so the tab appears a
/// moment after the first <c>GET /api/me</c> rather than instantly. That is the right
/// way round: the alternative is a saved "this person is an administrator" outliving the
/// role by months, which is the trap this project has already fallen into three times.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Whether the control the tab shows exists yet.
    /// </summary>
    /// <remarks>
    /// <see cref="SelectedTab"/> has a setter that reaches in here, and the window sets
    /// fields before <c>InitializeComponent</c> has run - so without this, one reordered
    /// line in a 1500-line constructor turns into a null reference on a field the XAML
    /// compiler generates. Set by <see cref="HookAdminVisibility"/>, which runs after
    /// the tree exists and is the only caller.
    /// </remarks>
    private bool _adminReady;

    /// <summary>Whether the Server tab is offered at all.</summary>
    public bool IsAdminVisible => ModlightAdmin.IsAdmin;

    public bool IsAdminSelected => _selectedTab == MainWindowTab.Admin;

    /// <summary>
    /// Start watching the role.
    /// </summary>
    /// <remarks>
    /// The raise afterwards is not redundant. The account strip asks the server who it
    /// is as soon as the window is built, and that request can finish between the
    /// bindings being created and this subscription existing - in which case the tab
    /// button has already read "false" and nothing would ever tell it otherwise.
    /// </remarks>
    private void HookAdminVisibility()
    {
        _adminReady = true;
        ModlightAdmin.Changed += OnAdminRoleChanged;
        OnPropertyChanged(nameof(IsAdminVisible));
    }

    private void UnhookAdminVisibility()
    {
        ModlightAdmin.Changed -= OnAdminRoleChanged;
    }

    /// <summary>
    /// The server has said something new about who is signed in.
    /// </summary>
    /// <remarks>
    /// Raised on whichever worker thread finished the request, so it is posted across
    /// rather than acted on: everything it touches is a binding.
    /// </remarks>
    private void OnAdminRoleChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ApplyAdminRole);
    }

    private void ApplyAdminRole()
    {
        if (_disposed || !_adminReady)
        {
            return;
        }

        try
        {
            OnPropertyChanged(nameof(IsAdminVisible));

            if (ModlightAdmin.IsAdmin)
            {
                if (IsAdminSelected)
                {
                    // Signed in as a different administrator while the tab was open: the
                    // numbers on screen are the other server's, or the other account's.
                    AdminTab.Model.Refresh();
                }

                return;
            }

            // The role is gone - signed out, signed in as somebody else, or an
            // administrator demoted on the website. Drop the report rather than leave one
            // install's totals sitting under a tab that is about to disappear: "hidden"
            // is a state of the UI, and data left lying around is what the next bug shows.
            AdminTab.Model.Forget();

            if (IsAdminSelected)
            {
                // Hiding the button is not enough. The window would still be showing the
                // tab it was on, with no way back to it and no way off it.
                SelectedTab = MainWindowTab.MySpace;
            }
        }
        catch (Exception exception)
        {
            // This runs from a static event on a background thread's continuation. A
            // throw here would take down the request that was only reporting a fact on
            // its way past.
            DebugHelper.WriteException(exception);
        }
    }

    /// <summary>
    /// The selected tab changed; the Server tab may now be the one on screen.
    /// </summary>
    /// <remarks>
    /// Fetching happens HERE rather than when the control is attached. The control is
    /// built with the window and stays in the tree with <c>IsVisible</c> false, so
    /// attaching would read the server on every launch for an administrator who never
    /// opens the tab - six aggregate queries against the database that serves the live
    /// website, to draw nothing at all. <see cref="AdminDiskViewModel.Show"/> is also
    /// what decides a second visit needs no request.
    /// </remarks>
    private void NoteAdminTabChanged()
    {
        OnPropertyChanged(nameof(IsAdminSelected));

        if (_adminReady && IsAdminSelected)
        {
            AdminTab.Model.Show();
        }
    }
}
