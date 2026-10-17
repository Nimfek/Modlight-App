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

using Newtonsoft.Json;
using ShareX.UploadersLib;
using System;

namespace ShareX;

/// <summary>Where one queued upload stands.</summary>
public enum ModlightQueueState
{
    /// <summary>Not being sent right now, and will be tried again by itself.</summary>
    Waiting,

    /// <summary>Bytes are moving.</summary>
    Sending,

    /// <summary>
    /// Stopped trying. Nothing will happen to this item until a person acts on it.
    /// </summary>
    /// <remarks>
    /// Reached only for refusals that repeating cannot fix - no room on the account,
    /// a token the server will not accept, a file the server will not take. The item
    /// and its bytes are kept: the whole reason this state exists rather than a
    /// failure is that every one of those causes is something the owner can remove,
    /// and throwing the upload away would punish them for a condition they were about
    /// to fix.
    /// </remarks>
    Blocked
}

/// <summary>
/// Why a queued upload is where it is.
/// </summary>
/// <remarks>
/// A code rather than a sentence, and it is stored as one. The queue outlives the
/// process, so a message frozen into the file at the moment of failure would still be
/// in last month's language after the user switched to Polish - and a stale sentence
/// about a stale failure is the hardest kind of wrong text to notice. The sentence is
/// built at the moment it is drawn, from this and from <see cref="ModlightQueueItem.Detail"/>.
/// </remarks>
public enum ModlightQueueReason
{
    None,

    /// <summary>Windows says this machine has no network at all.</summary>
    NoNetwork,

    /// <summary>There is a network, but nothing answered: DNS, TLS, a socket that died, a stall.</summary>
    Unreachable,

    /// <summary>The server answered, with something that may well not happen next time.</summary>
    ServerError,

    /// <summary>Nobody is signed in, so there is no account to send to.</summary>
    SignedOut,

    /// <summary>Signed in, but as somebody else than the account this was queued for.</summary>
    AccountChanged,

    /// <summary>The file this item points at is gone.</summary>
    SourceMissing,

    /// <summary>507 - the account has no room.</summary>
    OutOfSpace,

    /// <summary>401 - the server would not take this token.</summary>
    TokenRejected,

    /// <summary>413 - the server will not accept a file this size.</summary>
    TooLarge,

    /// <summary>Refused for a reason that will not change by itself.</summary>
    Refused,

    /// <summary>Sat in the queue past its deadline.</summary>
    TooOld,

    /// <summary>The server kept answering with errors until the attempts ran out.</summary>
    GaveUp
}

/// <summary>
/// One upload that has not happened yet, described well enough to happen later -
/// including after the application has been closed and reopened.
/// </summary>
/// <remarks>
/// <b>What is kept is the intent, not the bytes.</b> A queued item is a path, a name,
/// a destination folder and the account it was meant for. Copying the file would double
/// the disk cost of the exact case this feature exists for - the four-gigabyte capture
/// on a laptop whose lid just closed - and would do it at the worst possible moment,
/// while the machine is already unable to get rid of the data.
///
/// The one exception is content that exists nowhere else: a capture the user has told
/// the app not to save, or a clipboard or text upload that never had a file. Those are
/// spooled into the queue's own folder and <see cref="OwnsSource"/> is set, which is the
/// flag that says the queue must delete the file once it is no longer needed. Everything
/// else is referenced where it already lies and is never touched.
///
/// Consequence, and it is deliberate: a queued item whose file has been deleted fails,
/// and says that is why. The alternative - silently keeping a copy of every upload - is
/// a bigger surprise than the failure.
/// </remarks>
public sealed class ModlightQueueItem
{
    /// <summary>Identifies this item across restarts, and names its spool file.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The name the file is uploaded under, which need not be the name on disk.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Absolute path of the bytes to send.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>The queue created this file and must delete it when it is done with it.</summary>
    public bool OwnsSource { get; set; }

    /// <summary>Size when it was queued. Compared with the file on every attempt.</summary>
    public long Length { get; set; }

    /// <summary>Last write time when it was queued. Same purpose as <see cref="Length"/>.</summary>
    public DateTime SourceWrittenUtc { get; set; }

    /// <summary>Where it should land, or null for the account's own default folder.</summary>
    public string? FolderId { get; set; }

    /// <summary>Origin of the server it was queued for.</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// Short code of the account it was queued for.
    /// </summary>
    /// <remarks>
    /// Compared before every attempt. Without it, signing out and signing in as somebody
    /// else would post the previous user's screenshots into the new user's library, hours
    /// later, with nothing on screen to connect the two events.
    /// </remarks>
    public string AccountTag { get; set; } = "";

