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
using ShareX.UploadersLib.FileUploaders;

namespace ShareX;

/// <summary>
/// The account group of the settings window: who the files go as, where they land.
/// </summary>
/// <remarks>
/// <para>
/// Reached by clicking the account chip in the main window, which is where the owner
/// asked for it: "wolę żeby takie bezpośrednie ustawienia były klikając na mój profil
/// w aplikacji". The gear reaches the same window from the other end of the list.
/// </para>
/// <para>
/// The buttons that leave for the website are not a placeholder for work not done. The
/// panel changes these preferences through Server Actions - a browser-only mechanism -
/// and the HTTP surface the app can reach (<c>GET /api/me</c>) is read-only, so the
/// honest choice is to show the real value and a door to the one place that can change
/// it. See <see cref="AccountSettingsViewModel"/> for which fields those are.
/// </para>
/// <para>
/// The password is deliberately the same: it is Better Auth's, it can be guarded by
/// two-factor authentication and it can be rate limited, so a form here would have to
/// re-implement the challenge flow in order to fail in more ways than a browser does.
/// </para>
/// </remarks>
public partial class AccountSettingsView : UserControl
{
    private AccountSettingsViewModel ViewModel => (AccountSettingsViewModel)DataContext!;

    public AccountSettingsView()
    {
        InitializeComponent();
        DataContext = new AccountSettingsViewModel();

        // The one setting on these pages the app itself owns, moved here out of task
        // settings. Bound to the DEFAULT task settings, which is what every capture and
        // every drag-and-drop upload uses unless a hotkey overrides it.
        FolderSlot.Content = ModlightFolderPicker.Create(Program.DefaultTaskSettings.UploadSettings);
    }

    /// <summary>Show one page and hide the rest. The shell's section list decides which.</summary>
    public void SelectPage(string pageId) => ViewModel.SelectPage(pageId);

    /// <summary>Called by the shell when the settings window closes.</summary>
    public void Release() => ViewModel.Dispose();

    private void OnSignInClick(object? sender, RoutedEventArgs e) => SignInWindowIntegration.Show();

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => ViewModel.Refresh();

    private void OnOpenProfileClick(object? sender, RoutedEventArgs e) => OpenOnServer("/dashboard/profile");

    /// <summary>
    /// The panel's profile page, which is where the password is changed.
    /// </summary>
    /// <remarks>
    /// The same address as the button above it on purpose: the security card is part of
    /// that page, and sending people to a fragment that may be renamed would be a link
    /// that quietly stops landing anywhere.
    /// </remarks>
    private void OnChangePasswordClick(object? sender, RoutedEventArgs e) => OpenOnServer("/dashboard/profile");

    private void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        ModlightSignIn.SignOut();
        ViewModel.Refresh();
    }

    /// <summary>Open a page on the server this app is signed in to, and nowhere else.</summary>
    private static void OpenOnServer(string path)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null || string.IsNullOrEmpty(account.Host))
        {
            SignInWindowIntegration.Show();
            return;
        }

        URLHelpers.OpenURL(account.Host.TrimEnd('/') + path);
    }
}
