#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>How far along one download is. Every field is a fact yt-dlp just reported.</summary>
internal sealed class YtDlpProgress
{
    /// <summary>0 to 100. See <see cref="YtDlpRun"/> on what it is a fraction of.</summary>
    public double Percent { get; init; }

    public long Downloaded { get; init; }

    /// <summary>The size of the stream being fetched, or 0 when the server did not say.</summary>
    public long Total { get; init; }

    /// <summary>Bytes per second, or 0 when not yet known.</summary>
    public double Speed { get; init; }

    /// <summary>Seconds left, or -1 when not yet known.</summary>
    public int Eta { get; init; } = -1;

    /// <summary>A stream has finished and something else is happening - merging, usually.</summary>
    public bool IsWorking { get; init; }
}

/// <summary>
/// What yt-dlp settled on, once it has chosen a format and before it fetches a byte.
/// </summary>
/// <remarks>
/// It arrives early on purpose. "There is no 1080p version of this, so it is coming down at
/// 360p" is worth knowing before a forty-minute download rather than after it, and yt-dlp
/// prints the chosen format at its <c>video</c> stage, which runs before the first fragment.
///
/// This is the answer to the quieter half of the reported bug. The old code asked for a
/// quality and then never looked at what it got: a run that produced 360p exited 0, wrote a
/// file and reported success, and the only trace of what had happened was a WARNING line
/// that <see cref="YtDlpRun.LastError"/> deliberately skips.
/// </remarks>
internal sealed class YtDlpFormatResult
{
    /// <summary>The short side of the video in pixels, or 0 for audio and for unknown.</summary>
    public int Resolution { get; init; }

    /// <summary>Why this is not what was asked for, ready to show. Empty when it is.</summary>
    public string Note { get; init; } = "";
}

/// <summary>What became of a run.</summary>
internal sealed class YtDlpOutcome
{
    public bool Success { get; init; }

    /// <summary>We stopped it: a cancel, or a pause. Not a failure and not worth a message.</summary>
    public bool Canceled { get; init; }

    /// <summary>Where the file ended up. Null only when something went wrong.</summary>
    public string? FilePath { get; init; }

    /// <summary>The title yt-dlp found, if it got far enough to find one.</summary>
    public string? Title { get; init; }

    /// <summary>Ready to put in front of a person. Empty on success.</summary>
    public string Error { get; init; } = "";

    /// <summary>
    /// This failed for want of a YouTube sign-in, so the row offers one.
    /// </summary>
    /// <remarks>
    /// Decided by <see cref="YtDlpError"/> from what yt-dlp said and whether a jar was in
    /// use, and carried here rather than worked out again in the view - the view has
    /// neither of those two facts.
    /// </remarks>
    public bool NeedsSignIn { get; init; }

    /// <summary>
    /// This failed the way a site whose extractor has rotted fails, so the row offers to
    /// fetch a newer yt-dlp and try the same address again.
    /// </summary>
    /// <remarks>
    /// Worked out by <see cref="YtDlpError"/> from what yt-dlp printed, and carried here
    /// for the same reason as <see cref="NeedsSignIn"/>: the view has neither the output
    /// nor the rules for reading it, and a button that appears because a translated
    /// sentence contained a word is a button that appears in the wrong place the first time
    /// somebody rewrites the sentence.
    /// </remarks>
    public bool NeedsUpdate { get; init; }
}

