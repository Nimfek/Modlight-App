#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Collections.Generic;

namespace ShareX;

/// <summary>What a "Download as..." entry does to the file it came from.</summary>
/// <remarks>
/// Rendered as the small grey line under the format name, exactly as the web panel
/// renders it. Every value here has a sentence in both languages; see
/// <c>ModlightConvertText</c>, which is what turns one of these into that sentence.
/// </remarks>
internal enum ModlightDownloadHint
{
    /// <summary>Nothing worth saying: a still picture going to another still format.</summary>
    None,

    /// <summary>The animation survives (GIF or APNG into an animated WebP).</summary>
    KeepsAnimation,

    /// <summary>This file holds several frames and one of them comes out.</summary>
    SingleFrame,

    /// <summary>Nobody has counted this file's frames; if it moves, one frame comes out.</summary>
    FrameIfAnimated,

    /// <summary>The same picture and sound, poured into another container.</summary>
    ContainerCopy,

    /// <summary>A short silent excerpt of something longer.</summary>
    AnimationClip,

    /// <summary>The whole moving picture, in another format.</summary>
    KeepsMotion,

    /// <summary>The sound lifted out of a file that also has pictures.</summary>
    AudioTrack,

    /// <summary>The same sound in another sound format.</summary>
    AudioFormat,

    /// <summary>Sound on a still picture, for places that only accept video.</summary>
    AudioAsVideo
}

/// <summary>One entry of the "Download as..." menu.</summary>
internal sealed class ModlightDownloadOption
{
    public ModlightDownloadOption(string to, ModlightDownloadHint hint, bool losesMotion)
    {
        To = to;
        Hint = hint;
        LosesMotion = losesMotion;
    }

    /// <summary>The value for <c>?to=</c>, and the label once upper-cased.</summary>
    public string To { get; }

    public ModlightDownloadHint Hint { get; }

    /// <summary>
    /// This entry throws the motion away, so it sits below a heading of its own.
    /// </summary>
    /// <remarks>
    /// Taking a still out of a GIF is a thing people do, so the entry stays - but it must
    /// never be reachable by accident from a menu whose other entries keep the animation.
    /// </remarks>
    public bool LosesMotion { get; }
}

/// <summary>
/// Which formats one file can be turned into.
/// </summary>
/// <remarks>
/// A port of the server's three modules - <c>lib/media/download-menu.ts</c>,
/// <c>av-targets.ts</c> and <c>convert-targets.ts</c> - and it has to stay a port rather
/// than becoming a fourth opinion. Those files are the ONLY answer the /convert route
/// admits: a target this menu offers that the route does not know is a menu entry that
/// answers 415, and one it knows but this menu hides is a conversion the owner cannot
/// reach. The comments below carry the measurements the server's own comments carry,
/// because they are the reason each line is the shape it is.
///
/// TWO THINGS THIS CLIENT KNOWS LESS THAN THE PANEL DOES.
///
/// GET /api/uploads does not return <c>uploads.frames</c>, so every file here is treated
/// as "nobody has counted", which is also the state every imported row is
/// really in. That is why the animation hints below say "if this file moves" rather than
/// asserting it: a warning has to describe the file in front of the reader, and where we
/// cannot tell, saying so is the honest answer.
///
/// The viewer is always the OWNER - the listing endpoint returns nothing else - so the
/// encoder-backed targets are all offered. A public visitor's shorter menu is the
/// panel's problem, not this one's.
///
/// A container entry is a genuine offer that a particular file can still decline: whether
/// a .mkv's codecs fit an .mp4 is a question about the bytes, and neither the panel nor
/// this menu opens the file to draw itself. That is what the dry run is for - see
/// <see cref="ModlightLibrary.CheckConversionAsync"/>.
/// </remarks>
internal static class ModlightDownloadMenu
{
    /* -- image side (convert-targets.ts) ------------------------------------ */

    /// <summary>Every picture target, in menu order.</summary>
    private static readonly string[] ImageTargets = { "pdf", "png", "jpg", "webp", "avif" };

    /// <summary>Containers that can hold an animation.</summary>
    private static readonly HashSet<string> AnimationCapable = new(StringComparer.Ordinal)
    {
        "image/gif", "image/apng", "image/webp", "image/avif"
    };

