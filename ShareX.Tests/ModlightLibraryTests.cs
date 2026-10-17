#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.Tests;

/// <summary>
/// How My space pages the library, and the order it fetches preview images in.
/// </summary>
/// <remarks>
/// <para>
/// Two things live here because they arrived together and only make sense together. The
/// grid is paged now, the way the web panel is paged, and 120 files a page is four times
/// what the endless grid used to hold at once - which turns the fetch order from something
/// that was merely wrong into something a person can feel.
/// </para>
/// <para>
/// <see cref="LibraryPager"/>, <see cref="ThumbnailQueue{T}"/> and
/// <see cref="VisibleBand"/> are compiled into this assembly rather than referenced,
/// like <c>ModlightSettingsPlan</c> next door and for the same reason: they were written
/// free of Avalonia, of HTTP and of the settings file so that the REAL shipping code -
/// not a copy of it - can be exercised here. If somebody reaches for a dispatcher or a
/// window inside one of them, this project stops compiling and says so.
/// </para>
/// <para>
/// The scroll measurement at the bottom prints numbers rather than asserting them, except
/// for the one claim that must never stop being true: after a fast scroll, the tiles on
/// screen are served before the tiles that were scrolled past.
/// </para>
/// </remarks>
internal static class ModlightLibraryTests
{
    private static int failures;

    /// <summary>
    /// Milliseconds one preview takes, measured against production.
    /// </summary>
    /// <remarks>
    /// Twenty-four preview images fetched one at a time from this machine to
    /// host.example.com: 188 ms fastest, 238 ms median, 283 ms at the ninetieth percentile,
    /// 326 ms slowest. The median is used here. It is not the interesting number - the
    /// interesting number is how many of them a tile has to wait behind.
    /// </remarks>
    private const long LatencyMs = 238;

    /// <summary>Fetch slots, as <see cref="MySpaceViewModel"/> configures them.</summary>
    private const int Slots = 4;

    /// <summary>Cards either side of the visible run that keep a picture, as the grid uses.</summary>
    private const int Margin = 40;

    /// <summary>Cards on screen at once: about four rows of six in a 1040x720 window.</summary>
    private const int Visible = 24;

    internal static int Run()
    {
        failures = 0;

        Console.WriteLine("Library paging and the thumbnail queue");

        PageArithmeticMatchesThePanel();
        PageSizesAreThePanelsOwn();
        TheStripElidesLikeThePanels();
        ChangingTheSizeKeepsYourPlace();

        SemaphoresAreFirstComeFirstServed();
        TheQueueServesTheFrontFirst();
        TheQueueAbandonsWhatIsNoLongerWanted();
        TheQueueNeverExceedsItsSlots();
        TheBandIsOrderedOutwards();

        MeasureAFastScroll();

        TheMotionBudgetHoldsTheClipsProductionSends();
        TheMotionBudgetNeverEnlargesASmallPreview();
        TheMotionBudgetSurvivesNonsense();

        Console.WriteLine(failures == 0
            ? "  all library paging checks passed"
            : "  " + failures + " library paging check(s) FAILED");

        return failures;
    }

    // ---------------------------------------------------------------- the pager

    /// <summary>
    /// "1-120 of 6,926" has to mean in this window what it means in the browser.
    /// </summary>
    /// <remarks>
    /// The owner reads both. Two surfaces listing the same rows from the same endpoint
    /// that disagree about which page a file is on is a mistake nobody notices until they
    /// go looking for a file on the page it "should" be on.
    /// </remarks>
    private static void PageArithmeticMatchesThePanel()
    {
        // The real account: 6,926 uploads at the time of writing.
        Check(LibraryPager.PageCount(6926, 120) == 58, "6,926 files at 120 a page is 58 pages");
        Check(LibraryPager.PageCount(6926, 30) == 231, "6,926 files at 30 a page is 231 pages");
        Check(LibraryPager.First(6, 120) == 601, "page 6 of 120 starts at file 601");
        Check(LibraryPager.Last(6, 120, 6926) == 720, "page 6 of 120 ends at file 720");
        Check(LibraryPager.Last(58, 120, 6926) == 6926, "the last page ends at the last file, not past it");

        // An empty listing is one page, not zero: the alternative makes "page 1 of 0"
        // sayable and every caller has to special-case it.
        Check(LibraryPager.PageCount(0, 120) == 1, "an empty listing is one page");
        Check(LibraryPager.ClampPage(99, 1) == 1, "a page past the end clamps to one that exists");
        Check(LibraryPager.ClampPage(0, 58) == 1, "page zero clamps to page one");
    }

