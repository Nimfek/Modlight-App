#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ShareX.AvaloniaUI;
using ShareX.HelpersLib;
using ShareX.Localization;

namespace ShareX;

/// <summary>The list of external components, with the buttons that act on one row.</summary>
public partial class ComponentsView : UserControl
{
    public ComponentsView()
    {
        InitializeComponent();
    }

    private ComponentsViewModel? Model => DataContext as ComponentsViewModel;

    /// <summary>
    /// Read what is installed and, if it is time, ask what has been published.
    /// </summary>
    /// <remarks>
    /// Here rather than in the view model's constructor because both of those things are
    /// slow in ways a constructor must not be: reading a version starts a process, and the
    /// check makes a network request. Neither is awaited by anything on screen - the lines
    /// they fill in appear when they appear.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        ComponentsViewModel? model = Model;

        if (model != null)
        {
            _ = model.StartAsync();
        }
    }

    private static ComponentRowViewModel? Row(object? sender) =>
        (sender as Control)?.DataContext as ComponentRowViewModel;

    private void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        ComponentRowViewModel? row = Row(sender);

        if (row == null)
        {
            return;
        }

        // Ticking is what the button means: somebody who presses Install on a component they
        // had unticked has just changed their mind, and finding it unticked again next time
        // would be the settings page disagreeing with what they did.
        row.IsWanted = true;
        _ = row.InstallAsync();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Row(sender)?.Cancel();

    /// <summary>
    /// Fetch the newest published build of this component.
    /// </summary>
    /// <remarks>
    /// Not asked for confirmation, unlike Remove. Nothing is lost by it: the file being
    /// replaced is one this code fetched, the replacement is refused unless the project's
    /// own published checksum matches, and the old build is only unlinked once the new one
    /// is in place.
    /// </remarks>
    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        ComponentRowViewModel? row = Row(sender);

        if (row != null)
        {
            _ = row.UpdateAsync();
        }
    }

    private void OnCheckNowClick(object? sender, RoutedEventArgs e)
    {
        ComponentsViewModel? model = Model;

        if (model != null)
        {
            _ = model.CheckNowAsync();
        }
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        ComponentRowViewModel? row = Row(sender);

        if (row == null)
        {
            return;
        }

        // Asked, because the largest of these is a gigabyte and the answer to "do it again"
        // is a gigabyte of somebody's connection.
        string question = Helpers.SafeStringFormat(Strings.Components_RemoveConfirm,
            row.Name, ModlightComponents.FolderFor(row.Component));

        if (MessageBox.Show(question, Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        row.Remove();
    }

    private void OnInstallMissingClick(object? sender, RoutedEventArgs e)
    {
        ComponentsViewModel? model = Model;

        if (model != null)
        {
            _ = model.InstallMissingAsync();
        }
    }
}
