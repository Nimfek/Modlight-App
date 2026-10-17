#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>How old the yt-dlp on this PC is, and both version numbers behind that answer.</summary>
/// <remarks>
/// The two strings are carried alongside the verdict because the sentence the reader gets
/// names them. "This downloader is out of date" is an assertion somebody has to take on
/// trust; "2026.06.14 is installed, 2026.08.19 has been published" is the same assertion
/// with its evidence attached, and it is checkable in ten seconds by anybody who doubts it.
/// </remarks>
internal readonly record struct YtDlpVersionFacts(YtDlpStanding Standing, string Installed, string Latest)
{
    public static YtDlpVersionFacts Unchecked => new(YtDlpStanding.Unknown, "", "");
}

/// <summary>
/// Asking how old this yt-dlp actually is, before anybody says it is too old.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The Download tab used to conclude "this downloader is out
/// of date for this site" from the wording of an error message and nothing else, and shipped
/// that sentence to the owner on a machine running the newest yt-dlp in existence. A version
/// claim needs a version number. There are two available and neither was being read: what
/// the binary on this disk answers to <c>--version</c>, and what the project has published.
/// This is the one place that puts them side by side.
/// </para>
/// <para>
/// <b>Nothing here is allowed to be slow, and nothing here is allowed to be wrong.</b> It
/// runs on a download that has already failed, so the row is standing there with a spinner
/// on it - hence the deadline, after which the answer is <see cref="YtDlpStanding.Unknown"/>
/// and the wording says so instead of guessing. Unknown is a first-class answer here, not a
/// failure: a laptop on a train cannot reach GitHub, and "I could not check" is a true
/// sentence where both "you are out of date" and "you are up to date" would be inventions.
/// </para>
/// <para>
/// <b>Once per hour, not once per row.</b> A pasted playlist of fifty dead TikTok links is
/// fifty failures arriving within seconds of each other, and each of them would otherwise
/// start a process and an HTTP request - fifty of the sixty unauthenticated requests GitHub
/// allows in an hour, spent on a question whose answer cannot have changed in between. So
/// the answer is cached, the gate serialises whoever asks, and everybody after the first
/// reads what the first one found.
/// </para>
/// </remarks>
internal static class YtDlpVersionCheck
{
    /// <summary>How long a definite answer stands.</summary>
    /// <remarks>
    /// yt-dlp publishes every few weeks, so an hour is nowhere near the resolution of the
    /// thing being measured - it is short enough that somebody who updates the downloader
    /// mid-session is not told the old answer for the rest of the day, and long enough that
    /// a queue of failures costs one request rather than one each.
    /// </remarks>
    private static readonly TimeSpan GoodFor = TimeSpan.FromHours(1);

    /// <summary>How long to leave a check that did not complete alone.</summary>
    /// <remarks>
    /// Shorter than <see cref="GoodFor"/>, because "we could not find out" is the answer
    /// most worth revisiting: it is what a captive portal, a proxy login page or a dropped
    /// connection leaves behind, and all three of those tend to be over within minutes.
    /// </remarks>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The whole budget: asking the binary, and asking the publisher.
    /// </summary>
    /// <remarks>
    /// Measured rather than guessed - the bundled yt-dlp answers <c>--version</c> in 0.9 s
    /// on the owner's machine, twice in a row, cold and warm, because PyInstaller unpacks
    /// itself on every run. The rest of the budget is the HTTP request. Past this the row
    /// stops waiting and says the check did not finish, which is worse than an answer and
    /// far better than a made-up one.
    /// </remarks>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static readonly SemaphoreSlim gate = new(1, 1);

    private static YtDlpVersionFacts cached = YtDlpVersionFacts.Unchecked;

    private static DateTime cachedUtc = DateTime.MinValue;

