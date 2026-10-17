#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using ShareX.Localization;
using ShareX.UploadersLib;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;

namespace ShareX;

/// <summary>What became of one item that left the queue.</summary>
internal sealed class ModlightQueueOutcome
{
    public required ModlightQueueItem Item { get; init; }
    public bool Succeeded { get; init; }
    public bool Canceled { get; init; }
    public ModlightQueueReason Reason { get; init; } = ModlightQueueReason.None;
    public string Detail { get; init; } = "";

    /// <summary>The link to hand to a person: whichever of page or raw this account prefers.</summary>
    public string Link { get; init; } = "";

    public string ViewUrl { get; init; } = "";
    public string RawUrl { get; init; } = "";
    public DateTime When { get; init; } = DateTime.Now;
}

/// <summary>
/// Uploads that could not go out when they were made, kept until they can.
/// </summary>
/// <remarks>
/// <b>The problem.</b> Before this, an upload attempted while the link was down was an
/// upload that failed. The capture stayed on disk, the notification said something about
/// a socket, and getting the file up was the user's problem - which on a laptop that
/// moves between networks is a thing that happens several times a day, and on a four
/// gigabyte recording is a thing nobody does twice.
///
/// <b>The shape of the fix.</b> A failure with no answer from the server is not a
/// failure, it is a postponement. The task hands the upload over here, ends quietly, and
/// this drains the backlog in the background - across network changes, across restarts,
/// and resuming a part-sent file where it stopped rather than at zero.
///
/// <b>Three decisions worth stating.</b>
///
/// 1. Only unanswered attempts are taken over. If the server said 507, the account is
///    full: the upload fails now, loudly, with that sentence, exactly as before. Queuing
///    a refusal that repetition cannot change would turn a clear message into a row that
///    quietly retries for a week. See <see cref="IsWorthQueuing"/>.
///
/// 2. Waiting for a network and being refused by a server are counted differently. No
///    network is a condition of the world that will change, so it costs no attempts and
///    the item simply waits. A server that answers with errors gets
///    <see cref="MaxServerAttempts"/> tries and then stops and says so.
///
/// 3. Cancel means cancel, and it leaves a mark. A cancelled item is reported as
///    cancelled and lands in the history that way. Making the row disappear would read
///    as "sent".
///
/// One item at a time, deliberately. A backlog of ten files hitting the link the moment
/// it returns is how a queue turns a recovered connection back into a broken one, and
/// nothing here is in a hurry - by definition every item in it is already late.
///
/// Threading: one background thread owns every transfer. <see cref="items"/> is guarded
/// by <see cref="gate"/> and never handed out - callers get copies. Events are raised
/// off the worker thread and subscribers marshal for themselves.
/// </remarks>
internal static class ModlightUploadQueue
{
    /// <summary>How many times a server that answers with errors is asked again.</summary>
    /// <remarks>
    /// Eight, spread over the backoff below, is a little over an hour of asking. Beyond
    /// that the honest reading is not "the server is briefly unwell" but "this will not
    /// work", and the useful thing to do is stop and say so rather than keep a machine
    /// that is already struggling company.
    /// </remarks>
    private const int MaxServerAttempts = 8;

    /// <summary>How long an item may sit here before waiting stops being patience.</summary>
    /// <remarks>
    /// Two weeks. Long enough to cover a holiday, a broken router and a forgotten laptop;
    /// short enough that a spooled capture nobody will ever come back to does not sit in
    /// the personal folder for ever. What happens at the end is a row in the history
    /// saying it was given up on, never a silent deletion.
    /// </remarks>
    private static readonly TimeSpan maximumAge = TimeSpan.FromDays(14);

    /// <summary>The backoff ladder, in seconds, capped at the last step.</summary>
    private static readonly int[] backoff = { 15, 30, 60, 120, 300, 600, 900 };

    /// <summary>
    /// How often the worker looks even when nothing has woken it.
    /// </summary>
    /// <remarks>
    /// The network events below are the fast path and they cover the common case, but they
    /// are not complete: a router that comes back on the same adapter, a VPN, a captive
    /// portal that starts letting traffic through - none of those necessarily raise
    /// anything. A quiet poll costs nothing and means the worst case for noticing is this
    /// interval rather than never.
    /// </remarks>
    private static readonly TimeSpan idlePoll = TimeSpan.FromSeconds(15);