/// <summary>
/// One run of yt-dlp: build the arguments, read what it says, hand back a file.
/// </summary>
/// <remarks>
/// <para>
/// <b>The output protocol.</b> yt-dlp is asked to speak in lines nobody has to guess at:
/// <c>--progress-template</c> with the RAW numeric fields (never <c>_speed_str</c>, which
/// carries ANSI colour codes), and <c>--print</c> with two markers of our own. Everything
/// is prefixed, so a line from the user's own config or a future yt-dlp release cannot be
/// mistaken for ours. Verified against yt-dlp 2026.03.17.
/// </para>
/// <para>
/// <b>Where the file went</b> is the question this whole design turns on, and
/// <c>--print after_move:%(filepath)s</c> is the only answer that is always true. The
/// download's own filename is the pre-merge one - fetch a 1080p video and the bytes land
/// in <c>.f137.mp4</c> before ffmpeg makes the <c>.mp4</c> the user gets. <c>after_move</c>
/// runs after merging, after any embedding and after the rename, so it names the file that
/// exists on disk when it is over.
/// </para>
/// <para>
/// <b>Arguments are a list, never a string.</b> A URL from a paste box is the widest
/// untrusted input this app has, and it goes to a child process. ArgumentList hands each
/// element to the OS separately, so no quoting rule of ours can be broken by what is in
/// it, and <c>--</c> before the URL means even an address starting with a dash cannot
/// become an option. <see cref="DownloadLink"/> has already refused anything that is not
/// absolute http or https.
/// </para>
/// <para>
/// <b>Pausing is stopping.</b> There is no pause signal a console process on Windows can
/// be sent that a download will survive - suspending the process holds a socket open that
/// the far end will drop anyway. So a pause kills the process, and yt-dlp's own
/// <c>.part</c> file is the resume: run the identical command again and it continues from
/// the byte it reached. That is why the arguments are derived only from things that do not
/// change between runs. Measured, not assumed: killed at 166,819 of 788,493 bytes, restarted,
/// and the finished file was the correct 788,493.
/// </para>
/// </remarks>
internal sealed class YtDlpRun
{
    /// <summary>Marks a progress line. Deliberately not a word yt-dlp itself would print.</summary>
    private const string ProgressMarker = "@ML|";

    private const string TitleMarker = "@MT|";
    private const string FinalPathMarker = "@MF|";
    private const string PredictedPathMarker = "@MP|";

    /// <summary>Marks the chosen format's size, printed once, before the download starts.</summary>
    private const string ResolutionMarker = "@MR|";

    /// <summary>What yt-dlp prints for a field it has no value for.</summary>
    private const string NotAvailable = "NA";

    private readonly DownloadLink link;
    private readonly string folder;
    private readonly DownloadFormat format;
    private readonly Action<YtDlpProgress> onProgress;
    private readonly Action<string> onTitle;
    private readonly Action<YtDlpFormatResult> onFormat;

    private readonly object gate = new();
    private readonly List<string> finalPaths = new();
    private readonly List<string> errors = new();

    private Process? process;
    private string? predictedPath;
    private string? title;

    /// <summary>
    /// The merger this run is using, or null. Read once at the start of the run.
    /// </summary>
    /// <remarks>
    /// Once, rather than wherever it is wanted, because two answers inside one run would be
    /// a contradiction: the arguments would ask for streams that have to be joined and the
    /// verdict afterwards would say nothing could be. The user can install ffmpeg while a
    /// download is in flight - the next run picks it up, this one does not change shape
    /// underneath itself.
    /// </remarks>
    private string? ffmpeg;

    /// <summary>Bytes belonging to streams that have already finished this run.</summary>
    private long completedBytes;

    public YtDlpRun(DownloadLink link, string folder, DownloadFormat format,
        Action<YtDlpProgress> onProgress, Action<string> onTitle, Action<YtDlpFormatResult> onFormat)
    {
        this.link = link;
        this.folder = folder;
        this.format = format;
        this.onProgress = onProgress;
        this.onTitle = onTitle;
        this.onFormat = onFormat;
    }

