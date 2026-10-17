#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System.Collections.Generic;

namespace ShareX;

/// <summary>
/// What the app remembers about moving previews between hovers: the bytes of the ones it
/// has, and the names of the files that will never have one.
/// </summary>
/// <remarks>
/// Separate from <see cref="CloudThumbnailCache"/> rather than sharing it under a
/// decorated key, because the two hold different things at different prices. A still is
/// tens of kilobytes; twenty-one moving previews measured on production run from 62.6 kB
/// to 815.9 kB, mean 220 kB. Mixing them would also let a scroll through a folder of
/// screenshots evict the one preview the user keeps going back to.
///
/// ENCODED bytes, like the cache next door and for the same reason: an
/// <see cref="AnimatedThumbnail"/> is a set of native surfaces owned by whichever tile is
/// playing, and caching those would mean either disposing frames a control still points
/// at or never disposing them.
///
/// COUNTED IN BYTES, AND THAT IS A CORRECTION. This used to hold sixty-four ENTRIES, on
/// the arithmetic that a preview was at most 138 kB and sixty-four of them 8.8 MB. Then
/// the server stopped sampling eight frames across a file and started sending one
/// continuous clip of thirty, and the largest preview on the owner's own account is now
/// 815.9 kB - so the same cap would be fifty-two megabytes, sitting behind a 64 MiB
/// ceiling on decoded stills, and nothing in the code would have said so. A cap in
/// entries was only ever a cap on memory by accident; sixteen mebibytes is twenty clips
/// at that worst case and about seventy-five at the mean, which is still more videos
/// than anyone revisits in a sitting.
///
/// LEAST-RECENTLY-USED, unlike the still cache, which is oldest-first. That one is read
/// once per card on the way past a scroll, so recency predicts nothing. These are fetched
/// one at a time BY HOVERING, which is a person coming back to a file on purpose - so
/// recency is exactly the right guess, and first-in-first-out would throw away the clip
/// somebody keeps pointing at to keep one they glanced past.
///
/// Both halves are keyed by the file's code, which is unique across the deployment, so
/// nothing here has to be thrown away when the account changes.
/// </remarks>
internal static class CloudMotionCache
{
    /// <summary>How many bytes of encoded previews to keep.</summary>
    private const long MaxBytes = 16L * 1024 * 1024;

    private sealed class Entry
    {
        public byte[] Bytes = System.Array.Empty<byte>();
        public LinkedListNode<string> Node = null!;
    }

    private static readonly object sync = new();
    private static readonly Dictionary<string, Entry> entries = new();

    /// <summary>Most recently used first, so the eviction victim is always the last node.</summary>
    private static readonly LinkedList<string> order = new();

    private static long held;

    /// <summary>
    /// Files the server has refused a moving preview for.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT capped, and that is the cheaper choice rather than the lazy one.
    /// The set is bounded by the account's library - about 6,900 files on the owner's, so
    /// roughly half a megabyte of short strings, less than one decoded tile. A cap with
    /// eviction would trade that half-megabyte for the app forgetting a refusal and
    /// re-asking a question the server has already answered permanently, which is the one
    /// behaviour this exists to prevent.
    ///
    /// A refusal is a fact about the FILE and it lives as long as the process, but no
    /// longer: a row can gain the ability to produce one (a frame count backfilled, a
    /// file converted in place), so it is not written to disk.
    /// </remarks>
    private static readonly HashSet<string> refused = new();

    public static byte[]? Get(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        lock (sync)
        {
            if (!entries.TryGetValue(code, out Entry? entry))
            {
                return null;
            }

            // The whole of "least recently USED": a read counts, not only a write. Without
            // this line the order is insertion order under a different name.
            order.Remove(entry.Node);
            order.AddFirst(entry.Node);

            return entry.Bytes;
        }
    }

    public static void Put(string code, byte[] bytes)
    {
        if (string.IsNullOrEmpty(code) || bytes == null)
        {
            return;
        }

        lock (sync)
        {
            if (entries.ContainsKey(code))
            {
                return;
            }

            entries[code] = new Entry { Bytes = bytes, Node = order.AddFirst(code) };
            held += bytes.Length;

            // Never down to nothing: the entry that was just added is the one certain to
            // be wanted, and an artefact larger than the whole budget would otherwise be
            // fetched, filed and evicted before the hover that asked for it could use it.
            while (held > MaxBytes && order.Count > 1)
            {
                string oldest = order.Last!.Value;

                order.RemoveLast();

                if (entries.Remove(oldest, out Entry? evicted))
                {
                    held -= evicted.Bytes.Length;
                }
            }
        }
    }

    /// <summary>This file will never produce a moving preview. Write it down and stop asking.</summary>
    public static void Refuse(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        lock (sync)
        {
            refused.Add(code);
        }
    }

    public static bool IsRefused(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        lock (sync)
        {
            return refused.Contains(code);
        }
    }
}
