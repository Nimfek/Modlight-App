#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

public enum ModlightSignInStatus
{
    Success,
    /// <summary>The person said no in the browser.</summary>
    Denied,
    /// <summary>Nobody answered before the deadline.</summary>
    TimedOut,
    /// <summary>The app stopped waiting - window closed, Cancel pressed.</summary>
    Canceled,
    /// <summary>Something posted to the loopback port that did not carry our nonce.</summary>
    StateMismatch,
    /// <summary>The server did not recognise the token.</summary>
    TokenRejected,
    /// <summary>Anything else; see <see cref="ModlightSignInResult.Message"/>.</summary>
    Failed
}

public sealed class ModlightSignInResult
{
    public ModlightSignInStatus Status { get; }

    /// <summary>Ready to put in front of a person, in their language.</summary>
    public string Message { get; }

    /// <summary>Who was signed in. Only set when <see cref="Status"/> is Success.</summary>
    public ModlightAccountInfo? Account { get; }

    public bool IsSuccess => Status == ModlightSignInStatus.Success;

    public ModlightSignInResult(ModlightSignInStatus status, string message, ModlightAccountInfo? account = null)
    {
        Status = status;
        Message = message;
        Account = account;
    }
}

/// <summary>
/// Signing this installation in to a Modlight account, without ever seeing a password.
/// </summary>
/// <remarks>
/// The browser authenticates - it already has the session, the 2FA and the password
/// manager - and hands back an API token through a loopback redirect (RFC 8252), the
/// same shape `gh auth login` and `aws sso login` use. The app's part is four steps:
/// bind a port, open the authorize page, check that what comes back carries the nonce
/// it sent out, and prove the token works before storing it.
///
/// No UI in here. <see cref="SignInWindow"/> drives it, and anything else can too.
/// </remarks>
public static class ModlightSignIn
{
    /// <summary>
    /// How long to hold the port open.
    /// </summary>
    /// <remarks>
    /// Was two minutes, and that was measured against the wrong thing: not "how long
    /// does the round trip take" but "how long does a PERSON take". The first real
    /// sign-in failed on it. Two minutes has to cover finding the browser window,
    /// signing in to the panel, getting through 2FA, and actually reading the consent
    /// screen before clicking - and reading it is the one step we want people to take
    /// their time over, since the whole page exists to say what is being handed away.
    ///
    /// Ten minutes is what gh, aws and every other loopback client allow, for the same
    /// reason. The cost of the longer window is one idle thread and one bound port on a
    /// sign-in that was abandoned; the cost of the short one was a failure the user
    /// could do nothing about except paste the token by hand.
    /// </remarks>
    private static readonly TimeSpan approvalTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The refusal code the authorize page posts when the person declines.</summary>
    private const string DeniedError = "access_denied";

    /// <summary>What the account's token list will call this machine.</summary>
    /// <remarks>
    /// The machine name is in it because the list is read months later, by someone
    /// deciding which of four tokens to revoke. "Desktop: Modlight" times four is not
    /// a list anybody can act on. Clipped to the length the server is willing to show.
    /// </remarks>
    public static string ClientName
    {
        get
        {
            string name = $"{Program.AppName} on {Environment.MachineName}";
            return name.Length > 64 ? name.Substring(0, 64) : name;
        }
    }

