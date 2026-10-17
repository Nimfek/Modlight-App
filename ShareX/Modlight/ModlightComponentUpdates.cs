#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>What became of one attempt to bring a component up to date.</summary>
public enum ModlightComponentUpdateOutcome
{
    /// <summary>A newer build is installed now.</summary>
    Updated,

    /// <summary>The newest published build is already the one here.</summary>
    AlreadyCurrent,

    /// <summary>The check did not complete. Offline, blocked, or an answer we would not act on.</summary>
    CheckFailed,

    /// <summary>There is a newer build and it could not be put in place.</summary>
    InstallFailed,

    /// <summary>This component does not follow a publisher, so there is nothing to check.</summary>
    NotTracked,

    /// <summary>It is not installed, or the copy in use is not one we may replace.</summary>
    NotOurs,

    /// <summary>What is on the disk could not be identified, so nothing was compared.</summary>
    VersionUnknown,

    Cancelled
}

/// <summary>The outcome, plus the version it is about.</summary>
public readonly record struct ModlightComponentUpdateResult(
    ModlightComponentUpdateOutcome Outcome, string Version, string Detail)
{
    public bool Installed => Outcome == ModlightComponentUpdateOutcome.Updated;
}

/// <summary>
/// Keeping yt-dlp current, and giving the rest of the app a way to say "now".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why only this one.</b> ffmpeg can sit still for a year; yt-dlp cannot sit still for a
/// month, because the hundred sites it reads keep changing underneath it. The component
/// system as it stood installed a file once and never looked at it again -
/// <c>IsInstalled</c> asked whether the file exists and nothing anywhere asked which build
/// it was - so a pinned yt-dlp went stale and there was no route in the entire application
/// to a newer one. Not the first run, not Remove-then-Install, nothing. That guaranteed the
/// failure the owner met: TikTok refusing outright, and a signed-in YouTube download
/// quietly arriving as 360p with no error at all.
/// </para>
/// <para>
/// <b>The schedule is a day.</b> Stable yt-dlp releases land every few weeks, but the fix
/// for a site that broke this morning lands within days of it breaking, and the tool's own
/// advice to everyone who runs it by hand is <c>yt-dlp -U</c> - effectively a check per
/// run. A day is the same order of freshness for one request to api.github.com every
/// twenty-four hours, against an unauthenticated budget of sixty an hour. Checking on every
/// launch would buy nothing on a machine that is launched ten times a day; checking weekly
/// would put us back inside the window the owner just lost 46 days to.
/// </para>
/// <para>
/// <b>Nothing here is ever in front of a download.</b> The scheduled check runs from a
/// background task started well after launch, and the Download tab never awaits it. A
/// machine that is offline, behind a captive portal or on a proxy that answers everything
/// with a login page downloads videos exactly as it did before; the check writes a line to
/// the log and gives up until the backoff has passed.
/// </para>
/// <para>
/// <b>What is fetched is checked.</b> The build comes from
/// <see cref="ModlightToolClient"/>, which will not hand back anything without a digest
/// published by the project, and it is installed through the same
/// <see cref="ModlightComponents"/> path as every other component: staged in a hidden
/// folder under a <c>.part</c> name nothing searches for, hashed, and only then moved. A
/// download that stops halfway leaves a <c>.part</c> file, and a <c>.part</c> file is never
/// a tool - which is the whole reason the staging directory exists.
/// </para>
/// </remarks>
public static class ModlightComponentUpdates
{
    /// <summary>How long a check is good for. See the class remarks for why a day.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a failed check waits before it is worth trying again.
    /// </summary>
    /// <remarks>
    /// In memory, not in the settings file, and deliberately: a failure must not consume
    /// the day's check, or a laptop that was opened on a train would go a further day
    /// without one. Half an hour is short enough that a connection coming back is noticed
    /// during the same session and long enough that a captive portal is not being asked the
    /// same question every time a window opens.
    /// </remarks>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long after launch the first check happens.
    /// </summary>
    /// <remarks>
    /// Modlight starts with Windows. Whatever else is happening in the first half minute of
    /// somebody's session - a browser, a mail client, a sync client, all of them fighting
    /// over the same disk and the same line - a version check is the least urgent thing in
    /// the room and can wait for its turn.
    /// </remarks>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    /// <summary>Long enough for a cold PyInstaller binary to unpack itself and print one line.</summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);

    /// <summary>One check at a time, whoever asked.</summary>
    private static readonly SemaphoreSlim gate = new(1, 1);

