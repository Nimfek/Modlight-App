#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.Localization;

namespace ShareX;

/// <summary>A failure, in the three things the queue needs to know about one.</summary>
internal readonly struct YtDlpFailure
{
    public YtDlpFailure(string message, bool needsSignIn, bool needsUpdate = false)
    {
        Message = message;
        NeedsSignIn = needsSignIn;
        NeedsUpdate = needsUpdate;
    }

    /// <summary>Ready to put in front of a person.</summary>
    public string Message { get; }

    /// <summary>
    /// A YouTube sign-in would plausibly fix this, so the row offers the button.
    /// </summary>
    /// <remarks>
    /// A separate fact from the message rather than something the view infers by reading
    /// it. A button that appears because a sentence contained a word is a button that
    /// appears in the wrong place the first time somebody rewrites the sentence, and one
    /// of those sentences is translated.
    /// </remarks>
    public bool NeedsSignIn { get; }

    /// <summary>
    /// A newer yt-dlp is worth fetching for this failure, so the row offers to fetch one
    /// and try the same address again.
    /// </summary>
    /// <remarks>
    /// Two conditions, both required, and the second one is the correction: yt-dlp has to
    /// have called this a fault in itself AND the copy here must not already be the newest
    /// build published. The rule is <see cref="YtDlpErrorReader.OffersUpdate"/>, kept in the
    /// pure reader so it can be proved rather than described.
    ///
    /// Never true at the same time as <see cref="NeedsSignIn"/>. The two remedies point in
    /// opposite directions, and a row offering both is a row that has admitted it does not
    /// know which.
    /// </remarks>
    public bool NeedsUpdate { get; }
}

/// <summary>
/// Turning what yt-dlp printed into something a person can do something about.
/// </summary>
/// <remarks>
/// <para>
/// Each class of failure has a different remedy, and that is the whole reason this exists
/// rather than showing the raw line: "this needs a signed-in browser", "this video is gone",
/// "the site will not serve it and will not say why", "this is blocked where you are", "your
/// address is blocked wherever you are", "you are being rate-limited" and "the downloader
/// itself is at fault" are different next actions, and a user shown the same red text for
/// all of them learns only that downloads sometimes fail.
/// </para>
/// <para>
/// Anything unrecognised keeps yt-dlp's own words. A wrong guess is worse than a raw
/// message: yt-dlp's line names the site, the video and the HTTP status, and a person can
/// search it. A message of ours that misdiagnoses sends them somewhere there is nothing
/// to find - which is exactly what shipped, and what the version check below undoes.
/// </para>
/// <para>
/// The reading itself is <see cref="YtDlpErrorReader"/>, which is free of localisation so
/// it can be run against real yt-dlp output in the tests. What is left here is the wording,
/// and the facts the row acts on.
/// </para>
/// </remarks>
internal static class YtDlpError
{
    /// <summary>
    /// What went wrong, and what the row should offer about it.
    /// </summary>
    /// <param name="kind">
    /// The reading, done once by the caller - which needs it anyway, to decide whether
    /// asking how old yt-dlp is could change the answer.
    /// </param>
    /// <param name="raw">The last thing yt-dlp said.</param>
    /// <param name="youTube">
    /// Whether the link was YouTube's. It decides the wording, because the sign-in this app
    /// offers is a YouTube one: telling somebody whose Instagram download failed to sign in
    /// to YouTube would be worse than saying nothing.
    /// </param>
    /// <param name="signedIn">
    /// Whether a jar was already in use for this run. A refusal WITH a session is different
    /// advice from a refusal without one - the first means the session went stale, the
    /// second means there was never one.
    /// </param>
    /// <param name="version">
    /// What is known about how old this yt-dlp is. Only ever read for
    /// <see cref="YtDlpErrorKind.SelfReportedBug"/>, and
    /// <see cref="YtDlpVersionFacts.Unchecked"/> is a legitimate value: it means nothing may
    /// be said about the version, and the wording then does not say anything about it.
    /// </param>
    public static YtDlpFailure Describe(YtDlpErrorKind kind, string raw, bool youTube, bool signedIn,
        YtDlpVersionFacts version)
    {
        switch (kind)
        {
            case YtDlpErrorKind.BotCheck:
                // Called out separately rather than lumped in with "needs a login", because
                // it is the failure a Modlight user will actually meet: YouTube serves it to
                // datacentre and VPN addresses on videos that are entirely public.
                return SignIn(Strings.Download_ErrorBotCheck, youTube, signedIn);

            case YtDlpErrorKind.AgeRestricted:
                // The only sign-in case where the CAUSE survives the remedy. Everywhere else
                // "sign in and try again" is the whole story; here it is not, because the
                // person may already be signed in with an account the gate will refuse
                // again, and "sign in to YouTube" read by somebody who is signed in to
                // YouTube is an instruction with nothing behind it.
                return youTube
                    ? new YtDlpFailure(Strings.Download_ErrorAgeRestrictedSignIn, true)
                    : new YtDlpFailure(Strings.Download_ErrorAgeRestricted, false);

            case YtDlpErrorKind.GeoBlocked:
                return new YtDlpFailure(Strings.Download_ErrorGeo, false);

            case YtDlpErrorKind.IpBlocked:
                return new YtDlpFailure(Strings.Download_ErrorIpBlocked, false);

            case YtDlpErrorKind.NeedsAccount:
                return SignIn(Strings.Download_ErrorNeedsAccount, youTube, signedIn);

            case YtDlpErrorKind.Gone:
                return new YtDlpFailure(Strings.Download_ErrorGone, false);

            case YtDlpErrorKind.Unavailable:
                return new YtDlpFailure(Strings.Download_ErrorUnavailable, false);

            case YtDlpErrorKind.NoMedia:
                return new YtDlpFailure(Strings.Download_ErrorNoMedia, false);

            case YtDlpErrorKind.RateLimited:
                return new YtDlpFailure(Strings.Download_ErrorRateLimited, false);

            case YtDlpErrorKind.Unsupported:
                return new YtDlpFailure(Strings.Download_ErrorUnsupported, false);

            case YtDlpErrorKind.Network:
                return new YtDlpFailure(Strings.Download_ErrorNetwork, false);

            case YtDlpErrorKind.Forbidden:
                return SignIn(Strings.Download_ErrorForbidden, youTube, signedIn);

            case YtDlpErrorKind.SelfReportedBug:
                return SelfReportedBug(raw, version);
        }

        string line = YtDlpErrorReader.Clean(raw);

        return line.Length == 0
            ? new YtDlpFailure(Strings.Download_ErrorUnknown, false)
            : new YtDlpFailure(string.Format(Strings.Download_ErrorRaw, line), false);
    }