    public DateTime QueuedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>After this, waiting stops being patience and starts being a leak.</summary>
    public DateTime DeadlineUtc { get; set; }

    /// <summary>
    /// How many times the SERVER answered with an error.
    /// </summary>
    /// <remarks>
    /// Counted separately from waiting for a network on purpose, and it is the difference
    /// the whole retry policy turns on. A missing network is a condition of the world that
    /// will change; a server that has answered "500" eight times running is not going to
    /// answer differently on the ninth, and a client that keeps asking is a client
    /// hammering somebody's machine for a week.
    /// </remarks>
    public int ServerAttempts { get; set; }

    /// <summary>Not before this. Set by the backoff.</summary>
    public DateTime NextAttemptUtc { get; set; }

    /// <summary>
    /// How long the last wait was, which is what the next one doubles.
    /// </summary>
    /// <remarks>
    /// Persisted, unlike the deadline it produces. A restart is a change in the world and
    /// deserves an immediate attempt, so <see cref="Revive"/> clears the wait - but a
    /// machine that is restarted every ten minutes must not thereby get a fresh
    /// fifteen-second retry loop for ever, so the step it had reached is remembered.
    ///
    /// Reset to nothing whenever an attempt actually moved bytes. A transfer that got four
    /// hundred megabytes further before the link dropped has proved the link works, and
    /// making it wait a quarter of an hour on that evidence is the opposite of what the
    /// measurement says.
    /// </remarks>
    public int BackoffSeconds { get; set; }

    /// <summary>
    /// The resumable session on the server, once there is one.
    /// </summary>
    /// <remarks>
    /// This single string is what makes the queue worth having. It survives the process,
    /// so a four-gigabyte upload that was 90% sent when the machine was shut down resumes
    /// at 90% the next morning rather than starting again - which, on the connection this
    /// app is built for, is the difference between an upload that completes and one that
    /// never does. The server keeps the session for 24 hours; after that it answers 404
    /// and the transfer starts over, which is handled rather than reported.
    ///
    /// Worth knowing what this string is: after creation, tus does not re-authenticate, so
    /// holding this URL is what authorises appending to that one upload. It is therefore a
    /// capability, written in clear into Queue.json - deliberately, because it is worth far
    /// less than the API token sitting in the same folder (one upload, into an account that
    /// is already the owner's, expiring within a day), and encrypting it would buy a day of
    /// protection at the cost of a key-management story of its own.
    /// </remarks>
    public string? TusUrl { get; set; }

    /// <summary>Last offset the server confirmed. Advisory: the server is asked again before every attempt.</summary>
    public long TusOffset { get; set; }

    public ModlightQueueState State { get; set; } = ModlightQueueState.Waiting;

    public ModlightQueueReason Reason { get; set; } = ModlightQueueReason.None;

    /// <summary>Whatever the server said, or a number to put in the sentence. Never the sentence itself.</summary>
    public string Detail { get; set; } = "";

    /// <summary>Bytes moved so far in the attempt happening now. Not persisted.</summary>
    [JsonIgnore]
    public ProgressManager? Progress { get; set; }

    /// <summary>
    /// Put the item back into a state a fresh process can act on.
    /// </summary>
    /// <remarks>
    /// Called on everything read back from disk. <see cref="ModlightQueueState.Sending"/>
    /// is the only state that cannot survive a restart - it describes a socket, and there
    /// is no socket - so an item caught mid-transfer by a shutdown comes back as waiting.
    /// Its offset is still on the server, so nothing is lost by the demotion.
    ///
    /// The delay is not carried over either: a backoff timer that was counting down to
    /// "try again in fifteen minutes" was counting against a condition that a restart has
    /// very likely already changed.
    /// </remarks>
    public void Revive()
    {
        Progress = null;

        if (State == ModlightQueueState.Sending)
        {
            State = ModlightQueueState.Waiting;
            Reason = ModlightQueueReason.None;
        }

        if (State == ModlightQueueState.Waiting)
        {
            NextAttemptUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Whether the values read back off the disk describe an item at all.</summary>
    public bool IsUsable()
    {
        return !string.IsNullOrEmpty(Id) &&
               !string.IsNullOrEmpty(FileName) &&
               !string.IsNullOrEmpty(SourcePath) &&
               !string.IsNullOrEmpty(Host) &&
               Length >= 0;
    }
}
