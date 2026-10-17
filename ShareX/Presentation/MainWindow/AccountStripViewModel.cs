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

using ShareX.HelpersLib;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The right-hand end of the tab strip: which account the files go to, and how full it is.
/// </summary>
/// <remarks>
/// Those two questions sit together because they are one question - "where does this
/// land, and is there room" - and the answer to both arrives in the same GET /api/me.
///
/// Nothing here is cached across runs. <see cref="ModlightAccount"/> deliberately
/// stores no quota ("a stale number on screen is worse than no number"), so the
/// indicator is empty until the server answers, and empty again the moment the
/// account is signed out. That is the point: zeros would read as "your library is
/// empty", which is a different and untrue statement.
/// </remarks>
internal sealed class AccountStripViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// How long a reading is trusted before another call is worth making.
    /// </summary>
    /// <remarks>
    /// The indicator is refreshed from several places - the window being shown, a menu
    /// command finishing, every status change of every running task - and without a
    /// floor on the interval a batch upload would turn each progress tick into an HTTP
    /// request. Twenty seconds is short enough that the number moves while you watch an
    /// upload finish, and long enough that a burst collapses into one call.
    /// </remarks>
    private static readonly TimeSpan freshFor = TimeSpan.FromSeconds(20);

    private CancellationTokenSource? inFlight;

    /// <summary>Host and token of the account the current figures belong to.</summary>
    private string identity = "";

    private DateTime readAt;

    private bool isSignedIn;
    private string accountLabel = "";
    private string accountTooltip = "";
    private bool hasStorage;
    private bool hasStorageBar;
    private bool hasStorageProblem;
    private string storageText = "";
    private string storageTooltip = "";
    private double storagePercent;

    public AccountStripViewModel()
    {
        // Signed out until proven otherwise, so the button never renders as a blank gap
        // in the window that has not yet asked who is signed in.
        accountLabel = Strings.MainWindow_SignIn;
        accountTooltip = Strings.MainMenuBuilder_SignIn;
    }

    /// <summary>
    /// The account's picture, or the letters that stand in for it.
    /// </summary>
    /// <remarks>
    /// Owned here rather than reached for from the XAML, because it holds a decoded
    /// bitmap and a running timer: something has to be told when the account goes away,
    /// and this class is already told that.
    /// </remarks>
    public AccountAvatar Avatar { get; } = new();

    /// <summary>Whether an account is configured at all. Drives the account button's two faces.</summary>
    public bool IsSignedIn
    {
        get => isSignedIn;
        private set
        {
            SetField(ref isSignedIn, value);
            OnPropertyChanged(nameof(IsSignedOut));
        }
    }

    /// <summary>
    /// The other face of <see cref="IsSignedIn"/>, as a property rather than a negated
    /// binding: Avalonia's <c>!</c> prefix is not compile-checked here, and a silently
    /// broken binding would leave both icons on top of each other.
    /// </summary>
    public bool IsSignedOut => !isSignedIn;

    /// <summary>Short enough for a toolbar: the display name, or an invitation to sign in.</summary>
    public string AccountLabel
    {
        get => accountLabel;
        private set => SetField(ref accountLabel, value);
    }

    public string AccountTooltip
    {
        get => accountTooltip;
        private set => SetField(ref accountTooltip, value);
    }

    /// <summary>False until the server has said something. Hides the whole indicator.</summary>
    public bool HasStorage
    {
        get => hasStorage;
        private set => SetField(ref hasStorage, value);
    }

    /// <summary>Only an account with a limit gets a bar; an unlimited one has nothing to fill.</summary>
    public bool HasStorageBar
    {
        get => hasStorageBar;
        private set => SetField(ref hasStorageBar, value);
    }

    /// <summary>The reading could not be taken, which is worth saying rather than hiding.</summary>
    public bool HasStorageProblem
    {
        get => hasStorageProblem;
        private set
        {
            SetField(ref hasStorageProblem, value);
            OnPropertyChanged(nameof(HasStorageReading));
        }
    }

    /// <summary>A figure the server actually gave us, rather than a failure to report one.</summary>
    public bool HasStorageReading => !hasStorageProblem;

    public string StorageText
    {
        get => storageText;
        private set => SetField(ref storageText, value);
    }

    /// <summary>The long form, with what is left, for the tooltip.</summary>
    public string StorageTooltip
    {
        get => storageTooltip;
        private set => SetField(ref storageTooltip, value);
    }

    public double StoragePercent
    {
        get => storagePercent;
        private set => SetField(ref storagePercent, value);
    }

    /// <summary>
    /// Bring the strip up to date, asking the server only when that is worth doing.
    /// </summary>
    /// <remarks>
    /// UI thread only. Everything this class touches - the fields, the in-flight token
    /// source, the properties - is single-threaded by that rule, and the fetch keeps it
    /// that way by awaiting on Avalonia's synchronisation context rather than posting
    /// back by hand.
    ///
    /// A different account than last time is always fetched immediately: the figures on
    /// screen belong to the previous one, and showing them under a new name would be a
    /// lie rather than a delay.
    /// </remarks>
    public void Refresh()
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            Clear();
            return;
        }

        IsSignedIn = true;
        ShowIdentity(ModlightSignIn.DisplayName(account), account.Host, account.Token, account.Avatar);

        bool changedAccount = IdentityOf(account.Host, account.Token) != identity;

        if (changedAccount)
        {
            ClearStorage();
        }

        if (inFlight != null)
        {
            return;
        }

        if (!changedAccount && HasStorage && DateTime.UtcNow - readAt < freshFor)
        {
            return;
        }

        CancellationTokenSource source = new CancellationTokenSource();
        inFlight = source;
        _ = FetchAsync(account.Host, account.Token, source);
    }

    /// <summary>Stop waiting on an answer that is no longer wanted.</summary>
    public void Cancel()
    {
        CancellationTokenSource? source = inFlight;
        inFlight = null;

        if (source != null)
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private async Task FetchAsync(string host, string token, CancellationTokenSource source)
    {
        try
        {
            ModlightAccountInfo? info = await ModlightApi.GetAccountAsync(host, token, source.Token);

            if (Owns(source))
            {
                if (info == null)
                {
                    // The server answered and refused the token. Signing out is not ours
                    // to do - the person may be about to fix it in the panel - but the
                    // indicator must not keep showing a figure it can no longer verify.
                    ShowProblem(host, token, Strings.ModlightSignIn_TokenRejected);
                }
                else
                {
                    Show(host, token, info);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by Cancel() or by the request's own deadline. Nothing to say.
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);

            if (Owns(source))
            {
                ShowProblem(host, token, string.Format(Strings.ModlightSignIn_Unreachable, host, exception.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(inFlight, source))
            {
                inFlight = null;
                source.Dispose();
            }
        }
    }

    /// <summary>
    /// Whether this reply is still the one being waited for.
    /// </summary>
    /// <remarks>
    /// A sign-out or a switch to another account while the request was in the air
    /// replaces the token source. Without this check the old server's answer would
    /// land on the new account's strip.
    /// </remarks>
    private bool Owns(CancellationTokenSource source) =>
        ReferenceEquals(inFlight, source) && !source.IsCancellationRequested;

    private void Show(string host, string token, ModlightAccountInfo info)
    {
        identity = IdentityOf(host, token);
        readAt = DateTime.UtcNow;

        // The name, the tag and the picture arrive in the same answer as the quota and
        // were being dropped on the floor, so the corner of the window showed whatever the
        // config file happened to have been written with - which for an account whose
        // stored name was empty meant the address of the server standing in for a person.
        // Kept as well as shown: a name changed in the panel now reaches this machine
        // within one refresh instead of only at the next sign-in.
        ModlightSignIn.Adopt(info);

        ShowIdentity(ModlightSignIn.DisplayName(info), host, token, info.Avatar);

        ModlightQuota? quota = info.Quota;

        if (quota == null)
        {
            ClearStorage();
            return;
        }

        // NOT ToSizeString and NOT the BinaryUnits preference: this is the
        // panel's own number, and it has to read the same in both places.
        // See ModlightFormat for the 5.58-vs-5.2 mismatch this fixes.
        string used = ModlightFormat.CloudSize(quota.Used);

        if (quota.Unlimited || quota.Max == null || quota.Max <= 0)
        {
            StorageText = string.Format(Strings.MainWindow_StorageUnlimited, used);
            StorageTooltip = string.Format(Strings.SignInWindow_QuotaUnlimited, used);
            StoragePercent = 0;
            HasStorageBar = false;
        }
        else
        {
            long max = quota.Max.Value;
            long remaining = quota.Remaining ?? Math.Max(0, max - quota.Used);

            StorageText = string.Format(Strings.MainWindow_StorageUsed, used, ModlightFormat.CloudSize(max));
            StorageTooltip = string.Format(Strings.SignInWindow_QuotaUsed,
                used, ModlightFormat.CloudSize(max), ModlightFormat.CloudSize(remaining));

            StoragePercent = Math.Min(100d, quota.Used * 100d / max);
            HasStorageBar = true;
        }

        HasStorageProblem = false;
        HasStorage = true;
    }

    private void ShowProblem(string host, string token, string reason)
    {
        identity = IdentityOf(host, token);

        // Not stamped as read: the next refresh should try again rather than sit on a
        // failure for the freshness window.
        readAt = default;

        StorageText = Strings.MainWindow_StorageUnavailable;
        StorageTooltip = reason;
        StoragePercent = 0;
        HasStorageBar = false;
        HasStorageProblem = true;
        HasStorage = true;
    }

    /// <summary>
    /// Put a person in the corner of the window, and the server underneath them.
    /// </summary>
    /// <remarks>
    /// The address is deliberately in the tooltip and not in the label. It is worth having
    /// - one machine can be signed in to a deployment that is not the usual one, and that
    /// is exactly the thing you want to be able to check - but it answers "where", and the
    /// button answers "who". They were the same string, and the address won whenever the
    /// stored name was empty.
    /// </remarks>
    private void ShowIdentity(string name, string host, string token, string? avatar)
    {
        // Blank only for a config written without a name, a tag or an address to reach
        // the server with - old, or half-written. "Modlight account" is a poor label and
        // an honest one; the address that used to fill this gap was neither, because it
        // is the same for everyone signed in to that deployment.
        string label = string.IsNullOrWhiteSpace(name) ? Strings.MainWindow_AccountUnnamed : name;

        AccountLabel = label;
        AccountTooltip = string.IsNullOrWhiteSpace(host)
            ? string.Format(Strings.MainMenuBuilder_SignedInAs, label)
            : string.Format(Strings.MainMenuBuilder_SignedInAs, label) + Environment.NewLine + host;

        // The NAME, not the label: two letters of a stand-in would be a made-up monogram,
        // and the person-shaped glyph says "we do not know who this is" far better.
        Avatar.Show(host, token, avatar, name);
    }

    private void Clear()
    {
        Cancel();
        ClearStorage();
        Avatar.Clear();

        IsSignedIn = false;
        AccountLabel = Strings.MainWindow_SignIn;
        AccountTooltip = Strings.MainMenuBuilder_SignIn;
    }

    private void ClearStorage()
    {
        identity = "";
        readAt = default;
        StorageText = "";
        StorageTooltip = "";
        StoragePercent = 0;
        HasStorageBar = false;
        HasStorageProblem = false;
        HasStorage = false;
    }

    /// <summary>
    /// What makes one account's figures different from another's.
    /// </summary>
    /// <remarks>
    /// The token is in it as well as the host, because refreshing a token on the same
    /// server produces a different grant with, in principle, a different answer - and
    /// because a token swap is exactly when a cached figure should be dropped.
    /// </remarks>
    private static string IdentityOf(string host, string token) => host + "\n" + token;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