    /// <summary>
    /// What is installed, what is published, and which of the two is newer.
    /// </summary>
    /// <remarks>
    /// Never throws. Every path out of here that is not a real comparison returns
    /// <see cref="YtDlpVersionFacts.Unchecked"/>, and the wording layer treats that as "say
    /// nothing about the version".
    /// </remarks>
    public static async Task<YtDlpVersionFacts> FactsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return YtDlpVersionFacts.Unchecked;
        }

        try
        {
            if (Fresh())
            {
                return cached;
            }

            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Deadline);

            YtDlpVersionFacts facts = await MeasureAsync(deadline.Token);

            cached = facts;
            cachedUtc = DateTime.UtcNow;

            DebugHelper.WriteLine("Download: yt-dlp is " +
                (facts.Installed.Length > 0 ? facts.Installed : "an unreadable build") + ", the newest published is " +
                (facts.Latest.Length > 0 ? facts.Latest : "unknown") + " -> " + facts.Standing + ".");

            return facts;
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Download: could not work out how old yt-dlp is.");
            return YtDlpVersionFacts.Unchecked;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drop the remembered answer, because the file it was about has been replaced.</summary>
    public static void Forget()
    {
        cached = YtDlpVersionFacts.Unchecked;
        cachedUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Whether the remembered answer may still be used.
    /// </summary>
    /// <remarks>
    /// A stamp in the future is treated as stale rather than trusted, the same rule the
    /// scheduled component check follows: it is what a corrected clock leaves behind, and
    /// the other reading of it - "good for another year" - is the one failure of a cache
    /// nobody would ever notice.
    /// </remarks>
    private static bool Fresh()
    {
        if (cachedUtc == DateTime.MinValue)
        {
            return false;
        }

        TimeSpan age = DateTime.UtcNow - cachedUtc;

        if (age < TimeSpan.Zero)
        {
            return false;
        }

        return age < (cached.Standing == YtDlpStanding.Unknown ? RetryAfter : GoodFor);
    }

    private static async Task<YtDlpVersionFacts> MeasureAsync(CancellationToken cancellationToken)
    {
        ModlightComponent? component = ModlightComponentCatalog.Find(ModlightComponentCatalog.YtDlp);

        if (component == null || !ModlightComponents.IsInstalled(component))
        {
            return YtDlpVersionFacts.Unchecked;
        }

        // The binary, not the catalogue constant and not the settings file. Both of those
        // stop being true the first time an update lands or somebody drops their own build
        // in the folder, and both fail in the same direction - claiming something newer
        // than what is actually about to run.
        string installed = await ModlightComponentUpdates.InstalledVersionAsync(component, cancellationToken);

        if (installed.Length == 0)
        {
            // An unreadable build cannot be called out of date OR current. It is exactly the
            // case a bool would have had to lie about.
            return YtDlpVersionFacts.Unchecked;
        }

        string latest = ModlightComponentUpdates.LatestKnown(component);

        if (latest.Length == 0)
        {
            // Nothing found the publisher yet this run: the scheduled check runs 45 seconds
            // after launch and at most once a day, so on most failures it either has not
            // happened or happened yesterday. Ask now - this is the moment the answer is
            // worth a request.
            ModlightToolFetchResult fetch = await ModlightComponentUpdates.CheckAsync(component, cancellationToken);

            if (fetch.Status != ModlightToolFetchStatus.Ok || fetch.Build == null)
            {
                return new YtDlpVersionFacts(YtDlpStanding.Unknown, installed, "");
            }

            latest = fetch.Build.VersionText;
        }

        // Both sides have to parse before either is compared. IsNewer already answers false
        // when they do not, but false there means "do not update", which read as a verdict
        // would come out as "you are current" - a claim about a number nobody could read.
        if (!ModlightToolRelease.TryParseVersion(installed, out _) ||
            !ModlightToolRelease.TryParseVersion(latest, out _))
        {
            return new YtDlpVersionFacts(YtDlpStanding.Unknown, installed, latest);
        }

        return new YtDlpVersionFacts(
            ModlightToolRelease.IsNewer(latest, installed) ? YtDlpStanding.Behind : YtDlpStanding.Current,
            installed, latest);
    }
}
