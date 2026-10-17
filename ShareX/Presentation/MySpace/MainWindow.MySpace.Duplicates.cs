#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Interactivity;
using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The way into the duplicate scan, which lives in the trash.
/// </summary>
/// <remarks>
/// Its own file rather than more of the four this tab already occupies, and its own
/// button rather than an entry in a card's context menu: the scan is about the LIBRARY,
/// not about a file, so hanging it off a right-click on one file would be an action whose
/// target is not the thing it was invoked on.
///
/// It sits in the trash because that is where the owner asked for it, and the placement
/// argues for itself: the trash is the screen for getting rid of things, and everything
/// the scan proposes lands in the trash rather than being destroyed - so the result of
/// the scan and the place it was started from are the same place. The button shares the
/// strip with "Empty trash" and is deliberately NOT adjacent to it: one of them proposes
/// a reversible move and the other empties the bin for good.
/// </remarks>
public partial class MainWindow
{
    private bool _scanningDuplicates;

    /// <summary>False while the window is open, so a second one cannot be started behind it.</summary>
    public bool CanScanDuplicates => !_scanningDuplicates;

    private void OnScanDuplicatesClick(object? sender, RoutedEventArgs e) => _ = ScanDuplicatesAsync();

    private async Task ScanDuplicatesAsync()
    {
        if (_scanningDuplicates)
        {
            return;
        }

        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        _scanningDuplicates = true;
        OnPropertyChanged(nameof(CanScanDuplicates));

        try
        {
            int trashed = await ModlightDuplicatesWindow.ShowAsync(this, account);

            if (trashed == 0)
            {
                // Opened and closed, or nothing was ticked. Reloading the grid and
                // refreshing the account strip for that would be two requests to redraw
                // exactly what is already on screen.
                return;
            }

            // A reload rather than taking cards off one at a time: what just moved is
            // spread across the whole library, including pages this window never loaded.
            MySpace.Reload();
            Account.Refresh();

            Notice(LucideIcons.copy, string.Format(CultureInfo.CurrentCulture,
                Strings.MySpace_DuplicatesTrashed, trashed));
        }
        catch (Exception exception)
        {
            ReportFailure(exception, account.Host);
        }
        finally
        {
            _scanningDuplicates = false;
            OnPropertyChanged(nameof(CanScanDuplicates));
        }
    }
}