    public async Task<YtDlpOutcome> RunAsync(CancellationToken cancellationToken)
    {
        string? tool = YtDlpTool.YtDlpPath;

        if (tool == null)
        {
            return new YtDlpOutcome { Error = Strings.Download_ErrorToolMissing };
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return new YtDlpOutcome { Error = string.Format(Strings.Download_ErrorFolder, folder, exception.Message) };
        }

        // Fixed for the whole run: the arguments below and the verdict at the end have to
        // be talking about the same ffmpeg.
        ffmpeg = YtDlpTool.FFmpegPath;

        DateTime startedUtc = DateTime.UtcNow;

        ProcessStartInfo info = new(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = folder
        };

        foreach (string argument in BuildArguments())
        {
            info.ArgumentList.Add(argument);
        }

        // yt-dlp is a Python program, and Python on Windows picks its output encoding from
        // the console code page when it is not told otherwise. Titles here are Polish and
        // routinely emoji; without this the filenames come back mangled or the process
        // dies writing them.
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";

        using Process child = new() { StartInfo = info };

        child.OutputDataReceived += (_, e) => Read(e.Data);
        child.ErrorDataReceived += (_, e) => Read(e.Data);

        try
        {
            child.Start();
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);

            // Nearly always the tool being gone since it was found - uninstalled, or a
            // PATH entry on a network share that is no longer there. Worth rechecking so
            // the tab can go back to saying "not installed" rather than blaming the URL.
            YtDlpTool.Rescan();

            return new YtDlpOutcome { Error = string.Format(Strings.Download_ErrorStartFailed, exception.Message) };
        }

        // Before the first byte of output: from here on it dies with Modlight however
        // Modlight dies, including a kill that gives us no chance to tidy up.
        ChildProcessJob.Adopt(child);

        lock (gate)
        {
            process = child;
        }

        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        // The registration is disposed before anything is read out of this run, so a
        // cancel arriving after the process is gone cannot kill a reused process id.
        using (cancellationToken.Register(Stop))
        {
            // Not the caller's token: this must return only once the process is actually
            // gone. Waiting on a cancelled token would return while yt-dlp was still
            // writing, and a resume would then race its own leftovers.
            await child.WaitForExitAsync(CancellationToken.None);
        }

        lock (gate)
        {
            process = null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new YtDlpOutcome { Canceled = true, Title = title };
        }

        if (child.ExitCode != 0)
        {
            // Read now rather than when the run started: the jar can be written or thrown
            // away while a download is in flight, and the advice on screen has to match
            // the state the person is actually in when they read it.
            string raw = LastError();

            // The line the user is NOT going to see once it has been classified. It is the
            // only record of what actually happened, and the difference between "the site
            // changed" and "this account cannot see it" lives in it.
            DebugHelper.WriteLine("Download: yt-dlp exited " + child.ExitCode + " for " + link.Source +
                (raw.Length > 0 ? ": " + raw : " with nothing to say."));

            YtDlpErrorKind kind = YtDlpErrorReader.Classify(raw);

            // Only asked where the answer could change what the reader is told, which is the
            // one class where yt-dlp blames itself. Everywhere else the cause is settled and
            // a child process plus an HTTP request would buy nothing: a private video is
            // private on every version of yt-dlp there has ever been.
            YtDlpVersionFacts version = YtDlpErrorReader.WantsVersionCheck(kind)
                ? await YtDlpVersionCheck.FactsAsync(CancellationToken.None)
                : YtDlpVersionFacts.Unchecked;

            YtDlpFailure failure =
                YtDlpError.Describe(kind, raw, link.IsYouTube, YouTubeCookies.IsSignedIn, version);

            return new YtDlpOutcome
            {
                Title = title,
                Error = failure.Message,
                NeedsSignIn = failure.NeedsSignIn,
                NeedsUpdate = failure.NeedsUpdate
            };
        }

        string? file = ResolveFile(startedUtc);

        if (file == null)
        {
            return new YtDlpOutcome { Title = title, Error = Strings.Download_ErrorNoOutput };
        }

