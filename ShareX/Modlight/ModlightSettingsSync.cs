#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShareX.HelpersLib;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// This installation's configuration, kept in step with the account's copy.
/// </summary>
/// <remarks>
/// <para>
/// The requirement is the owner's (docs/decyzje-modlight-app.md, "Ustawienia w chmurze"):
/// the configuration - hotkeys above all - belongs to the ACCOUNT, so signing in on a new
/// machine puts everything back where it was. The endpoint has existed since Round 11
/// (<c>GET|PUT /api/client-settings</c>, an opaque blob with a size cap and a version for
/// conflict detection); what was missing was anything that actually used it.
/// </para>
/// <para>
/// RUNS AT THREE MOMENTS, for three different reasons. On sign-in, because that is when a
/// new machine has an account to restore from. On a timer, because the other machine's
/// change is a fact this one has no way of hearing about. And a debounced run when the
/// configuration is saved, because a change nobody uploads is a change that is one power
/// cut away from not existing - debounced, because a settings window writes on every
/// keystroke and a PUT per keystroke is a denial of service somebody pays for.
/// </para>
/// <para>
/// WHICH SIDE WINS is decided by <see cref="ModlightSettingsPlan"/>, from the marker the
/// server last confirmed rather than from any clock. See that file; the rule that matters
/// most is that an installation which has never been confirmed cannot push, so a fresh
/// install's factory defaults can never replace a configuration somebody built up.
/// </para>
/// <para>
/// WHAT TRAVELS is decided by <see cref="ModlightSettingsDocument"/>, and it is an
/// allowlist. No credential is in it: not the API token, not the uploader credentials, not
/// the AI provider keys, not the proxy password, and not the YouTube cookie jar - which is
/// not in a config file at all.
/// </para>
/// </remarks>
public static class ModlightSettingsSync
{
    /// <summary>The client discriminator the endpoint stores this row under.</summary>
    private const string ClientKind = "desktop";

    /// <summary>Long enough for a cold server, short enough that nobody wonders.</summary>
    private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long after a configuration save to send it.
    /// </summary>
    /// <remarks>
    /// The window is restarted by every save, so a person working through the settings
    /// window produces one upload after they stop, not one per control they touch. Twenty
    /// seconds is comfortably longer than the pauses inside an editing session and
    /// comfortably shorter than the gap before somebody walks away from the machine.
    /// </remarks>
    private static readonly TimeSpan debounceDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The periodic verification.
    /// </summary>
    /// <remarks>
    /// This is the half that catches the OTHER machine's change, so the interval is a
    /// statement about how stale this machine may be: a quarter of an hour is short enough
    /// that a hotkey added on the laptop is on the desktop by the time somebody walks over
    /// to it, and long enough that a machine left running all week costs the server a few
    /// hundred conditional reads rather than a few hundred thousand.
    /// </remarks>
    private static readonly TimeSpan interval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The first run, deliberately not immediate.
    /// </summary>
    /// <remarks>
    /// Startup is the busiest moment this process has - config files, hotkey registration,
    /// the tray icon, the update check - and none of it should queue behind a network call.
    /// Nothing is lost by waiting: the sign-in path kicks its own run, so the case that
    /// actually needs to be fast (a machine that has just been signed in to) does not wait
    /// for this at all.
    /// </remarks>
    private static readonly TimeSpan startupDelay = TimeSpan.FromSeconds(45);

    private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

    private static Timer? heartbeat;
    private static Timer? debounce;
    private static int started;

    /// <summary>Depth of "we are writing the config ourselves", so an apply cannot nudge itself.</summary>
    private static int applying;

    /// <summary>
    /// Set when the server has told us this credential may not do this.
    /// </summary>
    /// <remarks>
    /// A 403 is not a transient failure; the token's scope will be the same in fifteen
    /// minutes. Retrying it on a timer is a request per quarter hour, for ever, that can
    /// only ever be refused - so the loop stops and a sign-in starts it again, because a
    /// sign-in is the one event that can produce a different answer.
    /// </remarks>
    private static bool refused;

    /// <summary>Whether the "your configuration is bigger than the server accepts" line has been said.</summary>
    private static bool oversizeReported;

    /// <summary>
    /// Begin syncing. Safe to call more than once; only the first call does anything.
    /// </summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1)
        {
            return;
        }

