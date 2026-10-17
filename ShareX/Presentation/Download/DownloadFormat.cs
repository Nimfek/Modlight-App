#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ShareX;

/// <summary>
/// Whether a download is meant to be watched or listened to.
/// </summary>
/// <remarks>
/// Serialised into ApplicationConfig as a number, so the order of these members is part of
/// the file format. Append, never insert.
/// </remarks>
public enum DownloadMediaKind
{
    /// <summary>Picture and sound. The default, and what a video site is for.</summary>
    Video,

    /// <summary>Sound only, turned into a file any music player opens.</summary>
    Audio
}

/// <summary>
/// How good a video the user is asking for.
/// </summary>
/// <remarks>
/// <para>
/// A ceiling, not a demand: every value below <see cref="Best"/> means "the best there is,
/// up to this". A video that does not go that high is not an error - see
/// <see cref="DownloadFormat"/> for what happens then.
/// </para>
/// <para>
/// Serialised into ApplicationConfig as a number. Append, never insert.
/// </para>
/// </remarks>
public enum DownloadQuality
{
    /// <summary>Whatever the site's best is. The default.</summary>
    Best,

    H2160,
    H1440,
    H1080,
    H720,
    H480,
    H360
}

/// <summary>
/// What an audio-only download is turned into.
/// </summary>
/// <remarks>
/// Two entries and no more, because the question being answered is "will this open" rather
/// than "which codec". Serialised as a number; append, never insert.
/// </remarks>
public enum DownloadAudioFormat
{
    /// <summary>What YouTube already sends, so the stream is copied rather than re-encoded.</summary>
    M4a,

    /// <summary>Re-encoded. Slower and lossy, and the one format everything on earth plays.</summary>
    Mp3
}

/// <summary>
/// One answer to "what should be downloaded", and the yt-dlp arguments that ask for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a type and not three fields on the view model.</b> A queued row keeps the
/// choice that was on screen when it was queued, not the one that is on screen when it
/// runs - the picker is a setting for the next download, and a queue of five rows that all
/// silently changed shape because somebody moved a dropdown would be a queue nobody could
/// predict. It also has to be immutable for a second reason: a pause is a kill and a resume
/// is the identical command run again (see <see cref="YtDlpRun"/>), so the arguments must
/// not be able to differ between the two.
/// </para>
/// <para>
/// <b>The cap is a SORT and not a filter.</b> <c>-S res:1080</c> rather than
/// <c>-f bestvideo[height&lt;=1080]</c>, and the difference is not a preference:
/// </para>
/// <list type="bullet">
/// <item><c>height</c> is the wrong axis for a vertical video. A TikTok or a Short at
/// "1080p" is 1080 wide and 1920 high, so <c>height&lt;=1080</c> throws away the 1080 AND
/// the 720 rendition and lands on 480. yt-dlp's <c>res</c> sort field is
/// <c>min(width, height)</c>, which is what a person means by "1080p" whichever way up the
/// video is.</item>
/// <item>A sort limit already falls back the way this is supposed to. <c>res:720</c> means
/// "the largest not over 720, or the smallest there is if everything is over it", so a
/// video that only exists at 360p comes down at 360p instead of failing. Measured against
/// yt-dlp 2026.07.04: <c>res:720</c> on a 4K source picked 720, <c>res:100</c> picked 144
/// rather than erroring.</item>
/// </list>
/// <para>
/// <b>Free of everything.</b> No HelpersLib, no Avalonia, no Strings - the labels are the
/// view model's business and the selectors are pinned by ShareX.Tests, which compiles this
/// file rather than a copy of it. Anything reached for here that is not System stops that
/// project compiling.
/// </para>
/// </remarks>
internal sealed class DownloadFormat
{
    public DownloadFormat(DownloadMediaKind kind, DownloadQuality quality, DownloadAudioFormat audioFormat)
    {
        Kind = kind;
        Quality = quality;
        AudioFormat = audioFormat;
    }

    public DownloadMediaKind Kind { get; }

    public DownloadQuality Quality { get; }

    public DownloadAudioFormat AudioFormat { get; }

    public bool IsAudio => Kind == DownloadMediaKind.Audio;

    /// <summary>
    /// The ceiling in pixels of the video's short side, or 0 for "no ceiling".
    /// </summary>
    /// <remarks>
    /// Short side rather than height, because that is what <c>res</c> means to yt-dlp and
    /// what "1080p" means to a person - see the class remarks. Always 0 for audio.
    /// </remarks>
    public int MaxResolution => IsAudio ? 0 : HeightOf(Quality);

    /// <summary>The file extension an audio download ends up with.</summary>
    public string AudioExtension => AudioFormat == DownloadAudioFormat.Mp3 ? "mp3" : "m4a";

    /// <summary>
    /// Everything yt-dlp needs to be told about WHAT to fetch.
    /// </summary>
    /// <param name="isYouTube">Decides the codec preference; see the sort below.</param>
    /// <param name="ffmpeg">
    /// The merger, or null when this PC has none. Without it nothing can be joined and
    /// nothing can be converted, so both halves of this method get quieter - which is a
    /// real loss and is why the tab says so out loud rather than handing back 360p and
    /// calling it a success.
    /// </param>
    public IEnumerable<string> Arguments(bool isYouTube, string? ffmpeg)
    {
        bool canProcess = ffmpeg != null;

        yield return "-f";
        yield return Selector(canProcess);

        string? sort = Sort(isYouTube);

        if (sort != null)
        {
            yield return "-S";
            yield return sort;
        }

        if (IsAudio)
        {
            if (canProcess)
            {
                yield return "-x";

                yield return "--audio-format";
                yield return AudioExtension;

                // Best. For m4a on a source that is already AAC this costs nothing at all,
                // because yt-dlp copies the stream instead of re-encoding it; for mp3 it is
                // LAME at its highest setting.
                yield return "--audio-quality";
                yield return "0";
            }
        }
        else if (canProcess)
        {
            yield return "--merge-output-format";
            yield return "mp4";
        }

        if (canProcess)
        {
            yield return "--ffmpeg-location";
            yield return ffmpeg!;
        }
    }

