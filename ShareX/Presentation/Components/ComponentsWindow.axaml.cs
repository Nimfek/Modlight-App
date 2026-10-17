#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using ShareX.AvaloniaUI.Theming;
using System;

namespace ShareX;

/// <summary>
/// The window a feature opens when the file it needs is not on this PC.
/// </summary>
/// <remarks>
/// It replaces a message box that named a path and offered OK. The difference is not
/// politeness: the old dialog left the person exactly where they were, with a filename and
/// no way to act on it, which is why "ffmpeg does not exist at the following path" has been
/// the end of screen recording for anybody who never read the documentation.
/// </remarks>
public partial class ComponentsWindow : Window
{
    private readonly ComponentsViewModel model;

    public ComponentsWindow() : this(new ComponentsViewModel())
    {
    }

    public ComponentsWindow(ComponentsViewModel model)
    {
        this.model = model;

        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        DataContext = model;

        Closing += OnClosing;
    }

    /// <summary>Whether the component that sent the user here is now installed.</summary>
    public bool RequirementMet => model.RequiredSatisfied;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Closing the window stops what it started.
    /// </summary>
    /// <remarks>
    /// A download that keeps running behind a window that has gone is a gigabyte of somebody
    /// else's connection with nothing on screen to stop it. Nothing is lost by stopping: the
    /// partial file stays in the staging folder and the next attempt continues from where
    /// this one got to.
    /// </remarks>
    private void OnClosing(object? sender, WindowClosingEventArgs e) => model.CancelAll();
}