    /// <summary>
    /// ...and the subset sharp can re-encode AS an animation.
    /// </summary>
    /// <remarks>
    /// AVIF is absent in both directions, measured rather than assumed. Reading: libheif
    /// hands libvips only the primary item of an AVIF sequence. Writing: sharp does not
    /// produce animated AVIF at all - it stacks the frames into one tall strip.
    /// </remarks>
    private static readonly HashSet<string> AnimationReadable = new(StringComparer.Ordinal)
    {
        "image/gif", "image/apng", "image/webp"
    };

    /// <summary>
    /// The target a file already IS, which is therefore not a conversion.
    /// </summary>
    /// <remarks>
    /// "Download this PNG as PNG" spends a decode and an encode to hand back the file the
    /// reader already has, and for JPEG it hands back a visibly worse one.
    /// </remarks>
    private static readonly Dictionary<string, string> SelfTarget = new(StringComparer.Ordinal)
    {
        ["image/png"] = "png",
        ["image/apng"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/webp"] = "webp",
        ["image/avif"] = "avif"
    };

    /* -- audio and video side (av-targets.ts) -------------------------------- */

    /// <summary>
    /// Which containers each kind of source is offered, in menu order.
    /// </summary>
    /// <remarks>
    /// Every entry is a stream COPY, never a transcode, and that is the whole
    /// authorization and cost story: measured on the production container, a .mov to .mp4
    /// copy of a 37 MB file takes 269 ms, while re-encoding a 32 s video takes 5.5 s -
    /// six times realtime, which does not fit in an HTTP request.
    ///
    /// MKV is offered for everything because Matroska takes every codec; it is the entry
    /// that cannot decline. WebM is offered only FROM Matroska: its codec list is
    /// VP8/VP9/AV1 plus Opus/Vorbis, and a .mkv in circulation is almost always H.264, so
    /// the reverse direction was removed after it answered 409 for every real file.
    /// </remarks>
    private static readonly Dictionary<string, string[]> ContainerOffers = new(StringComparer.Ordinal)
    {
        ["mp4"] = new[] { "mov", "mkv" },
        ["mov"] = new[] { "mp4", "mkv" },
        ["mkv"] = new[] { "mp4", "mov" },
        ["webm"] = new[] { "mkv" },
        ["other"] = new[] { "mp4", "mov", "mkv" }
    };

    /// <summary>The container family a row is in, from the type we would reason with.</summary>
    private static readonly Dictionary<string, string> SourceContainer = new(StringComparer.Ordinal)
    {
        ["video/mp4"] = "mp4",
        ["video/x-m4v"] = "mp4",
        ["video/m4v"] = "mp4",
        ["video/3gpp"] = "mp4",
        ["video/3gpp2"] = "mp4",
        ["video/quicktime"] = "mov",
        ["video/x-matroska"] = "mkv",
        ["video/webm"] = "webm"
    };

    /// <summary>Sound formats we can encode into.</summary>
    private static readonly string[] AudioTargets = { "mp3", "ogg", "wav" };

    /// <summary>...and the types that already ARE each one.</summary>
    private static readonly Dictionary<string, string> AudioSelf = new(StringComparer.Ordinal)
    {
        ["audio/mpeg"] = "mp3",
        ["audio/mp3"] = "mp3",
        ["audio/ogg"] = "ogg",
        ["audio/vorbis"] = "ogg",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
        ["audio/wave"] = "wav"
    };

    /// <summary>Image containers libvips cannot animate for us, but ffmpeg can.</summary>
    private static readonly HashSet<string> FfmpegOnlyAnimation = new(StringComparer.OrdinalIgnoreCase)
    {
        "avif", "heic", "heif"
    };

    /// <summary>
    /// Types that say nothing, so the extension gets to answer instead.
    /// </summary>
    /// <remarks>
    /// This is the case Round 7 was about: Windows reads a file's type out of the
    /// registry, which has no entry for .avif and half the formats invented since, so
    /// ShareX stored them as application/octet-stream. The server now sniffs at upload
    /// time, but rows predating that are still out there.
    /// </remarks>
    private static readonly HashSet<string> UselessTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/octet-stream", "binary/octet-stream", "application/unknown", "unknown/unknown"
    };

