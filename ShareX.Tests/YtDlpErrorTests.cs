#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;

namespace ShareX.Tests;

/// <summary>
/// Reading yt-dlp's failures, against lines yt-dlp really prints.
/// </summary>
/// <remarks>
/// <para>
/// The suite exists because a new class of error was added to a reader that already had
/// seven, and the risk in doing that is not that the new one fails to fire - it is that it
/// fires on somebody else's message. Each class leads the reader somewhere different: sign
/// in, wait, try elsewhere, update. Sent to the wrong one, a person stops looking, so the
/// half of this file that matters is the half that proves the OTHER classes still win the
/// messages they used to win.
/// </para>
/// <para>
/// It failed to prevent exactly that, once, and the shape of the failure is worth keeping
/// in front of whoever edits this next. The "out of date" class was written against an
/// assumption stated in a comment - that yt-dlp attaches its "please report this issue /
/// confirm you are on the latest version" block ONLY to failures it did not expect, and
/// that a failure with a real cause behind it carries none of it - and every line in this
/// file agreed with the assumption, because every line in this file had been chosen by the
/// person who held it. The assumption is false, and the owner's TikTok download proved it.
/// So the block below marked CAPTURED is different in kind from the rest: those lines were
/// not written here, they were produced by running the real binary and pasting what came
/// back, which is the only procedure that can contradict the person doing the writing.
/// </para>
/// <para>
/// Every string in the CAPTURED block came out of yt-dlp 2026.08.19 on 2026-09-10, and the
/// commands that produced them are named beside each one.
/// </para>
/// </remarks>
internal static class YtDlpErrorTests
{
    /// <summary>
    /// The owner's failure, captured from his URL rather than reconstructed.
    /// </summary>
    /// <remarks>
    /// yt-dlp --ignore-config --simulate
    /// https://www.tiktok.com/@newmt2.com/video/7679863973902077206
    ///
    /// He was told "this downloader is out of date for this site" about the newest build
    /// yt-dlp has published. What TikTok is saying is that the post cannot be served.
    /// </remarks>
    private const string OwnerTikTok =
        "ERROR: [TikTok] 7679863973902077206: Video not available, status code 10240; please report this " +
        "issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template. " +
        "Confirm you are on the latest version using  yt-dlp -U";

    /// <summary>An earlier TikTok failure of his, from a build that really was months old.</summary>
    private const string OldTikTok =
        "ERROR: [TikTok] 7682821322468117792: Unexpected response from webpage request; please report this " +
        "issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template. " +
        "Confirm you are on the latest version using  yt-dlp -U";

    /// <summary>
    /// The second counter-example, and the more convincing one.
    /// </summary>
    /// <remarks>
    /// yt-dlp --ignore-config --simulate https://www.instagram.com/p/AAAAAAAAAAA/
    ///
    /// One message that says, in order: this needs a login, and if it does not, report a bug
    /// in me. A real cause and the whole report block in the same sentence. Any rule that
    /// reads the block as "the downloader is old" gets this one wrong too.
    /// </remarks>
    private const string Instagram =
        "ERROR: [Instagram] AAAAAAAAAAA: Instagram sent an empty media response. Check if this post is " +
        "accessible in your browser without being logged-in. If it is not, then use --cookies-from-browser " +
        "or --cookies for the authentication. See  https://github.com/yt-dlp/yt-dlp/wiki/FAQ  for how to " +
        "manually pass cookies. Otherwise, if the post is accessible in browser without being logged-in, " +
        "please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the " +
        "appropriate issue template. Confirm you are on the latest version using  yt-dlp -U";

    private static int failures;

