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
/// The order the cards around the viewport are worth fetching in.
/// </summary>
/// <remarks>
/// Its own file, and pure, for one reason: it is the fix. The bug the owner reported was
/// never that the grid asked for too much - it was the ORDER it asked in, so the order is
/// the thing that has to be pinned by a measurement rather than reasoned about, and a
/// measurement cannot reach into a view model that wants a dispatcher and a window.
/// ShareX.Tests links this file and drives the real thing.
///
/// Nothing here knows what a card is. It walks index positions and hands them out, which
/// is all the caller needs: <see cref="MySpaceViewModel"/> turns each into "does this card
/// still want a picture", and the measurement turns it into "has this one arrived yet".
/// </remarks>
internal static class VisibleBand
{
    /// <summary>
    /// Every index within <paramref name="margin"/> of the visible run, nearest first.
    /// </summary>
    /// <remarks>
    /// Visible first, in reading order, then outwards a step at a time - below before
    /// above at each distance, because a grid is read downwards and of two rows equally
    /// far from the viewport the one underneath is the one about to be looked at.
    ///
    /// Handed out through a callback rather than returned as a list so that a scroll,
    /// which raises dozens of these a second, does not allocate one per event.
    /// </remarks>
    public static void Order(int count, int visibleFirst, int visibleLast, int margin, Action<int> take)
    {
        if (count <= 0)
        {
            return;
        }

        int first = Math.Clamp(visibleFirst, 0, count - 1);
        int last = Math.Clamp(visibleLast, first, count - 1);

        for (int index = first; index <= last; index++)
        {
            take(index);
        }

        for (int step = 1; step <= margin; step++)
        {
            int below = last + step;
            int above = first - step;

            if (below < count)
            {
                take(below);
            }

            if (above >= 0)
            {
                take(above);
            }
        }
    }
}