    private static void PageSizesAreThePanelsOwn()
    {
        Check(LibraryPager.PageSizes.Length == 3 &&
              LibraryPager.PageSizes[0] == 30 &&
              LibraryPager.PageSizes[1] == 60 &&
              LibraryPager.PageSizes[2] == 120,
            "the sizes offered are the panel's 30 / 60 / 120");

        Check(LibraryPager.DefaultPageSize == 120, "the default is the size the owner named");

        // 120 is also the server's MAX_PER_PAGE, so nothing above it can be asked for.
        Check(LibraryPager.ClampPageSize(500) == 120, "a size above the server's ceiling becomes the default");
        Check(LibraryPager.ClampPageSize(45) == 120, "a size no button offers becomes the default");
        Check(LibraryPager.ClampPageSize(0) == 120, "a missing size becomes the default");
        Check(LibraryPager.ClampPageSize(30) == 30, "a size that is offered is kept");
    }

    private static void TheStripElidesLikeThePanels()
    {
        Check(Strip(LibraryPager.Strip(1, 5)) == "1 2 3 4 5", "five pages are all shown");
        Check(Strip(LibraryPager.Strip(1, 7)) == "1 2 3 4 5 6 7", "seven pages are all shown");
        Check(Strip(LibraryPager.Strip(1, 58)) == "1 2 . 58", "at the start the strip elides on the right");
        Check(Strip(LibraryPager.Strip(6, 58)) == "1 . 5 6 7 . 58", "in the middle it elides on both sides");
        Check(Strip(LibraryPager.Strip(58, 58)) == "1 . 57 58", "at the end it elides on the left");
    }

    /// <summary>
    /// Changing the page size must not throw away where somebody was.
    /// </summary>
    private static void ChangingTheSizeKeepsYourPlace()
    {
        // Looking at file 3,001 - page 101 of 30 - and switching to 120.
        int first = LibraryPager.First(101, 30);

        Check(first == 3001, "page 101 of 30 starts at file 3,001");
        Check(LibraryPager.Anchor(first, 120) == 26, "the same file is on page 26 of 120");
        Check(LibraryPager.First(26, 120) == 3001, "and page 26 of 120 starts on it exactly");

        Check(LibraryPager.Anchor(1, 120) == 1, "the top of the library stays the top");
    }

    private static string Strip(IReadOnlyList<int> steps)
    {
        List<string> parts = new List<string>();

        foreach (int step in steps)
        {
            parts.Add(step > 0 ? step.ToString() : ".");
        }

        return string.Join(" ", parts);
    }

    // ---------------------------------------------------------------- the queue

    /// <summary>
    /// The premise of the whole rewrite, checked against the real type rather than quoted.
    /// </summary>
    /// <remarks>
    /// SemaphoreSlim hands its slots out in the order they were asked for. That is what
    /// made a fast scroll slow: every tile the pointer flew past got in the queue ahead of
    /// the tiles it stopped on, and no handle exists on a suspended continuation to
    /// reorder them afterwards. If this check ever fails, the fix downstairs was solving a
    /// problem that had gone away.
    /// </remarks>
    private static void SemaphoresAreFirstComeFirstServed()
    {
        using SemaphoreSlim gate = new SemaphoreSlim(0);

        List<Task> waiters = new List<Task>();

        for (int i = 0; i < 5; i++)
        {
            waiters.Add(gate.WaitAsync());
        }

        List<int> served = new List<int>();
        bool[] taken = new bool[waiters.Count];

        // ONE slot at a time, and the answer read from the WAITER itself rather than from
        // a continuation hung off it. That is not a matter of taste: SemaphoreSlim builds
        // its waiter with RunContinuationsAsynchronously, so ExecuteSynchronously on a
        // ContinueWith is ignored and the five continuations are handed to the thread
        // pool, which runs them in whatever order it pleases. Written that way this check
        // passes on an idle machine and fails on a busy one - it measured the scheduler
        // rather than the semaphore - and the premise of the whole rewrite deserves
        // better than a check that flickers. The COMPLETION order, which is the thing
        // being claimed, is set inside Release and is not affected by any of that.
        for (int release = 0; release < waiters.Count; release++)
        {
            gate.Release();

            int woke = -1;

            // Normally found first time round, because Release sets the result before it
            // returns. The spin is here so that a runtime which disagrees fails the check
            // instead of hanging the suite.
            for (int spin = 0; spin < 2000 && woke < 0; spin++)
            {
                for (int i = 0; i < waiters.Count; i++)
                {
                    if (!taken[i] && waiters[i].IsCompleted)
                    {
                        woke = i;
                        break;
                    }
                }

                if (woke < 0)
                {
                    Thread.Sleep(1);
                }
            }

            if (woke < 0)
            {
                break;
            }

            taken[woke] = true;
            served.Add(woke);
        }

        Check(string.Join(",", served) == "0,1,2,3,4",
            "SemaphoreSlim serves waiters in the order they arrived");
    }

