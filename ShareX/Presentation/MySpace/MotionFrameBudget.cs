#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;

namespace ShareX;

/// <summary>
/// How large one moving tile is decoded, and how much of its clip is kept.
/// </summary>
/// <remarks>
/// Its own file, and pure, for the reason <see cref="VisibleBand"/> is: the numbers here
/// are the whole of a memory budget, they were WRONG once already - the server swapped
/// eight sampled frames for a thirty-frame clip and every figure written against the old
/// artefact was out by a factor of four overnight - and a budget that is only argued for
/// in a comment is one nobody notices going stale. ShareX.Tests links this file and
/// drives it with the shapes actually measured on production, so the next time the
/// artefact changes shape the arithmetic says so instead of the memory graph saying it.
///
/// Nothing here decodes anything. <see cref="AnimatedThumbnail"/> asks what to build and
/// then builds it; separating the two is what lets the question be asked without Skia,
/// Avalonia or a window.
/// </remarks>
internal static class MotionFrameBudget
{
    /// <summary>
    /// How wide a frame is kept, in pixels.
    /// </summary>
    /// <remarks>
    /// The same number the still path decodes to - <c>MySpaceViewModel.ThumbnailDecodeWidth</c>
    /// - and for the same reason: the card draws into 178x126 logical pixels, which is 267
    /// across at the 150% scaling most laptops ship at. Matching it also matters in a way
    /// it does not for a still: the moving tile is laid directly over the still one, so a
    /// difference in sharpness between the two would be visible at the moment of the swap.
    ///
    /// A CEILING, never a target. A preview whose canvas is already narrower than this is
    /// kept at its own size rather than enlarged - one measured on production is 144x256,
    /// and stretching it to 288 would cost four times the memory to show the same picture
    /// blurred.
    /// </remarks>
    public const int MaxWidth = 288;

    /// <summary>
    /// How many bytes of decoded frames one tile may hold.
    /// </summary>
    /// <remarks>
    /// The real fence, and it is counted in BYTES because the frames are not all the same
    /// size: the same thirty frames are 9.49 MiB square and 15.82 MiB at 9:16, and a
    /// ceiling counted in frames would be a ceiling on memory only by accident. That was
    /// survivable while a preview was eight frames; at thirty it is the difference between
    /// ten megabytes and sixteen.
    ///
    /// Ten mebibytes buys the WHOLE clip for nineteen of the twenty-one previews measured
    /// on production - every landscape, square and short-of-square tile, at 9.49 MiB or
    /// less for thirty frames - and clips only the two tall ones: twenty-three frames of
    /// thirty for a 3:4 tile, eighteen of thirty for a 9:16 one, which is 1.5 seconds of a
    /// 2.5 second clip before it loops.
    ///
    /// FRAMES are what gets dropped, and only on the tall tiles, because the two
    /// alternatives are worse. Decoding smaller - capping the HEIGHT at 288 as well as the
    /// width - would leave the moving tile visibly softer than the still it is laid
    /// directly over, and the swap is the exact moment that difference shows. Raising the
    /// ceiling to sixteen megabytes would spend sixty percent more on the two rarest
    /// shapes, to buy the last second of something that loops anyway.
    /// </remarks>
    public const long MaxBytes = 10L * 1024 * 1024;

    /// <summary>
    /// The most frames one tile may hold, whatever they cost.
    /// </summary>
    /// <remarks>
    /// A second fence, for the shape <see cref="MaxBytes"/> cannot catch: a file whose
    /// frames are tiny fits thousands of them inside ten megabytes, and every one is a
    /// decode and a native surface. Sixty is twice what the server sends, so the server
    /// could double the clip without a client change, and nothing this side has to know
    /// the number thirty.
    /// </remarks>
    public const int MaxFrames = 60;

    /// <summary>The smallest reel worth building. Below this it is a still with a timer.</summary>
    /// <remarks>
    /// Two, and it is a FLOOR rather than a rounding: the budget is allowed to clip a clip,
    /// but a reel of one frame would be laid over the tile's own still, change nothing
    /// anybody can see, and cost a decode and a timer to do it. At 288 pixels wide no
    /// frame the server can produce is large enough for this to bite - the tallest shape
    /// measured affords eighteen - so it is a fence around a future server rather than a
    /// case that happens today.
    /// </remarks>
    public const int MinFrames = 2;

    /// <summary>What one tile will be decoded to, and how much of its clip it will keep.</summary>
    /// <param name="Width">Pixel width of every frame.</param>
    /// <param name="Height">Pixel height of every frame.</param>
    /// <param name="Frames">How many frames to decode.</param>
    /// <param name="Bytes">What holding them all comes to.</param>
    public readonly record struct Plan(int Width, int Height, int Frames, long Bytes);

    /// <summary>
    /// Work out the plan for one preview.
    /// </summary>
    /// <remarks>
    /// Worked out ONCE per file rather than tested per frame: every frame of one artefact
    /// is the same size, so the answer cannot change halfway through the decode loop, and
    /// a budget re-tested inside the loop is a budget that can stop on a different frame
    /// depending on when it was asked.
    /// </remarks>
    /// <param name="canvasWidth">The artefact's own width, from its header.</param>
    /// <param name="canvasHeight">The artefact's own height.</param>
    /// <param name="available">How many frames the artefact actually offers.</param>
    public static Plan For(int canvasWidth, int canvasHeight, int available)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0 || available <= 0)
        {
            return new Plan(0, 0, 0, 0);
        }

        int width = Math.Min(MaxWidth, canvasWidth);
        int height = Math.Max(1, (int)Math.Round(canvasHeight * (double)width / canvasWidth));

        long frameBytes = Math.Max(1L, (long)width * height * 4L);
        int affordable = (int)Math.Min(int.MaxValue, MaxBytes / frameBytes);

        int frames = Math.Min(Math.Min(available, MaxFrames), Math.Max(MinFrames, affordable));

        // Never more than the file has. The floor above is what makes this line necessary:
        // it can raise the answer past `available` on a one-frame artefact, and decoding a
        // frame that is not there is the kind of mistake that reads as a decoder fault.
        frames = Math.Min(frames, available);

        return new Plan(width, height, frames, frameBytes * frames);
    }
}
