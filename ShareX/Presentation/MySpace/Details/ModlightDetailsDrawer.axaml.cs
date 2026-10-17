#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ShareX;

/// <summary>
/// The right-hand details drawer, as a control.
/// </summary>
/// <remarks>
/// Nothing here decides anything. Every handler is one line into
/// <see cref="ModlightDetailsModel"/>, which is where the rules are, and the window is
/// what actually performs an edit - so a change made from the drawer goes through the
/// same request, the same confirmation and the same toast as the same change made from
/// the context menu. Two routes to one behaviour, not two behaviours.
///
/// Its own control rather than another slab of the main window's layout, for the reason
/// the Upload tab states at its own: that window is being edited by several hands at
/// once, and a control that keeps its markup, its styles and its handlers in one folder
/// cannot be broken by, or break, anything happening in there.
/// </remarks>
public partial class ModlightDetailsDrawer : UserControl
{
    public ModlightDetailsDrawer()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The drawer's state, which the window owns.
    /// </summary>
    /// <remarks>
    /// Read off the DataContext rather than created here, because the window has to be
    /// able to open the drawer from a context menu - and a model this control made for
    /// itself would be one nothing else can reach.
    /// </remarks>
    private ModlightDetailsModel? Model => DataContext as ModlightDetailsModel;

    private void OnPreviousClick(object? sender, RoutedEventArgs e) => Model?.Previous();

    private void OnNextClick(object? sender, RoutedEventArgs e) => Model?.Next();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Model?.Close();

    private void OnCopyLinkClick(object? sender, RoutedEventArgs e) => Model?.CopyPrimaryLink();

    private void OnOpenClick(object? sender, RoutedEventArgs e) => Model?.OpenInBrowser();

    private void OnDownloadClick(object? sender, RoutedEventArgs e) => Model?.Download();

    private void OnCopyRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DetailsLink row })
        {
            Model?.CopyRow(row);
        }
    }

    /// <summary>
    /// The folder list, anchored on the button that asked for it.
    /// </summary>
    /// <remarks>
    /// The button is handed along because the menu has to open UNDER it. The window
    /// builds the entries - they are the same ones the card's own context menu offers,
    /// including the tick beside the folder the file is already in.
    /// </remarks>
    private void OnChangeFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button anchor)
        {
            Model?.ChangeFolder(anchor);
        }
    }

    private void OnVisibilityClick(object? sender, RoutedEventArgs e) => Model?.ToggleVisibility();

    private void OnLinkSettingsClick(object? sender, RoutedEventArgs e) => Model?.EditLinkSettings();

    private void OnTrashClick(object? sender, RoutedEventArgs e) => Model?.Trash();

    private void OnRestoreClick(object? sender, RoutedEventArgs e) => Model?.Restore();

    private void OnDeleteForeverClick(object? sender, RoutedEventArgs e) => Model?.DeleteForever();

    /// <summary>
    /// The arrows and Escape, but only while the focus is inside the drawer.
    /// </summary>
    /// <remarks>
    /// Handled here rather than on the window on purpose. The grid answers the same keys
    /// for its own reasons - Escape clears the selection there, and clearing the
    /// selection must go on working while the drawer is open - so these are taken only
    /// when the person is actually in the drawer, and marked handled so they do not then
    /// also reach the grid behind it.
    /// </remarks>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        ModlightDetailsModel? model = Model;

        if (model == null)
        {
            return;
        }

        // Somebody selecting the URL out of a fact would otherwise page the drawer.
        if (e.Source is TextBox || e.Source is SelectableTextBlock)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                model.Previous();
                e.Handled = true;
                break;

            case Key.Right:
                model.Next();
                e.Handled = true;
                break;

            case Key.Escape:
                model.Close();
                e.Handled = true;
                break;
        }
    }
}