    /// <summary>
    /// A queue whose fetches finish only when the test says so.
    /// </summary>
    /// <remarks>
    /// The fetch has to be able to be IN FLIGHT for any of this to mean anything: a
    /// delegate returning a completed task runs the whole pump inline and every question
    /// about ordering answers itself. Completion is inline rather than posted, because the
    /// test is one thread and that is what "back on the queue's own thread" means here.
    /// </remarks>
    private sealed class Bench<T> where T : notnull
    {
        private readonly List<TaskCompletionSource<bool>> inFlight = new List<TaskCompletionSource<bool>>();

        public readonly List<T> Started = new List<T>();

        public int Live { get; private set; }

        public int Peak { get; private set; }

        public ThumbnailQueue<T> Queue { get; }

        public Bench(int slots)
        {
            Queue = new ThumbnailQueue<T>(slots, Fetch, action => action());
        }

        public string Order => string.Join(",", Started);

        private Task Fetch(T item)
        {
            Started.Add(item);
            Live++;
            Peak = Math.Max(Peak, Live);

            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>();

            inFlight.Add(done);
            return done.Task;
        }

        /// <summary>Let everything finish, including whatever finishing starts.</summary>
        public void Drain()
        {
            for (int i = 0; i < inFlight.Count; i++)
            {
                Live--;
                inFlight[i].SetResult(true);
            }

            inFlight.Clear();
        }
    }

    private static void TheQueueServesTheFrontFirst()
    {
        Bench<string> bench = new Bench<string>(2);

        bench.Queue.Want(new[] { "a", "b", "c", "d" });

        // Two slots, so only the first two start; the rest are waiting.
        Check(bench.Order == "a,b", "only as many start as there are slots");

        // Now the viewport moves: d and c are what is on screen, a and b are behind us.
        bench.Queue.Want(new[] { "d", "c" });
        bench.Drain();

        Check(bench.Order == "a,b,d,c",
            "the reordered front is served next, not the order it was asked in");
    }

    private static void TheQueueAbandonsWhatIsNoLongerWanted()
    {
        Bench<string> bench = new Bench<string>(1);

        bench.Queue.Want(new[] { "a", "b", "c", "d", "e" });

        Check(bench.Queue.Issued == 1 && bench.Queue.Waiting == 4, "one in flight, four waiting");

        // A scroll lands somewhere else entirely. Nothing that was waiting is wanted.
        bench.Queue.Want(new[] { "z" });

        Check(bench.Queue.Abandoned == 4, "the four that were waiting are dropped, not fetched");

        bench.Drain();

        Check(bench.Order == "a,z",
            "only the one already in flight and the one still wanted are ever fetched");
    }

    private static void TheQueueNeverExceedsItsSlots()
    {
        Bench<int> bench = new Bench<int>(Slots);

        for (int round = 0; round < 20; round++)
        {
            int[] want = new int[50];

            for (int i = 0; i < want.Length; i++)
            {
                want[i] = (round * 7) + i;
            }

            bench.Queue.Want(want);
            bench.Drain();
        }

        Check(bench.Peak <= Slots,
            "never more than " + Slots + " fetches in flight (peak " + bench.Peak + ")");
    }

