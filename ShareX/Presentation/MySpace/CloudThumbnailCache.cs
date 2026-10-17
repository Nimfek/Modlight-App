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
/// The bytes of preview images already fetched, kept so that leaving a folder and
/// coming back does not ask the server for them again.
/// </summary>
/// <remarks>
/// This holds ENCODED bytes rather than decoded <c>Bitmap</c>s, and that is the whole
/// design. A decoded bitmap is a disposable native resource owned by whichever card is
/// currently bound to it; caching those would mean either disposing an image some
/// off-screen <c>Image</c> control still points at - which fails at the moment it
/// scrolls back into view - or never disposing them at all. Bytes are a plain array:
/// evicting one costs nothing and can break nothing, and decoding is cheap next to the
/// round trip it saves.
///
/// Oldest-first eviction rather than least-recently-used, because the access pattern
/// here is a scroll through a listing: entries are read once each on the way past, so
/// recency says almost nothing about what will be wanted next, and insertion order is
/// the same answer for a fraction of the bookkeeping.
/// </remarks>
internal static class CloudThumbnailCache
{
    /// <summary>
    /// How many previews to keep.
    /// </summary>
    /// <remarks>
    /// A server preview runs tens of kilobytes, so four hundred of them is a handful of
    /// megabytes - small enough to be uninteresting, large enough to cover several pages
    /// of scrolling plus the folder the user keeps switching back to. It has to be a cap
    /// and not "everything": the owner's account holds around 8700 files.
    /// </remarks>
    private const int Capacity = 400;

    private static readonly object sync = new();
    private static readonly Dictionary<string, byte[]> entries = new();
    private static readonly Queue<string> order = new();

    public static byte[]? Get(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        lock (sync)
        {
            return entries.TryGetValue(code, out byte[]? bytes) ? bytes : null;
        }
    }

    public static void Put(string code, byte[] bytes)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        lock (sync)
        {
            if (!entries.TryAdd(code, bytes))
            {
                return;
            }

            order.Enqueue(code);

            while (order.Count > Capacity)
            {
                entries.Remove(order.Dequeue());
            }
        }
    }
}
