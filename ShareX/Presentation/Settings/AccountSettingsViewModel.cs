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
/// The account's own settings, which until now were reachable only from the website.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS CAN AND CANNOT DO, because the split is not obvious and pretending otherwise
/// would produce a screen full of controls that quietly do nothing.
/// </para>
/// <para>
/// The server publishes these preferences over HTTP but does not accept them:
/// <c>GET /api/me</c> answers with the tag, the link host, the private-by-default switch,
/// the copy-raw switch, the avatar and the default folder, and there is no <c>PATCH</c>.
/// The panel changes them through Server Actions, which are a browser mechanism a desktop
/// client cannot call. So each of those is shown with its real current value and a door to
/// the page that can change it, and none of them is drawn as an editable control that
/// would silently discard what the user typed.
/// </para>
/// <para>
/// The one setting on these pages the app genuinely owns is the upload destination folder,
/// because that is a client preference: it rides along on each upload rather than living
/// on the account. It used to sit in task settings, three levels down a tree, which is
/// where nobody looked for it.
/// </para>
/// </remarks>
internal sealed class AccountSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? _inFlight;
    private bool _disposed;

    private string _selectedPageId = "profile";
    private bool _isSignedIn;
    private string _displayName = "";
    private string _email = "";
    private string _tag = "";
    private string _host = "";
    private string _linkPreview = "";
    private string _quotaText = "";
    private double _quotaPercent;
    private bool _hasQuotaBar;
    private string _privacyText = "";
    private string _copyLinkText = "";
    private string _status = "";
    private bool _isBusy;

    /// <summary>The account's picture, or the two letters that stand in for it.</summary>
    /// <remarks>
    /// Its own instance rather than the tab strip's: an Avalonia bitmap is a surface a
    /// control points at, and sharing one would mean whichever window closes first
    /// disposing the picture the other is still drawing. The bytes are cached inside
    /// <see cref="AccountAvatar"/>, so the second decode is the only cost.
    /// </remarks>
    public AccountAvatar Avatar { get; } = new();

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set
        {
            SetField(ref _isSignedIn, value);
            OnPropertyChanged(nameof(IsSignedOut));
        }
    }

    public bool IsSignedOut => !_isSignedIn;

    public string DisplayName
    {
        get => _displayName;
        private set => SetField(ref _displayName, value);
    }

    public string Email
    {
        get => _email;
        private set => SetField(ref _email, value);
    }

    /// <summary>The account's short code - the segment its links are built from.</summary>
    public string Tag
    {
        get => _tag;
        private set => SetField(ref _tag, value);
    }

    /// <summary>The hostname this account's links are published on.</summary>
    public string Host
    {
        get => _host;
        private set => SetField(ref _host, value);
    }

    /// <summary>What a link to this account looks like, built from the two above.</summary>
    public string LinkPreview
    {
        get => _linkPreview;
        private set => SetField(ref _linkPreview, value);
    }

    public string QuotaText
    {
        get => _quotaText;
        private set => SetField(ref _quotaText, value);
    }

    public double QuotaPercent
    {
        get => _quotaPercent;
        private set => SetField(ref _quotaPercent, value);
    }

    public bool HasQuotaBar
    {
        get => _hasQuotaBar;
        private set => SetField(ref _hasQuotaBar, value);
    }

    /// <summary>Whether new uploads start private, in words.</summary>
    public string PrivacyText
    {
        get => _privacyText;
        private set => SetField(ref _privacyText, value);
    }

    /// <summary>Which of the two link shapes this account copies, in words.</summary>
    public string CopyLinkText
    {
        get => _copyLinkText;
        private set => SetField(ref _copyLinkText, value);
    }

    /// <summary>Why the figures are missing, when they are.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            SetField(ref _status, value);
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public bool IsProfilePage => _selectedPageId == "profile";
    public bool IsLinksPage => _selectedPageId == "links";
    public bool IsStoragePage => _selectedPageId == "storage";

    public AccountSettingsViewModel()
    {
        ShowIdentity();
        Refresh();
    }

    /// <summary>Show one of the three account pages.</summary>
    public void SelectPage(string pageId)
    {
        if (!SetField(ref _selectedPageId, pageId))
        {
            return;
        }

        OnPropertyChanged(nameof(IsProfilePage));
        OnPropertyChanged(nameof(IsLinksPage));
        OnPropertyChanged(nameof(IsStoragePage));
    }

    /// <summary>
    /// Ask the server who this token belongs to and how full the account is.
    /// </summary>
    /// <remarks>
    /// Unconditional, unlike the tab strip's twenty-second floor: this window is opened by
    /// hand and rarely, and somebody who just changed a setting in the panel and came back
    /// here to check it must not be shown a cached answer from before the change.
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
        ShowIdentity();

        _inFlight?.Cancel();
        _inFlight?.Dispose();

        CancellationTokenSource source = new();
        _inFlight = source;
        IsBusy = true;
        Status = Strings.SettingsWindow_AccountLoading;

        _ = FetchAsync(account.Host, account.Token, source);
    }

    private async Task FetchAsync(string host, string token, CancellationTokenSource source)
    {
        try
        {
            ModlightAccountInfo? info = await ModlightApi.GetAccountAsync(host, token, source.Token);

            if (!Owns(source))
            {
                return;
            }

            if (info == null)
            {
                Status = Strings.ModlightSignIn_TokenRejected;
                return;
            }

            Status = "";
            DisplayName = ModlightSignIn.DisplayName(info);
            Email = info.Email ?? "";
            Tag = info.Tag ?? "";
            Host = info.Host ?? host;
            LinkPreview = BuildLinkPreview(Host, Tag);
            PrivacyText = info.HideUploads
                ? Strings.SettingsWindow_AccountPrivateYes
                : Strings.SettingsWindow_AccountPrivateNo;
            CopyLinkText = info.CopyRaw
                ? Strings.SettingsWindow_AccountCopyRaw
                : Strings.SettingsWindow_AccountCopyPage;
            Avatar.Show(host, token, info.Avatar, DisplayName);
            ShowQuota(info.Quota);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a newer Refresh or by the request's own deadline.
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);

            if (Owns(source))
            {
                Status = Strings.SettingsWindow_AccountUnreachable;
            }
        }
        finally
        {
            if (Owns(source))
            {
                _inFlight = null;
                IsBusy = false;
                source.Dispose();
            }
        }
    }

    private bool Owns(CancellationTokenSource source) => !_disposed && ReferenceEquals(_inFlight, source);

    private void ShowQuota(ModlightQuota? quota)
    {
        if (quota == null)
        {
            QuotaText = "";
            HasQuotaBar = false;
            return;
        }

        if (quota.Unlimited || quota.Max is not > 0)
        {
            // No bar, because a bar with no end is a decoration: it would have to show
            // some fraction of a limit that does not exist.
            QuotaText = string.Format(Strings.SettingsWindow_AccountQuotaUnlimited, ModlightFormat.CloudSize(quota.Used));
            HasQuotaBar = false;
            return;
        }

        long max = quota.Max.Value;
        QuotaText = string.Format(
            Strings.SettingsWindow_AccountQuotaUsed,
            ModlightFormat.CloudSize(quota.Used),
            ModlightFormat.CloudSize(max));
        QuotaPercent = Math.Clamp(quota.Used * 100d / max, 0, 100);
        HasQuotaBar = true;
    }

    /// <summary>What the panel shows next to the tag: the start of every link this account makes.</summary>
    private static string BuildLinkPreview(string host, string tag)
    {
        if (string.IsNullOrEmpty(host))
        {
            return "";
        }

        string origin = host.TrimEnd('/');
        return string.IsNullOrEmpty(tag) ? origin + "/…" : origin + "/" + tag + "/…";
    }

    /// <summary>
    /// Fill in what is known without asking anybody, so the page is never blank while it loads.
    /// </summary>
    private void ShowIdentity()
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            return;
        }

        DisplayName = ModlightSignIn.DisplayName(account);
        Email = account.Email ?? "";
        Tag = account.Tag ?? "";
        Host = account.Host ?? "";
        LinkPreview = BuildLinkPreview(Host, Tag);
        Avatar.Show(Host, account.Token, account.Avatar, DisplayName);
    }

    private void Clear()
    {
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;

        IsSignedIn = false;
        IsBusy = false;
        Avatar.Clear();
        DisplayName = "";
        Email = "";
        Tag = "";
        Host = "";
        LinkPreview = "";
        QuotaText = "";
        HasQuotaBar = false;
        PrivacyText = "";
        CopyLinkText = "";
        Status = "";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
        Avatar.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
