#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Threading;
using ShareX.HelpersLib;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// Decides when a tile in the grid is allowed to move, gets it the frames, and runs them.
/// </summary>
/// <remarks>
/// WHY HOVER, AND NOT EVERY TILE. The server renders these on demand and the cost was
/// measured: a 4K video is 7.4 seconds of its CPU the first time and 0.5 ms once cached,
/// a 1080p one about 4 seconds. A folder of two hundred recordings drawn eagerly is
/// therefore something like twenty minutes of one machine's CPU to produce two hundred
/// previews the user will glance at maybe three of - and the server puts every one of
/// them through a weighted limiter, so they do not even arrive in parallel: they queue,
/// behind each other and behind every still thumbnail, every /convert and every poster
/// frame the rest of the deployment is asking for at the same time. The grid would go
/// quiet for minutes and the cause would be this feature. Hovering is the one signal
/// that says which single file a person is actually interested in.
///
/// The numbers also settle the DWELL. At 7.4 seconds a preview will usually not arrive
/// while a pointer is merely passing over a tile, so asking on pointer-enter would buy
/// nothing and spend everything; a third of a second of stillness is the cheapest
/// available evidence that somebody stopped on purpose.
///
/// ONE AT A TIME, and never cancelled once sent. Single-flight is obvious - two of these
/// in the air is fourteen seconds of somebody else's machine. Not cancelling is the part
/// worth stating: the serving route passes the caller's abort straight through to the
/// render, so dropping a request that is already running throws away the work AND the
/// cache entry it was about to become. Letting it finish means the seven seconds are
/// spent once, ever, for that file - by anyone, in the app or in the browser. A hover
/// that has moved on simply does not get shown the result.
///
/// WHAT THE USER SEES. The still thumbnail is the base layer and stays where it is; the
/// frames are laid over it, in the same box, at the same size, rendered by the server
/// from the same source with the same fit. So there is nothing to fall back to and
/// nothing to flash: before the first frame arrives the tile is exactly the tile it
/// already was, and when the pointer leaves it is that again.
/// </remarks>
internal sealed class CloudMotionPlayer : IDisposable
{
    /// <summary>
    /// How long a pointer has to sit still on a tile before anything is asked for.
    /// </summary>
    /// <remarks>
    /// Long enough that crossing the grid to reach a scrollbar asks for nothing at all -
    /// a pointer travelling at any ordinary speed is on a 178 px tile for well under this
    /// - and short enough that somebody who stopped to look does not feel they are
    /// waiting on a delay we added.
    /// </remarks>
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(320);

    private readonly DispatcherTimer dwellTimer;
    private readonly DispatcherTimer frameTimer;

    /// <summary>The card the pointer is on, whether or not anything has been asked for yet.</summary>
    private CloudFileViewModel? hovered;

    /// <summary>When the pointer arrived on it, to tell a pause from a pass.</summary>
    private DateTime hoveredAt;

    /// <summary>The card whose frames are on screen.</summary>
    private CloudFileViewModel? playing;

    /// <summary>The card a request is out for. Non-null means the one slot is taken.</summary>
    private CloudFileViewModel? fetching;

    /// <summary>The card whose bytes are being turned into frames. The one decode slot.</summary>
    private CloudFileViewModel? decoding;

    private AnimatedThumbnail? reel;
    private int frameIndex;
    private bool disposed;

    public CloudMotionPlayer()
    {
        dwellTimer = new DispatcherTimer { Interval = Dwell };
        dwellTimer.Tick += OnDwellElapsed;

        frameTimer = new DispatcherTimer();
        frameTimer.Tick += OnFrameElapsed;
    }

    /// <summary>How many bytes of decoded frames are being held. One tile's worth, or none.</summary>
    public long Bytes => reel?.Bytes ?? 0;

    /// <summary>
    /// The pointer has arrived on a card.
    /// </summary>
    /// <remarks>
    /// The account is taken here rather than looked up later so that a preview cannot be
    /// fetched with one account's token for a file listed under another's, and the
    /// listing's own token comes along so that changing folder abandons the fetch the way
    /// it abandons everything else in flight.
    /// </remarks>
    public void Enter(CloudFileViewModel file, string host, string token, CancellationToken cancellationToken)
    {
        if (disposed || !Wanted(file))
        {
            return;
        }

        if (ReferenceEquals(hovered, file))
        {
            return;
        }

        // Whatever was moving is not this card. Stopped before the new one is even asked
        // for, because two tiles moving at once reads as a bug rather than as a preview.
        Stop();

        hovered = file;
        hoveredAt = DateTime.UtcNow;

        dwellTimer.Stop();
        dwellTimer.Tag = new Context(file, host, token, cancellationToken);
        dwellTimer.Start();
    }