        heartbeat = new Timer(_ => Kick("timer"), null, startupDelay, interval);
        debounce = new Timer(_ => Kick("config saved"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The configuration was written to disk. Send it, once the writing has stopped.
    /// </summary>
    /// <remarks>
    /// Hung off the save calls rather than off a change event because there is no change
    /// event: <see cref="ApplicationConfig"/> is a bag of public fields. A save is the
    /// closest thing to "something actually changed" this codebase has, and it is not a
    /// bad one - every path that changes a setting ends in one.
    ///
    /// A save that only moved the marker still nudges, and that is harmless: the marker is
    /// not in the document, so the digest has not moved and the run ends after one read.
    /// </remarks>
    public static void Nudge()
    {
        if (started == 0 || refused || Volatile.Read(ref applying) > 0)
        {
            return;
        }

        try
        {
            debounce?.Change(debounceDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>A token has just landed. Restore this machine from the account, now.</summary>
    public static void SignedIn()
    {
        refused = false;
        Start();

        // Realigned so the next periodic verification is a whole interval after the run
        // below, rather than landing seconds later on whatever schedule the old timer was
        // on. Holding the reference matters for more than that: a Timer nothing refers to
        // is collectable, and a collected timer stops firing.
        heartbeat?.Change(interval, interval);

        Kick("signed in");
    }

    private static void Kick(string reason)
    {
        _ = RunAsync(reason);
    }

    private static async Task RunAsync(string reason)
    {
        // Non-blocking: a run already under way will see anything this one would have,
        // because it reads the configuration when it gets there rather than when it was
        // asked. Queueing a second one would only pile up timer ticks behind a slow server.
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await SyncAsync(reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // Settings are not worth a dialog, exactly as the skin is not. The machine
            // keeps what it has and the next tick tries again.
            DebugHelper.WriteException(exception, "Modlight settings sync failed");
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task SyncAsync(string reason, CancellationToken cancellationToken)
    {
        ModlightAccount? account = ModlightSignIn.Current;
        ApplicationConfig? settings = Program.Settings;

        if (account == null || settings == null || refused)
        {
            return;
        }

        JObject? local = ModlightSettingsDocument.Build();

        if (local == null)
        {
            return;
        }

        CloudDocument? remote = await SendAsync(HttpMethod.Get, account, null, 0, cancellationToken).ConfigureAwait(false);

        if (remote == null)
        {
            return;
        }

        ModlightCloudSettings marker = settings.CloudSettings ??= new ModlightCloudSettings();
        string accountKey = ModlightSettingsPlan.AccountKey(account.Host, account.Tag);
        string localDigest = ModlightSettingsDocument.Digest(local);

        SchemaVerdict verdict = ReadSchema(remote);

        if (verdict == SchemaVerdict.Unknown)
        {
            // Written by a client this one does not understand. Adopting would apply
            // fields it cannot read; pushing would destroy that client's configuration on
            // every machine on the account. Doing nothing is the only move that cannot
            // lose anything, and it resolves itself when this installation is updated.
            DebugHelper.WriteLine("Modlight settings sync: the server holds a document this build does not understand. Nothing done.");
            return;
        }

        ModlightSyncPlan plan = verdict == SchemaVerdict.Empty
            // A stored row with nothing in it - an interrupted first write, or a row left
            // by something that is not this client. There is no configuration in it to
            // lose, so it is replaced rather than treated as a document to reason about.
            // (Version 0 is the other thing entirely and is not here: see ReadSchema.)
            ? new ModlightSyncPlan(ModlightSyncAction.Push, remote.Version, "the stored row holds no document")
            : ModlightSettingsPlan.Decide(
                accountKey,
                marker.Account,
                marker.Version,
                marker.Digest,
                localDigest,
                remote.Version,
                ModlightSettingsDocument.Digest(remote.Settings));

        DebugHelper.WriteLine($"Modlight settings sync ({reason}): {plan}, server v{remote.Version}.");

        switch (plan.Action)
        {
            case ModlightSyncAction.Nothing:
                return;

            case ModlightSyncAction.Confirm:
                Remember(marker, accountKey, remote.Version, localDigest);
                return;

            case ModlightSyncAction.Adopt:
                Backup(local, "local");

                // The marker moves ONLY if the document was really applied. An adopt that
                // stood down - because somebody has the settings window open - would
                // otherwise be written down as "the server's document is what this machine
                // holds", and the very next run would read this machine as dirty and push
                // its unchanged settings over the ones it had just declined to take.
                if (!await AdoptAsync(remote.Settings).ConfigureAwait(false))
                {
                    return;
                }

                // The digest of what the SERVER confirmed, not of what this machine ended
                // up holding. If applying was lossy - an older build meeting a document
                // with a field it does not carry - the next run sees a machine holding
                // something the server has not confirmed, and pushes. Recording the
                // post-apply digest instead would make this machine adopt the same
                // document again on every tick, for ever.
                Remember(marker, accountKey, remote.Version, ModlightSettingsDocument.Digest(remote.Settings));
                return;

            case ModlightSyncAction.Seed:
            case ModlightSyncAction.Push:
                if (plan.Action == ModlightSyncAction.Push && verdict == SchemaVerdict.Known)
                {
                    // The document about to be replaced, kept where a person can get it
                    // back from. This is the one branch that discards somebody's work.
                    Backup(remote.Settings, "server");
                }

                int length = ModlightSettingsDocument.CanonicalLength(local);

                if (remote.LimitBytes > 0 && length > remote.LimitBytes)
                {
                    // Not sent, and not treated as a refusal either: reading is still
                    // worth doing, so this machine can still ADOPT what the account holds
                    // even though it cannot contribute. Said once rather than every
                    // quarter of an hour, because nothing about it will have changed.
                    if (!oversizeReported)
                    {
                        oversizeReported = true;
                        DebugHelper.WriteLine($"Modlight settings sync: the configuration is {length} bytes and the server accepts {remote.LimitBytes}. Not sent.");
                    }

                    return;
                }

                oversizeReported = false;

                CloudDocument? saved = await SendAsync(HttpMethod.Put, account, local, plan.Version, cancellationToken).ConfigureAwait(false);

                if (saved == null)
                {
                    return;
                }

                if (saved.Conflict)
                {
                    // Somebody wrote between our read and our write. Deliberately not
                    // retried inside this run: the plan was made against a document that
                    // is now out of date, so the next run reads the new one and decides
                    // again with the same rules rather than pushing harder.
                    DebugHelper.WriteLine($"Modlight settings sync: another machine wrote first (now v{saved.Version}). Deciding again next time.");
                    return;
                }

                Remember(marker, accountKey, saved.Version, localDigest);
                return;
        }
    }

    /// <summary>Write down what the server confirmed, and keep it across restarts.</summary>
    private static void Remember(ModlightCloudSettings marker, string accountKey, int version, string digest)
    {
        marker.Account = accountKey;
        marker.Version = version;
        marker.Digest = digest;

        Volatile.Write(ref applying, Volatile.Read(ref applying) + 1);

        try
        {
            SettingManager.SaveApplicationConfigAsync();
        }
        finally
        {
            Volatile.Write(ref applying, Volatile.Read(ref applying) - 1);
        }
    }

    private enum SchemaVerdict
    {
        /// <summary>Nothing is stored for this account at all.</summary>
        Missing,
        /// <summary>A document this build can read.</summary>
        Known,
        /// <summary>A row exists, and holds no document.</summary>
        Empty,
        /// <summary>A document written by something else, or by a later build.</summary>
        Unknown
    }

    private static SchemaVerdict ReadSchema(CloudDocument remote)
    {
        // Deliberately distinct from Empty, and handed to the planner rather than
        // shortcut here: "nobody has ever written" is a case the planner has a rule and a
        // test for, and shortcutting it would leave that rule unreachable in the real
        // program while the test went on passing.
        if (remote.Version == 0)
        {
            return SchemaVerdict.Missing;
        }

        if (!remote.Settings.HasValues)
        {
            return SchemaVerdict.Empty;
        }

        JToken? schema = remote.Settings["schema"];

        if (schema == null || schema.Type != JTokenType.Integer)
        {
            return SchemaVerdict.Unknown;
        }

        return (int)schema <= ModlightSettingsDocument.Schema ? SchemaVerdict.Known : SchemaVerdict.Unknown;
    }

    /// <summary>
    /// Put the server's document onto this machine.
    /// </summary>
    /// <remarks>
    /// On the UI thread, and not because of a data race: registering a system hotkey binds
    /// it to the thread that owns the window receiving <c>WM_HOTKEY</c>, so doing this from
    /// a timer thread would register the account's hotkeys to a thread that is about to
    /// end. Before the UI exists there is no hotkey manager to talk to and the work is
    /// pure data, so it runs where it stands.
    /// </remarks>
    /// <returns>True when the document was actually applied.</returns>
    private static async Task<bool> AdoptAsync(JObject document)
    {
        if (Application.Current == null)
        {
            return Adopt(document);
        }

        return await Dispatcher.UIThread.InvokeAsync(() => Adopt(document));
    }

    private static bool Adopt(JObject document)
    {
        if (IsEditorOpen())
        {
            // Somebody is looking at these settings right now. Swapping the objects
            // underneath an open editor gives them a window full of values that are no
            // longer the ones being edited, and - for the hotkey list - an editor holding
            // rows that have just been unregistered. The next run will do it.
            DebugHelper.WriteLine("Modlight settings sync: a settings window is open, so nothing was replaced.");
            return false;
        }

        Volatile.Write(ref applying, Volatile.Read(ref applying) + 1);

        try
        {
            bool changed = ModlightSettingsDocument.ApplyApp(document["app"] as JObject);

            List<HotkeySettings>? hotkeys = ModlightSettingsDocument.ReadHotkeys(document["hotkeys"] as JObject);

            if (hotkeys != null && Program.HotkeysConfig != null)
            {
                Program.HotkeysConfig.Hotkeys = hotkeys;

                // Never shows the failed-hotkey dialog. A key the account uses may already
                // belong to another program on THIS machine, and a modal appearing out of
                // a background sync is a dialog nobody asked for, about a change nobody
                // made here. The Capture tab reports failed hotkeys where the fix is.
                Program.HotkeyManager?.UpdateHotkeys(hotkeys, false);

                // Rebuilt because both sources it reads - the default task settings and
                // every hotkey's - are different objects now. Watch folders left bound to
                // the superseded objects would keep watching, and write into a
                // configuration nothing else reads.
                Program.WatchFolderManager?.UpdateWatchFolders();

                changed = true;
            }

            if (changed)
            {
                SettingManager.SaveApplicationConfigAsync();
                SettingManager.SaveHotkeysConfigAsync();
                DebugHelper.WriteLine("Modlight settings sync: adopted the account's settings.");
            }

            return changed;
        }
        finally
        {
            Volatile.Write(ref applying, Volatile.Read(ref applying) - 1);
        }
    }

    /// <summary>Whether a window that edits any of this is open.</summary>
    private static bool IsEditorOpen()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return false;
        }

        foreach (Avalonia.Controls.Window window in desktop.Windows)
        {
            if (window is SettingsWindow)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keep a copy of a document that is about to be superseded.
    /// </summary>
    /// <remarks>
    /// One file per side, overwritten, rather than a dated series: the value of this is
    /// "the thing that just disappeared is still somewhere", which the most recent copy
    /// answers completely, and a growing pile of configuration files in the backup folder
    /// is a cost with no matching benefit. Failure is ignored on purpose - a backup that
    /// cannot be written must not stop the sync it was insuring.
    /// </remarks>
    private static void Backup(JObject document, string side)
    {
        try
        {
            string folder = SettingManager.BackupFolder;

            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, $"CloudSettings-superseded-{side}.json"),
                document.ToString(Formatting.Indented),
                new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }

    /// <summary>What one call to the endpoint answered.</summary>
    private sealed class CloudDocument
    {
        public int Version;
        public JObject Settings = new JObject();
        public int LimitBytes;

        /// <summary>The write was refused because somebody else wrote first.</summary>
        public bool Conflict;
    }

    /// <summary>
    /// One call to <c>/api/client-settings</c>.
    /// </summary>
    /// <remarks>
    /// The token goes in the <c>x-api-key</c> HEADER, never in the request line, for the
    /// reason it does everywhere else in this client: a query string is written to the
    /// reverse proxy's access log.
    ///
    /// Null means "nothing was learned" - unreachable, refused, or an answer that did not
    /// parse - and every caller treats that as "keep what we have". A 409 is different and
    /// is NOT null: it carries the winning document, which is information.
    /// </remarks>
    private static async Task<CloudDocument?> SendAsync(
        HttpMethod method,
        ModlightAccount account,
        JObject? body,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);

        using HttpRequestMessage request = new HttpRequestMessage(method, account.Host + "/api/client-settings?client=" + ClientKind);
        request.Headers.TryAddWithoutValidation("x-api-key", account.Token);

        if (body != null)
        {
            JObject envelope = new JObject
            {
                // The version this write asserts it is replacing. Never omitted: the
                // endpoint reads a missing version as "take this, whatever was there",
                // which is precisely the silent clobber the whole marker exists to avoid.
                ["version"] = expectedVersion,
                ["settings"] = body
            };

            request.Content = new StringContent(envelope.ToString(Formatting.None), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await HttpClientFactory.Create().SendAsync(request, deadline.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Either the token was revoked or it is not wide enough for this. Both are
            // answers that will not change until somebody signs in again.
            DebugHelper.WriteLine($"Modlight settings sync: the server refused this token ({(int)response.StatusCode}). Stopped until the next sign-in.");
            refused = true;
            return null;
        }

        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            DebugHelper.WriteLine("Modlight settings sync: the server refused the configuration as too large. Stopped.");
            refused = true;
            return null;
        }

        bool conflict = response.StatusCode == HttpStatusCode.Conflict;

        if (!response.IsSuccessStatusCode && !conflict)
        {
            DebugHelper.WriteLine($"Modlight settings sync: the server answered {(int)response.StatusCode}.");
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        JObject answer = JObject.Parse(json);

        return new CloudDocument
        {
            Version = (int?)answer["version"] ?? 0,
            Settings = answer["settings"] as JObject ?? new JObject(),
            LimitBytes = (int?)answer["limitBytes"] ?? 0,
            Conflict = conflict
        };
    }
}
