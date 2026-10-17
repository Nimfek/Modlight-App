#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Microsoft.Win32;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

public enum ModlightComponentStage
{
    Idle,

    /// <summary>Another component is being installed. Only one download runs at a time.</summary>
    Queued,
    Downloading,
    Verifying,
    Installing,
    Done
}

public readonly record struct ModlightComponentProgress(
    ModlightComponentStage Stage,
    long Received,
    long Total,
    double BytesPerSecond);

public enum ModlightInstallOutcome
{
    Installed,
    AlreadyInstalled,
    Cancelled,

    /// <summary>Nothing answered. Say what is missing and why, and stop.</summary>
    Offline,

    /// <summary>It arrived and it was not what was promised. Nothing was installed.</summary>
    ChecksumFailed,

    /// <summary>Not enough room on the disk for it.</summary>
    NoSpace,

    /// <summary>Downloaded, but unpacking or moving it into place failed.</summary>
    InstallFailed,

    /// <summary>The server said no, or the body stopped short.</summary>
    DownloadFailed,

    /// <summary>The URL was not https, so nothing was requested.</summary>
    Refused
}

public readonly record struct ModlightInstallResult(ModlightInstallOutcome Outcome, string Message)
{
    public bool Success => Outcome is ModlightInstallOutcome.Installed or ModlightInstallOutcome.AlreadyInstalled;
}

/// <summary>
/// Which bytes to fetch for a component: the pinned ones, or a newer published build.
/// </summary>
/// <remarks>
/// Split out of <see cref="ModlightComponent"/> the day yt-dlp started following its
/// publisher. Everything downstream - the space check, the resumable part file, the digest
/// gate, the move into place - takes this rather than reading the catalogue, so an update
/// and a first install are literally the same code path with a different three fields.
/// Two paths would have meant two places for the digest check to be forgotten in.
/// </remarks>
public readonly record struct ModlightComponentSource(string Url, string Sha256, long Size, string Version);

/// <summary>
/// The external components: what is installed, what the user asked for, and getting it here.
/// </summary>
/// <remarks>
/// <para>
/// Three features in this application depend on a file that is not part of it - screen
/// recording and GIFs need ffmpeg, the Download tab needs yt-dlp, the background remover
/// needs an ONNX model - and a fourth, the metadata tools, needs ExifTool. Until now every
/// one of them failed the same way: a message box naming a path that does not exist, and an
/// OK button. That is a dead end dressed as an error.
/// </para>
/// <para>
/// They are not bundled, and that is a decision rather than an omission: the update channel
/// ships the WHOLE installer for every patch, so a bundled gigabyte is not paid once, it is
/// paid on every update forever. So the installer asks which of them the user wants - all
/// ticked, each with a line saying what it is for - and this fetches them.
/// </para>
/// <para>
/// Where they land: <c>Documents\Modlight\Tools</c> and <c>Documents\Modlight\Models</c>,
/// which are already what YtDlpTool searches and what the background remover scans, and
/// which - unlike the installation directory under Program Files - a standard user can
/// actually write to. That is why an "install this now" button can work at all from a
/// running, unelevated application.
/// </para>
/// </remarks>
public static class ModlightComponents
{
    /// <summary>Where the installer leaves the components the user unticked.</summary>
    private const string SetupKeyPath = @"Software\Modlight\Setup";

    private const string SetupSkipValue = "SkipComponents";

    /// <summary>Staging directory, inside the destination folder so a finished file is a move, not a copy.</summary>
    private const string StagingFolderName = ".incoming";

    /// <summary>
    /// What a replaced file is renamed to on its way out.
    /// </summary>
    /// <remarks>
    /// Windows lets a running executable be renamed but not deleted - the image is held by
    /// handle, not by name - and that is the whole trick that lets yt-dlp be updated while
    /// a download is running: the old build carries on out of a file nobody will look for
    /// again, and the next run finds the new one. The suffix is deliberately not an
    /// extension anything searches for; <see cref="SweepRetired"/> clears these once the
    /// old process has let go.
    /// </remarks>
    private const string RetiredSuffix = ".old-";

    /// <summary>Headroom demanded on top of the download itself, for the unpack.</summary>
    private const long SpaceHeadroom = 64L * 1024 * 1024;

