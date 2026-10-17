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
/// The window that gets this installation a token: type the server, click once, approve
/// in the browser, and see who you ended up as.
/// </summary>
/// <remarks>
/// The identity card at the bottom is not decoration. A token that was minted but is not
/// actually usable looks exactly like a working one until the first screenshot fails at
/// an inconvenient moment, so the flow spends one round trip on GET /api/me and shows
/// the answer - name, tag, server, room left. If that card is there, uploading works.
/// </remarks>
public partial class SignInWindow : Window
{
    private readonly SignInViewModel viewModel;

    public SignInWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();

        viewModel = new SignInViewModel();
        DataContext = viewModel;

        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>True once an account is signed in, whether it happened here or earlier.</summary>
    public bool IsSignedIn => ModlightSignIn.Current != null;

    private async void OnOpened(object? sender, EventArgs e)
    {
        Activate();
        HostTextBox.Focus();
        await viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        viewModel.Cancel();

        // The avatar holds a decoded bitmap and, when it moves, a running timer. A window
        // that is gone must not still be asking the dispatcher for the next frame.
        viewModel.Avatar.Dispose();
    }

    private async void OnSignInClick(object? sender, RoutedEventArgs e) => await viewModel.SignInAsync();

    private async void OnUseTokenClick(object? sender, RoutedEventArgs e) => await viewModel.UseTokenAsync();

    private void OnToggleManualClick(object? sender, RoutedEventArgs e) =>
        viewModel.ShowManualToken = !viewModel.ShowManualToken;

    private void OnToggleServerClick(object? sender, RoutedEventArgs e) =>
        viewModel.ShowServer = !viewModel.ShowServer;

    private void OnCancelClick(object? sender, RoutedEventArgs e) => viewModel.Cancel();

    private void OnSignOutClick(object? sender, RoutedEventArgs e) => viewModel.SignOut();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}

public sealed class SignInViewModel : INotifyPropertyChanged
{
    private string _host = ModlightAccount.DefaultHost;
    private string _manualToken = "";
    private bool _showManualToken;
    private bool _showServer = !HasDefaultHost;
    private bool _isBusy;
    private StatusTone _tone;
    private string _statusMessage = "";
    private string _accountName = "";
    private string _accountDetails = "";
    private string _quotaText = "";
    private double _quotaPercent;
    private bool _hasQuota;
    private bool _hasQuotaBar;
    private bool _hasAccount;
    private bool _isSignedIn;

    /// <summary>Cancels whatever is currently in flight. Null when nothing is.</summary>
    private CancellationTokenSource? cancellation;

    public string Host
    {
        get => _host;
        set => SetField(ref _host, value);
    }

    public string ManualToken
    {
        get => _manualToken;
        set => SetField(ref _manualToken, value);
    }

    /// <summary>
    /// Whether this build names a server of its own (ModlightDefaultHost).
    /// </summary>
    private static bool HasDefaultHost => !string.IsNullOrEmpty(ModlightAccount.DefaultHost);

    /// <summary>
    /// Whether the server field is on screen.
    /// </summary>
    /// <remarks>
    /// A build that names its server (ModlightDefaultHost) keeps the field behind
    /// "Other server": its address is already filled in, and a URL box at the top of the
    /// sign-in window asks every user a question that has one answer. A build without
    /// one - anything compiled from the public source - shows it from the start,
    /// because until the user names a server there is nothing to sign in to.
    /// </remarks>
    public bool ShowServer
    {
        get => _showServer;
        set => SetField(ref _showServer, value);
    }

    /// <summary>
    /// The "Other server" link. Only where there is a default to step away from: with no
    /// default the field is already open, and the link could only hide the one input the
    /// window cannot work without.
    /// </summary>
    public bool ShowServerToggle => IsIdle && HasDefaultHost;

    public bool ShowManualToken
    {
        get => _showManualToken;
        set
        {
            if (SetField(ref _showManualToken, value))
            {
                OnPropertyChanged(nameof(ShowManualToggle));
            }
        }
    }

    /// <summary>
    /// The window is showing a confirmation rather than a form.
    /// </summary>
    /// <remarks>
    /// Signed in AND nothing wrong, which is not pedantry: opening this window with a
    /// token the server has since revoked leaves an account on screen that cannot upload
    /// anything, and a screen congratulating somebody on that would be lying. A rejected
    /// token puts the heading, the primary button and the whole sign-in flow back.
    /// </remarks>
    public bool IsConfirmed => IsSignedIn && !HasError;

    /// <summary>The other side of it, for everything that belongs to the form.</summary>
    public bool NeedsSignIn => !IsConfirmed;

    /// <summary>
    /// The heading, which states the outcome once there is one.
    /// </summary>
    /// <remarks>
    /// A screen that has just succeeded should say so in the largest text on it. Leaving
    /// "Sign in to Modlight" above a signed-in account reads as an instruction to do
    /// something that is already done.
    /// </remarks>
    public string HeadingText => IsConfirmed
        ? Strings.SignInWindow_HeadingSignedIn
        : Strings.SignInWindow_Heading;