    /// <summary>
    /// The <c>-f</c> expression: which streams, not which of them is best.
    /// </summary>
    /// <remarks>
    /// yt-dlp's own default shape, <c>bv*+ba/b</c>, spelled out. Nothing is filtered here
    /// on resolution or container - a filter that matches nothing is a failed download,
    /// and every preference this app has can be expressed in <see cref="Sort"/> instead,
    /// where "not available" costs a rank rather than the whole run.
    /// </remarks>
    private string Selector(bool canProcess)
    {
        if (IsAudio)
        {
            // With ffmpeg the container is decided afterwards by --audio-format, so this
            // only has to ask for sound. Without it, nothing will be converted, so ask for
            // the one container Windows opens on its own rather than for a raw Opus stream.
            return canProcess ? "bestaudio/best" : "bestaudio[ext=m4a]/bestaudio/best";
        }

        // Nothing can be merged, so only the pre-muxed formats are reachable. On YouTube
        // that is one format and it is 360p, which is exactly the state the tab warns
        // about before anybody presses Download.
        return canProcess ? "bestvideo*+bestaudio/best" : "best";
    }

    /// <summary>
    /// The <c>-S</c> ranking, or null when yt-dlp's own is right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>vcodec:h264</c> for everything except YouTube, which is inherited and stays:
    /// TikTok advertises H.265 renditions carrying an audio codec they do not actually
    /// have, so the default ranking lands on a rendition that produces a SILENT mp4 and
    /// reports success. It is never applied to YouTube, whose 1440p and 2160p tiers exist
    /// only as VP9 and AV1 - demoting those would cap everyone at 1080p.
    /// </para>
    /// <para>
    /// <c>ext:mp4:m4a</c> last, so the container preference only breaks ties. It replaces
    /// an <c>[ext=mp4]</c> filter that used to sit in the selector, where it was not a
    /// preference at all: on a site that publishes webm only, it silently removed every
    /// format and dropped the run onto the fallback branch.
    /// </para>
    /// </remarks>
    private string? Sort(bool isYouTube)
    {
        if (IsAudio)
        {
            // The bitrate ranking yt-dlp already applies is the right one, and the
            // container is settled by --audio-format after the fact.
            return null;
        }

        List<string> fields = new List<string>(3);

        int cap = MaxResolution;

        if (cap > 0)
        {
            fields.Add("res:" + cap.ToString(CultureInfo.InvariantCulture));
        }
        else if (!isYouTube)
        {
            // Resolution above codec, which is the order this app has always used off
            // YouTube. Not added for YouTube at Best, where yt-dlp's default ranking
            // already puts resolution where it belongs and prepending a bare res would
            // quietly promote it above the quality field.
            fields.Add("res");
        }

        if (!isYouTube)
        {
            fields.Add("vcodec:h264");
        }

        fields.Add("ext:mp4:m4a");

        return string.Join(",", fields);
    }

    /// <summary>
    /// The quality decision spelled the way yt-dlp reads it, for the tooltip on the picker.
    /// </summary>
    /// <remarks>
    /// The owner asked to be told what each entry in the dropdown actually maps to, and a
    /// sentence in English is an interpretation of that while this is the thing itself. It
    /// describes the case with ffmpeg present, because the case without it has a notice of
    /// its own that says the quality is capped rather than pretending otherwise.
    /// </remarks>
    public string Recipe(bool isYouTube)
    {
        string text = "-f " + Selector(true);

        string? sort = Sort(isYouTube);

        if (sort != null)
        {
            text += "  -S " + sort;
        }

        return text + (IsAudio
            ? "  -x --audio-format " + AudioExtension + " --audio-quality 0"
            : "  --merge-output-format mp4");
    }

    private static int HeightOf(DownloadQuality quality) => quality switch
    {
        DownloadQuality.H2160 => 2160,
        DownloadQuality.H1440 => 1440,
        DownloadQuality.H1080 => 1080,
        DownloadQuality.H720 => 720,
        DownloadQuality.H480 => 480,
        DownloadQuality.H360 => 360,
        _ => 0
    };

    /// <summary>
    /// Every quality worth offering, best first.
    /// </summary>
    /// <remarks>
    /// Here rather than in the view model so that the list and the numbers behind it cannot
    /// drift apart, and so the tests can walk it.
    /// </remarks>
    public static IReadOnlyList<DownloadQuality> Qualities { get; } = new[]
    {
        DownloadQuality.Best,
        DownloadQuality.H2160,
        DownloadQuality.H1440,
        DownloadQuality.H1080,
        DownloadQuality.H720,
        DownloadQuality.H480,
        DownloadQuality.H360
    };

    /// <summary>The pixel ceiling a quality stands for, or 0 for "no ceiling".</summary>
    public static int ResolutionOf(DownloadQuality quality) => HeightOf(quality);

    /// <summary>
    /// The short side of what actually came down, from yt-dlp's width and height.
    /// </summary>
    /// <remarks>
    /// The same <c>min</c> yt-dlp's <c>res</c> uses, so the number the tab reports and the
    /// number the ceiling was compared against are the same measurement. A vertical video
    /// reported as 1080x1920 is a 1080p video, and saying "1920p" would be inventing a
    /// resolution nobody offers.
    /// </remarks>
    public static int ResolutionFrom(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            return Math.Min(width, height);
        }

        return Math.Max(Math.Max(width, height), 0);
    }
}