    private static void TheBandIsOrderedOutwards()
    {
        List<int> order = new List<int>();

        VisibleBand.Order(100, 10, 12, 3, order.Add);

        Check(string.Join(",", order) == "10,11,12,13,9,14,8,15,7",
            "visible first, then outwards a step at a time, below before above");

        order.Clear();
        VisibleBand.Order(5, 0, 1, 10, order.Add);

        Check(string.Join(",", order) == "0,1,2,3,4", "the band stops at the ends of the listing");
    }

    // ---------------------------------------------------------- the measurement

    /// <summary>What one run of the simulation came to.</summary>
    private sealed class Result
    {
        public int Issued;
        public long FirstMs = -1;
        public long SettledMs = -1;
    }

    /// <summary>
    /// A fast scroll, run twice: once under the old policy, once under the new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Virtual time, not real: every fetch takes exactly <see cref="LatencyMs"/> simulated
    /// milliseconds, which is the median measured against production, and the clock jumps
    /// from event to event. Running it in real time would take a minute and a quarter -
    /// which is itself the finding.
    /// </para>
    /// <para>
    /// The new policy is the REAL <see cref="ThumbnailQueue{T}"/> and the REAL
    /// <see cref="VisibleBand"/>, driven by the simulated clock. The old policy is a
    /// transcription: the grid started a task per card in the band, in index order, and
    /// every one of them waited on a shared SemaphoreSlim, which
    /// <see cref="SemaphoresAreFirstComeFirstServed"/> shows serves in arrival order. Only
    /// the network is modelled; the ordering is exactly what shipped.
    /// </para>
    /// </remarks>
    private static void MeasureAFastScroll()
    {
        Console.WriteLine("  a fast scroll, then a stop - " + LatencyMs + " ms a preview, " +
            Slots + " at a time");

        Report("the endless grid, 1,200 cards loaded", 1200);
        Report("one page, 120 cards", 120);
    }

    private static void Report(string what, int cards)
    {
        Result old = Simulate(cards, legacy: true);
        Result now = Simulate(cards, legacy: false);

        Console.WriteLine("    " + what);
        Console.WriteLine("      before: " + old.Issued + " requests, first tile " +
            Seconds(old.FirstMs) + ", full screen " + Seconds(old.SettledMs));
        Console.WriteLine("      after:  " + now.Issued + " requests, first tile " +
            Seconds(now.FirstMs) + ", full screen " + Seconds(now.SettledMs));

        // The one claim that must not stop being true. Everything else here is a number to
        // read; this is the behaviour the owner reported as broken.
        Check(now.SettledMs < old.SettledMs,
            "a freshly scrolled-to screenful fills faster than it used to (" + what + ")");
    }

    private static string Seconds(long ms) =>
        ms < 0 ? "never" : (ms / 1000.0).ToString("0.00") + " s";

