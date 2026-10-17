#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX;

/// <summary>What came of opening the sign-in window.</summary>
internal enum YouTubeSignInResult
{
    /// <summary>A jar was written. Downloads can use it.</summary>
    Captured,

    /// <summary>The window was closed without a completed sign-in. Not an error.</summary>
    Abandoned,

    /// <summary>This PC has no WebView2 runtime, so there is no browser to sign in with.</summary>
    RuntimeMissing,

    /// <summary>The browser would not start. The reason is in the log.</summary>
    Failed
}

/// <summary>
/// A window with a browser in it, open on Google's sign-in, that closes itself once the
/// sign-in has happened.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> yt-dlp with no cookies is an anonymous client, and YouTube
/// refuses anonymous clients a great deal: age-gated videos, anything region-locked to a
/// signed-in account, and - the one that actually bites - the bot check it serves to
/// addresses it does not like, on videos that are entirely public. All of those arrive as
/// a 403 with no useful text. One signed-in session fixes every one of them.
/// </para>
/// <para>
/// <b>WinForms, in an Avalonia app.</b> Avalonia owns the message loop, but the loop is a
/// Win32 one and this process already hosts WinForms - <c>MainForm</c> is a WinForms window
/// on this same thread. So a <see cref="Form"/> shown from here is pumped like any other
/// window, and WebView2's WinForms control is a finished thing, whereas hosting the same
/// browser in Avalonia means a native control host and its own set of surprises. It is
/// modeless on purpose: a modal loop would nest a second message pump inside Avalonia's,
/// and the whole point of this design is that nothing blocks the UI thread.
/// </para>
/// <para>
/// <b>The threading rule, carried over from the tool this replaces.</b> That one deadlocked
/// because its cookie call blocked the calling thread on a semaphore that only the UI
/// thread could release - and it was being called FROM the UI thread. WebView2's
/// <c>GetCookiesAsync</c> is genuinely asynchronous, so that exact trap is not here, but
/// the rule it taught is: nothing on this path may block the UI thread, and no WebView2
/// task is ever waited on with <c>.Result</c> or <c>.Wait()</c>. Everything below is
/// awaited.
/// </para>
/// <para>
/// <b>How it knows it is done.</b> Not by reading the page - a check on what Google's HTML
/// looks like is a check that breaks at Google's next redesign. It watches for the
/// navigation landing on youtube.com and then asks the cookie jar whether it holds one of
/// the four names Google sets on a completed sign-in. That is a fact, and it is the same
/// fact the previous tool used.
/// </para>
/// </remarks>
internal sealed class YouTubeSignInForm : Form
{
    /// <summary>
    /// How long to let YouTube settle before reading the jar.
    /// </summary>
    /// <remarks>
    /// Carried over from the tool this replaces, where it was found to be necessary: the
    /// page fires its load event before the last of the post-login cookies have been set,
    /// and a jar read at that instant is short of the ones that matter. Awaited, never
    /// slept: this is the UI thread.
    /// </remarks>
    private static readonly TimeSpan settle = TimeSpan.FromMilliseconds(800);

    private static YouTubeSignInForm? current;

    private readonly TaskCompletionSource<YouTubeSignInResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly WebView2 view = new();

    private YouTubeSignInResult result = YouTubeSignInResult.Abandoned;

    /// <summary>A harvest is already running, or has already succeeded.</summary>
    private bool settled;

    private YouTubeSignInForm()
    {
        // The instruction is the title, which is the one piece of window furniture that
        // needs no colour of its own and cannot fall out of step with the theme.
        Text = Strings.Download_SignInWindowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new System.Drawing.Size(1000, 760);
        MinimumSize = new System.Drawing.Size(560, 480);
        ShowInTaskbar = true;

        view.Dock = DockStyle.Fill;
        Controls.Add(view);

        // The app's own theme: icon, dark title bar, control colours. Nothing is picked here.
        ShareXResources.ApplyTheme(this);
    }