    /// <summary>
    /// Run the whole flow: open the browser, wait for the answer, store the token.
    /// </summary>
    /// <param name="host">What the user typed. Normalised here, not by the caller.</param>
    /// <param name="cancellationToken">Cancelling gives up on the wait and frees the port.</param>
    public static async Task<ModlightSignInResult> RunAsync(string host, CancellationToken cancellationToken)
    {
        if (!ModlightApi.TryNormalizeHost(host, out string origin))
        {
            return new ModlightSignInResult(ModlightSignInStatus.Failed, Strings.ModlightSignIn_InvalidHost);
        }

        ModlightLoopbackListener listener;

        try
        {
            listener = new ModlightLoopbackListener();
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return new ModlightSignInResult(ModlightSignInStatus.Failed,
                string.Format(Strings.ModlightSignIn_ListenerFailed, exception.Message));
        }

        using (listener)
        {
            // Alphanumeric, from RandomCrypto - inside the [A-Za-z0-9._~-] set the
            // authorize page will accept, and the only thing that will let us tell our
            // own answer apart from one another local process decided to send.
            string state = Helpers.GetRandomAlphanumeric(32);

            string url = $"{origin}/authorize?port={listener.Port}" +
                $"&name={Uri.EscapeDataString(ClientName)}" +
                $"&state={state}";

            URLHelpers.OpenURL(url);
            DebugHelper.WriteLine($"Modlight sign-in: waiting on 127.0.0.1:{listener.Port}");

            ModlightHandoff handoff;

            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(approvalTimeout);

                try
                {
                    handoff = await listener.WaitAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    return cancellationToken.IsCancellationRequested
                        ? new ModlightSignInResult(ModlightSignInStatus.Canceled, Strings.ModlightSignIn_Canceled)
                        : new ModlightSignInResult(ModlightSignInStatus.TimedOut, Strings.ModlightSignIn_TimedOut);
                }
                catch (Exception exception)
                {
                    DebugHelper.WriteException(exception);
                    return new ModlightSignInResult(ModlightSignInStatus.Failed,
                        string.Format(Strings.ModlightSignIn_ListenerFailed, exception.Message));
                }
            }

            // Everything below this line depends on the nonce matching. Any local
            // process can connect to a loopback port and post a token of its choosing;
            // the only thing it cannot know is the value this run generated. So a
            // mismatch ends the sign-in - it is not a warning to carry on past.
            if (!NonceEquals(handoff.State, state))
            {
                DebugHelper.WriteLine("Modlight sign-in: discarded a loopback response with the wrong state.");
                return new ModlightSignInResult(ModlightSignInStatus.StateMismatch, Strings.ModlightSignIn_StateMismatch);
            }

            if (!string.IsNullOrEmpty(handoff.Error))
            {
                DebugHelper.WriteLine($"Modlight sign-in: refused in the browser ({handoff.Error}).");

                return new ModlightSignInResult(ModlightSignInStatus.Denied,
                    handoff.Error == DeniedError
                        ? Strings.ModlightSignIn_Denied
                        : string.Format(Strings.ModlightSignIn_DeniedWithReason, handoff.Error));
            }

            if (!ModlightApi.IsPlausibleToken(handoff.Token))
            {
                return new ModlightSignInResult(ModlightSignInStatus.Failed, Strings.ModlightSignIn_NoToken);
            }

            return await UseTokenAsync(origin, handoff.Token!, cancellationToken);
        }
    }

    /// <summary>
    /// Check a token against the server and, if it works, keep it.
    /// </summary>
    /// <remarks>
    /// Also the manual route: when the page cannot reach the loopback port it shows the
    /// token instead and asks the person to paste it, so that fallback needs somewhere
    /// to land. Nothing is stored before GET /api/me answers - a token saved without
    /// being tried is a destination that fails on the first screenshot instead of here.
    /// </remarks>
    public static async Task<ModlightSignInResult> UseTokenAsync(string host, string token, CancellationToken cancellationToken)
    {
        if (!ModlightApi.TryNormalizeHost(host, out string origin))
        {
            return new ModlightSignInResult(ModlightSignInStatus.Failed, Strings.ModlightSignIn_InvalidHost);
        }

        string trimmed = token?.Trim() ?? "";

        if (!ModlightApi.IsPlausibleToken(trimmed))
        {
            return new ModlightSignInResult(ModlightSignInStatus.Failed, Strings.ModlightSignIn_NoToken);
        }

        ModlightAccountInfo? account;

        try
        {
            account = await ModlightApi.GetAccountAsync(origin, trimmed, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ModlightSignInResult(ModlightSignInStatus.Canceled, Strings.ModlightSignIn_Canceled);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return new ModlightSignInResult(ModlightSignInStatus.Failed,
                string.Format(Strings.ModlightSignIn_Unreachable, origin, exception.Message));
        }

        if (account == null)
        {
            return new ModlightSignInResult(ModlightSignInStatus.TokenRejected, Strings.ModlightSignIn_TokenRejected);
        }

        Save(origin, trimmed, account);

        return new ModlightSignInResult(ModlightSignInStatus.Success,
            string.Format(Strings.ModlightSignIn_SignedInAs, DisplayName(account)), account);
    }

    /// <summary>The account currently signed in, or null.</summary>
    public static ModlightAccount? Current
    {
        get
        {
            ModlightAccount? account = Program.UploadersConfig?.ModlightAccount;
            return account != null && account.IsSignedIn ? account : null;
        }
    }

    /// <summary>
    /// Forget the account on this machine.
    /// </summary>
    /// <remarks>
    /// Local only. The contract has no revoke call, so the token stays valid on the
    /// server until it is deleted from the token list in the web panel - which is
    /// worth knowing if the reason for signing out was that somebody else is about to
    /// use this machine. The host is kept: it is where they will sign back in, and
    /// making them retype it would punish them for signing out.
    /// </remarks>
    public static void SignOut()
    {
        SettingManager.WaitUploadersConfig();

        ModlightAccount? account = Program.UploadersConfig?.ModlightAccount;

        if (account == null)
        {
            return;
        }

        account.Token = "";
        account.Tag = "";
        account.Name = "";
        account.Email = "";
        account.SignedInAt = default;

        // The role belonged to the credential that just went away. Not strictly required -
        // ModlightAdmin re-checks who the answer was about on every read, and there is now
        // nobody signed in - but leaving a stale "yes" sitting in a static because a
        // neighbouring guard happens to cover it is how the guard's removal becomes a bug.
        ModlightAdmin.Forget();

        SettingManager.SaveUploadersConfigAsync();

        DebugHelper.WriteLine("Modlight: signed out. The token is forgotten here but is still live on the server.");
    }

    /// <summary>Whatever this account can be called on screen, in order of usefulness.</summary>
    public static string DisplayName(ModlightAccountInfo account)
    {
        if (!string.IsNullOrWhiteSpace(account.Name))
        {
            return account.Name!;
        }

        if (!string.IsNullOrWhiteSpace(account.Email))
        {
            return account.Email!;
        }

        return account.Tag ?? "";
    }

    /// <summary>The same question asked of what the config file remembers.</summary>
    /// <remarks>
    /// The address of the server used to be the last resort here, and it is the reason the
    /// button in the corner of the window could read "https://files.example.com" where a person's
    /// name belongs. An address is not an identity - it is the same for everyone with an
    /// account on that deployment - so it is no longer one of the answers. It is still
    /// shown, underneath, as the place this account lives; see AccountStripViewModel.
    ///
    /// Empty rather than a stand-in when there is genuinely nothing: a caller that has to
    /// put something on screen knows better than this what to put, and the strip does.
    /// </remarks>
    public static string DisplayName(ModlightAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.Name))
        {
            return account.Name;
        }

        if (!string.IsNullOrWhiteSpace(account.Email))
        {
            return account.Email;
        }

        return account.Tag ?? "";
    }

    /// <summary>
    /// Take the identity from a fresh /api/me answer, and keep it.
    /// </summary>
    /// <remarks>
    /// The account row was written once, at sign-in, and then never again - so a name
    /// changed in the panel, a new picture, or a config written by a build that did not
    /// store one of these fields all stayed wrong on this machine until somebody signed
    /// out and back in. The strip already asks the server four times a minute for the
    /// storage figure; this is the rest of the same answer, which was being thrown away.
    ///
    /// Writes only when something actually moved. The alternative is a settings file
    /// rewritten every twenty seconds for ever.
    ///
    /// Never touches the host or the token: those are what the answer was fetched WITH,
    /// and letting a reply rewrite the credential it was authorised by is how a redirect
    /// becomes an account takeover.
    /// </remarks>
    /// <returns>True when the stored copy was changed.</returns>
    public static bool Adopt(ModlightAccountInfo info)
    {
        ModlightAccount? account = Program.UploadersConfig?.ModlightAccount;

        if (account == null || info == null)
        {
            return false;
        }

        string tag = info.Tag ?? "";
        string name = info.Name ?? "";
        string email = info.Email ?? "";
        string avatar = info.Avatar ?? "";

        if (account.Tag == tag && account.Name == name && account.Email == email && account.Avatar == avatar)
        {
            return false;
        }

        account.Tag = tag;
        account.Name = name;
        account.Email = email;
        account.Avatar = avatar;

        SettingManager.SaveUploadersConfigAsync();
        return true;
    }

    /// <summary>
    /// Compare the nonce without letting the time taken say how much of it matched.
    /// </summary>
    /// <remarks>
    /// As the code stands today nothing can time this: the listener writes its 204 and
    /// closes the socket before the body is even parsed, and it stops at the first
    /// readable body, so a local process gets one guess and no reply to measure. But
    /// both of those facts live in <see cref="ModlightLoopbackListener"/>, which knows
    /// nothing about this comparison - and "keep waiting when junk arrives" is exactly
    /// the kind of robustness fix someone would make there later, turning one guess
    /// into unlimited ones. Fixed-time costs a loop over 32 characters, so the
    /// guarantee is kept here rather than rested on a neighbour's current behaviour.
    ///
    /// The length is compared normally: it is a constant in this file, not a secret.
    /// </remarks>
    private static bool NonceEquals(string? received, string expected)
    {
        if (received == null || received.Length != expected.Length)
        {
            return false;
        }

        int difference = 0;

        for (int i = 0; i < expected.Length; i++)
        {
            difference |= received[i] ^ expected[i];
        }

        return difference == 0;
    }

    private static void Save(string origin, string token, ModlightAccountInfo info)
    {
        SettingManager.WaitUploadersConfig();

        ModlightAccount account = Program.UploadersConfig.ModlightAccount ?? new ModlightAccount();

        // The host we signed in to, not the one /api/me reported: that one is where
        // this account's LINKS are built (it can be a vanity domain), while uploads
        // and every other call go to the deployment we were just granted a token by.
        account.Host = origin;
        account.Token = token;
        account.Tag = info.Tag ?? "";
        account.Name = info.Name ?? "";
        account.Email = info.Email ?? "";
        account.Avatar = info.Avatar ?? "";
        account.SignedInAt = DateTime.Now;

        Program.UploadersConfig.ModlightAccount = account;
        SettingManager.SaveUploadersConfigAsync();

        // The one moment a NEW token exists, which makes it the one moment the account's
        // skin can be asked for and applied. Fire and forget, and deliberately after the
        // account has been stored: ModlightTheme reads the credential back out of the
        // config rather than being handed it, so there is one answer to "who are we"
        // instead of two that can disagree.
        _ = ModlightTheme.SyncAsync(CancellationToken.None);

        // And the rest of the configuration, for the same reason and at the same moment.
        // This is the case the whole feature exists for: a machine that has just been
        // signed in to has never been confirmed by this account, so ModlightSettingsPlan
        // will restore the account's settings onto it rather than let its defaults out.
        ModlightSettingsSync.SignedIn();
    }
}