    /// <summary>Enough of the server's extension table to answer the questions asked here.</summary>
    private static readonly Dictionary<string, string> TypeFromExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png",
        ["apng"] = "image/apng",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
        ["avif"] = "image/avif",
        ["mp4"] = "video/mp4",
        ["m4v"] = "video/x-m4v",
        ["mov"] = "video/quicktime",
        ["mkv"] = "video/x-matroska",
        ["webm"] = "video/webm",
        ["mp3"] = "audio/mpeg",
        ["ogg"] = "audio/ogg",
        ["oga"] = "audio/ogg",
        ["wav"] = "audio/wav",
        ["m4a"] = "audio/mp4",
        ["flac"] = "audio/flac"
    };

    /// <summary>
    /// Everything this file offers, ordered so nothing that loses motion comes first.
    /// </summary>
    /// <param name="kind">The server's own class for the row: image, video, audio, ...</param>
    public static IReadOnlyList<ModlightDownloadOption> For(string? kind, string? mimeType, string? extension)
    {
        string type = EffectiveType(mimeType, extension);
        string ext = Normalise(extension);

        List<ModlightDownloadOption> main = new();
        List<ModlightDownloadOption> frame = new();

        List<ModlightDownloadOption> av = AudioVideoTargets(kind, type, ext);
        HashSet<string> claimed = new(StringComparer.Ordinal);

        foreach (ModlightDownloadOption option in av)
        {
            claimed.Add(option.To);
            main.Add(option);
        }

        // Video and audio belong to ffmpeg alone. An ANIMATION is genuinely claimed by
        // both - sharp keeps a GIF's motion in WebP, ffmpeg turns the same GIF into an
        // MP4 seventy times smaller - and where the two name the same target, ffmpeg
        // wins, because it is the one that reaches every frame of an AVIF sequence.
        bool anythingKeepsMotion = false;
        List<ModlightDownloadOption> images = new();

        foreach (ModlightDownloadOption option in ImageTargetsFor(kind, type))
        {
            if (claimed.Contains(option.To))
            {
                continue;
            }

            images.Add(option);
            anythingKeepsMotion |= option.Hint == ModlightDownloadHint.KeepsAnimation;
        }

        foreach (ModlightDownloadOption option in av)
        {
            anythingKeepsMotion |= option.Hint == ModlightDownloadHint.KeepsMotion;
        }

        foreach (ModlightDownloadOption option in images)
        {
            // "One frame comes out" is a guess on a row nobody has counted. Where the
            // motion is reachable by some other entry the guess is worth acting on and the
            // still targets are demoted; with nothing to demote them below they stay put
            // and say "if" instead, rather than heading a section about a loss that may
            // not be happening.
            bool demote = option.Hint == ModlightDownloadHint.SingleFrame ||
                          (option.Hint == ModlightDownloadHint.FrameIfAnimated && anythingKeepsMotion);

            (demote ? frame : main).Add(new ModlightDownloadOption(option.To, option.Hint, demote));
        }

        main.AddRange(frame);
        return main;
    }

    private static List<ModlightDownloadOption> AudioVideoTargets(string? kind, string type, string ext)
    {
        List<ModlightDownloadOption> options = new();

        if (kind == "video")
        {
            string source = SourceContainer.TryGetValue(type, out string? family) ? family : "other";

            foreach (string to in ContainerOffers[source])
            {
                options.Add(new ModlightDownloadOption(to, ModlightDownloadHint.ContainerCopy, losesMotion: false));
            }

            // A moving excerpt, and the sound. There is no still-frame entry: GIF is what
            // people mean by "make me a moving snippet", and a video's most-wanted
            // conversion is another video, not a picture of one.
            options.Add(new ModlightDownloadOption("gif", ModlightDownloadHint.AnimationClip, losesMotion: false));
            options.Add(new ModlightDownloadOption("mp3", ModlightDownloadHint.AudioTrack, losesMotion: false));
            return options;
        }

        if (kind == "audio")
        {
            foreach (string to in AudioTargets)
            {
                // No entry for the format the file already is: "download this MP3 as MP3"
                // is a re-encode that can only ever lose quality.
                bool isSelf = (AudioSelf.TryGetValue(type, out string? self) && self == to) ||
                              string.Equals(ext, to, StringComparison.OrdinalIgnoreCase);

                if (!isSelf)
                {
                    options.Add(new ModlightDownloadOption(to, ModlightDownloadHint.AudioFormat, losesMotion: false));
                }
            }

            // Sound on a still picture. Everywhere that accepts a video and refuses an
            // audio file - most social timelines, some chat clients - this is the only way
            // to post the thing.
            options.Add(new ModlightDownloadOption("mp4", ModlightDownloadHint.AudioAsVideo, losesMotion: false));
            return options;
        }

        if (kind == "image")
        {
            // The case this feature started from: an AVIF image sequence. libvips reads
            // only its cover frame, so the picture menu can offer nothing but stills and
            // the frames in the file are unreachable; ffmpeg demuxes it and gets them all.
            if (FfmpegOnlyAnimation.Contains(ext))
            {
                options.Add(new ModlightDownloadOption("gif", ModlightDownloadHint.KeepsMotion, losesMotion: false));
                options.Add(new ModlightDownloadOption("webp", ModlightDownloadHint.KeepsMotion, losesMotion: false));
                return options;
            }

            // A GIF's most valuable conversion by a wide margin, and libvips cannot do it:
            // measured on a production 2.39 MB GIF, the MP4 is 32 kB with the motion
            // intact - seventy-four times smaller, in 231 ms.
            if (type == "image/gif")
            {
                options.Add(new ModlightDownloadOption("mp4", ModlightDownloadHint.KeepsMotion, losesMotion: false));
            }
        }

        return options;
    }

    private static List<ModlightDownloadOption> ImageTargetsFor(string? kind, string type)
    {
        List<ModlightDownloadOption> options = new();

        // The gate is the server's own class, not a "image/" prefix test, and it is
        // stricter on purpose: SVG, HEIC and JXL are images libvips cannot open in this
        // build, and admitting them meant offering five conversions the route then
        // answered 415 to.
        if (kind != "image")
        {
            return options;
        }

        bool canAnimate = AnimationCapable.Contains(type);
        bool keepsMotion = AnimationReadable.Contains(type);

        foreach (string to in ImageTargets)
        {
            if (SelfTarget.TryGetValue(type, out string? self) && self == to)
            {
                continue;
            }

            if (!canAnimate)
            {
                options.Add(new ModlightDownloadOption(to, ModlightDownloadHint.None, losesMotion: false));
                continue;
            }

            // Nobody has counted this file's frames (the listing does not carry the
            // number), so a still target says "if this moves" rather than asserting a
            // loss. AVIF is offered here too, as a still: it was withheld once on the
            // grounds that it "does not work", which was untrue - the real objection was
            // cost, and the server charges that where it belongs.
            ModlightDownloadHint hint = to == "webp" && keepsMotion
                ? ModlightDownloadHint.KeepsAnimation
                : ModlightDownloadHint.FrameIfAnimated;

            options.Add(new ModlightDownloadOption(to, hint, losesMotion: false));
        }

        // The one that survives intact goes first; the rest keep their listed order.
        options.Sort((a, b) =>
            (b.Hint == ModlightDownloadHint.KeepsAnimation ? 1 : 0) -
            (a.Hint == ModlightDownloadHint.KeepsAnimation ? 1 : 0));

        return options;
    }

    /// <summary>
    /// The type worth reasoning with: the stored one, unless it says nothing.
    /// </summary>
    /// <remarks>
    /// The extension counts ONLY when the stored type is one of the useless ones, which is
    /// the same order the server applies. Letting the extension win outright would mean a
    /// file named .jpg but stored as a PNG being offered the wrong conversions.
    /// </remarks>
    private static string EffectiveType(string? mimeType, string? extension)
    {
        string mime = (mimeType ?? "").Split(';')[0].Trim().ToLowerInvariant();

        if (mime.Length > 0 && !UselessTypes.Contains(mime))
        {
            return mime;
        }

        return TypeFromExtension.TryGetValue(Normalise(extension), out string? fromExt) ? fromExt : mime;
    }

    private static string Normalise(string? extension) => (extension ?? "").TrimStart('.').ToLowerInvariant();
}