    public string SubtitleText => IsConfirmed
        ? Strings.SignInWindow_SubtitleSignedIn
        : Strings.SignInWindow_Subtitle;

    public string HeadingIcon => IsConfirmed ? LucideIcons.circle_check : LucideIcons.log_in;

    /// <summary>
    /// Whether the "the browser could not reach this app" escape hatch is on screen.
    /// </summary>
    /// <remarks>
    /// It answers a problem, so it shows while there is a problem to answer: before
    /// signing in, after a failure, and from then on once it has been opened. Under a
    /// finished sign-in it would be asking a question that already has an answer.
    /// </remarks>
    public bool ShowManualToggle => NeedsSignIn || ShowManualToken;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(ShowServerToggle));
                OnPropertyChanged(nameof(HasSuccess));
            }
        }
    }

    /// <summary>Inputs and buttons are live only when nothing is running.</summary>
    public bool IsIdle => !IsBusy;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(HasSuccess));
                NotifyStage();
            }
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public bool HasError => HasStatus && _tone == StatusTone.Error;

    /// <summary>
    /// The status line earned a tick.
    /// </summary>
    /// <remarks>
    /// Success is stated, never inferred from "nothing went wrong": cancelling is not
    /// an error and it is not a success either, and a tick next to "Sign-in canceled"
    /// would tell somebody the opposite of what happened.
    /// </remarks>
    public bool HasSuccess => HasStatus && !IsBusy && _tone == StatusTone.Success;

    public bool HasAccount
    {
        get => _hasAccount;
        private set => SetField(ref _hasAccount, value);
    }

    public string AccountName
    {
        get => _accountName;
        private set => SetField(ref _accountName, value);
    }

    /// <summary>
    /// The account's picture, in the one place this app draws a real circle.
    /// </summary>
    /// <remarks>
    /// Its own instance rather than the strip's: two controls of different sizes cannot
    /// share one decoded bitmap without one of them disposing what the other is drawing.
    /// The bytes are shared behind them, which is the part worth sharing.
    /// </remarks>
    public AccountAvatar Avatar { get; } = new();

    public string AccountDetails
    {
        get => _accountDetails;
        private set
        {
            if (SetField(ref _accountDetails, value))
            {
                OnPropertyChanged(nameof(HasAccountDetails));
            }
        }
    }

    public bool HasAccountDetails => !string.IsNullOrEmpty(AccountDetails);

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

    public bool HasQuota
    {
        get => _hasQuota;
        private set => SetField(ref _hasQuota, value);
    }

    /// <summary>An unlimited account has a number but nothing to draw a bar against.</summary>
    public bool HasQuotaBar
    {
        get => _hasQuotaBar;
        private set => SetField(ref _hasQuotaBar, value);
    }

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set
        {
            if (SetField(ref _isSignedIn, value))
            {
                NotifyStage();
            }
        }
    }

    /// <summary>
    /// Everything that depends on which of the two screens this is.
    /// </summary>
    /// <remarks>
    /// One method rather than a notification per call site, because the two facts it is
    /// derived from - signed in, and something went wrong - change in four different
    /// places, and a screen left half-switched is worse than either of the two states.
    /// </remarks>
    private void NotifyStage()
    {
        OnPropertyChanged(nameof(IsConfirmed));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(HeadingText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(HeadingIcon));
        OnPropertyChanged(nameof(ShowManualToggle));
    }

    /// <summary>
    /// Show the stored account, then check it is still real.
    /// </summary>
    /// <remarks>
    /// The check matters more than the display: a token revoked from the web panel leaves
    /// this app looking signed in forever, and the first sign of trouble would otherwise
    /// be a failed upload. Better to find out while the window is open.
    /// </remarks>
    public async Task InitializeAsync()
    {
        // The uploaders config loads on a background thread at startup, so it may not
        // be there yet. Waiting for it on a worker keeps the window responsive.
        await Task.Run(SettingManager.WaitUploadersConfig);

        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            return;
        }

        Host = account.Host;
        ShowStored(account);

        await RunAsync(async token =>
        {
            SetStatus(Strings.SignInWindow_Checking, StatusTone.Neutral);

            ModlightAccountInfo? info = await ModlightApi.GetAccountAsync(account.Host, account.Token, token);

            if (info == null)
            {
                SetStatus(Strings.ModlightSignIn_TokenRejected, StatusTone.Error);
                return;
            }

            ShowAccount(info, account.Host);
            SetStatus(string.Format(Strings.ModlightSignIn_SignedInAs, ModlightSignIn.DisplayName(info)), StatusTone.Success);
        });
    }

    public async Task SignInAsync()
    {
        await RunAsync(async token =>
        {
            SetStatus(Strings.SignInWindow_Opening, StatusTone.Neutral);
            Apply(await ModlightSignIn.RunAsync(Host, token));
        });
    }

    public async Task UseTokenAsync()
    {
        await RunAsync(async token =>
        {
            SetStatus(Strings.SignInWindow_Checking, StatusTone.Neutral);
            Apply(await ModlightSignIn.UseTokenAsync(Host, ManualToken, token));
        });
    }

    public void Cancel() => cancellation?.Cancel();

    /// <summary>
    /// Drop the stored account and put the window back the way it looked before anyone
    /// had signed in.
    /// </summary>
    /// <remarks>
    /// Clearing the card matters as much as clearing the config: an identity left on
    /// screen after signing out is the app claiming an account it no longer holds.
    /// </remarks>
    public void SignOut()
    {
        if (IsBusy)
        {
            return;
        }

        ModlightSignIn.SignOut();

        AccountName = "";
        AccountDetails = "";
        Avatar.Clear();
        QuotaText = "";
        QuotaPercent = 0;
        HasQuota = false;
        HasQuotaBar = false;
        HasAccount = false;
        IsSignedIn = false;

        SetStatus(Strings.SignInWindow_SignedOut, StatusTone.Neutral);
    }

    /// <summary>One operation at a time, each with its own cancellation and its own guard.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        using CancellationTokenSource source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;

        try
        {
            await operation(source.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus(Strings.ModlightSignIn_Canceled, StatusTone.Neutral);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            SetStatus(exception.Message, StatusTone.Error);
        }
        finally
        {
            cancellation = null;
            IsBusy = false;
        }
    }

    private void Apply(ModlightSignInResult result)
    {
        SetStatus(result.Message, result.IsSuccess
            ? StatusTone.Success
            : result.Status == ModlightSignInStatus.Canceled ? StatusTone.Neutral : StatusTone.Error);

        if (result.IsSuccess && result.Account != null)
        {
            ManualToken = "";
            ShowManualToken = false;
            ShowAccount(result.Account, ModlightSignIn.Current?.Host ?? Host);
        }
    }

    /// <summary>What the config file remembers - identity only, since quota is never cached.</summary>
    private void ShowStored(ModlightAccount account)
    {
        AccountName = !string.IsNullOrWhiteSpace(account.Name) ? account.Name : account.Email;
        AccountDetails = BuildDetails(account.Email, account.Tag, account.Host);
        Avatar.Show(account.Host, account.Token, account.Avatar, AccountName);
        HasQuota = false;
        HasQuotaBar = false;
        HasAccount = true;
        IsSignedIn = true;
    }

    private void ShowAccount(ModlightAccountInfo info, string host)
    {
        AccountName = ModlightSignIn.DisplayName(info);
        AccountDetails = BuildDetails(info.Email, info.Tag, host);
        Avatar.Show(host, ModlightSignIn.Current?.Token ?? "", info.Avatar, AccountName);
        HasAccount = true;
        IsSignedIn = true;

        ModlightQuota? quota = info.Quota;

        if (quota == null)
        {
            HasQuota = false;
            HasQuotaBar = false;
            return;
        }

        // Same rule as the account strip: the server's number, spelled the way
        // the panel spells it. Two windows in one app disagreeing about the same
        // quota would be worse than either convention.
        if (quota.Unlimited || quota.Max == null || quota.Max <= 0)
        {
            QuotaText = string.Format(Strings.SignInWindow_QuotaUnlimited, ModlightFormat.CloudSize(quota.Used));
            HasQuotaBar = false;
        }
        else
        {
            long max = quota.Max.Value;
            long remaining = quota.Remaining ?? Math.Max(0, max - quota.Used);

            QuotaText = string.Format(Strings.SignInWindow_QuotaUsed,
                ModlightFormat.CloudSize(quota.Used), ModlightFormat.CloudSize(max), ModlightFormat.CloudSize(remaining));

            QuotaPercent = Math.Min(100d, quota.Used * 100d / max);
            HasQuotaBar = true;
        }

        HasQuota = true;
    }

    private static string BuildDetails(string? email, string? tag, string host)
    {
        List<string> lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(email))
        {
            lines.Add(email!);
        }

        string tagged = string.IsNullOrWhiteSpace(tag)
            ? host
            : string.Format(Strings.SignInWindow_TagAndServer, tag, host);

        lines.Add(tagged);

        return string.Join(Environment.NewLine, lines);
    }

    private void SetStatus(string message, StatusTone tone)
    {
        _tone = tone;
        StatusMessage = message;
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasSuccess));

        // The tone can change while the text does not - the same message can arrive twice,
        // once neutral and once as an error - and the StatusMessage setter only fires when
        // the text differs. Raising it here as well is what keeps the heading honest.
        NotifyStage();
    }

    /// <summary>Which of the three glyphs the status line gets, if any.</summary>
    private enum StatusTone
    {
        Neutral,
        Success,
        Error
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