    /// <summary>
    /// Whether there is a browser on this PC to sign in with.
    /// </summary>
    /// <remarks>
    /// Asked before the window is built rather than after. WebView2's runtime is a Windows
    /// component that arrives with Edge, so on any current Windows it is there - but "any
    /// current Windows" is not "every PC", and the alternative to asking is an empty white
    /// window with no explanation in it.
    /// </remarks>
    public static bool IsRuntimeInstalled
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception);
                return false;
            }
        }
    }

    /// <summary>
    /// Open the window, or bring the open one forward, and complete when it closes.
    /// </summary>
    /// <remarks>
    /// One window at a time. Two would share a browser profile and race each other into
    /// the same jar, and the second would be answering a question the first is already
    /// asking. The second caller waits on the first window's outcome, which is the answer
    /// they wanted anyway.
    /// </remarks>
    public static Task<YouTubeSignInResult> ShowAsync()
    {
        if (current is { IsDisposed: false } open)
        {
            open.Activate();
            return open.completion.Task;
        }

        if (!IsRuntimeInstalled)
        {
            return Task.FromResult(YouTubeSignInResult.RuntimeMissing);
        }

        // A sign-in that starts with no jar starts with no browser profile either. This is
        // what turns a sign-out whose folder delete lost the race with a closing browser
        // process into a real sign-out - see YouTubeCookies for the whole rule.
        if (!YouTubeCookies.IsSignedIn)
        {
            YouTubeCookies.WipeProfile();
        }

        YouTubeSignInForm form = new();
        current = form;

        form.FormClosed += (_, _) =>
        {
            current = null;
            form.completion.TrySetResult(form.result);
        };

        form.Show();
        form.Activate();

        return form.completion.Task;
    }

    /// <summary>
    /// Close the sign-in window if one is open, because the app is going away.
    /// </summary>
    /// <remarks>
    /// The Download tab owns this window without being its parent - it is a WinForms
    /// window and the tab is an Avalonia control - so nothing would otherwise take it down
    /// when the tab is torn down for good, and a browser window left standing over a
    /// closing app is a window with no owner to close it.
    /// </remarks>
    public static void CloseIfOpen()
    {
        if (current is { IsDisposed: false } open)
        {
            try
            {
                open.Close();
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception);
            }
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Started, not awaited: OnLoad returns to the message loop immediately and the
        // browser comes up underneath it.
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            // Before the browser, not after. WebView2 creates the profile folder when it
            // first needs it, and what lands in there is the same session the jar holds -
            // so the permissions have to be on the folder already, for the contents to
            // inherit them.
            YouTubeCookies.Prepare();

            // The profile is ours and it is on disk, which is the whole point: a private
            // session would make the owner sign in again every time Modlight restarts.
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: YouTubeCookies.ProfileFolder,
                options: null);

            await view.EnsureCoreWebView2Async(environment);

            if (IsDisposed || view.CoreWebView2 == null)
            {
                return;
            }

            view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            view.CoreWebView2.Navigate(YouTubeCookies.LoginUrl);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);

            result = YouTubeSignInResult.Failed;

            if (!IsDisposed)
            {
                Close();
            }
        }
    }

    /// <summary>
    /// Every finished navigation asks the same question: are we there yet?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event argument is deliberately IGNORED, and it must stay ignored.
    /// <c>IsSuccess</c> looks like the obvious guard and is not one: measured against a
    /// plain load of youtube.com in a fresh profile, WebView2 reported
    /// <c>IsSuccess = false</c> with <c>HttpStatusCode = 200</c> on a page that had loaded
    /// perfectly well - the sign-in flow is a chain of redirects and consent hops, and any
    /// of them ending in a way WebView2 calls unsuccessful would mean the cookies are
    /// never read and the window never closes. What decides is the address and the jar,
    /// both checked below.
    /// </para>
    /// <para>
    /// <c>async void</c> because this is an event handler, which is the one place it is
    /// the correct signature - and it has a catch-all for the same reason, since an
    /// exception escaping here has no caller to reach and would take the process down.
    /// </para>
    /// </remarks>
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            await CheckAsync();
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }

    private async Task CheckAsync()
    {
        if (settled || IsDisposed || view.CoreWebView2 == null)
        {
            return;
        }

        if (!IsSignedInPage(view.CoreWebView2.Source))
        {
            return;
        }

        // Claim the harvest before the first await. Navigations arrive in bursts, and two
        // overlapping harvests would write the jar twice and close the window twice.
        settled = true;

        await Task.Delay(settle);

        if (IsDisposed || view.CoreWebView2 == null)
        {
            return;
        }

        // Everything in the jar, not just what this URL would be sent. The profile has only
        // ever been to Google's sign-in and to YouTube, so "everything" is exactly the
        // sign-in that was just performed - and YouTube's session is spread across
        // .google.com and .youtube.com, so asking for one host's worth would leave half of
        // it behind.
        IReadOnlyList<CoreWebView2Cookie> cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync(null);

        List<string> names = new(cookies.Count);
        List<NetscapeCookie> jar = new(cookies.Count);

        foreach (CoreWebView2Cookie cookie in cookies)
        {
            names.Add(cookie.Name);
            jar.Add(new NetscapeCookie(cookie.Domain, cookie.Path, cookie.Name, cookie.Value,
                cookie.IsSecure, Expiry(cookie)));
        }

        if (!YouTubeCookies.IsSignedInJar(names))
        {
            // On youtube.com but not signed in - the sign-in page redirects through here
            // on its way in. Let the user carry on.
            settled = false;
            return;
        }

        try
        {
            if (YouTubeCookies.Save(jar) == 0)
            {
                // Signed in, and yet nothing representable came back. Not a state anybody
                // can act on from inside the window, so let the next navigation try again.
                settled = false;
                return;
            }
        }
        catch (Exception exception)
        {
            // A disk that will not take the file. Left alone this would be the worst
            // outcome of the lot: the sign-in has WORKED, the window would sit there
            // forever waiting for a completion that already happened, and closing it would
            // report nothing wrong. Fail out loud instead.
            DebugHelper.WriteException(exception);

            result = YouTubeSignInResult.Failed;

            if (!IsDisposed)
            {
                Close();
            }

            return;
        }

        result = YouTubeSignInResult.Captured;

        if (!IsDisposed)
        {
            Close();
        }
    }

    /// <summary>
    /// Whether this address is the far side of the sign-in.
    /// </summary>
    /// <remarks>
    /// Suffix matching on the host, never <c>Contains</c> - "youtube.com.example.org"
    /// contains "youtube.com" and belongs to somebody else, and this decides when to read a
    /// credential out of the browser. The signin path is excluded because the flow lands
    /// there first, on its way to the home page, with the post-login cookies not yet set.
    /// </remarks>
    private static bool IsSignedInPage(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();

        if (host != "youtube.com" && !host.EndsWith(".youtube.com", StringComparison.Ordinal))
        {
            return false;
        }

        return !uri.AbsolutePath.StartsWith("/signin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A cookie's expiry as seconds since the epoch, or zero for "session".
    /// </summary>
    /// <remarks>
    /// Zero is not a fallback that loses information: the jar format writes a session
    /// cookie as an EMPTY expiry column, and that is what a zero here produces. Reading
    /// <see cref="CoreWebView2Cookie.IsSession"/> first rather than inferring it from the
    /// date, because the wrapper's date for a session cookie is a sentinel rather than a
    /// time, and which sentinel is not something to depend on.
    /// </remarks>
    private static long Expiry(CoreWebView2Cookie cookie)
    {
        if (cookie.IsSession)
        {
            return 0;
        }

        try
        {
            DateTime expires = cookie.Expires;
            DateTime utc = expires.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(expires, DateTimeKind.Utc)
                : expires.ToUniversalTime();

            long seconds = new DateTimeOffset(utc).ToUnixTimeSeconds();

            return seconds > 0 ? seconds : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