    /// <summary>The pointer has left a card. Ignored when it left one it was not on.</summary>
    public void Leave(CloudFileViewModel file)
    {
        if (disposed || !ReferenceEquals(hovered, file))
        {
            return;
        }

        hovered = null;
        dwellTimer.Stop();
        Stop();
    }

    /// <summary>
    /// Forget everything: nothing is hovered, nothing is playing, no frames are held.
    /// </summary>
    /// <remarks>
    /// Called when the listing is thrown away. The cards are about to be disposed, and a
    /// timer still ticking against one of them would write a frame onto an object nothing
    /// is showing - and hold its pixels for as long as the timer ran.
    /// </remarks>
    public void Reset()
    {
        hovered = null;
        fetching = null;
        decoding = null;
        dwellTimer.Stop();

        // The pending attempt names a card, and the cards are what is being thrown away.
        dwellTimer.Tag = null;
        Stop();
    }

    /// <summary>One card is leaving the grid. Let go of it if it is the one being shown.</summary>
    public void Forget(CloudFileViewModel file)
    {
        if (ReferenceEquals(hovered, file))
        {
            hovered = null;
            dwellTimer.Stop();
            dwellTimer.Tag = null;
        }

        if (ReferenceEquals(playing, file))
        {
            Stop();
        }
    }

    /// <summary>
    /// Should this card be offered a moving preview at all?
    /// </summary>
    /// <remarks>
    /// The system setting is read on every hover rather than once at startup, so that
    /// turning "Show animations in Windows" off takes the motion away from the next tile
    /// somebody points at instead of at the next launch.
    ///
    /// The server's own answer comes first: <c>motionThumbUrl</c> is null for every row
    /// that cannot produce one, which on a library of screenshots is nearly all of them.
    /// </remarks>
    private static bool Wanted(CloudFileViewModel file) =>
        file.CanMotion && !CloudMotionCache.IsRefused(file.Code) && ClientAreaAnimation.Enabled;

    private void OnDwellElapsed(object? sender, EventArgs e)
    {
        dwellTimer.Stop();

        if (dwellTimer.Tag is Context context && ReferenceEquals(hovered, context.File))
        {
            _ = BeginAsync(context);
        }
    }

    /// <summary>
    /// Get one card its frames, and show them if the pointer is still there.
    /// </summary>
    private async Task BeginAsync(Context context)
    {
        CloudFileViewModel file = context.File;

        if (!Wanted(file) || context.Cancellation.IsCancellationRequested)
        {
            return;
        }

        byte[]? bytes = CloudMotionCache.Get(file.Code);

        if (bytes == null)
        {
            // The one slot is taken. Nothing is queued behind it on purpose: by the time
            // it frees up the pointer will be wherever it is then, and that is a better
            // guess at what to fetch than anything decided seconds ago.
            if (fetching != null)
            {
                return;
            }

            fetching = file;

            try
            {
                ModlightMotionPreview preview = await Task.Run(() =>
                    ModlightLibrary.GetMotionThumbnailAsync(context.Host, context.Token,
                        file.MotionThumbnailUrl, context.Cancellation));

                if (preview.IsRefused)
                {
                    // The server has said this file will never have one. Written down so
                    // that pointing at the tile again is free.
                    CloudMotionCache.Refuse(file.Code);
                    return;
                }

                if (!preview.IsOk)
                {
                    // Backpressure, a timeout, a dropped connection. About this moment
                    // rather than about the file, so nothing is remembered and the next
                    // hover is free to try again. There is no retry here: a loop against
                    // a server that just said it is busy is the wrong answer.
                    return;
                }

                bytes = preview.Bytes!;
                CloudMotionCache.Put(file.Code, bytes);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception);
                return;
            }
            finally
            {
                fetching = null;

                // The pointer may have settled somewhere else while this was in the air.
                // Never on THIS file: the rest of this method is about to show it, and
                // starting a second attempt for the same card would decode it twice and
                // swap the tile twice to arrive at the same picture.
                Resume(file);
            }
        }

        if (context.Cancellation.IsCancellationRequested || !ReferenceEquals(hovered, file))
        {
            // Not wasted: the bytes are in the cache, so the seconds the server spent are
            // spent once and the next hover on this tile starts moving immediately.
            return;
        }

        byte[] encoded = bytes;