    private static readonly object cacheLock = new();

    /// <summary>What each installed binary answered, against the file it answered for.</summary>
    private static readonly Dictionary<string, string> versionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The newest build each component's publisher had, as of the last check this run.</summary>
    private static readonly Dictionary<string, string> latestKnown = new(StringComparer.OrdinalIgnoreCase);

    private static DateTime lastAttemptUtc = DateTime.MinValue;

    private static bool scheduled;

    /// <summary>Whether a newer build may be installed without being asked.</summary>
    /// <remarks>
    /// <para>
    /// On by default, which is the decision worth defending. Three things make it
    /// defensible and each of them matters: the artefact is eighteen megabytes, not a
    /// gigabyte; it is refused unless the project's own published digest matches what
    /// arrived; and the failure it prevents is a SILENT one - a stale yt-dlp does not say
    /// it is stale, it says "Unexpected response from webpage request", or worse, hands
    /// over a 360p file and reports success.
    /// </para>
    /// <para>
    /// The switch exists for the connection where eighteen megabytes is a decision rather
    /// than a detail. Turning it off does not turn off the check: the settings page still
    /// says a newer build exists, and the button beside it still works.
    /// </para>
    /// </remarks>
    public static bool AutoUpdate
    {
        get => Program.Settings?.ExternalComponentsAutoUpdate ?? true;
        set
        {
            ApplicationConfig? settings = Program.Settings;

            if (settings == null || settings.ExternalComponentsAutoUpdate == value)
            {
                return;
            }

            settings.ExternalComponentsAutoUpdate = value;
            SettingManager.SaveApplicationConfigAsync();
        }
    }

    /// <summary>When the last check that actually completed was, or null.</summary>
    public static DateTime? LastCheckUtc
    {
        get
        {
            DateTime stamp = Program.Settings?.ExternalComponentsLastUpdateCheck ?? DateTime.MinValue;
            return stamp == DateTime.MinValue ? null : stamp;
        }
    }

    /// <summary>True when this component has a publisher to follow and a copy we may replace.</summary>
    public static bool CanUpdate(ModlightComponent component) =>
        component.TracksUpdates && ModlightComponents.IsInstalled(component) &&
        ModlightComponents.IsManaged(component);

    /// <summary>The newest build the publisher had when we last asked, or empty.</summary>
    /// <remarks>
    /// Memory only, never the settings file. A remembered "newer version available" would
    /// outlive the update that answered it and greet somebody with an offer that is already
    /// done, which is worse than an empty line.
    /// </remarks>
    public static string LatestKnown(ModlightComponent component)
    {
        lock (cacheLock)
        {
            return latestKnown.TryGetValue(component.Id, out string? version) ? version : "";
        }
    }

