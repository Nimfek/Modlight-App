#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.Localization;

namespace ShareX;

/// <summary>
/// The words for what a conversion does, and for why the server would not do it.
/// </summary>
/// <remarks>
/// One place, because the same two vocabularies are needed in two: the menu labels the
/// entries with what each one costs the file, and the refusal toast has to turn
/// <c>X-Convert-Refused</c> into a sentence. The server keeps its half of this in
/// <c>lib/media/convert-notes.ts</c> and has a test that walks its own source to prove
/// every token it can emit has a translation - which is the standard this has to meet
/// from the other side.
///
/// An UNKNOWN token gets the "refused without saying why" sentence rather than a guess.
/// That rule is worth stating: the server's own mapper once ended in a default that
/// claimed the file had been cut short, so an unrecognised note became a confident,
/// specific and false statement about somebody's download. Silence is the better failure.
/// </remarks>
internal static class ModlightConvertText
{
    /// <summary>The small grey line under a "Download as..." entry.</summary>
    public static string Hint(ModlightDownloadHint hint) => hint switch
    {
        ModlightDownloadHint.KeepsAnimation => Strings.MySpace_ConvertKeepsAnimation,
        ModlightDownloadHint.SingleFrame => Strings.MySpace_ConvertSingleFrame,
        ModlightDownloadHint.FrameIfAnimated => Strings.MySpace_ConvertFrameIfAnimated,
        ModlightDownloadHint.ContainerCopy => Strings.MySpace_ConvertContainerCopy,
        ModlightDownloadHint.AnimationClip => Strings.MySpace_ConvertAnimationClip,
        ModlightDownloadHint.KeepsMotion => Strings.MySpace_ConvertKeepsMotion,
        ModlightDownloadHint.AudioTrack => Strings.MySpace_ConvertAudioTrack,
        ModlightDownloadHint.AudioFormat => Strings.MySpace_ConvertAudioFormat,
        ModlightDownloadHint.AudioAsVideo => Strings.MySpace_ConvertAudioAsVideo,
        _ => ""
    };

    /// <summary>Why the server said no, in a sentence.</summary>
    public static string Refusal(string reason) => reason switch
    {
        "encode-requires-owner" => Strings.MySpace_RefusedEncodeRequiresOwner,
        "ffmpeg-unavailable" => Strings.MySpace_RefusedFfmpegUnavailable,
        "video-codec" => Strings.MySpace_RefusedVideoCodec,
        "audio-codec" => Strings.MySpace_RefusedAudioCodec,
        "no-video" => Strings.MySpace_RefusedNoVideo,
        "no-audio" => Strings.MySpace_RefusedNoAudio,
        "unreadable" => Strings.MySpace_RefusedUnreadable,
        "too-large" => Strings.MySpace_RefusedTooLarge,
        "too-long" => Strings.MySpace_RefusedTooLong,
        "too-many-pixels" => Strings.MySpace_RefusedTooManyPixels,
        "wrong-target" => Strings.MySpace_RefusedWrongTarget,
        "empty-output" => Strings.MySpace_RefusedEmptyOutput,
        "busy" => Strings.MySpace_RefusedBusy,
        "timeout" => Strings.MySpace_RefusedTimeout,
        "missing" => Strings.MySpace_RefusedMissing,
        "offline" => Strings.MySpace_RefusedOffline,
        _ => Strings.MySpace_RefusedUnknown
    };
}
