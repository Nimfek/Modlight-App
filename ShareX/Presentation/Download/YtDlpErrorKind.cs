#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;

namespace ShareX;

/// <summary>What kind of failure yt-dlp reported, before anybody words it.</summary>
internal enum YtDlpErrorKind
{
    /// <summary>Nothing recognised. The reader gets yt-dlp's own line.</summary>
    Unknown,

    /// <summary>"Sign in to confirm you're not a bot", which YouTube serves to whole networks.</summary>
    BotCheck,

    /// <summary>An age gate. A signed-in account old enough to see it is what opens it.</summary>
    AgeRestricted,

    /// <summary>Somewhere else's video, and it stays somewhere else's.</summary>
    GeoBlocked,

    /// <summary>This address is turned away, wherever it is. A VPN is the usual cause.</summary>
    IpBlocked,

    /// <summary>Private, members-only, login required: an account that can see it would fix it.</summary>
    NeedsAccount,

    /// <summary>Deleted, terminated, or a 404. Nothing will bring it back.</summary>
    Gone,

    /// <summary>
    /// The site refuses to serve it and will not say why - removed, private or restricted,
    /// and the refusal reads the same whichever it is.
    /// </summary>
    Unavailable,

    /// <summary>The address is fine and there is simply no video or audio in it.</summary>
    NoMedia,

    /// <summary>Too many requests. Waiting fixes it; nothing else does.</summary>
    RateLimited,

    /// <summary>Not an address yt-dlp knows what to do with.</summary>
    Unsupported,

    /// <summary>The site was never reached: DNS, a refused connection, a dead line.</summary>
    Network,

    /// <summary>A flat 403 with nothing attached.</summary>
    Forbidden,

    /// <summary>
    /// yt-dlp called this failure a bug in itself.
    /// </summary>
    /// <remarks>
    /// A statement about the MESSAGE, not about the version on this PC. Whether the fix is
    /// a newer yt-dlp is a separate question with a separate answer, and running the two
    /// together is what produced the wrong diagnosis this file was rewritten after: see
    /// <see cref="YtDlpErrorReader.OffersUpdate"/>.
    /// </remarks>
    SelfReportedBug
}

/// <summary>How the yt-dlp on this PC stands against the newest build its authors published.</summary>
/// <remarks>
/// Three states and not a bool, because "we did not find out" is the common one - the
/// publisher is one HTTP request away and that request fails on every machine that is
/// offline, behind a captive portal or out of GitHub's hourly budget. A bool would have to
/// fold that case into either "behind" or "current", and both of those are claims.
/// </remarks>
internal enum YtDlpStanding
{
    /// <summary>Not asked, or the answer did not arrive. Nothing may be claimed either way.</summary>
    Unknown,

    /// <summary>A newer build than the one here has been published.</summary>
    Behind,

    /// <summary>What is here IS the newest published build.</summary>
    Current
}

/// <summary>
/// Reading yt-dlp's last line. Pure: no strings for people, no localisation, no UI.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="YtDlpError"/> so the decision can be run against real yt-dlp
/// output in the test project, which is the only way to be sure of the one property that
/// matters here: that adding a class does not quietly steal messages from the classes that
/// were already right. A misclassification is not a cosmetic problem - each class leads the
/// reader somewhere different, and the wrong door is worse than no door, because somebody
/// sent to fix the wrong thing stops looking.
/// </para>
/// <para>
/// Order is the whole design. The most specific reading wins: a bot check also mentions
/// signing in, a rate limit on a private video is still a private video, and an age gate is
/// worded as a sign-in. Anything that reaches the bottom is left as
/// <see cref="YtDlpErrorKind.Unknown"/> on purpose.
/// </para>
/// <para>
/// <b>The correction this file exists in its present shape for.</b> It used to decide that
/// yt-dlp was out of date by matching "confirm you are on the latest version" and "please
/// report this issue on", on the stated assumption that yt-dlp attaches that block only to
/// failures it raises as UNEXPECTED, and that a failure with a real cause behind it carries
/// none of it. The assumption is false. In yt-dlp's own source the block comes from
/// <c>bug_reports_message()</c> and is appended by <c>ExtractorError</c> whenever
/// <c>expected</c> is not set - and plenty of ordinary causes are raised without it. The
/// TikTok extractor ends with
/// <c>raise ExtractorError(f'Video not available, status code {status}', video_id=video_id)</c>,
/// no <c>expected=True</c> anywhere, so TikTok saying a post cannot be served arrives
/// wearing the whole "report this bug" block. The owner was told his downloader was too old
/// on the day it was the newest build published. So: the specific cause is read FIRST, and
/// the block is only ever read as "yt-dlp blames itself", never as "yt-dlp is out of date" -
/// that second step needs a version number, and it is not this file's to take.
/// </para>
/// </remarks>
internal static class YtDlpErrorReader
{
    /// <summary>Longer than this and the message stops being read.</summary>
    private const int MaxLength = 300;

