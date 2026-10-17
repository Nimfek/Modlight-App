#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The order preview images are fetched in, and the licence to stop wanting one.
/// </summary>
/// <remarks>
/// WHAT WAS WRONG. The grid used to hold its four fetch slots in a
/// <c>SemaphoreSlim</c>, and every card that came within forty places of the viewport
/// started a task that waited on it. SemaphoreSlim hands its slots out FIRST COME, FIRST
/// SERVED - a documented property of it, not an accident of timing - so the queue was the
/// order the cards were passed, which on a scroll is the order they were scrolled PAST.
/// Flick through six hundred tiles and six hundred waiters are lined up ahead of the two
/// dozen tiles now on screen; at the 238 ms a preview was measured to take from this
/// machine to production, four at a time, the tile under the pointer waits about half a
/// minute for a picture that costs a quarter of a second to fetch. That is the bug the
/// owner reported as "after scrolling quickly, thumbnails take a very long time to
/// appear", and no number of extra slots fixes it: more slots only drain a queue that is
/// in the wrong order slightly faster.
///
/// A semaphore cannot be fixed in place, because the thing that has to change is the order
/// of the WAITERS, and a waiter is a suspended continuation - there is no handle on it and
/// nothing to reorder. So the waiting is done here, in a list this class owns, where the
/// order can be rewritten every time the viewport moves and where an entry can simply be
/// thrown away.
///
/// WHAT IT DOES. <see cref="Want"/> is handed the whole set that is worth fetching, best
/// first, and it REPLACES what was queued: anything the caller no longer names is dropped
/// on the spot. A scroll therefore costs nothing to walk away from, and the tiles that
/// arrived under the pointer go to the front.
///
/// WHAT IT DOES NOT DO: cancel a fetch already in flight. At most <see cref="slots"/> of
/// those exist, so the worst a newly visible tile waits for them is one round trip, and
/// the bytes they are about to return go into <see cref="CloudThumbnailCache"/>, which the
/// band around the viewport reads from - so they are a prefetch rather than waste. Killing
/// them would throw away work that is nearly done in exchange for a quarter of a second,
/// which is the same reasoning <see cref="CloudMotionPlayer"/> gives for never cancelling
/// a preview render.
///
/// SINGLE-THREADED, like everything else in this grid: every method here runs on the UI
/// thread. The fetch delegate may go wherever it likes, which is why completion comes back
/// through <see cref="post"/> rather than on whatever thread the task happened to end on.
/// </remarks>
/// <typeparam name="T">
/// The card type. Generic - and only <c>notnull</c>, not <c>class</c> - so that the
/// measurement in ShareX.Tests can drive the real thing with plain integers rather than
/// building a stand-in for a view model to hold a policy that never looks inside one.
/// </typeparam>
internal sealed class ThumbnailQueue<T> where T : notnull
{
    private readonly int slots;
    private readonly Func<T, Task> fetch;
    private readonly Action<Action> post;

    /// <summary>Wanted but not started, best first. Rewritten by every <see cref="Want"/>.</summary>
    private readonly List<T> queue = new();

    /// <summary>Membership test for <see cref="queue"/>, so <see cref="Want"/> stays linear.</summary>
    private readonly HashSet<T> queued = new();

    /// <summary>Started and not finished. Never more than <see cref="slots"/>.</summary>
    private readonly HashSet<T> running = new();

    /// <summary>How far into <see cref="queue"/> the pump has got.</summary>
    private int head;

    /// <param name="slots">How many fetches may be in flight at once.</param>
    /// <param name="fetch">Get one card its picture. Anything it throws is swallowed here.</param>
    /// <param name="post">
    /// Run this back on the queue's own thread. In the app that is the dispatcher; in a
    /// test it is the fake one, which is the whole reason it is a parameter.
    /// </param>
    public ThumbnailQueue(int slots, Func<T, Task> fetch, Action<Action> post)
    {
        this.slots = Math.Max(1, slots);
        this.fetch = fetch;
        this.post = post;
    }

    /// <summary>How many fetches have been started since this queue was built.</summary>
    public int Issued { get; private set; }

    /// <summary>How many were queued and then dropped without ever being asked for.</summary>
    public int Abandoned { get; private set; }

    /// <summary>How many are in flight right now.</summary>
    public int Running => running.Count;

    /// <summary>How many are waiting right now.</summary>
    public int Waiting => queued.Count;

    /// <summary>
    /// These, in this order, are the pictures worth having. Everything else is not.
    /// </summary>
    /// <remarks>
    /// The caller passes the WHOLE want-set every time rather than adding to it, which is
    /// what makes abandonment free: there is no "cancel these" call to get wrong, and no
    /// way for an entry to outlive the reason it was queued. The list is short by
    /// construction - a page of cards, or the band around the viewport - so rebuilding it
    /// on every scroll event is a couple of hundred reference copies.
    ///
    /// Items already in flight are skipped rather than requeued: they are being fetched,
    /// which is what being at the front of the queue is for.
    /// </remarks>
    public void Want(IReadOnlyList<T> ordered)
    {
        int carried = 0;

        foreach (T item in ordered)
        {
            if (queued.Contains(item))
            {
                carried++;
            }
        }

        Abandoned += queued.Count - carried;

        queue.Clear();
        queued.Clear();
        head = 0;

        foreach (T item in ordered)
        {
            if (!running.Contains(item) && queued.Add(item))
            {
                queue.Add(item);
            }
        }

        Pump();
    }

    /// <summary>Nothing is wanted any more. Used when the listing is thrown away.</summary>
    /// <remarks>
    /// What is already in flight is not stopped here either, and does not need to be: the
    /// fetches carry the listing's own cancellation token, which the caller cancels at the
    /// same moment. This only makes sure the pump does not start anything else on the way
    /// down.
    /// </remarks>
    public void Clear()
    {
        Abandoned += queued.Count;
        queue.Clear();
        queued.Clear();
        head = 0;
    }

    private void Pump()
    {
        while (running.Count < slots && head < queue.Count)
        {
            T item = queue[head++];

            // Only ever false for an item Want dropped between being listed and being
            // reached, which cannot happen while both run on one thread - but the pump is
            // the one place that would silently double-fetch if it ever did.
            if (!queued.Remove(item))
            {
                continue;
            }

            running.Add(item);
            Issued++;
            _ = RunAsync(item);
        }
    }

    private async Task RunAsync(T item)
    {
        try
        {
            await fetch(item).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // One picture that will not load is not a reason to stop the grid. The caller
            // does the reporting; what matters here is that the slot always comes back.
        }
        finally
        {
            post(() =>
            {
                running.Remove(item);
                Pump();
            });
        }
    }
}