    /// <summary>How long a quota answer is trusted before it is asked for again.</summary>
    private static readonly TimeSpan quotaFreshness = TimeSpan.FromSeconds(60);

    /// <summary>Bytes that can be parked when the content exists nowhere else.</summary>
    /// <remarks>
    /// Generous for a capture or a clipboard paste, which is all this path ever sees, and
    /// still a ceiling: spooling is a copy, and a copy is the one thing the design says
    /// this queue does not do to anything that already has a file of its own.
    /// </remarks>
    private const long MaxSpoolBytes = 512L * 1024 * 1024;

    private static readonly object gate = new();
    private static readonly List<ModlightQueueItem> items = new();
    private static readonly AutoResetEvent wake = new(false);

    private static bool started;
    private static ModlightQueueItem? sending;
    private static CancellationTokenSource? sendCancel;
    private static DateTime quotaAskedUtc;
    private static long? quotaRemaining;

    /// <summary>The list or one of its items changed. Redraw from <see cref="Snapshot"/>.</summary>
    public static event Action? Changed;

    /// <summary>Bytes moved. Fired often; subscribers throttle.</summary>
    public static event Action? Progressed;

    /// <summary>One item left the queue.</summary>
    public static event Action<ModlightQueueOutcome>? Finished;

    /// <summary>How many uploads are waiting. Cheap; safe to ask on every redraw.</summary>
    public static int Count
    {
        get
        {
            lock (gate)
            {
                return items.Count;
            }
        }
    }

    /// <summary>
    /// Start draining, and pick up whatever the last run left behind.
    /// </summary>
    /// <remarks>
    /// Reading the file happens on the worker thread rather than here, because this is
    /// called from the application's start-up path and start-up must not wait on a disk
    /// that a virus scanner is busy with.
    /// </remarks>
    public static void Initialize()
    {
        lock (gate)
        {
            if (started)
            {
                return;
            }

            started = true;
        }

        Thread worker = new(Run)
        {
            Name = "Modlight upload queue",

            // Background, so a queue with work left in it can never be the reason the
            // application refuses to close. Nothing is lost by being killed here: the file
            // is written on every change, and the server remembers the offset.
            IsBackground = true
        };

        worker.Start();

        try
        {
            NetworkChange.NetworkAvailabilityChanged += (_, _) => Wake();
            NetworkChange.NetworkAddressChanged += (_, _) => Wake();
        }
        catch (Exception exception)
        {
            // Not fatal: the poll below finds the network coming back anyway, a few
            // seconds later than these would have.
            DebugHelper.WriteException(exception, "Modlight queue could not watch for network changes.");
        }
    }

    /// <summary>Look again now.</summary>
    public static void Wake() => wake.Set();

    /// <summary>A copy of the queue, newest last, for drawing.</summary>
    public static ModlightQueueItem[] Snapshot()
    {
        lock (gate)
        {
            return items.ToArray();
        }
    }