    /// <summary>
    /// An age gate.
    /// </summary>
    /// <remarks>
    /// Ahead of <see cref="botCheck"/>, which is not an accident: YouTube words this one as
    /// "Sign in to confirm your age", the bot check as "Sign in to confirm you're not a
    /// bot", and the shared prefix used to hand every age gate to the bot check. The two
    /// remedies read alike and are not: the bot check is about this NETWORK looking
    /// automated and clears when any signed-in session is used; the age gate is about this
    /// ACCOUNT, and does not clear however many times somebody signs in with the one that
    /// is already failing.
    /// Captured 2026-09-10 from yt-dlp 2026.08.19:
    /// "[youtube] Tq92D6wQ1mg: Sign in to confirm your age. Use --cookies-from-browser or
    /// --cookies for the authentication."
    /// </remarks>
    private static readonly string[] ageRestricted =
    {
        "confirm your age",
        "age-restricted",
        "age restricted",
        "inappropriate for some users"
    };

    private static readonly string[] botCheck =
    {
        "confirm you're not a bot",
        "confirm you are not a bot",
        "sign in to confirm"
    };

    private static readonly string[] geoBlocked =
    {
        "not available in your country",
        "not available in your region",
        "not available from your location",
        "made this video available in your country",
        "blocked it in your country",
        "blocked in your country",
        "geo restricted",
        "geo-restricted",
        "region locked",
        "region-locked"
    };

    /// <summary>
    /// This connection is turned away, and it would be turned away from anywhere on it.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="geoBlocked"/> because the advice is the opposite one. A
    /// geo-block is answered by a VPN; this is what a VPN CAUSES - TikTok raises it, with
    /// expected=True, for datacentre ranges and for exit nodes it has seen too much of.
    /// Telling somebody to switch a VPN on when the VPN is the reason is the same shape of
    /// wrong answer this file was rewritten to stop making.
    /// Captured 2026-09-10: "[TikTok] 1234567890123456789: Your IP address is blocked from
    /// accessing this post".
    /// </remarks>
    private static readonly string[] ipBlocked =
    {
        "ip address is blocked",
        "blocked from accessing this post"
    };

    private static readonly string[] needsAccount =
    {
        "private video",
        "video is private",
        "account is private",
        "this post is private",
        "members-only",
        "join this channel",
        "login required",
        "log into an account that has access",
        "log in for access",
        "requested format is not available", // only reached when auth hid the good formats
        "sign in to view",
        "sign in if you've been granted access",
        "--cookies"
    };

    /// <summary>
    /// The thing at that address is gone.
    /// </summary>
    /// <remarks>
    /// The 404 is spelt out rather than left as the bare number it used to be. "404" as a
    /// substring matches a video id, a duration and a view count - and a false "this video
    /// is gone" is the worst kind of wrong answer, because it is a dead end: somebody told
    /// their download failed for a reason nothing can fix does not try anything else. The
    /// phrases below are how yt-dlp actually writes it.
    ///
    /// Ahead of <see cref="unavailable"/>, and that order carries information: what is here
    /// is the site NAMING the reason, so "Video unavailable. This video has been removed by
    /// the uploader" is a removal and not a shrug.
    /// </remarks>
    private static readonly string[] gone =
    {
        "video unavailable",
        "has been removed",
        "no longer available",
        "has been deleted",
        "account has been terminated",
        "http error 404",
        "httperror 404",
        "error 404",
        "404: not found"
    };

    /// <summary>
    /// The site will not serve it and does not say why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner's failure, and the reason this class exists. TikTok answers a removed,
    /// private or restricted post with a numeric status it does not document -
    /// "Video not available, status code 10240" - and yt-dlp passes the number through
    /// verbatim. Its extractor names only two of them (10204, an IP block; 10216 and 10222,
    /// a private post or account) and raises everything else through the fallback that
    /// carries the bug-report block. So the number is not worth decoding here and is not
    /// worth showing: what is true of every one of them is that the item is not obtainable
    /// and the site is not saying which of three reasons applies.
    /// </para>
    /// <para>
    /// Deliberately NOT folded into <see cref="YtDlpErrorKind.Gone"/>. "It is gone" is a
    /// dead end and ends the search; "the site will not serve it, and the quickest way to
    /// find out why is to open the link" is a next step, and it is the honest one when the
    /// site itself refused to distinguish removed from private from region-locked.
    /// </para>
    /// <para>
    /// Captured 2026-09-10 from yt-dlp 2026.08.19: "[TikTok] 7679863973902077206: Video not
    /// available, status code 10240; please report this issue on ..." and
    /// "[youtube] AAAAAAAAAAA: This video is unavailable" - the second of which the previous
    /// list missed entirely, because it held "video unavailable" and YouTube writes "video
    /// IS unavailable".
    /// </para>
    /// </remarks>
    private static readonly string[] unavailable =
    {
        "video not available, status code",
        "video not available",
        "video is unavailable",
        "this video is not available",
        "post is not available",
        "content isn't available",
        "content is not available",
        "media is not available"
    };

