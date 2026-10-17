#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ShareX.HelpersLib;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace ShareX;

/// <summary>
/// The frames of one moving tile, decoded and ready to be shown in turn.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS AT ALL. Avalonia 12.1.1 has no animated image: its
/// <see cref="Bitmap"/> is one surface, and handing it an animated WebP gets the first
/// frame back with no error and no way to tell. That is the exact failure worth naming -
/// a tile that claims to move and does not - so the frames are pulled out by hand
/// instead, with <see cref="SKCodec"/>, which is already here because Skia is what
/// Avalonia draws with. Nothing new ships to make this work.
///
/// The same codec already decodes every still tile on this screen: the server renders
/// thumbnails as WebP, so a moving one is that decoder asked for more of the file.
///
/// MEMORY, RECOUNTED. The server stopped sampling eight frames across the file and
/// started sending ONE CONTINUOUS CLIP of up to thirty, at 12 fps - and every number
/// written here against the old artefact was wrong by a factor of four the day that
/// shipped. Twenty-one previews pulled from production and measured chunk by chunk,
/// every one of them thirty frames at 83 ms - 12 fps, real time - bar an animated GIF
/// at twenty-seven and a small one at fifteen:
///
///   canvas 480x480   (12 of 21) -> 288x288 decoded, 30 frames =  9.49 MiB
///   canvas 480x268..382 (5)     -> 288x161 to 288x229         =  5.31 - 7.55 MiB
///   canvas 400x300   (1)        -> 288x216 decoded, 30 frames =  7.12 MiB
///   canvas 352x480   (1)        -> 288x393 decoded, 30 frames = 12.95 MiB
///   canvas 288x480   (1)        -> 288x480 decoded, 30 frames = 15.82 MiB
///   canvas 144x256   (1)        -> never enlarged, 15 frames  =  2.11 MiB
///   encoded bytes               -> 62.6 kB to 815.9 kB, mean 220 kB
///
/// So the honest figure for one moving tile is ten megabytes, not two and a half, and
/// for a 9:16 phone video - which is not exotic - the whole clip would be sixteen.
///
/// What keeps that from multiplying is that only ONE of these exists at a time, which
/// is now ENFORCED rather than hoped for. <see cref="CloudMotionPlayer"/> is the only
/// thing that builds one; it holds one reel and disposes it the moment the pointer
/// leaves, and the pointer leaving is what precedes the next decode, so there is never
/// a second reel to put anywhere. What that argument did NOT cover, until this recount
/// went looking, is the decode itself: a hover on a clip already in
/// <see cref="CloudMotionCache"/> skips the fetch and the one slot the fetch holds, so
/// a second sweep across a folder of videos started a decode per dwell whether or not
/// the last had finished - several of these at once on any machine slow enough to
/// overlap them, which is precisely the machine that cannot afford it. The decode has
/// its own slot now, next to the fetch's. The other thing that multiplied with the
/// longer clip is the encoded cache, which is why <see cref="CloudMotionCache"/> counts
/// bytes rather than entries.
/// </remarks>
internal sealed class AnimatedThumbnail : IDisposable
{

    /// <summary>What a frame is shown for when the file says nothing usable.</summary>
    private const int FallbackDelayMs = 120;

    /// <summary>The bounds the server itself clamps its per-frame delays to.</summary>
    private const int MinDelayMs = 40;
    private const int MaxDelayMs = 2000;

    private readonly Bitmap[] frames;
    private readonly int[] delays;
    private bool disposed;

    private AnimatedThumbnail(Bitmap[] frames, int[] delays, long bytes)
    {
        this.frames = frames;
        this.delays = delays;
        Bytes = bytes;
    }

    public int FrameCount => frames.Length;

    /// <summary>How many bytes of decoded pixels this is holding.</summary>
    public long Bytes { get; }

    public Bitmap Frame(int index) => frames[Wrap(index, frames.Length)];

    /// <summary>How long frame <paramref name="index"/> stays up, in milliseconds.</summary>
    public int Delay(int index) => delays[Wrap(index, delays.Length)];

    private static int Wrap(int index, int length) => ((index % length) + length) % length;

