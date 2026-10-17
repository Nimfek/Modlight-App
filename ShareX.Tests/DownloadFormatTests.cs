#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Collections.Generic;

namespace ShareX.Tests;

/// <summary>
/// What the Download tab asks yt-dlp for, spelled out.
/// </summary>
/// <remarks>
/// <para>
/// These strings are handed to another program, which is the whole reason they are worth
/// pinning. Nothing in the app can tell you a format selector is wrong: a typo does not
/// throw, it picks a different stream, the download succeeds and the user gets a file that
/// is quietly worse than the one they asked for. That is not a hypothetical - it is exactly
/// the reported bug this file arrived with, where every quality collapsed to one pre-muxed
/// 360p stream and the run still exited 0.
/// </para>
/// <para>
/// <see cref="DownloadFormat"/> is compiled into this assembly rather than referenced, like
/// its neighbours: it was written free of Avalonia, of HelpersLib and of the settings file
/// so that the REAL code, not a copy, can be exercised here. Anything reached for inside it
/// that is not System stops this project compiling.
/// </para>
/// <para>
/// Measured against yt-dlp 2026.07.04 on a 4K source before being written down here:
/// <c>-S res:720</c> selected 720, <c>-S res:100</c> selected 144 rather than failing, and
/// <c>-S ext:mp4:m4a</c> with no ceiling selected 2160.
/// </para>
/// </remarks>
internal static class DownloadFormatTests
{
    private const string Ffmpeg = @"C:\Tools\ffmpeg.exe";

    private static int failures;

    internal static int Run()
    {
        Console.WriteLine("Download format");

        failures = 0;

        TheDefaultAsksForTheBestThereIs();
        AQualityCeilingIsASortAndNotAFilter();
        NothingIsEverFilteredOnResolutionOrContainer();
        AudioIsTurnedIntoSomethingOrdinary();
        TheH264PreferenceStaysOffYouTubeAndOnlyThere();
        WithoutFfmpegNothingIsAskedToBeJoined();
        AVerticalVideoIsMeasuredByItsShortSide();
        EveryOfferedQualityHasANumberBehindIt();

        return failures;
    }

    /// <summary>
    /// Best, with a merger, asks for the best video and the best audio and joins them.
    /// </summary>
    /// <remarks>
    /// The owner's default and the one that has to be right: this is the path that was
    /// producing 360p. <c>bv*+ba/b</c> is yt-dlp's own default shape, and the ceiling that
    /// used to sit inside it as <c>[ext=mp4]</c> has moved to the sort, where a container
    /// that does not exist costs a rank instead of every format.
    /// </remarks>
    private static void TheDefaultAsksForTheBestThereIs()
    {
        IReadOnlyList<string> arguments = Arguments(
            new DownloadFormat(DownloadMediaKind.Video, DownloadQuality.Best, DownloadAudioFormat.M4a),
            isYouTube: true, Ffmpeg);

        Check(Value(arguments, "-f") == "bestvideo*+bestaudio/best", "Best asks for the best video plus the best audio");
        Check(Value(arguments, "-S") == "ext:mp4:m4a", "with mp4 and m4a preferred but not required");
        Check(Value(arguments, "--merge-output-format") == "mp4", "and merged into an mp4");
        Check(Value(arguments, "--ffmpeg-location") == Ffmpeg, "by the ffmpeg it was told about");
        Check(Value(arguments, "-x") == null, "and nothing is extracted from it");
    }

    /// <summary>
    /// A resolution ceiling is expressed as <c>-S res:N</c>.
    /// </summary>
    /// <remarks>
    /// The distinction the whole design turns on. <c>-f bestvideo[height&lt;=1080]</c> is a
    /// filter: a video that has nothing at or under the ceiling matches nothing and the run
    /// fails. <c>-S res:1080</c> is a preference with a limit, which falls back down to the
    /// largest that fits and, when nothing fits, up to the smallest there is. The owner
    /// asked for a quality that does not exist to degrade rather than fail.
    /// </remarks>
    private static void AQualityCeilingIsASortAndNotAFilter()
    {
        IReadOnlyList<string> arguments = Arguments(
            new DownloadFormat(DownloadMediaKind.Video, DownloadQuality.H1080, DownloadAudioFormat.M4a),
            isYouTube: true, Ffmpeg);

        Check(Value(arguments, "-S") == "res:1080,ext:mp4:m4a", "1080p is a sort limit, not a bracket filter");
        Check(Value(arguments, "-f") == "bestvideo*+bestaudio/best", "so the selector still matches everything");

        Check(Sort(DownloadQuality.H2160) == "res:2160,ext:mp4:m4a", "2160p limits at 2160");
        Check(Sort(DownloadQuality.H1440) == "res:1440,ext:mp4:m4a", "1440p limits at 1440");
        Check(Sort(DownloadQuality.H720) == "res:720,ext:mp4:m4a", "720p limits at 720");
        Check(Sort(DownloadQuality.H480) == "res:480,ext:mp4:m4a", "480p limits at 480");
        Check(Sort(DownloadQuality.H360) == "res:360,ext:mp4:m4a", "360p limits at 360");
        Check(Sort(DownloadQuality.Best) == "ext:mp4:m4a", "and Best limits at nothing at all");
    }