    internal static int Run()
    {
        failures = 0;

        Console.WriteLine("YtDlpError: CAPTURED - real output of yt-dlp 2026.08.19 on 2026-09-10");

        // tiktok.com/@newmt2.com/video/7679863973902077206 - the wrong diagnosis, undone.
        Is("the owner's TikTok link", OwnerTikTok, YtDlpErrorKind.Unavailable);

        // youtube.com/watch?v=AAAAAAAAAAA. The old list held "video unavailable" and YouTube
        // writes "video IS unavailable", so this fell all the way through to Unknown.
        Is("a YouTube id that leads nowhere",
            "ERROR: [youtube] AAAAAAAAAAA: This video is unavailable", YtDlpErrorKind.Unavailable);

        // tiktok.com/@tiktok/video/1234567890123456789 - TikTok status 10204, raised with
        // expected=True, so it arrives with no report block at all.
        Is("TikTok turning this address away",
            "ERROR: [TikTok] 1234567890123456789: Your IP address is blocked from accessing this post",
            YtDlpErrorKind.IpBlocked);

        // example.invalid - three hundred characters of Python where "check your
        // connection" was the whole answer.
        Is("a name that does not resolve",
            "ERROR: [generic] 1: Unable to download webpage: HTTPSConnection(host='example.invalid', " +
            "port=443): Failed to resolve 'example.invalid' ([Errno 11001] getaddrinfo failed) (caused by " +
            "TransportError(\"HTTPSConnection(host='example.invalid', port=443): Failed to resolve\"))",
            YtDlpErrorKind.Network);

        // youtube.com/watch?v=Tq92D6wQ1mg
        Is("an age gate, which opens with the words of a bot check",
            "ERROR: [youtube] Tq92D6wQ1mg: Sign in to confirm your age. Use --cookies-from-browser or " +
            "--cookies for the authentication. See  https://github.com/yt-dlp/yt-dlp/wiki/FAQ  for how to " +
            "manually pass cookies.", YtDlpErrorKind.AgeRestricted);

        Is("a login wall wearing the whole report block", Instagram, YtDlpErrorKind.NeedsAccount);

        // x.com/i/status/1234567890123456789
        Is("a post with no video in it",
            "ERROR: [twitter] 1234567890123456789: No video could be found in this tweet",
            YtDlpErrorKind.NoMedia);

        // facebook.com/watch/?v=1234567890 - yt-dlp genuinely unable to read the page.
        Is("a document yt-dlp cannot parse",
            "ERROR: [facebook] 1234567890: Cannot parse data; please report this issue on  " +
            "https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template. " +
            "Confirm you are on the latest version using  yt-dlp -U", YtDlpErrorKind.SelfReportedBug);

        // vimeo.com/999999999
        Is("a 404 behind Python's own restatement of it",
            "ERROR: [vimeo] 999999999: Unable to download webpage: HTTP Error 404: Not Found (caused by " +
            "<HTTPError 404: Not Found>)", YtDlpErrorKind.Gone);

        // example.com and a line of prose pasted into the box.
        Is("an address with no extractor", "ERROR: Unsupported URL: https://example.com/",
            YtDlpErrorKind.Unsupported);
        Is("something that is not an address at all",
            "ERROR: [generic] 'not a url at all' is not a valid URL", YtDlpErrorKind.Unsupported);

        // youtube.com/playlist?list=PLdoesnotexist000 - a 400 nobody has a sentence for, and
        // yt-dlp's own line is the better answer.
        Is("a status we have no advice about",
            "ERROR: [youtube:tab] PLdoesnotexist000: Unable to download API page: HTTP Error 400: Bad " +
            "Request (caused by <HTTPError 400: Bad Request>)", YtDlpErrorKind.Unknown);

        Console.WriteLine("YtDlpError: yt-dlp blaming itself, which is not the same as being old");
        Is("the older TikTok failure, which really was extractor rot", OldTikTok,
            YtDlpErrorKind.SelfReportedBug);
        Is("a page whose fields have moved",
            "ERROR: [youtube] dQw4w9WgXcQ: Unable to extract yt initial data; please report this issue on " +
            "https://github.com/yt-dlp/yt-dlp/issues?q=", YtDlpErrorKind.SelfReportedBug);
        Is("a signature routine that no longer matches",
            "ERROR: [youtube] dQw4w9WgXcQ: Signature extraction failed: Some formats may be missing",
            YtDlpErrorKind.SelfReportedBug);
        Is("and the report block on its own",
            "ERROR: [instagram] ABC: Unexpected response from webpage request; please report this issue on " +
            "https://github.com/yt-dlp/yt-dlp/issues?q=", YtDlpErrorKind.SelfReportedBug);

        Console.WriteLine("YtDlpError: the classes that were already right keep their messages");
        Is("the bot check, which also says the word cookies",
            "ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm you're not a bot. Use --cookies-from-browser or " +
            "--cookies for the authentication.", YtDlpErrorKind.BotCheck);
        Is("a private video",
            "ERROR: [youtube] dQw4w9WgXcQ: Private video. Sign in if you've been granted access to this video",
            YtDlpErrorKind.NeedsAccount);
        Is("members only",
            "ERROR: [youtube] dQw4w9WgXcQ: Join this channel to get access to members-only content like this " +
            "video, and other exclusive perks.", YtDlpErrorKind.NeedsAccount);
        Is("a TikTok post behind a login",
            "ERROR: [TikTok] 7682821322468117792: You do not have permission to view this post. Log into an " +
            "account that has access. Use --cookies-from-browser or --cookies for the authentication.",
            YtDlpErrorKind.NeedsAccount);
        Is("a geo block",
            "ERROR: [youtube] dQw4w9WgXcQ: Video unavailable. This video is not available in your country",
            YtDlpErrorKind.GeoBlocked);
        Is("an uploader who kept it at home",
            "ERROR: [youtube] dQw4w9WgXcQ: Video unavailable. The uploader has not made this video available " +
            "in your country", YtDlpErrorKind.GeoBlocked);
        Is("a deleted video",
            "ERROR: [youtube] dQw4w9WgXcQ: Video unavailable. This video has been removed by the uploader",
            YtDlpErrorKind.Gone);
        Is("a rate limit",
            "ERROR: [youtube] dQw4w9WgXcQ: Unable to download API page: HTTP Error 429: Too Many Requests",
            YtDlpErrorKind.RateLimited);
        Is("a flat 403, whose status is in the part that gets cut",
            "ERROR: unable to download video data: HTTP Error 403: Forbidden (caused by " +
            "<HTTPError 403: Forbidden>); please report this issue", YtDlpErrorKind.Forbidden);

        Console.WriteLine("YtDlpError: a number in an id is not a status code");
        Is("a real 404",
            "ERROR: [generic] Unable to download webpage: HTTP Error 404: Not Found", YtDlpErrorKind.Gone);

        // The bare "404" this list used to hold matched any three digits in a row, and a
        // TikTok id is nineteen of them. It turned an out-of-date extractor into "this video
        // has been deleted", which is a dead end nobody argues with.
        Is("the older TikTok failure, on a video whose id contains 404",
            OldTikTok.Replace("7682821322468117792", "7404821322468117792"), YtDlpErrorKind.SelfReportedBug);
        Is("and a view count does not delete a video either",
            "ERROR: [sometube] Fun clip 404 hilarious: something went sideways", YtDlpErrorKind.Unknown);

        // "429" was still sitting in the rate-limit list with exactly the same defect, and
        // had simply not met an id containing it yet.
        Is("an id containing 429 is not a rate limit",
            "ERROR: [TikTok] 7429821322468117792: something we have never seen", YtDlpErrorKind.Unknown);

        Console.WriteLine("YtDlpError: what we refuse to guess at");
        Is("a disk that is full",
            "ERROR: unable to open for writing: [Errno 28] No space left on device", YtDlpErrorKind.Unknown);
        Is("a permission problem",
            "ERROR: unable to open for writing: [Errno 13] Permission denied", YtDlpErrorKind.Unknown);
        Is("a name we have never seen",
            "ERROR: [somesite] 12: The frobnicator declined", YtDlpErrorKind.Unknown);
        Is("nothing at all", "", YtDlpErrorKind.Unknown);
        Is("whitespace", "   \t  ", YtDlpErrorKind.Unknown);
        Is("null", null, YtDlpErrorKind.Unknown);

        Console.WriteLine("YtDlpError: nobody is told to update without a version behind it");

        // The rule the shipped diagnosis broke. A newer downloader cannot un-delete a video,
        // and it cannot be newer than the newest one published.
        True("the owner's failure offers no update at all",
            !YtDlpErrorReader.OffersUpdate(YtDlpErrorReader.Classify(OwnerTikTok), YtDlpStanding.Unknown));
        True("nor does it once we know a newer build exists",
            !YtDlpErrorReader.OffersUpdate(YtDlpErrorReader.Classify(OwnerTikTok), YtDlpStanding.Behind));
        True("a dead link never offers one",
            !YtDlpErrorReader.OffersUpdate(YtDlpErrorKind.Gone, YtDlpStanding.Behind));
        True("nor does a message we could not read",
            !YtDlpErrorReader.OffersUpdate(YtDlpErrorKind.Unknown, YtDlpStanding.Behind));

        True("a self-reported bug on an old build offers one",
            YtDlpErrorReader.OffersUpdate(YtDlpErrorKind.SelfReportedBug, YtDlpStanding.Behind));
        True("and so does one we could not check, because an offer is not a claim",
            YtDlpErrorReader.OffersUpdate(YtDlpErrorKind.SelfReportedBug, YtDlpStanding.Unknown));
        True("but never on the newest build there is",
            !YtDlpErrorReader.OffersUpdate(YtDlpErrorKind.SelfReportedBug, YtDlpStanding.Current));

        True("the version is only worth asking about when yt-dlp blames itself",
            YtDlpErrorReader.WantsVersionCheck(YtDlpErrorKind.SelfReportedBug));
        True("not for the owner's failure",
            !YtDlpErrorReader.WantsVersionCheck(YtDlpErrorReader.Classify(OwnerTikTok)));
        True("and not for a private video",
            !YtDlpErrorReader.WantsVersionCheck(YtDlpErrorKind.NeedsAccount));

        Console.WriteLine("YtDlpError: the line the reader is shown");
        Same("the ERROR prefix goes", "[youtube] dQw4w9WgXcQ: Nope",
            YtDlpErrorReader.Clean("ERROR: [youtube] dQw4w9WgXcQ: Nope"));
        Same("and Python's restatement of it goes", "unable to download video data: HTTP Error 403: Forbidden",
            YtDlpErrorReader.Clean(
                "ERROR: unable to download video data: HTTP Error 403: Forbidden (caused by <HTTPError 403>)"));

        // The report block is long, and Clean stops at 300 characters. If the self-reported
        // markers were read from the cleaned line rather than the raw one, a message with a
        // long title in front of the block would fall through to "unknown".
        Is("a long title in front of the report block",
            "ERROR: [SomeSite] " + new string('x', 400) + ": Confirm you are on the latest version using yt-dlp -U",
            YtDlpErrorKind.SelfReportedBug);

        Console.WriteLine(failures == 0
            ? "YtDlpError: all checks passed."
            : "YtDlpError: " + failures + " check(s) FAILED.");

        return failures;
    }

    private static void Is(string what, string? raw, YtDlpErrorKind expected)
    {
        YtDlpErrorKind actual = YtDlpErrorReader.Classify(raw);

        if (actual != expected)
        {
            failures++;
            Console.WriteLine("FAIL: " + what + "\n  expected: " + expected + "\n  actual:   " + actual);
            return;
        }

        Console.WriteLine("  ok   " + what + " -> " + actual);
    }

    private static void True(string what, bool actual)
    {
        if (!actual)
        {
            failures++;
            Console.WriteLine("FAIL: " + what);
            return;
        }

        Console.WriteLine("  ok   " + what);
    }

    private static void Same(string what, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            failures++;
            Console.WriteLine("FAIL: " + what + "\n  expected: " + expected + "\n  actual:   " + actual);
            return;
        }

        Console.WriteLine("  ok   " + what);
    }
}