    /// <summary>
    /// Take an upload that could not go out, and promise to send it later.
    /// </summary>
    /// <param name="task">The task that just failed. Left in a state that ends quietly.</param>
    /// <param name="answer">
    /// What the server said, or null when it never answered. The whole decision turns on
    /// this: see <see cref="IsWorthQueuing"/>.
    /// </param>
    /// <returns>True when the queue has taken responsibility for this upload.</returns>
    public static bool TryTakeOver(WorkerTask task, ResponseInfo? answer)
    {
        try
        {
            TaskInfo info = task.Info;

            if (info?.TaskSettings == null || !ModlightUploadGate.UploadsToModlight(info) || !IsWorthQueuing(answer))
            {
                return false;
            }

            ModlightAccount? account = ModlightSignIn.Current;

            if (account == null)
            {
                // Nothing to queue this against. The sign-in gate has already had its say
                // earlier in the task, and an item with no account could never be matched
                // to one later without guessing.
                return false;
            }

            ModlightQueueItem item = new()
            {
                FileName = NameFor(info),
                FolderId = info.ModlightFolderId,
                Host = account.Host,
                AccountTag = account.Tag,
                QueuedAtUtc = DateTime.UtcNow,
                DeadlineUtc = DateTime.UtcNow + maximumAge,
                NextAttemptUtc = DateTime.UtcNow.AddSeconds(backoff[0])
            };

            if (!Attach(item, task))
            {
                return false;
            }

            // The file must still be there when the queue gets to it. This flag means
            // "remove it once it is safely uploaded", and the upload has not happened -
            // leaving it set would have the task delete, seconds from now, the only copy
            // of the thing the queue is holding a path to. Exactly the same reasoning as
            // ModlightUploadGate.KeepLocally, for exactly the same reason.
            info.TaskSettings.AfterCaptureJob = info.TaskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.DeleteFile);

            lock (gate)
            {
                items.Add(item);
            }

            Persist();
            Changed?.Invoke();
            Wake();

            DebugHelper.WriteLine($"Modlight queue: {item.FileName} could not go out now, so it is queued ({item.Length} bytes).");

            Notify(string.Format(Strings.UploadQueue_Notice_Queued, item.FileName));

            return true;
        }
        catch (Exception exception)
        {
            // A failure to queue must never turn one failed upload into a crash. The task
            // then reports its own error, which is what would have happened anyway.
            DebugHelper.WriteException(exception, "Modlight queue could not take over an upload.");
            return false;
        }
    }

    /// <summary>Try this one right now, and forgive it its history.</summary>
    /// <remarks>
    /// A person pressing this is asserting that something has changed - they have freed
    /// space, signed back in, plugged the cable in. Taking them at their word means
    /// clearing the attempt count as well as the wait; leaving the count would let one
    /// button press fail instantly against a budget spent an hour ago.
    /// </remarks>
    public static void SendNow(string id)
    {
        lock (gate)
        {
            ModlightQueueItem? item = items.FirstOrDefault(x => x.Id == id);

            if (item == null || item.State == ModlightQueueState.Sending)
            {
                return;
            }

            item.State = ModlightQueueState.Waiting;
            item.Reason = ModlightQueueReason.None;
            item.Detail = "";
            item.ServerAttempts = 0;
            item.BackoffSeconds = 0;
            item.NextAttemptUtc = DateTime.UtcNow;
        }

        Persist();
        Changed?.Invoke();
        Wake();
    }

    /// <summary>
    /// Stop one queued upload for good.
    /// </summary>
    /// <remarks>
    /// The transfer is stopped and the item is reported as cancelled - it appears in the
    /// history as a cancellation, with the file it came from still named. It is not simply
    /// dropped from the list: a row that vanishes reads as "it went", which is the one
    /// thing that did not happen.
    /// </remarks>
    public static void Cancel(string id)
    {
        ModlightQueueItem? item;
        bool inFlight;

        lock (gate)
        {
            item = items.FirstOrDefault(x => x.Id == id);

            if (item == null)
            {
                return;
            }

            // The item's own state, not "is this the one the worker is holding". The worker
            // keeps its claim for a moment after the transfer has ended, and a click that
            // landed in that moment would otherwise do nothing at all - the queue would
            // quietly ignore a press of Cancel, which is the one button that must never be
            // ignored.
            inFlight = item.State == ModlightQueueState.Sending;

            if (inFlight)
            {
                // Left on the list, and left to the worker to finish off. Removing it here
                // would race the transfer thread for the same item.
                sendCancel?.Cancel();
                return;
            }

            items.Remove(item);
        }

        Release(item, abandonSession: true);
        Persist();

        Finished?.Invoke(new ModlightQueueOutcome { Item = item, Canceled = true });
        Changed?.Invoke();
    }

    /* ------------------------------------------------------------------ */
    /* The worker                                                          */
    /* ------------------------------------------------------------------ */

    private static void Run()
    {
        try
        {
            List<ModlightQueueItem> restored = ModlightQueueStore.Load();

            lock (gate)
            {
                items.AddRange(restored);
            }

            ModlightQueueStore.SweepSpool(restored);

            if (restored.Count > 0)
            {
                DebugHelper.WriteLine($"Modlight queue: {restored.Count} upload(s) left over from the last run.");
                Changed?.Invoke();
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Modlight queue could not be restored.");
        }

        while (true)
        {
            try
            {
                Pump();
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception, "Modlight queue");
            }

            wake.WaitOne(idlePoll);
        }
    }

    private static void Pump()
    {
        if (Count == 0)
        {
            return;
        }

        Expire();

        if (Count == 0)
        {
            return;
        }

        if (!IsNetworkUp())
        {
            Stamp(ModlightQueueReason.NoNetwork);
            return;
        }

        if (Program.UploadersConfig == null)
        {
            // Still loading. Saying "signed out" here would put a wrong sentence on every
            // row for the first second of every run.
            return;
        }

        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            Stamp(ModlightQueueReason.SignedOut);
            return;
        }

        // The two gate reasons above describe the machine, not the upload, so they have to
        // be taken back off the moment they stop being true. Without this a row would still
        // read "no connection" for as long as its backoff had left to run, minutes after
        // the network came back.
        Clear(ModlightQueueReason.NoNetwork, ModlightQueueReason.SignedOut);

        ModlightQueueItem? item = Due(account);

        if (item == null)
        {
            return;
        }

        Send(item, account);
    }

    /// <summary>
    /// The next item that is allowed to go, or null.
    /// </summary>
    /// <remarks>
    /// An item queued for a different account is skipped rather than stopped, and that is
    /// on purpose: signing back in as the right person makes it go, with nothing to press.
    /// Stopping it would leave somebody staring at a row that has already fixed itself. It
    /// still has a deadline, so a queue full of another account's uploads does not become
    /// permanent furniture.
    /// </remarks>
    private static ModlightQueueItem? Due(ModlightAccount account)
    {
        DateTime now = DateTime.UtcNow;
        ModlightQueueItem? pick = null;
        bool changed = false;

        lock (gate)
        {
            foreach (ModlightQueueItem candidate in items)
            {
                if (candidate.State != ModlightQueueState.Waiting || candidate.NextAttemptUtc > now)
                {
                    continue;
                }

                if (!Matches(candidate, account))
                {
                    if (candidate.Reason != ModlightQueueReason.AccountChanged)
                    {
                        candidate.Reason = ModlightQueueReason.AccountChanged;
                        candidate.Detail = ModlightSignIn.DisplayName(account);
                        changed = true;
                    }

                    continue;
                }

                pick = candidate;
                break;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return pick;
    }

    /// <summary>Take back a reason that has stopped being true.</summary>
    private static void Clear(params ModlightQueueReason[] reasons)
    {
        bool changed = false;

        lock (gate)
        {
            foreach (ModlightQueueItem item in items)
            {
                if (item.State == ModlightQueueState.Waiting && Array.IndexOf(reasons, item.Reason) >= 0)
                {
                    item.Reason = ModlightQueueReason.None;
                    item.Detail = "";
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static void Send(ModlightQueueItem item, ModlightAccount account)
    {
        if (!Verify(item))
        {
            return;
        }

        if (!HasRoom(item, account))
        {
            return;
        }

        CancellationTokenSource cancel = new();

        lock (gate)
        {
            item.State = ModlightQueueState.Sending;
            item.Reason = ModlightQueueReason.None;
            item.Detail = "";
            sending = item;
            sendCancel = cancel;
        }

        Persist();
        Changed?.Invoke();

        long startedAt = item.TusOffset;
        ProgressManager measured = new(Math.Max(item.Length, 1), startedAt);
        long lastSeen = startedAt;

        item.Progress = measured;

        ModlightResumableUpload transfer = new(account);

        transfer.Progress += (sent, _) =>
        {
            if (sent > lastSeen)
            {
                measured.UpdateProgress(sent - lastSeen);
                lastSeen = sent;
                Progressed?.Invoke();
            }
        };

        // The item stays claimed until its ending has been recorded, not merely until the
        // socket closes. Letting go any earlier leaves a window in which a click on Cancel
        // finds an item that is no longer being sent, drops it, and reports a cancellation
        // - for an upload that had already succeeded a microsecond before. Two rows in the
        // history, one of them a lie. Holding the claim across the switch makes that click
        // a no-op instead, which is the honest answer: an upload that has landed cannot be
        // un-landed by cancelling it.
        try
        {
            ModlightTransferResult result;

            try
            {
                result = transfer.SendAsync(item, cancel.Token).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                // SendAsync answers rather than throws, so this is belt and braces - but a
                // throw escaping here would leave the item marked Sending for ever, which
                // is the one state nothing recovers from on its own.
                DebugHelper.WriteException(exception, "Modlight queue transfer");
                result = ModlightTransferResult.Failure(ModlightTransferOutcome.Retry,
                    ModlightQueueReason.Unreachable, exception.Message);
            }

            item.Progress = null;

            // Whatever happened, the offset and the session URL are worth keeping: they
            // are what the next attempt starts from.
            bool moved = item.TusOffset > startedAt;

            switch (result.Outcome)
            {
                case ModlightTransferOutcome.Success:
                    Complete(item, result);
                    break;

                case ModlightTransferOutcome.Canceled:
                    Abandon(item);
                    break;

                case ModlightTransferOutcome.Blocked:
                    Block(item, result.Reason, result.Detail);
                    break;

                default:
                    Reschedule(item, result, moved);
                    break;
            }
        }
        finally
        {
            lock (gate)
            {
                sending = null;
                sendCancel = null;

                // Disposed under the same lock that Cancel takes to reach it, so there is
                // no moment in which it holds a reference to a disposed source.
                cancel.Dispose();
            }
        }
    }

    /// <summary>Make sure the bytes this item names are still there, and still the same bytes.</summary>
    private static bool Verify(ModlightQueueItem item)
    {
        try
        {
            FileInfo file = new(item.SourcePath);

            if (!file.Exists)
            {
                // The one outcome the design accepts as the price of not copying every
                // upload: the queue holds a path, and a path can stop pointing at a file.
                // It fails, it says exactly that, and it says it about a named file.
                Fail(item, ModlightQueueReason.SourceMissing, item.SourcePath);
                return false;
            }

            if (file.Length != item.Length || file.LastWriteTimeUtc != item.SourceWrittenUtc)
            {
                // The file changed while it waited. The declared length on the server's
                // session belongs to the old file, so that session is worthless; the new
                // contents are what the user has, so they are what goes.
                DebugHelper.WriteLine($"Modlight queue: {item.FileName} changed on disk, so the transfer starts again.");

                item.Length = file.Length;
                item.SourceWrittenUtc = file.LastWriteTimeUtc;
                item.TusUrl = null;
                item.TusOffset = 0;
            }

            return true;
        }
        catch (Exception exception)
        {
            // A path on a drive that is no longer mounted lands here. Treated as missing,
            // because from the queue's point of view it is.
            DebugHelper.WriteException(exception);
            Fail(item, ModlightQueueReason.SourceMissing, item.SourcePath);
            return false;
        }
    }

    /// <summary>
    /// Ask whether the account still has room, before sending anything.
    /// </summary>
    /// <remarks>
    /// Worth one small request because of what the alternative looks like. The resumable
    /// endpoint compares the declared length against whatever is left of the quota and
    /// answers 413 - the same code it uses for a file over the operator's own ceiling - so
    /// without this the message for a full account would be "the server will not take a
    /// file this size", which is true and useless. With it, the row says how much was
    /// needed and how much is free, and it says so before a byte moves.
    ///
    /// A server that cannot be reached for this answer is a server that cannot be reached
    /// for the upload either, so that case simply defers.
    /// </remarks>
    private static bool HasRoom(ModlightQueueItem item, ModlightAccount account)
    {
        try
        {
            if (DateTime.UtcNow - quotaAskedUtc > quotaFreshness)
            {
                using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(30));

                ModlightAccountInfo? info = ModlightApi
                    .GetAccountAsync(account.Host, account.Token, cancel.Token)
                    .GetAwaiter().GetResult();

                if (info == null)
                {
                    Block(item, ModlightQueueReason.TokenRejected, "");
                    return false;
                }

                quotaAskedUtc = DateTime.UtcNow;
                quotaRemaining = info.Quota?.Unlimited == true ? null : info.Quota?.Remaining;
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Modlight queue could not read the account's quota.");
            Reschedule(item, ModlightTransferResult.Failure(ModlightTransferOutcome.Retry,
                ModlightQueueReason.Unreachable, exception.Message), moved: false);
            return false;
        }

        if (quotaRemaining == null || item.Length <= quotaRemaining.Value)
        {
            return true;
        }

        Block(item, ModlightQueueReason.OutOfSpace,
            ModlightFormat.CloudSize(item.Length) + "|" + ModlightFormat.CloudSize(quotaRemaining.Value));

        return false;
    }

    /* ------------------------------------------------------------------ */
    /* Endings                                                             */
    /* ------------------------------------------------------------------ */

    private static void Complete(ModlightQueueItem item, ModlightTransferResult result)
    {
        lock (gate)
        {
            items.Remove(item);
        }

        // The session is finished, so there is nothing on the server to abandon.
        Release(item, abandonSession: false);
        Persist();

        // Deliberately NOT put on the clipboard. This can land hours after the file was
        // made, and by then the clipboard holds whatever the user is doing right now.
        // Taking it from them to give them a link they did not ask for at that moment is
        // theft dressed as helpfulness; the notice and the row both carry a copy button.
        DebugHelper.WriteLine($"Modlight queue: {item.FileName} went up. {result.CopyUrl}");

        Notify(string.Format(Strings.UploadQueue_Notice_Sent, item.FileName));

        Finished?.Invoke(new ModlightQueueOutcome
        {
            Item = item,
            Succeeded = true,
            Link = result.CopyUrl,
            ViewUrl = result.ViewUrl,
            RawUrl = result.RawUrl
        });

        Changed?.Invoke();
        Wake();
    }

    /// <summary>The user cancelled the item that was in flight.</summary>
    private static void Abandon(ModlightQueueItem item)
    {
        lock (gate)
        {
            items.Remove(item);
        }

        Release(item, abandonSession: true);
        Persist();

        Finished?.Invoke(new ModlightQueueOutcome { Item = item, Canceled = true });
        Changed?.Invoke();
        Wake();
    }

    /// <summary>Stopped, and waiting for a person rather than for a clock.</summary>
    private static void Block(ModlightQueueItem item, ModlightQueueReason reason, string detail)
    {
        bool announce;

        lock (gate)
        {
            announce = item.State != ModlightQueueState.Blocked || item.Reason != reason;

            item.State = ModlightQueueState.Blocked;
            item.Reason = reason;
            item.Detail = detail ?? "";
            item.Progress = null;
        }

        Persist();
        Changed?.Invoke();

        if (announce)
        {
            // Once per reason, not once per attempt. A blocked item is not retried, so
            // this is a single notice - and it has to exist, because the whole point of
            // stopping rather than looping is that somebody is told.
            DebugHelper.WriteLine($"Modlight queue: {item.FileName} stopped - {reason} {detail}");

            Notify(item.FileName + Environment.NewLine + ModlightQueueText.Sentence(reason, item.Detail));
        }
    }

    /// <summary>Off the queue for good, with a reason.</summary>
    private static void Fail(ModlightQueueItem item, ModlightQueueReason reason, string detail)
    {
        lock (gate)
        {
            items.Remove(item);
        }

        Release(item, abandonSession: true);
        Persist();

        DebugHelper.WriteLine($"Modlight queue: {item.FileName} given up on - {reason} {detail}");

        Finished?.Invoke(new ModlightQueueOutcome { Item = item, Reason = reason, Detail = detail ?? "" });
        Changed?.Invoke();
        Wake();
    }

    /// <summary>
    /// Put a failed attempt back on the clock.
    /// </summary>
    /// <remarks>
    /// The attempt budget is spent ONLY on <see cref="ModlightQueueReason.ServerError"/>.
    /// Anything the network did - and that includes a stall we cut off ourselves - leaves
    /// the count where it was, so a laptop that is off the network for a week arrives back
    /// with its budget intact. This is the distinction the task's retry policy turns on,
    /// and it lives in these four lines.
    /// </remarks>
    private static void Reschedule(ModlightQueueItem item, ModlightTransferResult result, bool moved)
    {
        bool exhausted;

        lock (gate)
        {
            if (result.Reason == ModlightQueueReason.ServerError)
            {
                item.ServerAttempts++;
            }

            exhausted = item.ServerAttempts > MaxServerAttempts;

            if (!exhausted)
            {
                if (moved)
                {
                    // Progress is evidence about the link, and it outweighs the failure
                    // that came after it.
                    item.BackoffSeconds = 0;
                }

                int wait;

                if (result.RetryAfter != null)
                {
                    // The server named a time. Honouring it is the whole point of the
                    // header, and second-guessing it is how a client earns a longer one.
                    wait = (int)Math.Clamp(result.RetryAfter.Value.TotalSeconds, 1, 3600);
                }
                else
                {
                    item.BackoffSeconds = Next(item.BackoffSeconds);
                    wait = Jitter(item.BackoffSeconds);
                }

                item.State = ModlightQueueState.Waiting;
                item.Reason = result.Reason;
                item.Detail = result.Detail ?? "";
                item.NextAttemptUtc = DateTime.UtcNow.AddSeconds(wait);
            }

            item.Progress = null;
        }

        if (exhausted)
        {
            Block(item, ModlightQueueReason.GaveUp, MaxServerAttempts.ToString());
            return;
        }

        Persist();
        Changed?.Invoke();
    }

    /// <summary>Drop everything that has been waiting past its deadline.</summary>
    private static void Expire()
    {
        DateTime now = DateTime.UtcNow;
        ModlightQueueItem[] overdue;

        lock (gate)
        {
            overdue = items.Where(x => x.DeadlineUtc != default && x.DeadlineUtc <= now &&
                                       x.State != ModlightQueueState.Sending).ToArray();
        }

        foreach (ModlightQueueItem item in overdue)
        {
            Fail(item, ModlightQueueReason.TooOld, "");
        }
    }

    /// <summary>Say the same thing on every waiting row, when the reason is the machine's.</summary>
    private static void Stamp(ModlightQueueReason reason)
    {
        bool changed = false;

        lock (gate)
        {
            foreach (ModlightQueueItem item in items)
            {
                if (item.State == ModlightQueueState.Waiting && item.Reason != reason)
                {
                    item.Reason = reason;
                    item.Detail = "";
                    changed = true;
                }
            }
        }

        if (changed)
        {
            // Not persisted. This says what is true of the machine this second, and it is
            // recomputed within a second of the next start; writing the file every time a
            // laptop's wifi flickers would be a write per flicker for nothing.
            Changed?.Invoke();
        }
    }

    /* ------------------------------------------------------------------ */
    /* Bits and pieces                                                     */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// Whether this failure is one the queue should take.
    /// </summary>
    /// <remarks>
    /// Null - nothing answered at all - is the case this whole feature exists for: DNS,
    /// TLS, a dead socket, a laptop off the network.
    ///
    /// The four codes below are the ones that mean the request never reached the
    /// application: a gateway that has nothing behind it, a proxy timing out, a front door
    /// asking us to come back later. Everything else the server can say is an answer about
    /// this upload, and answers are reported now rather than queued - 507 above all, which
    /// says the account is full and which no amount of retrying can talk round.
    /// </remarks>
    private static bool IsWorthQueuing(ResponseInfo? answer)
    {
        if (answer == null)
        {
            return true;
        }

        switch ((int)answer.StatusCode)
        {
            case 408: // request timeout
            case 429: // too many requests
            case 502: // bad gateway
            case 503: // service unavailable
            case 504: // gateway timeout
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Point the item at bytes it can read later.
    /// </summary>
    /// <remarks>
    /// A file that already exists is referenced where it lies. Anything else - a capture
    /// the user has told the app not to save, a clipboard or text upload that never had a
    /// file - is written into the queue's own folder, because there is no other copy of it
    /// anywhere in the world and letting the process end would destroy it.
    /// </remarks>
    private static bool Attach(ModlightQueueItem item, WorkerTask task)
    {
        TaskInfo info = task.Info;
        string path = info.FilePath ?? "";

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            FileInfo file = new(path);

            item.SourcePath = path;
            item.OwnsSource = false;
            item.Length = file.Length;
            item.SourceWrittenUtc = file.LastWriteTimeUtc;

            return true;
        }

        Stream? data = task.Data;

        if (data == null || !data.CanSeek || data.Length <= 0 || data.Length > MaxSpoolBytes)
        {
            return false;
        }

        item.SourcePath = ModlightQueueStore.Spool(item.Id, item.FileName, data);
        item.OwnsSource = true;

        FileInfo spooled = new(item.SourcePath);
        item.Length = spooled.Length;
        item.SourceWrittenUtc = spooled.LastWriteTimeUtc;

        return true;
    }

    /// <summary>
    /// The name to upload under, treated exactly as the live path treats it.
    /// </summary>
    /// <remarks>
    /// The same two transforms <see cref="WorkerTask.UploadData"/> applies before handing a
    /// name to an uploader. Skipping them would mean a file that goes out through the queue
    /// arrives under a different name than the same file going out directly, which is the
    /// kind of difference nobody would think to look for.
    /// </remarks>
    private static string NameFor(TaskInfo info)
    {
        string name = info.FileName;

        if (string.IsNullOrEmpty(name))
        {
            name = Path.GetFileName(info.FilePath ?? "");
        }

        name = URLHelpers.RemoveBidiControlCharacters(name);

        if (info.TaskSettings.UploadSettings.FileUploadReplaceProblematicCharacters)
        {
            name = URLHelpers.ReplaceReservedCharacters(name, "_");
        }

        return string.IsNullOrEmpty(name) ? "file" : name;
    }

    /// <summary>Whether this item belongs to whoever is signed in now.</summary>
    private static bool Matches(ModlightQueueItem item, ModlightAccount account)
    {
        if (!string.Equals(item.Host?.TrimEnd('/'), account.Host?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // An older item may predate the tag being recorded; the host alone is then all
        // there is to go on, and refusing on that basis would strand it for ever.
        return string.IsNullOrEmpty(item.AccountTag) ||
               string.Equals(item.AccountTag, account.Tag, StringComparison.Ordinal);
    }

    /// <summary>Let go of everything the item was holding on to.</summary>
    private static void Release(ModlightQueueItem item, bool abandonSession)
    {
        item.Progress = null;

        ModlightQueueStore.DiscardSpool(item);

        if (!abandonSession || string.IsNullOrEmpty(item.TusUrl))
        {
            return;
        }

        string url = item.TusUrl!;
        item.TusUrl = null;

        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            return;
        }

        // Best effort, off the worker thread so a cancellation is instant. The server
        // reclaims abandoned sessions on its own within a day, so failing here costs
        // nothing but a little of somebody's scratch space for a while.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                new ModlightResumableUpload(account).AbandonAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception);
            }
        });
    }

    /// <summary>
    /// Put one line in front of the user, and never let doing so break anything.
    /// </summary>
    /// <remarks>
    /// The notification path reads the default task settings for its placement and
    /// duration, and this queue runs from the moment the application starts - including
    /// the moments before those settings have finished loading. A queue that dropped an
    /// upload because it could not draw a toast would be a spectacular way to fail at the
    /// one thing it is for.
    /// </remarks>
    private static void Notify(string text)
    {
        try
        {
            TaskHelpers.ShowNotificationTip(text, "Modlight - " + Strings.UploadQueue_SectionTitle);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }

    private static bool IsNetworkUp()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (Exception exception)
        {
            // Cannot answer, so do not pretend to: attempting and failing is a better
            // wrong guess than waiting for ever on a question nobody answered.
            DebugHelper.WriteException(exception);
            return true;
        }
    }

    private static int Next(int current)
    {
        foreach (int step in backoff)
        {
            if (step > current)
            {
                return step;
            }
        }

        return backoff[^1];
    }

    /// <summary>
    /// Spread the retries out.
    /// </summary>
    /// <remarks>
    /// Ten files queued during one outage would otherwise all wake at the same instant and
    /// hit the same link together - which is how a queue turns a connection that has just
    /// come back into one that goes down again.
    /// </remarks>
    private static int Jitter(int seconds)
    {
        int spread = Math.Max(1, seconds / 5);

        return Math.Max(1, seconds - spread + Random.Shared.Next(spread * 2 + 1));
    }

    private static void Persist()
    {
        ModlightQueueItem[] copy;

        lock (gate)
        {
            copy = items.ToArray();
        }

        ModlightQueueStore.Save(copy);
    }
}