    /// <summary>The same thing, for callers with no interest in the version.</summary>
    public static YtDlpFailure Describe(string raw, bool youTube, bool signedIn) =>
        Describe(YtDlpErrorReader.Classify(raw), raw, youTube, signedIn, YtDlpVersionFacts.Unchecked);

    /// <summary>
    /// yt-dlp has blamed itself. Whether that means "you are behind" depends on a number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the correction, and it is worth stating plainly what it replaces. The old
    /// code answered every message carrying yt-dlp's bug-report block with one sentence -
    /// "this downloader is out of date for this site" - and an update button beside it. It
    /// said that to the owner about a build that was, that day, the newest one yt-dlp had
    /// ever published. The failure it was really looking at was TikTok refusing to serve a
    /// post.
    /// </para>
    /// <para>
    /// Three answers now, because there are three states of knowledge and each leads
    /// somewhere different. BEHIND names both numbers and offers the update, which is the
    /// only one of the three where pressing it changes anything. CURRENT says so and offers
    /// nothing - the fix is upstream, in a yt-dlp release that does not exist yet, and a
    /// button that provably cannot help is a second wrong answer after the first. UNKNOWN
    /// admits the check did not finish and still offers the button, because an offer is not
    /// a claim: pressing it asks the publisher directly, and the answer comes back either
    /// way.
    /// </para>
    /// <para>
    /// CURRENT and UNKNOWN both carry yt-dlp's own line. There is nothing left for Modlight
    /// to add at that point, and the raw text is what somebody would paste into a search or
    /// into the issue yt-dlp was asking them to open.
    /// </para>
    /// </remarks>
    private static YtDlpFailure SelfReportedBug(string raw, YtDlpVersionFacts version)
    {
        // Non-empty by construction: Classify answers Unknown for an empty line, so nothing
        // reaches this method without one.
        string line = YtDlpErrorReader.Clean(raw);

        bool offersUpdate = YtDlpErrorReader.OffersUpdate(YtDlpErrorKind.SelfReportedBug, version.Standing);

        if (version.Standing == YtDlpStanding.Behind)
        {
            return new YtDlpFailure(
                string.Format(Strings.Download_ErrorToolBehind, version.Installed, version.Latest),
                false, offersUpdate);
        }

        if (version.Standing == YtDlpStanding.Current)
        {
            return new YtDlpFailure(
                string.Format(Strings.Download_ErrorToolCurrent, version.Installed, line), false, offersUpdate);
        }

        return new YtDlpFailure(string.Format(Strings.Download_ErrorToolUnchecked, line), false, offersUpdate);
    }

    /// <summary>
    /// The same cause, worded for whether there is anything the person can do about it.
    /// </summary>
    /// <remarks>
    /// Off YouTube the generic sentence stands and no button is offered, because there is
    /// no sign-in here that would help. On YouTube the message names the remedy, and which
    /// remedy depends on whether a session was already in play.
    /// </remarks>
    private static YtDlpFailure SignIn(string generic, bool youTube, bool signedIn)
    {
        if (!youTube)
        {
            return new YtDlpFailure(generic, false);
        }

        return new YtDlpFailure(
            signedIn ? Strings.Download_ErrorSignInStale : Strings.Download_ErrorSignIn,
            true);
    }
}