    /// <summary>
    /// The address works. There is just nothing in it to download.
    /// </summary>
    /// <remarks>
    /// Captured 2026-09-10: "[twitter] 1234567890123456789: No video could be found in this
    /// tweet". Worth its own sentence because every other class here says something about
    /// permission, geography or software, and this one says the link was simply the wrong
    /// link - which is the only failure on this list the reader can fix in two seconds.
    /// </remarks>
    private static readonly string[] noMedia =
    {
        "no video could be found",
        "there is no video in this",
        "there's no video in this",
        "no video formats found",
        "no media found"
    };

    /// <summary>
    /// Too many requests.
    /// </summary>
    /// <remarks>
    /// The bare "429" this list used to hold was the same latent bug the 404 above was
    /// already fixed for, and it had simply not been triggered yet: three digits in a row
    /// match a video id, and a TikTok id is nineteen of them. Spelt out now, for the same
    /// reason and before somebody's id contains it.
    /// </remarks>
    private static readonly string[] rateLimited =
    {
        "too many requests",
        "http error 429",
        "httperror 429",
        "429: too many requests",
        "error 429",
        "rate-limit",
        "rate limit"
    };

    private static readonly string[] unsupported =
    {
        "unsupported url",
        "is not a valid url"
    };

    /// <summary>
    /// The site was never reached.
    /// </summary>
    /// <remarks>
    /// Matched against the RAW line: Python puts the transport failure inside the
    /// "(caused by ...)" tail that <see cref="Clean"/> removes. Every marker here is an
    /// operating-system or Python phrase rather than an English sentence, because the
    /// alternatives - "timeout", "refused" - are words that turn up in the titles and ids
    /// yt-dlp prints beside its errors.
    /// Captured 2026-09-10: "[generic] 1: Unable to download webpage: HTTPSConnection(...):
    /// Failed to resolve 'example.invalid' ([Errno 11001] getaddrinfo failed) (caused by
    /// TransportError(...))" - which used to reach the reader as three hundred characters
    /// of Python.
    /// </remarks>
    private static readonly string[] network =
    {
        "failed to resolve",
        "getaddrinfo failed",
        "name or service not known",
        "temporary failure in name resolution",
        "connection refused",
        "connection reset",
        "connection aborted",
        "network is unreachable",
        "read operation timed out",
        "connection timed out",
        "certificate verify failed",
        "unable to connect to proxy",
        "transporterror"
    };

    /// <summary>
    /// A plain refusal, with no explanation attached.
    /// </summary>
    /// <remarks>
    /// Matched against the RAW line rather than the cleaned one, because <see cref="Clean"/>
    /// cuts everything from "(caused by" onwards - and that is exactly where yt-dlp puts
    /// <c>HTTPError 403</c>. Phrases rather than the bare number, since "403" on its own
    /// turns up in video titles and in durations.
    /// </remarks>
    private static readonly string[] forbidden =
    {
        "http error 403",
        "httperror 403",
        "403: forbidden",
        "403 forbidden"
    };

    /// <summary>
    /// yt-dlp saying this failure is a fault in yt-dlp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first two are the block <c>bug_reports_message()</c> appends. What it means is
    /// exactly what it says - "I did not expect this, report it to me" - and NOT that the
    /// copy on this PC is old. yt-dlp attaches it to every error raised without
    /// <c>expected=True</c>, and its extractors raise plenty of ordinary, permanent,
    /// nothing-to-do-with-the-version causes that way. That is why this list is read LAST,
    /// after every specific cause above has had its turn, and why what it produces is a
    /// statement about the message rather than about the version.
    /// </para>
    /// <para>
    /// The rest are the shapes extractor rot takes when it is raised as an ordinary error:
    /// a page whose fields have moved, a signature or nsig routine that no longer matches
    /// what the site now serves, a document yt-dlp cannot parse. Captured 2026-09-10:
    /// "[facebook] 1234567890: Cannot parse data; please report this issue on ...".
    /// </para>
    /// <para>
    /// Matched against the RAW line, not the cleaned one. <see cref="Clean"/> stops at 300
    /// characters and yt-dlp's report block is long enough to sit past that mark.
    /// </para>
    /// </remarks>
    private static readonly string[] selfReportedBug =
    {
        "confirm you are on the latest version",
        "confirm you're on the latest version",
        "please report this issue on",
        "unexpected response from webpage",
        "unable to extract",
        "cannot parse data",
        "nsig extraction failed",
        "signature extraction failed"
    };

