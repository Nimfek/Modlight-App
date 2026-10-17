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

/// <summary>
/// A ceiling on how many bytes of DECODED pictures the grid may hold at once, and the
/// order in which it gives them up when it is over.
/// </summary>
/// <remarks>
/// Not the same thing as <see cref="CloudThumbnailCache"/>, which bounds the ENCODED
/// bytes and exists to save round trips. This bounds the decoded pixel buffers, which
/// are between ten and thirty times larger and were never bounded at all: every card in
/// the listing kept its bitmap for as long as the listing existed, and the listing is the
/// whole account. At 480x480 - the size the server renders a tile at - one picture is
/// 900 kB of BGRA, so the owner's 8700 files came to something over six gigabytes if he
/// scrolled to the end.
///
/// Measured in BYTES rather than in entries because the entries are not the same size: a
/// picture the server had to shrink less is worth several that it shrank a lot, and a cap
/// counted in cards would be a cap on memory only by accident.
///
/// Least-recently-used, unlike the encoded cache next door, and for the opposite reason.
/// That one is read once per card on the way past, so recency predicts nothing. This one
/// is asked "which pictures is the user looking at" on every scroll event, so recency is
/// exactly the answer: the tail of this list is the part of the grid furthest from the
/// viewport in the direction last travelled.
///
/// Pinned entries are never evicted. Those are the cards actually on screen, and letting
/// the cap take one of them would blank a tile the user is looking at and immediately
/// fetch it back - a cap that costs memory instead of saving it. The pinned set is at
/// most a screenful, so it bounds itself.
/// </remarks>
/// <typeparam name="T">The card type. Kept generic so the policy can be tested on its own.</typeparam>
internal sealed class ThumbnailBudget<T> where T : class
{
    private sealed class Entry
    {
        public LinkedListNode<T> Node = null!;
        public long Cost;
        public bool Pinned;
    }

    private readonly long capacityBytes;
    private readonly Action<T> release;

    /// <summary>Most recently used first, so the eviction victim is always the last node.</summary>
    private readonly LinkedList<T> order = new();

    private readonly Dictionary<T, Entry> entries = new();
    private readonly List<T> pinnedItems = new();

    public ThumbnailBudget(long capacityBytes, Action<T> release)
    {
        this.capacityBytes = capacityBytes;
        this.release = release;
    }

    /// <summary>How many pictures are being held.</summary>
    public int Count => entries.Count;

    /// <summary>How many bytes of decoded pixels are being held.</summary>
    public long Bytes { get; private set; }

    /// <summary>
    /// Take responsibility for one more picture, and give up the oldest until the total
    /// is back under the ceiling.
    /// </summary>
    public void Add(T item, long cost)
    {
        if (cost < 0)
        {
            cost = 0;
        }

        if (entries.TryGetValue(item, out Entry? existing))
        {
            Bytes += cost - existing.Cost;
            existing.Cost = cost;
            order.Remove(existing.Node);
            order.AddFirst(existing.Node);
        }
        else
        {
            entries[item] = new Entry { Node = order.AddFirst(item), Cost = cost };
            Bytes += cost;
        }

        Trim();
    }

    /// <summary>Say that this picture is still wanted, without changing what it costs.</summary>
    public void Touch(T item)
    {
        if (entries.TryGetValue(item, out Entry? entry))
        {
            order.Remove(entry.Node);
            order.AddFirst(entry.Node);
        }
    }

    /// <summary>
    /// The cards currently on screen, which eviction must leave alone.
    /// </summary>
    /// <remarks>
    /// Replaces the previous set rather than adding to it: a pin that outlived the scroll
    /// that set it would accumulate into "nothing may ever be evicted", which is the
    /// unbounded behaviour this class exists to end.
    /// </remarks>
    public void Pin(IReadOnlyList<T> items)
    {
        foreach (T previous in pinnedItems)
        {
            if (entries.TryGetValue(previous, out Entry? entry))
            {
                entry.Pinned = false;
            }
        }

        pinnedItems.Clear();

        foreach (T item in items)
        {
            if (entries.TryGetValue(item, out Entry? entry))
            {
                entry.Pinned = true;
                pinnedItems.Add(item);
            }
        }
    }

    /// <summary>Stop tracking one picture. The caller owns it from here.</summary>
    public void Forget(T item)
    {
        if (!entries.TryGetValue(item, out Entry? entry))
        {
            return;
        }

        order.Remove(entry.Node);
        entries.Remove(item);
        pinnedItems.Remove(item);
        Bytes -= entry.Cost;
    }

    /// <summary>Stop tracking everything. Used when the whole listing is thrown away.</summary>
    public void Clear()
    {
        order.Clear();
        entries.Clear();
        pinnedItems.Clear();
        Bytes = 0;
    }

    /// <summary>
    /// Give up the oldest pictures until the total is back under the ceiling.
    /// </summary>
    /// <remarks>
    /// The newest entry is never the victim, and that is not a nicety. Eviction walks from
    /// the tail, so if everything ahead of it is pinned the walk arrives at the entry that
    /// was added a moment ago - and a card would be handed its picture, lose it before it
    /// was drawn, and ask for it again on the next scroll, for ever. It only takes the
    /// visible cards alone to outweigh the ceiling for that to happen, which a large
    /// enough monitor arranges. Stopping at the head is also simply what least-recently-
    /// used means: the thing used last is the one thing certain to be wanted.
    /// </remarks>
    private void Trim()
    {
        LinkedListNode<T>? node = order.Last;

        while (Bytes > capacityBytes && node != null && node != order.First)
        {
            LinkedListNode<T>? previous = node.Previous;
            T item = node.Value;

            if (entries.TryGetValue(item, out Entry? entry) && !entry.Pinned)
            {
                order.Remove(node);
                entries.Remove(item);
                Bytes -= entry.Cost;
                release(item);
            }

            node = previous;
        }
    }
}