    /// <summary>
    /// The <c>-f</c> expression never carries a bracket condition.
    /// </summary>
    /// <remarks>
    /// A guard rather than a restatement of the two above. Every regression this file exists
    /// to catch has the same shape - somebody moves a preference back into the selector,
    /// where it stops being a preference - and this catches it wherever it is put.
    /// </remarks>
    private static void NothingIsEverFilteredOnResolutionOrContainer()
    {
        foreach (DownloadQuality quality in DownloadFormat.Qualities)
        {
            foreach (bool youTube in new[] { true, false })
            {
                string selector = Value(Arguments(
                    new DownloadFormat(DownloadMediaKind.Video, quality, DownloadAudioFormat.M4a),
                    youTube, Ffmpeg), "-f") ?? "";

                Check(!selector.Contains("height") && !selector.Contains("ext="),
                    "video at " + quality + (youTube ? " on YouTube" : " elsewhere") + " filters on nothing");
            }
        }
    }

    /// <summary>
    /// Audio comes out as a file a music player opens, not as whatever the site sent.
    /// </summary>
    /// <remarks>
    /// M4A is the default because YouTube already sends AAC, so <c>--audio-format m4a</c>
    /// copies the track through instead of re-encoding it - the default costs neither
    /// quality nor time. MP3 is the one everything on earth plays.
    /// </remarks>
    private static void AudioIsTurnedIntoSomethingOrdinary()
    {
        IReadOnlyList<string> m4a = Arguments(
            new DownloadFormat(DownloadMediaKind.Audio, DownloadQuality.Best, DownloadAudioFormat.M4a),
            isYouTube: true, Ffmpeg);

        Check(Value(m4a, "-f") == "bestaudio/best", "audio asks for the best sound there is");
        Check(Value(m4a, "-S") == null, "and leaves the ranking to yt-dlp, which sorts audio by bitrate");
        Check(Value(m4a, "-x") == "", "the audio is extracted");
        Check(Value(m4a, "--audio-format") == "m4a", "into an m4a by default");
        Check(Value(m4a, "--audio-quality") == "0", "at the best quality");
        Check(Value(m4a, "--merge-output-format") == null, "and nothing is merged into an mp4 container");

        IReadOnlyList<string> mp3 = Arguments(
            new DownloadFormat(DownloadMediaKind.Audio, DownloadQuality.Best, DownloadAudioFormat.Mp3),
            isYouTube: false, Ffmpeg);

        Check(Value(mp3, "--audio-format") == "mp3", "or into an mp3 when that is what was picked");
    }

    /// <summary>
    /// H.264 is preferred everywhere except YouTube.
    /// </summary>
    /// <remarks>
    /// Inherited behaviour that has to survive this change. TikTok advertises H.265
    /// renditions carrying an audio codec they do not actually have, so the default ranking
    /// lands on one that produces a SILENT mp4 and reports success. It is never applied to
    /// YouTube, whose 1440p and 2160p tiers exist only as VP9 and AV1 - demoting those would
    /// cap every YouTube download at 1080p, which is the bug this whole round is about.
    /// </remarks>
    private static void TheH264PreferenceStaysOffYouTubeAndOnlyThere()
    {
        DownloadFormat best = new(DownloadMediaKind.Video, DownloadQuality.Best, DownloadAudioFormat.M4a);

        Check(Value(Arguments(best, isYouTube: false, Ffmpeg), "-S") == "res,vcodec:h264,ext:mp4:m4a",
            "off YouTube, resolution first and then H.264");
        Check(Value(Arguments(best, isYouTube: true, Ffmpeg), "-S")?.Contains("h264") != true,
            "on YouTube, never H.264 - it would cap everyone at 1080p");

        DownloadFormat capped = new(DownloadMediaKind.Video, DownloadQuality.H720, DownloadAudioFormat.M4a);

        Check(Value(Arguments(capped, isYouTube: false, Ffmpeg), "-S") == "res:720,vcodec:h264,ext:mp4:m4a",
            "and a ceiling off YouTube keeps both");

        Check(Value(Arguments(
            new DownloadFormat(DownloadMediaKind.Audio, DownloadQuality.Best, DownloadAudioFormat.Mp3),
            isYouTube: false, Ffmpeg), "-S") == null,
            "a video codec preference is never applied to an audio download");
    }

