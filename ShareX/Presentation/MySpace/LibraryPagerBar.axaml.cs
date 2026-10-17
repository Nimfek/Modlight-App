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
/// The bar under the file grid: which page of the library is on screen, how big a page
/// is, and the way to another one.
/// </summary>
/// <remarks>
/// Its own control rather than another two hundred lines inside MainWindow.axaml, which
/// five people are editing at once. It appears there as ONE element with its DataContext
/// pointed at <see cref="LibraryPagerViewModel"/>, so everything about the shape of the
/// bar is in this pair of files and everything about what it means is in that view model.
///
/// Click handlers only. Nothing here decides anything.
/// </remarks>
public partial class LibraryPagerBar : UserControl
{
    public LibraryPagerBar()
    {
        InitializeComponent();
    }

    private LibraryPagerViewModel? Model => DataContext as LibraryPagerViewModel;

    private void OnPreviousClick(object? sender, RoutedEventArgs e) => Model?.Previous();

    private void OnNextClick(object? sender, RoutedEventArgs e) => Model?.Next();

    private void OnStepClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.Tag is LibraryPageStep step && step.IsPage)
        {
            Model?.GoTo(step.Page);
        }
    }

    /// <summary>
    /// Enter takes the typed page. Escape gives up on it.
    /// </summary>
    /// <remarks>
    /// Handled on the box rather than by making it the window's default button: this box
    /// sits inside a tab that has other things Enter means, and a default button would
    /// take the key whether or not anybody was typing a page number.
    /// </remarks>
    private void OnJumpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Model?.Jump();
            return;
        }

        if (e.Key == Key.Escape && Model != null)
        {
            e.Handled = true;
            Model.JumpText = "";
        }
    }
}
