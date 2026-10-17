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
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.ComponentModel;

namespace ShareX;

/// <summary>
/// The Server tab: what the administrator can see about the server's storage.
/// </summary>
/// <remarks>
/// <para>
/// Its own control rather than another slab of the main window's layout, the same way
/// the Upload and Download tabs are: the window hands it a visibility flag and it does
/// the rest for itself. Nothing here decides anything - every handler is one line to
/// <see cref="AdminDiskViewModel"/>.
/// </para>
/// <para>
/// The one piece of imperative layout is the stacked bar, and it is imperative on
/// purpose. Its four widths are star weights taken straight from byte counts, and
/// binding <c>ColumnDefinition.Width</c> would rest on a column definition inheriting
/// the data context - which it does not do in every Avalonia version, and fails by
/// silently drawing four equal segments rather than by throwing. A bar that lies about
/// proportion is the exact failure this screen exists to prevent, so the proportion is
/// assigned rather than bound.
/// </para>
/// </remarks>
public partial class AdminView : UserControl
{
    public AdminView()
    {
        InitializeComponent();

        // On the grid, not on this control: the window binds IsVisible on the
        // <local:AdminView/> element itself, and that binding resolves against whatever
        // DataContext the control has. Overriding it here would point that binding at
        // the view model, where the property does not exist, and the tab would sit on
        // top of the others for ever.
        Root.DataContext = Model;
    }

    internal AdminDiskViewModel Model { get; } = new();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        Model.PropertyChanged += OnModelChanged;
        UpdateBar();

        // Deliberately NOT fetched here. This control is constructed with the window and
        // stays in the tree with IsVisible false, so attaching would read the server on
        // every launch for an administrator who never opens the tab - six aggregate
        // queries against the database that serves the live website, to draw nothing.
        // MainWindow calls Show() when the tab is actually selected.
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        Model.PropertyChanged -= OnModelChanged;

        // A request still in the air holds this control, and through it the window. The
        // window is being torn down; the answer has nowhere to land.
        Model.Detach();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => Model.Refresh();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdminDiskViewModel.FreeWeight))
        {
            // The last of the four to be raised, so one handler redraws the whole bar
            // once instead of four times mid-update with a half-applied proportion.
            Dispatcher.UIThread.Post(UpdateBar);
        }
    }

    private void UpdateBar()
    {
        ColumnDefinitions columns = Bar.ColumnDefinitions;

        if (columns.Count < 4)
        {
            return;
        }

        columns[0].Width = Model.LiveWeight;
        columns[1].Width = Model.TrashWeight;
        columns[2].Width = Model.OtherWeight;
        columns[3].Width = Model.FreeWeight;
    }
}
