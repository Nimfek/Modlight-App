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
/// What the server last CONFIRMED about this installation's settings.
/// </summary>
/// <remarks>
/// Lives in ApplicationConfig beside the theme's own marker, and is deliberately NOT in
/// the document that gets synced - a marker describing this machine's conversation with
/// the server would be nonsense on another machine, and syncing it would make every
/// installation claim to have confirmed whatever the last one did.
///
/// Three fields and no timestamp, because a timestamp would only invite somebody to
/// compare it with the server's and decide something on the strength of two clocks.
/// </remarks>
public class ModlightCloudSettings
{
    /// <summary>Which deployment and account this marker is about. See <see cref="ModlightSettingsPlan.AccountKey"/>.</summary>
    public string Account { get; set; } = "";

    /// <summary>The version the server answered with. 0 means no sync has ever been confirmed.</summary>
    public int Version { get; set; }

    /// <summary>Digest of the document the server acknowledged at that version.</summary>
    public string Digest { get; set; } = "";
}

/// <summary>What one round of settings sync should do.</summary>
public enum ModlightSyncAction
{
    /// <summary>The two sides agree and the marker already says so.</summary>
    Nothing,

    /// <summary>Both sides hold the same document; only the marker is behind. Write it, send nothing.</summary>
    Confirm,

    /// <summary>The account has no stored settings. Send ours, and become the first copy.</summary>
    Seed,

    /// <summary>Take the server's document and put it on this machine.</summary>
    Adopt,

    /// <summary>Send this machine's document, replacing what the server holds.</summary>
    Push
}

/// <summary>A decision, the version it is conditional on, and why it was reached.</summary>
public readonly struct ModlightSyncPlan
{
    public ModlightSyncAction Action { get; }

    /// <summary>
    /// The version a write asserts it is replacing, for <see cref="ModlightSyncAction.Seed"/>
    /// and <see cref="ModlightSyncAction.Push"/>; the version being taken, for
    /// <see cref="ModlightSyncAction.Adopt"/> and <see cref="ModlightSyncAction.Confirm"/>.
    /// </summary>
    public int Version { get; }

    /// <summary>For the log. Not shown to anybody, and deliberately not translated.</summary>
    public string Reason { get; }

    public ModlightSyncPlan(ModlightSyncAction action, int version, string reason)
    {
        Action = action;
        Version = version;
        Reason = reason;
    }

    public override string ToString() => $"{Action} (v{Version}): {Reason}";
}