    /// <summary>
    /// Decode an animated WebP into frames, or answer null when it does not animate.
    /// </summary>
    /// <remarks>
    /// Null for a one-frame file is the point rather than a nicety. A still dressed up as
    /// an animation would be laid over the tile's own still, change nothing anybody can
    /// see, and cost a decode and a timer to do it - which is the "silently shows one
    /// frame while claiming to animate" failure arrived at from the other direction.
    ///
    /// Runs on a worker thread. It touches no control, only builds bitmaps.
    /// </remarks>
    public static AnimatedThumbnail? Decode(byte[] encoded)
    {
        SKCodec? codec = null;
        SKBitmap? canvas = null;
        List<Bitmap> built = new();

        try
        {
            // SKCodec takes ownership of the stream it is created from, which is why this
            // one is deliberately not in a using of its own: disposing the codec disposes
            // the stream with it.
            codec = SKCodec.Create(new MemoryStream(encoded, writable: false));

            if (codec == null || codec.FrameCount < 2)
            {
                return null;
            }

            SKCodecFrameInfo[] info = codec.FrameInfo;
            int available = Math.Min(codec.FrameCount, info.Length);

            if (available < MotionFrameBudget.MinFrames || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return null;
            }

            SKImageInfo full = new(codec.Info.Width, codec.Info.Height,
                SKColorType.Bgra8888, SKAlphaType.Premul);

            // ONE full-size surface, reused. Not merely an allocation saved: a WebP frame
            // may be a patch that has to be composited onto the one before it, and Skia
            // does that by being handed a buffer that already holds the prior frame. A
            // fresh buffer per frame would decode those as fragments on transparency.
            canvas = new SKBitmap(full);

            // How large each frame is kept and how much of the clip is afforded, worked
            // out once rather than tested per frame. <see cref="MotionFrameBudget"/> holds
            // the ceiling and the argument for it, and ShareX.Tests drives it with the
            // canvas shapes actually measured on production - which is the whole reason
            // the arithmetic lives somewhere a test can reach without Skia.
            MotionFrameBudget.Plan plan = MotionFrameBudget.For(full.Width, full.Height, available);

            int width = plan.Width;
            int height = plan.Height;
            int count = plan.Frames;

            int[] delayMs = new int[count];
            long bytes = 0;
            int decoded = -1;

            for (int index = 0; index < count; index++)
            {
                // Only claim a prior frame when the one actually sitting in the buffer is
                // the one this frame is built from. Lying here produces ghosting that
                // looks like a decoder bug and is not one.
                int required = info[index].RequiredFrame;
                int prior = required >= 0 && required == decoded ? decoded : -1;

                SKCodecOptions options = new(index) { PriorFrame = prior };
                SKCodecResult result = codec.GetPixels(full, canvas.GetPixels(), options);

                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    break;
                }

                decoded = index;

                Bitmap? frame = Scale(canvas, width, height);

                if (frame == null)
                {
                    break;
                }

                built.Add(frame);
                bytes += (long)width * height * 4L;
                delayMs[built.Count - 1] = Clamp(info[index].Duration);
            }

            if (built.Count < 2)
            {
                return null;
            }

            // A run that stopped early keeps what it got: five frames of a moving tile is
            // still a moving tile, and the alternative is a still one.
            return new AnimatedThumbnail(built.ToArray(), delayMs[..built.Count], bytes);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return null;
        }
        finally
        {
            canvas?.Dispose();
            codec?.Dispose();

            if (built.Count < 2)
            {
                foreach (Bitmap orphan in built)
                {
                    orphan.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Shrink one decoded frame straight into an Avalonia surface.
    /// </summary>
    /// <remarks>
    /// Written through an <see cref="SKPixmap"/> over the locked framebuffer, so the
    /// pixels are resampled once, directly into the buffer the compositor will read.
    /// Encoding a PNG and handing it back to a decoder - the obvious way to get from Skia
    /// to Avalonia - would be a compress and a decompress per frame per hover.
    /// </remarks>
    private static Bitmap? Scale(SKBitmap source, int width, int height)
    {
        WriteableBitmap target = new(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);

        try
        {
            using (ILockedFramebuffer buffer = target.Lock())
            {
                SKImageInfo info = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

                using SKPixmap destination = new(info, buffer.Address, buffer.RowBytes);

                if (!source.ScalePixels(destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
                {
                    target.Dispose();
                    return null;
                }
            }

            return target;
        }
        catch (Exception exception)
        {
            target.Dispose();
            DebugHelper.WriteException(exception);
            return null;
        }
    }

    /// <summary>
    /// A per-frame delay that will neither stall the loop nor spin it.
    /// </summary>
    /// <remarks>
    /// Read from the artefact rather than assumed, because the server sets it
    /// deliberately, and it changed: a video clip now says 83 ms a frame, which is the
    /// reciprocal of the 12 fps it was captured at, so the preview plays at REAL TIME. It
    /// used to say 400 ms, because eight frames taken from eight places in a file have no
    /// shared clock and playing them at any particular rate would have been a claim about
    /// a continuity that was not there. An animation still carries its own length spread
    /// over its frames. A zero - which is what a GIF written by some encoders says - would
    /// mean a timer firing as fast as the dispatcher allows.
    /// </remarks>
    private static int Clamp(int duration) =>
        duration <= 0 ? FallbackDelayMs : Math.Clamp(duration, MinDelayMs, MaxDelayMs);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        foreach (Bitmap frame in frames)
        {
            frame.Dispose();
        }
    }
}