    /// <summary>Start the daily check, once per run.</summary>
    /// <remarks>
    /// Fire and forget by design: this is called from startup, and a startup that waits on
    /// a network request is a startup that hangs when the network is a hotel wifi page.
    /// </remarks>
    public static void StartScheduledCheck()
    {
        if (scheduled)
        {
            return;
        }

        scheduled = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupDelay);
                await RunScheduledAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception, "Components: the scheduled update check failed.");
            }
        });
    }

    /// <summary>
    /// Ask now, if it is time to ask.
    /// </summary>
    /// <remarks>
    /// Called when the components page is opened, so somebody looking at the list is
    /// looking at something current - and gated the same way as the timer, so opening it
    /// ten times in an afternoon is one request, not ten.
    /// </remarks>
    public static Task RunScheduledIfDueAsync(CancellationToken cancellationToken) =>
        RunScheduledAsync(cancellationToken);

    /// <summary>
    /// Ask the publisher, and install a newer build if there is one.
    /// </summary>
    /// <param name="onlyWhenNewer">
    /// True for anything automatic. It means a build is installed only when this code can
    /// read the version on the disk AND the published one is strictly newer - so an
    /// unreadable binary, a clock problem or a publisher who re-tagged a release can never
    /// turn into a download nobody asked for. The manual button passes false, because a
    /// person who presses "update now" on a copy we cannot identify is asking for exactly
    /// the newest published build and is entitled to get it.
    /// </param>
    public static async Task<ModlightComponentUpdateResult> UpdateAsync(ModlightComponent component,
        IProgress<ModlightComponentProgress>? progress, bool onlyWhenNewer, CancellationToken cancellationToken)
    {
        if (!component.TracksUpdates)
        {
            return new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.NotTracked, "", "");
        }

        if (!ModlightComponents.IsInstalled(component))
        {
            // Installing a component nobody has asked for is InstallAsync's business, and
            // it fetches the pinned build - the one a human looked at.
            return new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.NotOurs, "", "");
        }

        if (!ModlightComponents.IsManaged(component))
        {
            // A copy beside the executable, put there by the installer into a directory
            // this process cannot write to - and one that would go on winning the search
            // even if a newer build were placed in the managed folder.
            return new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.NotOurs,
                "", ModlightComponents.InstalledPath(component));
        }

        string installed = await InstalledVersionAsync(component, cancellationToken);

        ModlightToolFetchResult fetch = await CheckAsync(component, cancellationToken);

        if (fetch.Status != ModlightToolFetchStatus.Ok || fetch.Build == null)
        {
            return new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.CheckFailed,
                installed, fetch.Detail);
        }

        ModlightToolBuild build = fetch.Build;

        if (installed.Length == 0)
        {
            if (onlyWhenNewer)
            {
                DebugHelper.WriteLine("Components: " + component.Id +
                    " did not say what version it is, so nothing was replaced automatically.");

                return new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.VersionUnknown,
                    "", build.VersionText);
            }
        }
        else if (!ModlightToolRelease.IsNewer(build.VersionText, installed))
        {
            return new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.AlreadyCurrent, installed, "");
        }

        DebugHelper.WriteLine("Components: updating " + component.Id + " from " +
            (installed.Length > 0 ? installed : "an unknown build") + " to " + build.VersionText +
            ", digest from " + build.DigestSource + ".");

        ModlightInstallResult install = await ModlightComponents.ReplaceAsync(component,
            new ModlightComponentSource(build.Url, build.Sha256, build.Size, build.VersionText),
            progress, cancellationToken);

        if (install.Outcome == ModlightInstallOutcome.Cancelled)
        {
            return new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.Cancelled, installed, "");
        }

        if (!install.Success)
        {
            return new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.InstallFailed,
                installed, install.Message);
        }

        ForgetVersions();

        // The tab caches which yt-dlp it found, and the next run has to use the one that
        // was just written rather than the path that was resolved before it existed.
        YtDlpTool.Rescan();

        // And it caches how old that yt-dlp is, which has just stopped being true. Left
        // alone, the next failed download would be told the build it is running is behind -
        // the very sentence this whole path exists to stop being said when it is false.
        YtDlpVersionCheck.Forget();

        return new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.Updated, build.VersionText, "");
    }

    /// <summary>Ask the publisher what the newest build is, and remember the answer.</summary>
    public static async Task<ModlightToolFetchResult> CheckAsync(ModlightComponent component,
        CancellationToken cancellationToken)
    {
        if (!component.TracksUpdates)
        {
            return ModlightToolFetchResult.Malformed("not tracked");
        }

        ModlightToolFetchResult fetch = await ModlightToolClient.FetchAsync(HttpClientFactory.Create(),
            component.UpdateRepository!, component.UpdateAsset!, component.UpdateChecksums ?? "",
            cancellationToken);

        if (fetch.Status == ModlightToolFetchStatus.Ok && fetch.Build != null)
        {
            lock (cacheLock)
            {
                latestKnown[component.Id] = fetch.Build.VersionText;
            }
        }
        else
        {
            DebugHelper.WriteLine("Components: could not check " + component.Id + " (" + fetch.Detail + ").");
        }

        return fetch;
    }

    /// <summary>
    /// Which build is actually on this PC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By asking the binary, which is the only answer that stays true. The catalogue
    /// constant stops being the truth the first time an update lands, and a version written
    /// into the settings file stops being the truth the moment somebody drops their own
    /// build into the folder - and both of them fail in the same direction, by claiming a
    /// newer version than is there.
    /// </para>
    /// <para>
    /// Cached against the file itself - path, length and write time - so the settings page
    /// does not start a process every time it is drawn, and so the cache cannot survive the
    /// file being replaced underneath it. Anything the binary prints that is not a version
    /// is treated as no answer at all: a banner line accepted as a version would be a
    /// version that never compares as older than anything.
    /// </para>
    /// </remarks>
    public static async Task<string> InstalledVersionAsync(ModlightComponent component,
        CancellationToken cancellationToken)
    {
        if (component.VersionArguments.Count == 0)
        {
            // Nothing to ask, so the pin is the answer: these components only ever arrive
            // through the catalogue.
            return ModlightComponents.IsInstalled(component) ? component.Version : "";
        }

        string path = ModlightComponents.InstalledPath(component);

        if (path.Length == 0)
        {
            return "";
        }

        string stamp;

        try
        {
            FileInfo info = new(path);
            stamp = path + "|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return "";
        }

        lock (cacheLock)
        {
            if (versionCache.TryGetValue(stamp, out string? cached))
            {
                return cached;
            }
        }

        string version = await AskAsync(path, component.VersionArguments, cancellationToken);

        lock (cacheLock)
        {
            versionCache[stamp] = version;
        }

        return version;
    }

    /// <summary>Drop the remembered versions, because a file has just been replaced.</summary>
    private static void ForgetVersions()
    {
        // The stamp includes the write time, so the entry for the replaced file can never
        // be hit again - but leaving a growing dictionary of dead stamps behind would be
        // untidy for no reason, and there are four components in it.
        lock (cacheLock)
        {
            versionCache.Clear();
        }
    }

    private static async Task<string> AskAsync(string path, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessStartInfo info = new(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using Process child = new() { StartInfo = info };

            if (!child.Start())
            {
                return "";
            }

            // It dies with Modlight, like every other child this application starts. A
            // version check left running after the app is gone is a stray process nobody
            // will ever connect to anything.
            ChildProcessJob.Adopt(child);

            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(VersionTimeout);

            Task<string> output = child.StandardOutput.ReadToEndAsync(CancellationToken.None);

            try
            {
                await child.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception)
                {
                    DebugHelper.WriteException(exception);
                }

                return "";
            }

            if (child.ExitCode != 0)
            {
                return "";
            }

            foreach (string line in (await output).Split('\n'))
            {
                string candidate = line.Trim();

                if (candidate.Length > 0)
                {
                    return ModlightToolRelease.TryParseVersion(candidate, out _) ? candidate : "";
                }
            }

            return "";
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: could not ask " + path + " for its version.");
            return "";
        }
    }

    private static async Task RunScheduledAsync(CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            if (!IsDue())
            {
                return;
            }

            lastAttemptUtc = DateTime.UtcNow;

            bool checkedAny = false;

            foreach (ModlightComponent component in ModlightComponents.All)
            {
                if (!component.TracksUpdates || !ModlightComponents.IsWanted(component) ||
                    !ModlightComponents.IsInstalled(component))
                {
                    continue;
                }

                if (!AutoUpdate || !ModlightComponents.IsManaged(component))
                {
                    // Still ask, so the settings page can say a newer build exists and the
                    // button beside it means something. Only the installing is switched off.
                    ModlightToolFetchResult fetch = await CheckAsync(component, cancellationToken);
                    checkedAny |= fetch.Status == ModlightToolFetchStatus.Ok;
                    continue;
                }

                ModlightComponentUpdateResult result =
                    await UpdateAsync(component, null, onlyWhenNewer: true, cancellationToken);

                checkedAny |= result.Outcome != ModlightComponentUpdateOutcome.CheckFailed;

                DebugHelper.WriteLine("Components: scheduled check for " + component.Id + " says " +
                    result.Outcome + (result.Version.Length > 0 ? " (" + result.Version + ")" : "") + ".");
            }

            if (checkedAny)
            {
                Stamp();
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: the scheduled update check failed.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether it is time to ask again.</summary>
    /// <remarks>
    /// A stamp in the future is treated as due rather than trusted. It is what a clock that
    /// was wrong and has been corrected leaves behind, and the alternative reading - "not
    /// for another year" - is the one failure mode of a scheduled check that nobody would
    /// ever notice.
    /// </remarks>
    private static bool IsDue()
    {
        DateTime now = DateTime.UtcNow;

        if (now - lastAttemptUtc < FailureBackoff && lastAttemptUtc != DateTime.MinValue)
        {
            return false;
        }

        DateTime last = Program.Settings?.ExternalComponentsLastUpdateCheck ?? DateTime.MinValue;

        if (last == DateTime.MinValue || last > now)
        {
            return true;
        }

        return now - last >= CheckInterval;
    }

    /// <summary>Record that a check completed. Only ever called after one did.</summary>
    private static void Stamp()
    {
        ApplicationConfig? settings = Program.Settings;

        if (settings == null)
        {
            return;
        }

        settings.ExternalComponentsLastUpdateCheck = DateTime.UtcNow;
        SettingManager.SaveApplicationConfigAsync();
    }
}