/// <summary>
/// Which side wins, decided before a single byte moves.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of HTTP, of JSON and of the config objects. The whole difficulty of a
/// two-way sync is this decision, and a decision buried in a method that also does a round
/// trip is a decision nobody can test. Everything here is a comparison between three short
/// strings and two integers, which is why <c>ShareX.Tests</c> can link this file and run
/// every branch of it in a process with no network in it.
/// </para>
/// <para>
/// THE MARKER, NOT THE CLOCK. The state this reads is what the server last CONFIRMED - the
/// version number it answered with, and a digest of the document it acknowledged - exactly
/// as <c>ApplicationThemeOptions.SkinConfirmed</c> does for the skin. Two clocks on two
/// machines are two opinions, and a desktop clock is not evidence about anything. "Did what
/// this installation holds ever reach the server" is a fact about this installation, and it
/// is the only thing that decides anything here.
/// </para>
/// </remarks>
public static class ModlightSettingsPlan
{
    /// <summary>
    /// Work out what to do with the document the server just reported.
    /// </summary>
    /// <param name="accountKey">Who we are signed in to now. See <see cref="AccountKey"/>.</param>
    /// <param name="confirmedAccount">Who the marker was written for.</param>
    /// <param name="confirmedVersion">The version the server last confirmed. 0 = never.</param>
    /// <param name="confirmedDigest">Digest of the document the server confirmed.</param>
    /// <param name="localDigest">Digest of the document this machine holds now.</param>
    /// <param name="remoteVersion">The version the server just reported. 0 = nothing stored.</param>
    /// <param name="remoteDigest">Digest of the document the server just reported.</param>
    public static ModlightSyncPlan Decide(
        string? accountKey,
        string? confirmedAccount,
        int confirmedVersion,
        string? confirmedDigest,
        string? localDigest,
        int remoteVersion,
        string? remoteDigest)
    {
        string local = localDigest ?? "";
        string remote = remoteDigest ?? "";

        // A marker written for a different account, or for a different deployment, is not
        // evidence about this one. Signing in as somebody else must not let their version
        // number vouch for our document, so the marker is dropped whole - which puts this
        // installation back into the "never confirmed" case below, where the cloud wins.
        bool markerApplies = !string.IsNullOrEmpty(accountKey) &&
            string.Equals(accountKey, confirmedAccount, StringComparison.Ordinal);

        int confirmedAt = markerApplies ? confirmedVersion : 0;
        string confirmed = markerApplies ? confirmedDigest ?? "" : "";

        // Both sides are holding the same bytes. Nothing to move in either direction; at
        // most the marker is behind, which is worth writing because it is what stops the
        // NEXT run from mistaking this machine for one holding an unsent change.
        if (remoteVersion > 0 && local.Length > 0 && string.Equals(local, remote, StringComparison.Ordinal))
        {
            return confirmedAt == remoteVersion && string.Equals(confirmed, remote, StringComparison.Ordinal)
                ? new ModlightSyncPlan(ModlightSyncAction.Nothing, remoteVersion, "already in step")
                : new ModlightSyncPlan(ModlightSyncAction.Confirm, remoteVersion, "same document, marker behind");
        }

        // Nothing is stored for this account. Somebody has to go first and there is no copy
        // to lose, so whatever this machine holds becomes the first one. The expected
        // version of 0 is an assertion rather than a formality: it means "and there had
        // better still be nothing", so two machines seeding at the same moment end with one
        // refused instead of one silently overwritten.
        if (remoteVersion == 0)
        {
            return new ModlightSyncPlan(ModlightSyncAction.Seed, 0, "nothing stored for this account");
        }

        // THE FRESH-INSTALL GUARD, and the reason this function is worth its own file.
        //
        // An installation that has never had a sync confirmed for this account cannot tell
        // "settings somebody chose" from "the defaults the installer wrote", because it has
        // no marker to compare against. A machine in that state must never push: the first
        // launch after an install would otherwise replace a configuration built up over
        // months with a factory default, and the person would watch their hotkeys disappear
        // on every one of their machines at once.
        //
        // So the cloud wins outright, whatever this machine is holding. The cost is that a
        // configuration imported from ShareX on a machine that has never synced is
        // superseded rather than uploaded - and that is the right way round, because the
        // local file is still on disk and can be imported again, while the cloud copy this
        // would have overwritten was the only one of itself.
        if (confirmedAt == 0)
        {
            return new ModlightSyncPlan(ModlightSyncAction.Adopt, remoteVersion, "this installation has never been confirmed");
        }

        // Clean: everything this machine holds was acknowledged by the server, so it is
        // holding no change of its own. Whatever the server has now is newer by
        // construction, and adopting it loses nothing.
        if (string.Equals(local, confirmed, StringComparison.Ordinal))
        {
            return new ModlightSyncPlan(ModlightSyncAction.Adopt, remoteVersion, "no local change, server moved");
        }

        // Both sides changed. This machine wins, and the reason is asymmetry of loss:
        // "dirty" means a person sat at THIS keyboard and changed something that has never
        // left the machine, so it exists in exactly one place and adopting would destroy
        // it. What it replaces was pushed by some installation, which still holds it and
        // can push it back. The superseded document is written to the backup folder before
        // the push goes out, so even that side is recoverable.
        //
        // The expected version is the one just read, not the one we confirmed: this is a
        // deliberate replacement of what we have SEEN, and it must still be refused if a
        // third machine writes in between, because that write is one nobody here has looked
        // at.
        return new ModlightSyncPlan(ModlightSyncAction.Push, remoteVersion, "local change the server never confirmed");
    }

    /// <summary>
    /// The identity a marker belongs to: this deployment and this account, nothing else.
    /// </summary>
    /// <remarks>
    /// Host AND tag, because either alone is wrong. Two accounts on one server share a
    /// host, and one account reachable under two hostnames is two separate rows as far as
    /// the settings endpoint is concerned. The separator cannot occur in either part, so no
    /// two different pairs can collide on one key.
    /// </remarks>
    public static string AccountKey(string? host, string? tag)
    {
        return (host ?? "") + "\n" + (tag ?? "");
    }
}
