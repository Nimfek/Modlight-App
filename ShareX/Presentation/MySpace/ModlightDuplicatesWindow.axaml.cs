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
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System.Globalization;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// Files stored more than once, and what to do about them.
/// </summary>
/// <remarks>
/// Reached from the TRASH, which is where the owner asked for it and where it belongs:
/// this is the screen for getting rid of things, and everything it proposes lands in the
/// trash rather than being destroyed, so the two are one gesture apart.
///
/// Sets are matched by sha256 and by nothing else - see ModlightDuplicates.cs for why a
/// name is not evidence. Rows with no checksum recorded are excluded by the server and
/// counted separately; on a server whose library was imported that is most
/// files, and treating "no checksum" as a value they share would offer to delete
/// nearly the whole library.
///
/// A set the owner keeps is remembered ON THE SERVER, by checksum, so the same decision
/// holds in the web panel and survives one copy being trashed or the same bytes being
/// uploaded again. That is the difference between a feature and a nag.
/// </remarks>
public partial class ModlightDuplicatesWindow : Window
{
    private DuplicatesViewModel? model;

    public ModlightDuplicatesWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
    }

    /// <summary>
    /// Scan, show what was found, and answer with how many files were trashed.
    /// </summary>
    /// <remarks>
    /// The count is the answer rather than a bool because the caller has two things to do
    /// with it: reload the library only when something actually moved, and say how much
    /// moved. Zero means the window was opened and closed, which needs neither.
    /// </remarks>
    internal static async Task<int> ShowAsync(Window owner, ModlightAccount account)
    {
        ModlightDuplicatesWindow window = new()
        {
            Title = Strings.MySpace_DuplicatesTitle
        };

        DuplicatesViewModel model = new(account);
        window.model = model;
        window.DataContext = model;

        // Started when the window opens rather than before it: a scan on a large library
        // takes a moment, and a caller left staring at nothing would press the button
        // again. The progress bar is already on screen when this begins.
        window.Opened += (_, _) => _ = model.ScanAsync();

        await window.ShowDialog(owner);

        return model.Trashed;
    }

    private void OnSelectExtrasClick(object? sender, RoutedEventArgs e) => model?.SelectExtras();

    private void OnClearClick(object? sender, RoutedEventArgs e) => model?.ClearSelection();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Wave a set off, or start counting it again.</summary>
    private void OnKeepClick(object? sender, RoutedEventArgs e)
    {
        // The group comes from the button's own DataContext rather than from a selected
        // item: this list has no selection of its own, and the tick boxes mean something
        // else entirely.
        if (model != null && sender is Control { DataContext: DuplicateGroupViewModel group })
        {
            _ = model.ToggleKeepAsync(group);
        }
    }

    /// <summary>
    /// Move every ticked copy to the trash, having said how many and how much.
    /// </summary>
    /// <remarks>
    /// Confirmed, and the question names the numbers. "Are you sure?" is the sentence
    /// people click through; "move 37 copies (4.1 GB) to the trash?" is one they read.
    /// It also says the files can be restored, because they can - and a warning harsher
    /// than the truth teaches people to ignore the next one.
    ///
    /// The window stays open afterwards. What was trashed simply leaves the list on the
    /// next scan, and closing on success would take away the chance to deal with the sets
    /// underneath it.
    /// </remarks>
    private void OnTrashClick(object? sender, RoutedEventArgs e) => _ = TrashAsync();

    private async Task TrashAsync()
    {
        if (model == null || !model.CanTrash)
        {
            return;
        }

        string question = string.Format(CultureInfo.CurrentCulture,
            Strings.MySpace_DuplicatesConfirm,
            model.SelectedCount,
            ModlightFormat.CloudSize(model.SelectedBytes));

        // The same question the rest of My space asks, defaulting to No.
        if (AvaloniaUI.MessageBox.Show(this, question,
                Program.Title + " - " + Strings.MySpace_DuplicatesTitle,
                AvaloniaUI.MessageBoxButtons.YesNo, AvaloniaUI.MessageBoxIcon.Warning,
                AvaloniaUI.MessageBoxDefaultButton.Button2) != AvaloniaUI.DialogResult.Yes)
        {
            return;
        }

        if (await model.TrashSelectedAsync() > 0)
        {
            // Re-read rather than crossed off locally: trashing a copy can empty a set,
            // and it always changes the totals in the header. Redrawing from the server
            // is one request and cannot drift.
            await model.ScanAsync(keepStatus: true);
        }
    }
}