    /// <summary>
    /// Which kind of failure this is.
    /// </summary>
    /// <param name="raw">The last thing yt-dlp said.</param>
    public static YtDlpErrorKind Classify(string? raw)
    {
        string line = Clean(raw);

        if (line.Length == 0)
        {
            return YtDlpErrorKind.Unknown;
        }

        string probe = line.ToLowerInvariant();

        // The raw line, not the cleaned one: the transport failure, the status code and the
        // report block all live in the parts Clean cuts.
        string full = (raw ?? "").ToLowerInvariant();

        if (Contains(probe, ageRestricted))
        {
            // Above the bot check, which shares its opening words. See the list.
            return YtDlpErrorKind.AgeRestricted;
        }

        if (Contains(probe, botCheck))
        {
            return YtDlpErrorKind.BotCheck;
        }

        if (Contains(probe, geoBlocked))
        {
            // Deliberately above the account checks. Somewhere else's video stays somewhere
            // else's after signing in, and an offer to fix it wastes the one thing the
            // person was going to try.
            return YtDlpErrorKind.GeoBlocked;
        }

        if (Contains(probe, ipBlocked))
        {
            return YtDlpErrorKind.IpBlocked;
        }

        if (Contains(probe, needsAccount))
        {
            return YtDlpErrorKind.NeedsAccount;
        }

        if (Contains(probe, gone))
        {
            return YtDlpErrorKind.Gone;
        }

        if (Contains(probe, unavailable))
        {
            return YtDlpErrorKind.Unavailable;
        }

        if (Contains(probe, noMedia))
        {
            return YtDlpErrorKind.NoMedia;
        }

        if (Contains(probe, rateLimited))
        {
            return YtDlpErrorKind.RateLimited;
        }

        if (Contains(probe, unsupported))
        {
            return YtDlpErrorKind.Unsupported;
        }

        if (Contains(full, network))
        {
            return YtDlpErrorKind.Network;
        }

        if (Contains(full, forbidden))
        {
            return YtDlpErrorKind.Forbidden;
        }

        // Last before "unknown", and that position is the whole argument for it. Every class
        // above reads a cause the site actually stated; this one reads yt-dlp's opinion of
        // its own failure, which is the weakest evidence on the page and is attached to
        // messages that have nothing to do with yt-dlp's age. Placed any higher it eats
        // them, which is precisely what it did.
        if (Contains(full, selfReportedBug))
        {
            return YtDlpErrorKind.SelfReportedBug;
        }

        return YtDlpErrorKind.Unknown;
    }

    /// <summary>
    /// Whether finding out how old this yt-dlp is could change what the reader is told.
    /// </summary>
    /// <remarks>
    /// Asked before the version check is run, because that check spawns a process and makes
    /// an HTTP request, and doing either on a failure whose answer is already settled - a
    /// private video, a dead link - would be paying for an answer nobody reads.
    /// </remarks>
    public static bool WantsVersionCheck(YtDlpErrorKind kind) =>
        kind == YtDlpErrorKind.SelfReportedBug;

    /// <summary>
    /// Whether the row should offer to fetch a newer yt-dlp.
    /// </summary>
    /// <remarks>
    /// The rule the wrong diagnosis broke, in one line and testable. Never for a cause the
    /// site stated - a newer downloader does not un-delete a video - and never once the
    /// version has been checked and found to be the newest published one, because an update
    /// button that provably cannot change anything is worse than no button: it is a second
    /// wrong answer offered after the first.
    /// </remarks>
    public static bool OffersUpdate(YtDlpErrorKind kind, YtDlpStanding standing) =>
        kind == YtDlpErrorKind.SelfReportedBug && standing != YtDlpStanding.Current;

    /// <summary>
    /// yt-dlp's line, without the parts that are for us rather than for the reader.
    /// </summary>
    /// <remarks>
    /// The "ERROR:" prefix is noise once the row is already marked as failed, and the
    /// bracketed cause yt-dlp appends - "(caused by HTTPError 403...)" - repeats what the
    /// first half already said in Python's words.
    /// </remarks>
    public static string Clean(string? raw)
    {
        string line = (raw ?? "").Trim();

        if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            line = line.Substring("ERROR:".Length).Trim();
        }

        int caused = line.IndexOf("(caused by", StringComparison.OrdinalIgnoreCase);

        if (caused > 0)
        {
            line = line.Substring(0, caused).Trim();
        }

        if (line.Length > MaxLength)
        {
            line = line.Substring(0, MaxLength).TrimEnd() + "…";
        }

        return line;
    }

    private static bool Contains(string probe, string[] markers)
    {
        foreach (string marker in markers)
        {
            if (probe.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
