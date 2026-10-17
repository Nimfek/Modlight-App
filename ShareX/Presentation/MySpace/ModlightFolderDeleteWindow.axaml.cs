#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using System;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>What the person chose to do with a folder.</summary>
internal enum FolderDeleteChoice
{
    /// <summary>Nothing. The window was dismissed.</summary>
    Cancel,

    /// <summary>The folder alone: its files fall back to "no folder", its children move up.</summary>
    KeepContents,

    /// <summary>The folder, its subfolders and every file inside, into the trash.</summary>
    WithContents
}

/// <summary>
/// Deleting a folder, with the destructive reading kept out of reach.
/// </summary>
/// <remarks>
/// Two things make the second path hard to take by accident, and both are deliberate.
///
/// It is a SEPARATE BUTTON rather than a checkbox on a shared one. A checkbox makes
/// "remove this folder" and "put its entire contents in the bin" the same click with a
/// different tick, and the tick is precisely what a person in a hurry does not read.
///
/// It is DISABLED for the first five seconds, with the remaining count on its face. The
/// delay is skipped for a folder holding nothing, because arming a button that cannot
/// destroy anything only teaches people to wait through the arming.
///
/// The safe path is listed first and carries the default, so Enter takes it. Escape
/// takes neither. This mirrors the web panel's own dialog down to the wording, which is
/// worth keeping identical: the same decision should not read differently depending on
/// which of the two surfaces somebody happens to be standing in front of.
/// </remarks>
public partial class ModlightFolderDeleteWindow : Window
{
    /// <summary>How long the destructive button stays out of reach.</summary>
    private static readonly TimeSpan ArmingDelay = TimeSpan.FromSeconds(5);

    private DispatcherTimer? countdown;
    private int secondsLeft;

    public ModlightFolderDeleteWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        Closed += (_, _) => StopCountdown();
    }

    private FolderDeleteChoice Choice { get; set; } = FolderDeleteChoice.Cancel;

    /// <summary>
    /// Ask what should happen to this folder.
    /// </summary>
    /// <param name="fileCount">Files in the whole subtree, which is what the purge would take.</param>
    /// <param name="subfolderCount">Folders nested below at any depth.</param>
    internal static async Task<FolderDeleteChoice> AskAsync(Window owner, string name, int fileCount,
        int subfolderCount)
    {
        ModlightFolderDeleteWindow window = new()
        {
            Title = Strings.MySpace_DeleteFolderTitle
        };

        window.Summary.Text = string.Format(Strings.MySpace_DeleteFolderSummary, name, fileCount, subfolderCount);

        // Nothing inside means nothing to lose, so the wait would only be ceremony - and
        // ceremony that fires on every empty folder is how people learn to sit through it
        // on the one that is full.
        if (fileCount == 0 && subfolderCount == 0)
        {
            window.PurgeButton.Content = Strings.MySpace_DeleteFolderPurgeAction;
            window.PurgeButton.IsEnabled = true;
        }
        else
        {
            window.StartCountdown();
        }

        await window.ShowDialog(owner);
        return window.Choice;
    }

    private void StartCountdown()
    {
        secondsLeft = (int)ArmingDelay.TotalSeconds;
        PurgeButton.Content = string.Format(Strings.MySpace_DeleteFolderArming, secondsLeft);

        countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        countdown.Tick += (_, _) =>
        {
            secondsLeft--;

            if (secondsLeft > 0)
            {
                PurgeButton.Content = string.Format(Strings.MySpace_DeleteFolderArming, secondsLeft);
                return;
            }

            StopCountdown();
            PurgeButton.Content = Strings.MySpace_DeleteFolderPurgeAction;
            PurgeButton.IsEnabled = true;
        };

        countdown.Start();
    }

    /// <summary>Stopped on close as well as on arming: a timer outliving its window keeps it alive.</summary>
    private void StopCountdown()
    {
        countdown?.Stop();
        countdown = null;
    }

    private void OnKeepClick(object? sender, RoutedEventArgs e)
    {
        Choice = FolderDeleteChoice.KeepContents;
        Close();
    }

    private void OnPurgeClick(object? sender, RoutedEventArgs e)
    {
        Choice = FolderDeleteChoice.WithContents;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
