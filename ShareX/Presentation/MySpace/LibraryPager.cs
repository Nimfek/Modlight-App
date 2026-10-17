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
/// The arithmetic behind "1-120 of 6,926", the page strip and the page-size selector.
/// </summary>
/// <remarks>
/// A transcription of components/dashboard/Pagination.tsx in the web panel, deliberately
/// and to the number. The two surfaces list the same rows from the same endpoint, and the
/// owner reads them one after the other: if this window's page 6 and the browser's page 6
/// showed different files, the mistake would be invisible until somebody went looking for
/// a file on the page it "should" be on. The sizes offered are the panel's own
/// <c>PER_PAGE_OPTIONS</c>, the ceiling is the server's <c>MAX_PER_PAGE</c>, and the rule
/// for where a page-size change lands is the panel's, spelled out in <see cref="Anchor"/>.
///
/// Pure - no Avalonia, no settings, no HTTP - so it can be linked into ShareX.Tests and
/// pinned there. The view model next door is what turns it into something to click.
///
/// This replaced endless scrolling, which is not merely a different look. That grid grew
/// without bound: reaching the end of the owner's 6,926 files meant 6,926 cards realised
/// at once in a panel Avalonia 12.1.1 cannot virtualise, every one of them holding its
/// place in the layout for as long as the window stayed on the tab. A page is a page.
/// </remarks>
internal static class LibraryPager
{
    /// <summary>
    /// The page sizes offered, smallest first.
    /// </summary>
    /// <remarks>
    /// The panel's <c>PER_PAGE_OPTIONS</c> exactly. 120 is also the server's
    /// <c>MAX_PER_PAGE</c> - a client asking for more is answered with 120 anyway and told
    /// so in the response - so there is no fourth entry to add that would mean anything.
    /// </remarks>
    public static readonly int[] PageSizes = { 30, 60, 120 };

    /// <summary>
    /// How many files a page holds when nobody has said otherwise.
    /// </summary>
    /// <remarks>
    /// 120, which is the size the owner named, and NOT the panel's default of 30. The
    /// difference is the surface: the panel's grid sits under a page header, a filter bar
    /// and a folder strip in a browser tab, and this one is a window whose whole job is
    /// the grid. Thirty tiles do not fill it, and a page that does not fill the window
    /// spends a page turn on every screenful.
    ///
    /// Being the largest offered size, it is also the one that makes the thumbnail queue
    /// matter, which is why the two arrived together: 120 cards is 120 pictures the queue
    /// has to order sensibly, where 30 would have hidden the problem rather than fixed it.
    /// </remarks>
    public const int DefaultPageSize = 120;

    /// <summary>How many numbered buttons are shown before the strip starts eliding.</summary>
    /// <remarks>The panel's number, so the two strips look alike at the same page counts.</remarks>
    public const int StripLimit = 7;

    /// <summary>A size this client will actually ask for, whatever it was handed.</summary>
    /// <remarks>
    /// Anything unrecognised becomes the default rather than the nearest, because the only
    /// ways to get here with an unrecognised value are a settings file written by an older
    /// build and one edited by hand - and in both cases "the size this build offers" is a
    /// better answer than a number no button on the bar can ever return to.
    /// </remarks>
    public static int ClampPageSize(int size)
    {
        foreach (int allowed in PageSizes)
        {
            if (allowed == size)
            {
                return size;
            }
        }

        return DefaultPageSize;
    }

    /// <summary>How many pages <paramref name="total"/> files come to. Never fewer than one.</summary>
    /// <remarks>
    /// One page rather than zero for an empty listing: the alternative makes "page 1 of 0"
    /// sayable, and every caller then has to special-case it.
    /// </remarks>
    public static int PageCount(int total, int perPage)
    {
        if (perPage <= 0)
        {
            return 1;
        }

        return Math.Max(1, (total + perPage - 1) / perPage);
    }

    /// <summary>The 1-based position of the first file on this page.</summary>
    public static int First(int page, int perPage) => ((Math.Max(1, page) - 1) * Math.Max(1, perPage)) + 1;

    /// <summary>The 1-based position of the last file on this page, never past the total.</summary>
    public static int Last(int page, int perPage, int total) =>
        Math.Min(Math.Max(0, total), Math.Max(1, page) * Math.Max(1, perPage));

    /// <summary>
    /// Which page to land on when the page size changes.
    /// </summary>
    /// <remarks>
    /// The panel's rule, and worth keeping for the reason the panel keeps it: anchor on
    /// the first file currently on screen, so changing 30 to 120 while looking at file
    /// 3,001 leaves that file on screen instead of teleporting back to the top of the
    /// library. Going to page 1 would be simpler and would throw away where somebody was.
    /// </remarks>
    public static int Anchor(int firstOnScreen, int newSize) =>
        Math.Max(1, (Math.Max(1, firstOnScreen) + Math.Max(1, newSize) - 1) / Math.Max(1, newSize));

    /// <summary>A page number that exists.</summary>
    public static int ClampPage(int page, int totalPages) => Math.Clamp(page, 1, Math.Max(1, totalPages));

    /// <summary>
    /// The compact page strip: 1 ... 4 5 [6] 7 8 ... 58.
    /// </summary>
    /// <remarks>
    /// Zero stands for the gap, which is the one thing this returns that is not a page.
    /// The panel builds the same list the same way; at 58 pages - the owner's library at
    /// 120 a page - walking a full strip would be 58 buttons across a window that is 1040
    /// pixels wide.
    /// </remarks>
    public static IReadOnlyList<int> Strip(int page, int totalPages)
    {
        totalPages = Math.Max(1, totalPages);
        page = ClampPage(page, totalPages);

        List<int> steps = new();

        if (totalPages <= StripLimit)
        {
            for (int n = 1; n <= totalPages; n++)
            {
                steps.Add(n);
            }

            return steps;
        }

        steps.Add(1);

        int start = Math.Max(2, page - 1);
        int end = Math.Min(totalPages - 1, page + 1);

        if (start > 2)
        {
            steps.Add(0);
        }

        for (int n = start; n <= end; n++)
        {
            steps.Add(n);
        }

        if (end < totalPages - 1)
        {
            steps.Add(0);
        }

        steps.Add(totalPages);
        return steps;
    }
}