    /// <summary>
    /// Fifty scroll events at 60 Hz travelling the whole listing, then a stop.
    /// </summary>
    /// <remarks>
    /// A flick, which is how the report was phrased: "after scrolling quickly". The
    /// measurement is taken from the moment the scrolling stops, because that is the
    /// moment the person starts looking at the tiles.
    /// </remarks>
    private static Result Simulate(int cards, bool legacy)
    {
        Clock clock = new Clock();
        bool[] arrived = new bool[cards];
        long[] arrivedAt = new long[cards];
        Result result = new Result();

        // The old scheme's "a fetch for this card is already under way" flag.
        bool[] pending = new bool[cards];

        // The old scheme's waiters, in arrival order, which is what a semaphore is.
        Queue<int> waiting = new Queue<int>();
        int running = 0;

        ThumbnailQueue<int>? queue = null;

        void Finish(int card)
        {
            arrived[card] = true;
            arrivedAt[card] = clock.Now;
        }

        void StartLegacy(int card)
        {
            running++;
            clock.At(LatencyMs, () =>
            {
                Finish(card);
                running--;
                PumpLegacy();
            });
        }

        void PumpLegacy()
        {
            while (running < Slots && waiting.Count > 0)
            {
                StartLegacy(waiting.Dequeue());
            }
        }

        if (!legacy)
        {
            queue = new ThumbnailQueue<int>(Slots,
                card =>
                {
                    TaskCompletionSource<bool> done = new TaskCompletionSource<bool>();
                    clock.At(LatencyMs, () =>
                    {
                        Finish(card);
                        done.SetResult(true);
                    });
                    return done.Task;
                },
                // The simulator is one thread and one loop, so "back on the queue's own
                // thread" is here. The dispatcher does this for real.
                action => action());
        }

        List<int> want = new List<int>();

        void Scroll(int first, int last)
        {
            if (legacy)
            {
                // What shipped: walk the band in INDEX order, start a task for anything
                // not already under way. Nothing is ever taken back out.
                int bandFirst = Math.Max(0, first - Margin);
                int bandLast = Math.Min(cards - 1, last + Margin);

                for (int card = bandFirst; card <= bandLast; card++)
                {
                    if (!arrived[card] && !pending[card])
                    {
                        pending[card] = true;
                        result.Issued++;
                        waiting.Enqueue(card);
                    }
                }

                PumpLegacy();
                return;
            }

            want.Clear();
            VisibleBand.Order(cards, first, last, Margin, card =>
            {
                if (!arrived[card])
                {
                    want.Add(card);
                }
            });

            queue!.Want(want);
        }

        // The flick: fifty events at 60 Hz, covering the whole listing.
        const int Events = 50;
        int travel = Math.Max(1, (cards - Visible + Events - 1) / Events);
        int at = 0;

        for (int step = 0; step < Events; step++)
        {
            int top = at;
            clock.At(step * 16, () => Scroll(top, top + Visible - 1));
            at = Math.Min(cards - Visible, at + travel);
        }

        // Where it came to rest, and when.
        int restTop = at;
        long restAt = (Events - 1) * 16;

        clock.At(restAt, () => Scroll(restTop, restTop + Visible - 1));
        clock.Run();

        if (!legacy)
        {
            result.Issued = queue!.Issued;
        }

        for (int card = restTop; card < restTop + Visible && card < cards; card++)
        {
            if (!arrived[card])
            {
                result.SettledMs = -1;
                return result;
            }

            long waited = arrivedAt[card] - restAt;

            if (result.FirstMs < 0 || waited < result.FirstMs)
            {
                result.FirstMs = waited;
            }

            if (waited > result.SettledMs)
            {
                result.SettledMs = waited;
            }
        }

        return result;
    }

    /// <summary>
    /// A clock that costs nothing to advance.
    /// </summary>
    /// <remarks>
    /// Events are taken in time order, and ties in the order they were booked, so a run is
    /// the same run every time. Linear search of a list a few thousand long, because a
    /// heap here would be a data structure to get right in a test that exists to check
    /// something else.
    /// </remarks>
    private sealed class Clock
    {
        private readonly List<(long At, long Seq, Action Run)> events = new List<(long, long, Action)>();
        private long seq;

        public long Now { get; private set; }

        public void At(long delay, Action run) => events.Add((Now + delay, seq++, run));

        public void Run()
        {
            while (events.Count > 0)
            {
                int best = 0;

                for (int i = 1; i < events.Count; i++)
                {
                    if (events[i].At < events[best].At ||
                        (events[i].At == events[best].At && events[i].Seq < events[best].Seq))
                    {
                        best = i;
                    }
                }

                (long at, long _, Action run) = events[best];

                events.RemoveAt(best);
                Now = at;
                run();
            }
        }
    }


    // ------------------------------------------------------ the moving tile's memory