        return new YtDlpOutcome { Success = true, FilePath = file, Title = title };
    }

    /// <summary>
    /// Kill the download and everything under it.
    /// </summary>
    /// <remarks>
    /// The tree, not the process: a merging download has ffmpeg beneath it, and killing
    /// only the parent would leave ffmpeg writing into a file the queue believes it owns.
    /// </remarks>
    private void Stop()
    {
        Process? running;

        lock (gate)
        {
            running = process;
        }

        if (running == null)
        {
            return;
        }

        try
        {
            if (!running.HasExited)
            {
                running.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            // It finished between the check and the kill, or Windows refused. Either way
            // the wait below is what decides when this run is over.
            DebugHelper.WriteException(exception);
        }
    }

    private IEnumerable<string> BuildArguments()
    {
        yield return "--color";
        yield return "never";

        // One progress line per update rather than a carriage-returned bar, which cannot
        // be read line by line.
        yield return "--newline";

        // --print implies --quiet, and --quiet takes the progress with it.
        yield return "--progress";

        // Defence against a yt-dlp.conf on this machine: our two later-stage --print
        // options already turn simulation off, but a config that asked for --simulate
        // would otherwise turn a download into a dry run with no file and no error.
        yield return "--no-simulate";

        // One link, one file. A watch URL that carries a list= parameter would otherwise
        // pull the whole playlist into a queue row that says one thing.
        yield return "--no-playlist";

        yield return "--windows-filenames";

        // The template caps the title at 200 BYTES, not characters, and this caps the
        // whole name. Both matter here: Polish titles are multi-byte, emoji are four
        // bytes each, and MAX_PATH is still a real limit under a deep Downloads folder.
        yield return "--trim-filenames";
        yield return "180";

        yield return "--retries";
        yield return "10";
        yield return "--fragment-retries";
        yield return "10";

        // Fragmented YouTube formats arrive in hundreds of pieces; four at a time is the
        // difference between a 1080p video taking a minute and taking five. It does
        // nothing for a single-file TikTok, and going higher is how a site starts
        // answering 429.
        yield return "--concurrent-fragments";
        yield return "4";

        // The signed-in session, when there is one and this is a YouTube link. It is what
        // turns "Sign in to confirm you're not a bot", every age gate and most of the 403s
        // into an ordinary download. See YouTubeCookies for what the file is, and for why
        // it is a jar of ours rather than --cookies-from-browser.
        //
        // Worth knowing: yt-dlp writes the jar BACK to this path when a run ends, carrying
        // whatever the session was refreshed to. That is welcome - it is part of what keeps
        // the sign-in alive - and it is safe only because the queue runs one download at a
        // time. Two runs sharing this argument would be two processes rewriting one
        // credential.
        string? cookies = YouTubeCookies.FileFor(link);

        if (cookies != null)
        {
            yield return "--cookies";
            yield return cookies;
        }

        yield return "-P";
        yield return folder;

        // The id in brackets is what makes the name unique - two videos can share a title,
        // and without it the second would collide with the first and be treated as an
        // already-finished download. It is also what makes a resume find its own .part.
        yield return "-o";
        yield return "%(title).200B [%(id)s].%(ext)s";

        // What to fetch, how good it should be, and what to turn it into. All of it lives
        // in DownloadFormat, which is what the picker above the queue writes and what a
        // paused row keeps hold of so a resume asks for exactly what the first attempt did.
        foreach (string argument in format.Arguments(link.IsYouTube, ffmpeg))
        {
            yield return argument;
        }

        string? quickJs = YtDlpTool.QuickJsPath;

        if (quickJs != null)
        {
            // Named with its full path rather than left to PATH, because ours is in a
            // Tools subfolder that is on nobody's PATH - and because a runtime found by
            // searching is a runtime that can silently become a different one.
            //
            // No --no-js-runtimes in front of it. yt-dlp ranks deno above quickjs and only
            // consults that ranking among the runtimes that are ENABLED, so adding ours
            // leaves a user who installed deno on deno, and puts everyone else on QuickJS
            // instead of on nothing. Clearing the list first would demote deno to spite
            // the one user who has it.
            yield return "--js-runtimes";
            yield return "quickjs:" + quickJs;
        }

        yield return "--progress-template";
        yield return "download:" + ProgressMarker +
            "%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
            "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";

        yield return "--print";
        yield return "before_dl:" + TitleMarker + "%(title)s";

        // The truth, printed after merging, embedding and the final rename.
        yield return "--print";
        yield return "after_move:" + FinalPathMarker + "%(filepath)s";

        // The prediction, for the one case after_move never fires: a file that was already
        // downloaded in full. Nothing is fetched and nothing is moved, so without this the
        // run would succeed with no file to hand back.
        yield return "--print";
        yield return "video:" + PredictedPathMarker + "%(filename)s";

        // What yt-dlp actually picked. The video stage runs after format selection and
        // before the first byte, which is the whole point: a person who asked for 1080p and
        // is getting 360p should be told while it is still worth cancelling.
        //
        // Both axes, never height alone. A vertical video is 1080 wide and 1920 high, and
        // calling that "1920p" would name a resolution nobody publishes - see
        // DownloadFormat on why the short side is the number that means "1080p".
        yield return "--print";
        yield return "video:" + ResolutionMarker + "%(height)s|%(width)s";

        yield return "--";
        yield return link.Url;
    }

    /// <summary>
    /// One line from either stream.
    /// </summary>
    /// <remarks>
    /// Both streams are read the same way on purpose. Which of the two yt-dlp writes a
    /// given kind of line to has changed between releases, and a marker is unambiguous
    /// wherever it turns up.
    /// </remarks>
    private void Read(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        if (line.StartsWith(ProgressMarker, StringComparison.Ordinal))
        {
            ReadProgress(line.Substring(ProgressMarker.Length));
            return;
        }

        if (line.StartsWith(TitleMarker, StringComparison.Ordinal))
        {
            string value = line.Substring(TitleMarker.Length).Trim();

            if (value.Length > 0 && value != NotAvailable && title == null)
            {
                title = value;
                onTitle(value);
            }

            return;
        }

        if (line.StartsWith(FinalPathMarker, StringComparison.Ordinal))
        {
            string value = line.Substring(FinalPathMarker.Length).Trim();

            if (value.Length > 0)
            {
                lock (gate)
                {
                    finalPaths.Add(value);
                }
            }

            return;
        }

        if (line.StartsWith(PredictedPathMarker, StringComparison.Ordinal))
        {
            string value = line.Substring(PredictedPathMarker.Length).Trim();

            if (value.Length > 0)
            {
                predictedPath = value;
            }

            return;
        }

        if (line.StartsWith(ResolutionMarker, StringComparison.Ordinal))
        {
            ReadResolution(line.Substring(ResolutionMarker.Length));
            return;
        }

        lock (gate)
        {
            // Everything else is diagnosis. Capped because a fragmented download that goes
            // wrong can produce a warning per fragment, and there can be thousands.
            if (errors.Count < 200)
            {
                errors.Add(line);
            }
        }
    }

    /// <summary>
    /// status | downloaded | total | total_estimate | speed | eta
    /// </summary>
    /// <remarks>
    /// The percentage is a fraction of the bytes we know about, which is not the same as a
    /// fraction of the download: yt-dlp fetches video and audio as separate streams and
    /// only announces the size of the one it is on. So it climbs to nearly 100 on the video
    /// and steps back once when the audio starts, by the audio's share of the total -
    /// typically three or four percent. Every alternative was worse: a bar that resets to
    /// zero halfway, or one that stalls at 100 while bytes are still arriving.
    /// </remarks>
    private void ReadProgress(string payload)
    {
        string[] fields = payload.Split('|');

        if (fields.Length < 6)
        {
            return;
        }

        string status = fields[0].Trim();
        long downloaded = ParseLong(fields[1]) ?? 0;
        long total = ParseLong(fields[2]) ?? ParseLong(fields[3]) ?? 0;
        double speed = ParseDouble(fields[4]) ?? 0;
        int eta = (int)(ParseDouble(fields[5]) ?? -1);

        if (status == "finished")
        {
            completedBytes += downloaded;

            onProgress(new YtDlpProgress
            {
                Percent = 100,
                Downloaded = completedBytes,
                Total = completedBytes,
                IsWorking = true
            });

            return;
        }

        if (status != "downloading")
        {
            return;
        }

        long done = completedBytes + downloaded;
        long whole = completedBytes + total;
        double percent = whole > 0 ? Math.Clamp(done * 100.0 / whole, 0, 100) : 0;

        onProgress(new YtDlpProgress
        {
            Percent = percent,
            Downloaded = done,
            Total = whole,
            Speed = speed,
            Eta = eta
        });
    }

    /// <summary>
    /// height | width, of the format yt-dlp chose.
    /// </summary>
    /// <remarks>
    /// Printed once per run, before the download. Audio has neither, and yt-dlp says NA for
    /// both - which is not a failure to report, so the verdict below is still raised: an
    /// audio download has its own thing to say about ffmpeg.
    /// </remarks>
    private void ReadResolution(string payload)
    {
        string[] fields = payload.Split('|');

        int height = fields.Length > 0 ? Pixels(fields[0]) : 0;
        int width = fields.Length > 1 ? Pixels(fields[1]) : 0;

        onFormat(Verdict(DownloadFormat.ResolutionFrom(width, height)));
    }

    /// <summary>
    /// Whether what yt-dlp chose is what was asked for, and how to say it if not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Falling short is not an error and is never treated as one. A video that only exists
    /// at 720p is a video that only exists at 720p, and refusing to fetch it because
    /// somebody's dropdown said 1080p would be turning a working download into a failure to
    /// make a point. So it comes down, and this sentence is the difference between "the app
    /// quietly gave me something worse" and "this one does not go that high".
    /// </para>
    /// <para>
    /// The other direction is possible too, and worth naming rather than hiding: a site
    /// whose smallest rendition is above the ceiling gets fetched at that size, because
    /// <c>-S res:N</c> falls back to the smallest there is instead of matching nothing.
    /// </para>
    /// </remarks>
    private YtDlpFormatResult Verdict(int resolution)
    {
        if (ffmpeg == null)
        {
            // The reported bug, said out loud. Without a merger the only reachable YouTube
            // format is the one pre-muxed 360p stream, and the old code took it, wrote the
            // file and called the run a success.
            return new YtDlpFormatResult
            {
                Resolution = resolution,
                Note = format.IsAudio
                    ? Strings.Download_NoteNoFFmpegAudio
                    : Strings.Download_NoteNoFFmpegVideo
            };
        }

        int asked = format.MaxResolution;

        // Nothing was asked for, or nothing was said. Either way there is no gap to report.
        if (asked == 0 || resolution <= 0)
        {
            return new YtDlpFormatResult { Resolution = resolution };
        }

        if (resolution < asked)
        {
            return new YtDlpFormatResult
            {
                Resolution = resolution,
                Note = string.Format(Strings.Download_NoteQualityLower, asked, resolution)
            };
        }

        if (resolution > asked)
        {
            return new YtDlpFormatResult
            {
                Resolution = resolution,
                Note = string.Format(Strings.Download_NoteQualityHigher, asked, resolution)
            };
        }

        return new YtDlpFormatResult { Resolution = resolution };
    }

    /// <summary>A pixel count from yt-dlp, or 0 for NA and for anything absurd.</summary>
    private static int Pixels(string value)
    {
        long parsed = ParseLong(value) ?? 0;

        return parsed > 0 && parsed <= int.MaxValue ? (int)parsed : 0;
    }

    /// <summary>
    /// Which file this run produced.
    /// </summary>
    /// <remarks>
    /// Three answers in order of how much they are worth. The first is yt-dlp's own; the
    /// other two exist because "yt-dlp exited 0 and we have no idea what it made" is the
    /// one failure the user could do nothing about.
    /// </remarks>
    private string? ResolveFile(DateTime startedUtc)
    {
        lock (gate)
        {
            for (int i = finalPaths.Count - 1; i >= 0; i--)
            {
                if (Exists(finalPaths[i]))
                {
                    return finalPaths[i];
                }
            }
        }

        // Already downloaded in full: nothing was fetched, so after_move never ran. The
        // predicted name carries the pre-merge extension, so the file beside it counts too.
        if (predictedPath != null)
        {
            if (Exists(predictedPath))
            {
                return predictedPath;
            }

            string? sibling = NewestMatching(predictedPath);

            if (sibling != null)
            {
                return sibling;
            }
        }

        return NewestSince(startedUtc);
    }

    private static string? NewestMatching(string predicted)
    {
        try
        {
            string? directory = Path.GetDirectoryName(predicted);
            string stem = Path.GetFileNameWithoutExtension(predicted);

            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(stem) || !Directory.Exists(directory))
            {
                return null;
            }

            return Newest(Directory.GetFiles(directory, stem + ".*"), DateTime.MinValue);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return null;
        }
    }

    private string? NewestSince(DateTime startedUtc)
    {
        try
        {
            // A second of slack: the file's timestamp comes from the file system's clock
            // and NTFS records it at a coarser resolution than the one this was read at.
            return Newest(Directory.GetFiles(folder), startedUtc.AddSeconds(-1));
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return null;
        }
    }

    private static string? Newest(IEnumerable<string> candidates, DateTime notBefore)
    {
        string? best = null;
        DateTime bestTime = DateTime.MinValue;

        foreach (string candidate in candidates)
        {
            string extension = Path.GetExtension(candidate);

            // yt-dlp's own scratch files. A .part is a download that did not finish, and
            // handing one to the uploader would upload a truncated video.
            if (extension.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".temp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTime written;

            try
            {
                written = File.GetLastWriteTimeUtc(candidate);
            }
            catch (Exception)
            {
                continue;
            }

            if (written >= notBefore && written > bestTime)
            {
                best = candidate;
                bestTime = written;
            }
        }

        return best;
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The line worth showing out of everything that was written.
    /// </summary>
    /// <remarks>
    /// An ERROR line first, wherever it appeared. It matters because yt-dlp's LAST line is
    /// routinely a warning - it prints one on every run when the installed version is more
    /// than ninety days old - and "your yt-dlp is old" as the explanation for a private
    /// video would send somebody to fix the wrong thing.
    /// </remarks>
    private string LastError()
    {
        lock (gate)
        {
            for (int i = errors.Count - 1; i >= 0; i--)
            {
                if (errors[i].StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                {
                    return errors[i];
                }
            }

            for (int i = errors.Count - 1; i >= 0; i--)
            {
                string line = errors[i].Trim();

                if (line.Length > 0 && !line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }

            return "";
        }
    }

    private static long? ParseLong(string value)
    {
        string text = value.Trim();

        if (text.Length == 0 || text == NotAvailable)
        {
            return null;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : null;
    }

    /// <summary>
    /// Invariant culture, always.
    /// </summary>
    /// <remarks>
    /// yt-dlp prints "13033483.601051992" whatever the machine's locale is, and this app
    /// runs on Polish Windows where the decimal separator is a comma. Parsing that with the
    /// current culture reads it as thirteen thousand billion bytes per second.
    /// </remarks>
    private static double? ParseDouble(string value)
    {
        string text = value.Trim();

        if (text.Length == 0 || text == NotAvailable)
        {
            return null;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : null;
    }
}