    /// <summary>
    /// Without ffmpeg, nothing is asked for that would have to be joined or converted.
    /// </summary>
    /// <remarks>
    /// This is the degraded path, and it is degraded on purpose rather than by accident:
    /// asking for two streams that cannot be joined is a failed download, and a smaller file
    /// beats no file. What was missing before was anybody saying so - the tab now states it
    /// before the download and the row states it again afterwards.
    /// </remarks>
    private static void WithoutFfmpegNothingIsAskedToBeJoined()
    {
        IReadOnlyList<string> video = Arguments(
            new DownloadFormat(DownloadMediaKind.Video, DownloadQuality.H1080, DownloadAudioFormat.M4a),
            isYouTube: true, ffmpeg: null);

        Check(Value(video, "-f") == "best", "without ffmpeg only a pre-muxed format is asked for");
        Check(Value(video, "-S") == "res:1080,ext:mp4:m4a", "the ceiling still applies to the ones there are");
        Check(Value(video, "--merge-output-format") == null, "nothing is told to merge");
        Check(Value(video, "--ffmpeg-location") == null, "and no path is given for a tool that is not there");

        IReadOnlyList<string> audio = Arguments(
            new DownloadFormat(DownloadMediaKind.Audio, DownloadQuality.Best, DownloadAudioFormat.Mp3),
            isYouTube: true, ffmpeg: null);

        Check(Value(audio, "-f") == "bestaudio[ext=m4a]/bestaudio/best",
            "audio without ffmpeg asks for the one container Windows opens unaided");
        Check(Value(audio, "-x") == null, "and does not ask for an extraction that cannot happen");
        Check(Value(audio, "--audio-format") == null, "nor for a conversion to the mp3 that was picked");
    }

    /// <summary>
    /// "1080p" means the short side, whichever way up the video is.
    /// </summary>
    /// <remarks>
    /// The reason the ceiling is <c>res</c> and not <c>height</c>. A TikTok or a Short at
    /// 1080p is 1080 wide and 1920 high; measured by height it would be a 1920p video, and a
    /// 1080 ceiling would throw away the 1080 rendition AND the 720 one and land on 480.
    /// </remarks>
    private static void AVerticalVideoIsMeasuredByItsShortSide()
    {
        Check(DownloadFormat.ResolutionFrom(1920, 1080) == 1080, "a 1920x1080 video is 1080p");
        Check(DownloadFormat.ResolutionFrom(1080, 1920) == 1080, "and so is a 1080x1920 one");
        Check(DownloadFormat.ResolutionFrom(0, 0) == 0, "an audio download has no resolution");
        Check(DownloadFormat.ResolutionFrom(0, 720) == 720, "a half-reported one is taken at its word");
        Check(DownloadFormat.ResolutionFrom(-1, -1) == 0, "and nonsense is not a resolution");
    }

    /// <summary>
    /// Every entry the dropdown offers maps to a real ceiling, and only Best maps to none.
    /// </summary>
    /// <remarks>
    /// The list and the numbers behind it live in one place so they cannot drift apart. This
    /// is what catches somebody adding an entry to the list and forgetting the arm of the
    /// switch that gives it a meaning - which would silently become an unlimited download
    /// labelled 1440p.
    /// </remarks>
    private static void EveryOfferedQualityHasANumberBehindIt()
    {
        int previous = int.MaxValue;
        int unlimited = 0;

        foreach (DownloadQuality quality in DownloadFormat.Qualities)
        {
            int resolution = DownloadFormat.ResolutionOf(quality);

            if (resolution == 0)
            {
                unlimited++;
                continue;
            }

            Check(resolution < previous, quality + " is smaller than the entry above it");
            previous = resolution;
        }

        Check(unlimited == 1, "exactly one entry has no ceiling");
        Check(DownloadFormat.ResolutionOf(DownloadQuality.Best) == 0, "and it is Best");
    }

    private static string? Sort(DownloadQuality quality) =>
        Value(Arguments(new DownloadFormat(DownloadMediaKind.Video, quality, DownloadAudioFormat.M4a),
            isYouTube: true, Ffmpeg), "-S");

    private static IReadOnlyList<string> Arguments(DownloadFormat format, bool isYouTube, string? ffmpeg) =>
        new List<string>(format.Arguments(isYouTube, ffmpeg));

    /// <summary>
    /// What follows a flag, "" for a flag that stands alone, or null when it is absent.
    /// </summary>
    /// <remarks>
    /// Three answers rather than two, because "-x is not there" and "-x is there with
    /// nothing after it" are the difference between converting the audio and not.
    /// </remarks>
    private static string? Value(IReadOnlyList<string> arguments, string flag)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] != flag)
            {
                continue;
            }

            if (i + 1 >= arguments.Count || arguments[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                return "";
            }

            return arguments[i + 1];
        }

        return null;
    }

    private static void Check(bool condition, string what)
    {
        if (condition)
        {
            Console.WriteLine("  ok   " + what);
            return;
        }

        failures++;
        Console.WriteLine("  FAIL " + what);
    }
}