    /// <summary>
    /// How far this installation has been carried forward. 0 is a config written before
    /// components existed. See <see cref="MigrateSettings"/>.
    /// </summary>
    private const int CurrentComponentsVersion = 1;

    /// <summary>
    /// One download at a time, whoever asked. Two gigabyte-scale downloads racing each other
    /// over one line finish later than they would in sequence and make both progress bars lie
    /// about how long they have left.
    /// </summary>
    private static readonly SemaphoreSlim gate = new(1, 1);

    private static bool startupDone;

    /// <summary>True when this run consumed the installer's choices, i.e. it is the first run.</summary>
    public static bool IsFirstRunAfterSetup { get; private set; }

    public static IReadOnlyList<ModlightComponent> All => ModlightComponentCatalog.All;

    /// <summary>What the catalogue pins for this component: the bytes a first install fetches.</summary>
    public static ModlightComponentSource PinnedSource(ModlightComponent component) =>
        new ModlightComponentSource(component.Url, component.Sha256, component.DownloadSize, component.Version);

    #region Where things are

    public static string FolderFor(ModlightComponent component) =>
        component.Folder == ModlightComponentFolder.Models ? Program.ModelsFolder : Program.ToolsFolder;

    /// <summary>Where this component would go if it were installed now.</summary>
    public static string TargetPath(ModlightComponent component)
    {
        try
        {
            return Path.Combine(FolderFor(component), component.FileName);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The component's file, or empty when it is not on this PC.
    /// </summary>
    /// <remarks>
    /// Tools are looked up through <see cref="ExternalTools.Locate"/> so a copy the installer
    /// placed beside the executable counts as installed - it is the same file, and telling
    /// somebody to download ffmpeg when ffmpeg is already sitting next to Modlight.exe would
    /// be the application failing to notice its own contents.
    /// </remarks>
    public static string InstalledPath(ModlightComponent component)
    {
        if (component.Folder == ModlightComponentFolder.Tools)
        {
            string located = ExternalTools.Locate(component.FileName);

            if (Exists(located))
            {
                return located;
            }
        }

        // Also asked directly, and not only through ExternalTools, because that answers with
        // ManagedFolder - a static the application sets during startup. If anything ever
        // reaches this before that line runs, the failure would be silent and absurd: a
        // component this code has just installed itself, reported as missing.
        string target = TargetPath(component);
        return Exists(target) ? target : string.Empty;
    }

    public static bool IsInstalled(ModlightComponent component) => InstalledPath(component).Length > 0;

    public static bool IsInstalled(string id)
    {
        ModlightComponent? component = ModlightComponentCatalog.Find(id);
        return component != null && IsInstalled(component);
    }

    /// <summary>
    /// True when the copy in use is one we fetched, and can therefore be removed again.
    /// </summary>
    /// <remarks>
    /// A copy sitting beside the executable was put there by the installer, inside a
    /// directory this process usually cannot write to. Offering "Remove" for it would be
    /// offering something that fails.
    /// </remarks>
    public static bool IsManaged(ModlightComponent component)
    {
        string installed = InstalledPath(component);
        string target = TargetPath(component);

        return installed.Length > 0 && target.Length > 0 &&
            string.Equals(installed, target, StringComparison.OrdinalIgnoreCase);
    }

    #endregion Where things are

    #region What the user asked for

    /// <summary>
    /// Whether the user wants this component.
    /// </summary>
    /// <remarks>
    /// Stored as the list of components they said NO to, not the list they said yes to. That
    /// is what makes "all ticked by default" survive: a component added in a later release
    /// is not in anybody's declined list, so it starts out wanted, the way a new checkbox on
    /// the installer page would.
    /// </remarks>
    public static bool IsWanted(ModlightComponent component)
    {
        List<string>? declined = Program.Settings?.ExternalComponentsDeclined;

        return declined == null || !declined.Any(id => string.Equals(id, component.Id, StringComparison.OrdinalIgnoreCase));
    }

    public static void SetWanted(ModlightComponent component, bool wanted)
    {
        ApplicationConfig? settings = Program.Settings;

        if (settings == null)
        {
            return;
        }

        settings.ExternalComponentsDeclined ??= [];

        bool changed;

        if (wanted)
        {
            changed = settings.ExternalComponentsDeclined
                .RemoveAll(id => string.Equals(id, component.Id, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        else
        {
            changed = IsWanted(component);

            if (changed)
            {
                settings.ExternalComponentsDeclined.Add(component.Id);
            }
        }

        if (changed)
        {
            SettingManager.SaveApplicationConfigAsync();
        }
    }

    #endregion What the user asked for

    #region Startup

    /// <summary>
    /// Take the installer's answers, carry an old config forward, and tidy up after crashes.
    /// </summary>
    public static void OnStartup()
    {
        if (startupDone || Program.Settings == null)
        {
            return;
        }

        startupDone = true;

        try
        {
            IsFirstRunAfterSetup = ReadSetupChoices();
            MigrateSettings();
            SweepStaging();

            // The one moment nothing we replaced can still be running is the moment we have
            // only just started.
            foreach (ModlightComponent component in All)
            {
                SweepRetired(component);
            }

            OfferFirstRun();

            // After everything above, and on a timer of its own: yt-dlp is the one
            // component that goes stale on its own, and this is what stops it doing so
            // silently. It never blocks anything - see ModlightComponentUpdates.
            ModlightComponentUpdates.StartScheduledCheck();
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: startup failed.");
        }
    }

    /// <summary>
    /// On the first run after installation, fetch what was ticked in the installer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With a window rather than quietly in the background, and that is the whole argument
    /// for doing it this way round. The largest of these is a gigabyte; starting a gigabyte
    /// of somebody's connection with nothing on screen is the behaviour people install
    /// firewalls to catch. The window says what is being fetched, shows how far it has got
    /// and can be stopped, and stopping costs nothing because the next attempt resumes.
    /// </para>
    /// <para>
    /// Only on the first run. Later launches do nothing at all: a component that is missing
    /// then is offered by the feature that wants it, which is the moment the person actually
    /// cares, and the settings page lists all four whenever they want to look.
    /// </para>
    /// <para>
    /// Never on a silent start - Modlight launches with Windows, and a window appearing over
    /// whatever somebody is doing at login is not an acceptable way to ask.
    /// </para>
    /// </remarks>
    private static void OfferFirstRun()
    {
        if (!IsFirstRunAfterSetup || Program.SilentRun || Program.Settings.SilentRun ||
            WantedButMissing().Count == 0)
        {
            return;
        }

        ComponentsWindowIntegration.ShowAll(Strings.Components_FirstRun);
    }

    /// <summary>Components the user wants that are not here yet.</summary>
    public static IReadOnlyList<ModlightComponent> WantedButMissing() =>
        All.Where(component => IsWanted(component) && !IsInstalled(component)).ToArray();

    /// <summary>
    /// Read what the installer was told, apply it once, and delete it.
    /// </summary>
    /// <returns>True when there were choices to read, which is what makes this a first run.</returns>
    /// <remarks>
    /// The registry rather than a file in the personal folder, because at install time nobody
    /// knows where that folder will be: PersonalPath.cfg, a portable flag or a policy value
    /// can all move it, and the installer deliberately does not read any of them.
    /// </remarks>
    private static bool ReadSetupChoices()
    {
        string? skipped;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SetupKeyPath, writable: true);

            if (key == null)
            {
                return false;
            }

            skipped = key.GetValue(SetupSkipValue) as string;

            if (skipped == null)
            {
                return false;
            }

            // Applied once. Leaving it behind would mean a user who later ticks a component
            // in settings finds it unticked again after every restart.
            key.DeleteValue(SetupSkipValue, throwOnMissingValue: false);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: could not read the installer's choices.");
            return false;
        }

        ApplicationConfig settings = Program.Settings;
        settings.ExternalComponentsDeclined ??= [];

        foreach (string id in skipped.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ModlightComponent? component = ModlightComponentCatalog.Find(id);

            if (component != null && IsWanted(component))
            {
                settings.ExternalComponentsDeclined.Add(component.Id);
            }
        }

        DebugHelper.WriteLine("Components: the installer skipped [" + skipped + "].");
        SettingManager.SaveApplicationConfigAsync();
        return true;
    }

    /// <summary>
    /// Carry an existing configuration forward. Runs once per installation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one thing that can hide a component after it has been installed is a path the user
    /// already has saved. FFmpegOptions.OverrideCLIPath plus CLIPath wins over everything -
    /// it is checked before the folder beside the executable and before the managed folder -
    /// so somebody who once pointed Modlight at ShareX's ffmpeg.exe, or whose settings were
    /// imported from ShareX by the installer, would download ffmpeg here and still be told it
    /// is missing, because the setting still names a file that is not there.
    /// </para>
    /// <para>
    /// So a BROKEN override is cleared, once, and a working one is left completely alone. An
    /// override pointing at a file that exists is a choice somebody made and is none of this
    /// code's business; an override pointing at nothing is a leftover. The version stamp is
    /// what keeps this from happening a second time - if the user re-enters a path that is
    /// wrong today, that is theirs to keep.
    /// </para>
    /// </remarks>
    private static void MigrateSettings()
    {
        ApplicationConfig settings = Program.Settings;

        if (settings.ExternalComponentsVersion >= CurrentComponentsVersion)
        {
            return;
        }

        settings.ExternalComponentsVersion = CurrentComponentsVersion;

        ScreenCaptureLib.FFmpegOptions? ffmpeg = Program.DefaultTaskSettings?.CaptureSettings?.FFmpegOptions;

        if (ffmpeg is { OverrideCLIPath: true } && !string.IsNullOrEmpty(ffmpeg.CLIPath))
        {
            string resolved;

            try
            {
                resolved = FileHelpers.GetAbsolutePath(ffmpeg.CLIPath);
            }
            catch (Exception)
            {
                resolved = ffmpeg.CLIPath;
            }

            if (!Exists(resolved))
            {
                DebugHelper.WriteLine("Components: dropped a custom ffmpeg path that points at nothing (" +
                    resolved + ").");
                ffmpeg.OverrideCLIPath = false;
                ffmpeg.CLIPath = "";
            }
        }

        SettingManager.SaveApplicationConfigAsync();
    }

    /// <summary>
    /// Clear out what a crash or a cancelled install left in the staging directories.
    /// </summary>
    /// <remarks>
    /// Unpacked directories go every time: they are a copy of something that is either
    /// installed or gone. Part files stay - they are what a resumed download continues from -
    /// unless they are older than a fortnight, at which point they are far more likely to be
    /// a version nobody will ever ask for again than a download somebody is coming back to.
    /// Neither is ever trusted on its own: nothing is installed without hashing it first.
    /// </remarks>
    private static void SweepStaging()
    {
        foreach (string folder in new[] { Program.ToolsFolder, Program.ModelsFolder })
        {
            string staging;

            try
            {
                staging = Path.Combine(folder, StagingFolderName);

                if (!Directory.Exists(staging))
                {
                    continue;
                }

                foreach (string directory in Directory.EnumerateDirectories(staging))
                {
                    Directory.Delete(directory, recursive: true);
                }

                DateTime cutoff = DateTime.UtcNow.AddDays(-14);

                foreach (string file in Directory.EnumerateFiles(staging, "*.part"))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception, "Components: could not tidy the staging folder.");
            }
        }
    }

    #endregion Startup

    #region Installing

    /// <summary>
    /// Get one component onto this PC: download, verify, unpack, and put it where the rest of
    /// the application already looks for it.
    /// </summary>
    public static Task<ModlightInstallResult> InstallAsync(ModlightComponent component,
        IProgress<ModlightComponentProgress>? progress, CancellationToken cancellationToken) =>
        FetchAsync(component, PinnedSource(component), replace: false, progress, cancellationToken);

    /// <summary>
    /// Put a specific published build in place of whatever is there now.
    /// </summary>
    /// <remarks>
    /// The one difference from an install is that "it is already there" stops being an
    /// answer - which is exactly the bug this fixes. Until now <see cref="IsInstalled"/>
    /// meant "the file exists", nothing looked at which build it was, and so bumping the
    /// pin in the catalogue changed nothing at all on a machine that already had a copy:
    /// not on the first run, not on Remove-then-Install. There was no route anywhere in the
    /// application to a newer yt-dlp.
    /// </remarks>
    public static Task<ModlightInstallResult> ReplaceAsync(ModlightComponent component,
        ModlightComponentSource source, IProgress<ModlightComponentProgress>? progress,
        CancellationToken cancellationToken) =>
        FetchAsync(component, source, replace: true, progress, cancellationToken);

    private static async Task<ModlightInstallResult> FetchAsync(ModlightComponent component,
        ModlightComponentSource source, bool replace, IProgress<ModlightComponentProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!replace && IsInstalled(component))
        {
            return new ModlightInstallResult(ModlightInstallOutcome.AlreadyInstalled, string.Empty);
        }

        progress?.Report(new ModlightComponentProgress(ModlightComponentStage.Queued, 0, source.Size, 0));

        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ModlightInstallResult(ModlightInstallOutcome.Cancelled, string.Empty);
        }

        try
        {
            return await InstallCoreAsync(component, source, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ModlightInstallResult(ModlightInstallOutcome.Cancelled, string.Empty);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: install failed.");
            return new ModlightInstallResult(ModlightInstallOutcome.InstallFailed, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<ModlightInstallResult> InstallCoreAsync(ModlightComponent component,
        ModlightComponentSource source, IProgress<ModlightComponentProgress>? progress,
        CancellationToken cancellationToken)
    {
        string folder = FolderFor(component);
        string staging = Path.Combine(folder, StagingFolderName);

        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(staging);
        HideStaging(staging);

        ModlightInstallResult? space = CheckSpace(folder, source.Size + SpaceHeadroom);

        if (space != null)
        {
            return space.Value;
        }

        // The digest in the name is not decoration: it is what makes a leftover from an
        // older pinned version impossible to mistake for a resumable download of this one.
        string part = Path.Combine(staging, component.Id + "-" + Shorten(source.Sha256) + ".part");
        DiscardOtherParts(staging, component, part);

        DebugHelper.WriteLine("Components: fetching " + component.Id + " " + source.Version + " from " + source.Url);

        progress?.Report(new ModlightComponentProgress(ModlightComponentStage.Downloading, 0, source.Size, 0));

        ModlightDownloadResult download = await ModlightComponentDownload.ToFileAsync(source.Url, part,
            source.Size,
            new Progress<ModlightDownloadProgress>(value => progress?.Report(
                new ModlightComponentProgress(ModlightComponentStage.Downloading, value.Received, value.Total, value.BytesPerSecond))),
            cancellationToken);

        if (!download.Completed)
        {
            return new ModlightInstallResult(download.Outcome switch
            {
                ModlightDownloadOutcome.Cancelled => ModlightInstallOutcome.Cancelled,
                ModlightDownloadOutcome.Offline => ModlightInstallOutcome.Offline,
                ModlightDownloadOutcome.WriteFailed => ModlightInstallOutcome.InstallFailed,
                ModlightDownloadOutcome.Refused => ModlightInstallOutcome.Refused,
                _ => ModlightInstallOutcome.DownloadFailed
            }, download.Message);
        }

        progress?.Report(new ModlightComponentProgress(ModlightComponentStage.Verifying,
            source.Size, source.Size, 0));

        // The same gate the updater puts in front of an installer it is about to run: hash
        // it, and delete it if it disagrees with what was promised. Deleting matters here
        // for the same reason it matters there - what would otherwise be left behind is an
        // executable of unknown provenance under a name that says it belongs to us.
        ModlightVerification verification =
            await ModlightUpdateFile.VerifyOrDeleteAsync(part, source.Sha256, cancellationToken);

        if (!verification.Matches)
        {
            DebugHelper.WriteLine("Components: REJECTED " + component.Id + ". Expected " + source.Sha256 +
                ", got " + (verification.Actual.Length == 0 ? "nothing" : verification.Actual) + ". " +
                (verification.Error ?? ""));

            return new ModlightInstallResult(ModlightInstallOutcome.ChecksumFailed, verification.Actual);
        }

        DebugHelper.WriteLine("Components: checksum verified for " + component.Id + " (" + verification.Actual + ").");

        progress?.Report(new ModlightComponentProgress(ModlightComponentStage.Installing,
            source.Size, source.Size, 0));

        ModlightInstallResult install = await Task.Run(() => Place(component, folder, staging, part), cancellationToken);

        if (install.Success)
        {
            progress?.Report(new ModlightComponentProgress(ModlightComponentStage.Done,
                source.Size, source.Size, 0));

            DebugHelper.WriteLine("Components: installed " + component.Id + " " + source.Version +
                " to " + TargetPath(component));

            SweepRetired(component);
        }

        return install;
    }

    /// <summary>Move what was downloaded into the folder the application reads.</summary>
    private static ModlightInstallResult Place(ModlightComponent component, string folder, string staging, string part)
    {
        string unpack = Path.Combine(staging, component.Id + "-unpack");

        try
        {
            switch (component.Packaging)
            {
                case ModlightComponentPackaging.File:
                    MoveIntoPlace(part, Path.Combine(folder, component.FileName));
                    break;

                case ModlightComponentPackaging.ZipSingleFile:
                {
                    ModlightInstallResult? space = CheckSpace(folder, UncompressedSize(part) + SpaceHeadroom);

                    if (space != null)
                    {
                        return space.Value;
                    }

                    Reset(unpack);

                    ZipManager.Extract(part, unpack, retainDirectoryStructure: false,
                        entry => string.Equals(entry.Name, component.FileName, StringComparison.OrdinalIgnoreCase),
                        component.MaxUncompressedSize);

                    string extracted = Path.Combine(unpack, component.FileName);

                    if (!File.Exists(extracted))
                    {
                        return new ModlightInstallResult(ModlightInstallOutcome.InstallFailed,
                            component.FileName + " was not in the archive");
                    }

                    MoveIntoPlace(extracted, Path.Combine(folder, component.FileName));
                    break;
                }

                case ModlightComponentPackaging.ZipTree:
                {
                    ModlightInstallResult? space = CheckSpace(folder, UncompressedSize(part) * 2 + SpaceHeadroom);

                    if (space != null)
                    {
                        return space.Value;
                    }

                    Reset(unpack);

                    ZipManager.Extract(part, unpack, retainDirectoryStructure: true, filter: null,
                        component.MaxUncompressedSize);

                    foreach (string entry in Directory.EnumerateFileSystemEntries(unpack))
                    {
                        string destination = Path.Combine(folder, Path.GetFileName(entry));

                        if (Directory.Exists(entry))
                        {
                            if (Directory.Exists(destination))
                            {
                                Directory.Delete(destination, recursive: true);
                            }

                            Directory.Move(entry, destination);
                        }
                        else
                        {
                            MoveIntoPlace(entry, destination);
                        }
                    }

                    break;
                }
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: could not put " + component.Id + " in place.");
            return new ModlightInstallResult(ModlightInstallOutcome.InstallFailed, exception.Message);
        }
        finally
        {
            // The unpacked copy always goes: it is a duplicate of something that is either
            // installed now or was never going to be.
            TryDeleteDirectory(unpack);
        }

        // Say it worked only if the file is actually there. An extraction that silently
        // produced nothing would otherwise be reported as a success and show up later as the
        // same "does not exist at the following path" this whole feature replaces.
        if (!Exists(Path.Combine(folder, component.FileName)))
        {
            return new ModlightInstallResult(ModlightInstallOutcome.InstallFailed,
                component.FileName + " is not there afterwards");
        }

        // Only now, and deliberately not in the finally above. Everything that can fail
        // after the download - no room to unpack, a locked destination file, an archive that
        // will not open - is worth retrying, and retrying with the verified bytes still in
        // the staging folder costs nothing, while retrying without them costs the whole
        // download again. For a gigabyte on a home connection that is the difference between
        // an annoyance and giving up.
        ModlightUpdateFile.TryDelete(part);

        return new ModlightInstallResult(ModlightInstallOutcome.Installed, string.Empty);
    }

    /// <summary>Delete a component we fetched. Never touches a copy the installer placed.</summary>
    public static bool Remove(ModlightComponent component)
    {
        string folder = FolderFor(component);
        bool removed = false;

        foreach (string name in component.OwnedEntries.Count > 0 ? component.OwnedEntries : [component.FileName])
        {
            try
            {
                string path = Path.Combine(folder, name);

                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    removed = true;
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    removed = true;
                }
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception, "Components: could not remove " + component.Id + ".");
                return false;
            }
        }

        if (removed)
        {
            DebugHelper.WriteLine("Components: removed " + component.Id + " from " + folder);
        }

        return removed;
    }

    #endregion Installing

    #region Plumbing

    /// <summary>
    /// Put a verified file where the application reads it, over whatever is there.
    /// </summary>
    /// <remarks>
    /// Not <c>File.Move(overwrite: true)</c>, which fails outright on a file Windows has
    /// mapped - and the file being replaced here is an executable that may well be running,
    /// because a scheduled update lands while somebody is downloading a video with the
    /// build it is replacing. Renaming the old one out of the way works in that case, and
    /// the rename is also what makes the failure recoverable: if the new file cannot be
    /// moved in afterwards, the old one goes back, and the folder never ends up holding
    /// neither.
    /// </remarks>
    private static void MoveIntoPlace(string source, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(source, destination);
            return;
        }

        string retired = destination + RetiredSuffix +
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        try
        {
            File.Move(destination, retired);
        }
        catch (IOException)
        {
            // The rename is what a locked file usually allows and an overwrite is what it
            // usually refuses, but the two are not the same permission on every filesystem
            // - a network share can answer the other way round. Try the plain overwrite and
            // let it be the error if it is one.
            File.Move(source, destination, overwrite: true);
            return;
        }

        try
        {
            File.Move(source, destination);
        }
        catch (Exception)
        {
            // Put back what was there. A tools folder holding neither the old build nor the
            // new one is the one outcome worse than not updating at all.
            try
            {
                File.Move(retired, destination);
            }
            catch (Exception restore)
            {
                DebugHelper.WriteException(restore, "Components: could not put the old file back.");
            }

            throw;
        }

        // Fails while the old build is still running, which is a perfectly normal thing for
        // it to be doing. SweepRetired clears it on the next launch.
        ModlightUpdateFile.TryDelete(retired);
    }

    /// <summary>
    /// Clear the files a replacement renamed out of the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By exact name - an owned entry, then the suffix, then a run of digits - rather than
    /// by a wildcard over the folder. Deleting <c>*.old-*</c> out of a directory the user
    /// can also put their own files in is a broader promise than this code is in a position
    /// to keep. Windows can also answer a wildcard with a file whose SHORT name matched, so
    /// the pattern is a filter and the check below is the decision.
    /// </para>
    /// <para>
    /// And only when the live file is there. The one way to end up with a retired copy and
    /// no replacement is the restore in <see cref="MoveIntoPlace"/> failing too - at which
    /// point that retired copy is the only yt-dlp on the machine, and deleting it because
    /// it has an odd name would turn a bad update into a missing tool.
    /// </para>
    /// </remarks>
    private static void SweepRetired(ModlightComponent component)
    {
        string folder = FolderFor(component);

        foreach (string name in component.OwnedEntries.Count > 0 ? component.OwnedEntries : [component.FileName])
        {
            try
            {
                string live = Path.Combine(folder, name);

                if (!File.Exists(live) && !Directory.Exists(live))
                {
                    continue;
                }

                string prefix = name + RetiredSuffix;

                foreach (string file in Directory.EnumerateFiles(folder, prefix + "*"))
                {
                    string found = Path.GetFileName(file);

                    if (!found.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string tail = found.Substring(prefix.Length);

                    if (tail.Length > 0 && long.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    {
                        ModlightUpdateFile.TryDelete(file);
                    }
                }
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception);
            }
        }
    }

    private static ModlightInstallResult? CheckSpace(string folder, long needed)
    {
        if (needed <= 0)
        {
            return null;
        }

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));

            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            DriveInfo drive = new(root);

            if (drive.IsReady && drive.AvailableFreeSpace < needed)
            {
                return new ModlightInstallResult(ModlightInstallOutcome.NoSpace, needed.ToSizeString());
            }
        }
        catch (Exception exception)
        {
            // A drive that cannot be measured is not a drive that is full. Carry on and let
            // the write itself be the judge.
            DebugHelper.WriteException(exception);
        }

        return null;
    }

    private static long UncompressedSize(string archivePath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            return archive.Entries.Sum(entry => entry.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void DiscardOtherParts(string staging, ModlightComponent component, string keep)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(staging, component.Id + "-*.part"))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }

    private static void Reset(string directory)
    {
        TryDeleteDirectory(directory);
        Directory.CreateDirectory(directory);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }

    private static void HideStaging(string staging)
    {
        try
        {
            DirectoryInfo info = new(staging);

            if ((info.Attributes & FileAttributes.Hidden) == 0)
            {
                info.Attributes |= FileAttributes.Hidden;
            }
        }
        catch (Exception)
        {
            // Cosmetic. A visible staging folder is not worth failing an install over.
        }
    }

    private static string Shorten(string digest) => digest.Length > 12 ? digest[..12] : digest;

    private static bool Exists(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    #endregion Plumbing
}