        // ONE DECODE AT A TIME, and this is the line that makes "one tile's worth, or
        // none" a fact rather than nearly one. The fetch above already has its own slot,
        // but a fetch is not what a reel costs: a hit in CloudMotionCache skips the fetch
        // entirely and goes straight here, and thirty frames at 288 px are 9.5 MiB square
        // and 15.8 MiB for a 9:16 phone video - measured, on production, in
        // AnimatedThumbnail. Sweep a pointer across a folder of videos on the SECOND pass,
        // when every clip is already cached, and one decode starts per dwell whether or
        // not the last has finished; nothing anywhere caps how many run at once, so the
        // budget AnimatedThumbnail spends so much care bounding per tile is multiplied by
        // however many the machine is slow enough to overlap. Skipping is safe because the
        // completion below picks up wherever the pointer has settled since, exactly as the
        // fetch does.
        if (decoding != null)
        {
            return;
        }

        decoding = file;

        AnimatedThumbnail? decoded;

        try
        {
            decoded = await Task.Run(() => AnimatedThumbnail.Decode(encoded));
        }
        finally
        {
            decoding = null;
            Resume(file);
        }

        if (decoded == null)
        {
            // Bytes that do not animate. A property of the artefact, not of the moment, so
            // it is filed with the refusals - otherwise every hover would decode it again
            // to learn the same thing, and the tile would claim to move while showing one
            // frame.
            CloudMotionCache.Refuse(file.Code);
            return;
        }

        if (disposed || context.Cancellation.IsCancellationRequested || !ReferenceEquals(hovered, file))
        {
            decoded.Dispose();
            return;
        }

        Play(file, decoded);
    }

    /// <summary>
    /// A request has finished; pick up the tile the pointer has been sitting on since.
    /// </summary>
    /// <remarks>
    /// Gated on the same dwell as the first request, which is what stops a slow sweep
    /// across a folder of videos from turning every completion into another fetch. The
    /// pointer has to have been where it is for longer than the dwell - not merely be
    /// somewhere - for anything to be asked for.
    /// </remarks>
    private void Resume(CloudFileViewModel justFetched)
    {
        if (disposed || hovered == null || ReferenceEquals(hovered, justFetched))
        {
            return;
        }

        if (dwellTimer.Tag is not Context context || !ReferenceEquals(hovered, context.File) ||
            DateTime.UtcNow - hoveredAt < Dwell)
        {
            return;
        }

        _ = BeginAsync(context);
    }

    private void Play(CloudFileViewModel file, AnimatedThumbnail decoded)
    {
        Stop();

        // The one line that says what this feature costs in memory while it runs, in the
        // log the rest of this app is diagnosed from. It is also the answer to "is it
        // really animating": a frame count above one, written when the loop starts.
        DebugHelper.WriteLine("Motion preview: " + file.Code + ", " + decoded.FrameCount +
            " frames, " + (decoded.Bytes / 1024) + " KiB decoded.");

        reel = decoded;
        playing = file;
        frameIndex = 0;

        file.SetMotionFrame(decoded.Frame(0));

        frameTimer.Interval = TimeSpan.FromMilliseconds(decoded.Delay(0));
        frameTimer.Start();
    }

    private void OnFrameElapsed(object? sender, EventArgs e)
    {
        if (reel == null || playing == null)
        {
            frameTimer.Stop();
            return;
        }

        frameIndex++;
        playing.SetMotionFrame(reel.Frame(frameIndex));

        TimeSpan next = TimeSpan.FromMilliseconds(reel.Delay(frameIndex));

        // Only when it actually changes: assigning Interval restarts the timer, so writing
        // the same value every tick would reset the countdown a fraction late each time
        // and the loop would drift slower than the file says.
        if (frameTimer.Interval != next)
        {
            frameTimer.Interval = next;
        }
    }

    /// <summary>
    /// Take the frames off the tile and give their memory back.
    /// </summary>
    /// <remarks>
    /// The card is told first and the frames are freed second. That order is the whole
    /// reason this is one method: clearing the property runs the binding synchronously on
    /// this thread, so by the time the surfaces are disposed no control is pointing at
    /// them. It is the same order <see cref="CloudFileViewModel.Thumbnail"/> uses for the
    /// picture underneath.
    /// </remarks>
    private void Stop()
    {
        frameTimer.Stop();

        playing?.ClearMotionFrame();
        playing = null;

        reel?.Dispose();
        reel = null;
        frameIndex = 0;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        dwellTimer.Stop();
        dwellTimer.Tick -= OnDwellElapsed;
        frameTimer.Tick -= OnFrameElapsed;

        hovered = null;
        Stop();
    }

    /// <summary>Everything one attempt needs, captured when the pointer arrived.</summary>
    private sealed record Context(CloudFileViewModel File, string Host, string Token, CancellationToken Cancellation);
}