    /// <summary>
    /// Twenty-one previews, measured on production, priced at the ceiling that ships.
    /// </summary>
    /// <remarks>
    /// Every shape here was pulled from host.example.com and parsed chunk by chunk: canvas from
    /// the VP8X header, frames counted as ANMF chunks. The clip is thirty frames at 83 ms
    /// - 12 fps, real time - for every video, which is the server's MOTION_FRAMES, and the
    /// numbers below are what those frames cost once decoded to 288 px and held as BGRA.
    ///
    /// It is pinned because it was wrong once: the server swapped eight sampled frames for
    /// a thirty-frame clip and this budget quadrupled overnight while the comments around
    /// it went on quoting 2,592 KiB a tile. A ceiling nobody can check is one that goes
    /// stale silently, and the way it announces itself is a memory graph rather than a
    /// failing test.
    /// </remarks>
    private static void TheMotionBudgetHoldsTheClipsProductionSends()
    {
        // 12 of the 21: an ordinary landscape or square video, fitted into the server's
        // 480x480 box. The whole clip, comfortably.
        MotionFrameBudget.Plan square = MotionFrameBudget.For(480, 480, 30);

        Check(square.Width == 288 && square.Height == 288, "a 480x480 preview decodes to 288x288");
        Check(square.Frames == 30, "and holds the whole thirty-frame clip");
        Check(Mib(square.Bytes) == "9.49", "at 9.49 MiB, the figure measured on production");

        // 288x480, a 9:16 phone video: the worst real shape, and the one the ceiling bites.
        MotionFrameBudget.Plan tall = MotionFrameBudget.For(288, 480, 30);

        Check(tall.Width == 288 && tall.Height == 480, "a 9:16 preview decodes to 288x480");
        Check(tall.Frames == 18, "and keeps eighteen frames of the thirty - 1.5 s of a 2.5 s loop");
        Check(tall.Bytes <= MotionFrameBudget.MaxBytes, "which is inside the ceiling, not over it");

        // 352x480, the other tall one.
        MotionFrameBudget.Plan portrait = MotionFrameBudget.For(352, 480, 30);

        Check(portrait.Width == 288 && portrait.Height == 393, "a 3:4 preview decodes to 288x393");
        Check(portrait.Frames == 23, "and keeps twenty-three of the thirty");

        // The five short-of-square ones, and the 400x300. All whole.
        int whole = 0;

        foreach ((int w, int h) in new[] { (480, 480), (480, 382), (480, 330), (480, 304), (480, 268), (400, 300) })
        {
            MotionFrameBudget.Plan plan = MotionFrameBudget.For(w, h, 30);

            if (plan.Frames == 30 && plan.Bytes <= MotionFrameBudget.MaxBytes)
            {
                whole++;
            }
        }

        Check(whole == 6, "every landscape and square shape measured keeps its whole clip");

        // The server may not send more than thirty, but the second fence is what stops a
        // future one from being taken at its word.
        Check(MotionFrameBudget.For(64, 64, 10_000).Frames == MotionFrameBudget.MaxFrames,
            "a preview of tiny frames is capped by the frame count, not by the bytes");
    }

    /// <summary>
    /// A preview smaller than the tile is kept at its own size.
    /// </summary>
    /// <remarks>
    /// One of the twenty-one is 144x256 - a GIF the server had no reason to shrink.
    /// Stretching it to 288 would cost four times the memory to show the same picture
    /// blurred, which is the still path's rule (<c>MySpaceViewModel.Decode</c>) arrived at
    /// from the other direction.
    /// </remarks>
    private static void TheMotionBudgetNeverEnlargesASmallPreview()
    {
        MotionFrameBudget.Plan small = MotionFrameBudget.For(144, 256, 15);

        Check(small.Width == 144 && small.Height == 256, "a 144x256 preview is not enlarged");
        Check(small.Frames == 15, "and keeps every frame it has");
        Check(Mib(small.Bytes) == "2.11", "at 2.11 MiB");
    }

    /// <summary>A header that says something impossible does not produce a plan to act on.</summary>
    /// <remarks>
    /// The canvas comes off the network. A zero or a negative is not a case anyone has
    /// seen, which is exactly why it is worth one line here rather than a division by zero
    /// in a worker thread.
    /// </remarks>
    private static void TheMotionBudgetSurvivesNonsense()
    {
        Check(MotionFrameBudget.For(0, 480, 30).Frames == 0, "a zero-width canvas plans nothing");
        Check(MotionFrameBudget.For(480, 0, 30).Frames == 0, "a zero-height canvas plans nothing");
        Check(MotionFrameBudget.For(480, 480, 0).Frames == 0, "an artefact with no frames plans nothing");
        Check(MotionFrameBudget.For(480, 480, 1).Frames == 1,
            "and a one-frame artefact is never asked for a second frame it does not have");
    }

    private static string Mib(long bytes) => (bytes / 1048576.0).ToString("0.00",
        System.Globalization.CultureInfo.InvariantCulture);

    private static void Check(bool condition, string what)
    {
        if (condition)
        {
            Console.WriteLine("  ok   " + what);
            return;
        }

        failures++;
        Console.WriteLine("  FAIL " + what);
    }
}
