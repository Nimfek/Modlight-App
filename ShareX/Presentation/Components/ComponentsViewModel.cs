#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// One row of the component list: what it is, whether it is here, and getting it here.
/// </summary>
public sealed class ComponentRowViewModel : INotifyPropertyChanged
{
    private readonly Action onChanged;
    private CancellationTokenSource? cancellation;
    private ModlightComponentStage stage = ModlightComponentStage.Idle;
    private double progressValue;
    private string progressText = string.Empty;
    private string message = string.Empty;
    private bool failed;
    private string installedVersion = string.Empty;
    private bool versionAsked;
    private bool updating;

    public ComponentRowViewModel(ModlightComponent component, Action onChanged, bool isRequired = false)
    {
        Component = component;
        this.onChanged = onChanged;
        IsRequired = isRequired;
    }

    public ModlightComponent Component { get; }

    /// <summary>True for the component whose absence sent the user to this list.</summary>
    public bool IsRequired { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => Component.Name();

    public string Purpose => Component.Purpose();

    /// <summary>"65.4 MB from github.com/ShareX/FFmpeg" - the price and the provenance, together.</summary>
    public string Details => Helpers.SafeStringFormat(Strings.Components_Details,
        Component.DownloadSize.ToSizeString(), Component.Source);

    /// <summary>True when this component follows its publisher between Modlight releases.</summary>
    public bool TracksUpdates => Component.TracksUpdates;

    /// <summary>
    /// Which build is here, once it has been asked.
    /// </summary>
    /// <remarks>
    /// Blank until the answer arrives rather than showing the catalogue's pin in the
    /// meantime. The pin stops being the truth the first time an update lands, and a line
    /// that is briefly wrong on a page somebody opened to find out what they have is worse
    /// than a line that appears a moment late.
    /// </remarks>
    public string VersionText
    {
        get
        {
            if (!IsInstalled)
            {
                return string.Empty;
            }

            if (installedVersion.Length > 0)
            {
                return Helpers.SafeStringFormat(Strings.Components_Version, installedVersion);
            }

            return versionAsked ? Strings.Components_VersionUnknown : string.Empty;
        }
    }

    public bool HasVersion => VersionText.Length > 0;

    /// <summary>What the publisher had when we last asked, when that is newer than this.</summary>
    public string UpdateNote
    {
        get
        {
            if (!TracksUpdates || !IsInstalled)
            {
                return string.Empty;
            }

            string latest = ModlightComponentUpdates.LatestKnown(Component);

            return ModlightToolRelease.IsNewer(latest, installedVersion)
                ? Helpers.SafeStringFormat(Strings.Components_UpdateAvailable, latest)
                : string.Empty;
        }
    }

    public bool HasUpdateNote => UpdateNote.Length > 0;

    /// <summary>The button. Offered whenever there is a copy of ours to replace.</summary>
    /// <remarks>
    /// Not only when a newer build is already known about: the check happens once a day, so
    /// most of the time nothing has been asked yet, and a button that appears only after
    /// the app has already noticed is a button that is missing at the exact moment somebody
    /// goes looking for it.
    /// </remarks>
    public bool CanUpdate => !IsBusy && !updating && ModlightComponentUpdates.CanUpdate(Component);

    /// <summary>
    /// Whether the user wants this component at all.
    /// </summary>
    /// <remarks>
    /// Unticking does NOT delete anything. The tick is the answer to "should Modlight fetch
    /// this", which is a different question from "throw away the gigabyte I already have" -
    /// and answering the second one by accident while meaning the first is not a mistake
    /// anybody should be able to make with one click. Removal is its own button.
    /// </remarks>
    public bool IsWanted
    {
        get => ModlightComponents.IsWanted(Component);
        set
        {
            if (value == ModlightComponents.IsWanted(Component))
            {
                return;
            }

            ModlightComponents.SetWanted(Component, value);
            Refresh();
        }
    }

    public bool IsInstalled => ModlightComponents.IsInstalled(Component);

    public bool IsBusy => stage is not (ModlightComponentStage.Idle or ModlightComponentStage.Done);

    public bool CanInstall => !IsBusy && !IsInstalled;

    public bool CanRemove => !IsBusy && IsInstalled && ModlightComponents.IsManaged(Component);

    public double ProgressValue
    {
        get => progressValue;
        private set => Set(ref progressValue, value);
    }

    public bool IsProgressIndeterminate =>
        stage is ModlightComponentStage.Queued or ModlightComponentStage.Verifying or ModlightComponentStage.Installing;

    public string ProgressText
    {
        get => progressText;
        private set => Set(ref progressText, value);
    }

    /// <summary>The status line: installed, not installed, what went wrong, or what is happening.</summary>
    public string Status
    {
        get
        {
            if (message.Length > 0)
            {
                return message;
            }

            if (IsInstalled)
            {
                return Strings.Components_Installed;
            }

            return IsWanted ? Strings.Components_NotInstalled : Strings.Components_Skipped;
        }
    }

    public bool HasFailed => failed;

    public async Task InstallAsync()
    {
        if (IsBusy || IsInstalled)
        {
            return;
        }

        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();

        failed = false;
        message = string.Empty;
        ProgressValue = 0;
        ProgressText = string.Empty;
        SetStage(ModlightComponentStage.Queued);

        Progress<ModlightComponentProgress> progress = new(Report);

        ModlightInstallResult result;

        try
        {
            result = await ModlightComponents.InstallAsync(Component, progress, cancellation.Token);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: install threw.");
            result = new ModlightInstallResult(ModlightInstallOutcome.InstallFailed, exception.Message);
        }

        message = Describe(result);
        failed = !result.Success && result.Outcome != ModlightInstallOutcome.Cancelled;
        ProgressText = string.Empty;
        SetStage(ModlightComponentStage.Idle);
    }

    /// <summary>Ask the binary which build it is, once, and show it.</summary>
    public async Task LoadVersionAsync()
    {
        if (!IsInstalled)
        {
            return;
        }

        string version = await ModlightComponentUpdates.InstalledVersionAsync(Component, CancellationToken.None);

        installedVersion = version;
        versionAsked = true;
        NotifyVersion();
    }

    /// <summary>
    /// Fetch the newest published build, if it is newer than this one.
    /// </summary>
    /// <remarks>
    /// <c>onlyWhenNewer: false</c>, unlike the scheduled check. A person pressing this on a
    /// copy the application could not identify is asking for the newest published build and
    /// is entitled to get it; the automatic path is the one that must never act on a
    /// version it could not read.
    /// </remarks>
    public async Task UpdateAsync()
    {
        if (IsBusy || updating || !ModlightComponentUpdates.CanUpdate(Component))
        {
            return;
        }

        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();

        updating = true;
        failed = false;
        message = Strings.Components_Checking;
        ProgressValue = 0;
        ProgressText = string.Empty;
        Refresh();

        ModlightComponentUpdateResult result;

        try
        {
            result = await ModlightComponentUpdates.UpdateAsync(Component, new Progress<ModlightComponentProgress>(Report),
                onlyWhenNewer: false, cancellation.Token);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: update threw.");
            result = new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.InstallFailed, "", exception.Message);
        }

        updating = false;
        stage = ModlightComponentStage.Idle;
        ProgressText = string.Empty;
        message = Describe(result);
        failed = result.Outcome is ModlightComponentUpdateOutcome.InstallFailed
            or ModlightComponentUpdateOutcome.CheckFailed;

        if (result.Installed)
        {
            installedVersion = result.Version;
            versionAsked = true;
        }

        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        NotifyVersion();
    }

    public void Cancel() => cancellation?.Cancel();

    public bool Remove()
    {
        if (!CanRemove)
        {
            return false;
        }

        bool removed = ModlightComponents.Remove(Component);

        message = removed ? string.Empty : Strings.Components_RemoveFailed;
        failed = !removed;
        Refresh();
        return removed;
    }

    /// <summary>Re-read everything that can have changed outside this row.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsWanted));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(CanUpdate));
        onChanged();
    }

    private void NotifyVersion()
    {
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(HasVersion));
        OnPropertyChanged(nameof(UpdateNote));
        OnPropertyChanged(nameof(HasUpdateNote));
        Refresh();
    }

    private void Report(ModlightComponentProgress value)
    {
        SetStage(value.Stage);

        if (value.Stage == ModlightComponentStage.Downloading && value.Total > 0)
        {
            ProgressValue = Math.Min(100, (double)value.Received / value.Total * 100);
            ProgressText = Helpers.SafeStringFormat(Strings.Components_Progress,
                value.Received.ToSizeString(), value.Total.ToSizeString(), ((long)value.BytesPerSecond).ToSizeString());
        }
        else
        {
            ProgressText = string.Empty;
        }
    }

    private void SetStage(ModlightComponentStage value)
    {
        stage = value;

        message = value switch
        {
            ModlightComponentStage.Queued => Strings.Components_Queued,
            ModlightComponentStage.Downloading => Strings.Components_Downloading,
            ModlightComponentStage.Verifying => Strings.Components_Verifying,
            ModlightComponentStage.Installing => Strings.Components_Installing,
            _ => message
        };

        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        Refresh();
    }

    private static string Describe(ModlightComponentUpdateResult result)
    {
        return result.Outcome switch
        {
            ModlightComponentUpdateOutcome.Updated =>
                Helpers.SafeStringFormat(Strings.Components_Updated, result.Version),
            ModlightComponentUpdateOutcome.AlreadyCurrent => Strings.Components_UpToDate,
            ModlightComponentUpdateOutcome.CheckFailed => Strings.Components_UpdateCheckFailed,
            ModlightComponentUpdateOutcome.NotOurs =>
                Helpers.SafeStringFormat(Strings.Components_UpdateNotOurs, result.Detail),
            ModlightComponentUpdateOutcome.Cancelled => Strings.Components_Cancelled,
            ModlightComponentUpdateOutcome.VersionUnknown => Strings.Components_VersionUnknown,
            ModlightComponentUpdateOutcome.NotTracked => string.Empty,
            _ => Helpers.SafeStringFormat(Strings.Components_UpdateFailed, result.Detail)
        };
    }

    private static string Describe(ModlightInstallResult result)
    {
        return result.Outcome switch
        {
            ModlightInstallOutcome.Installed or ModlightInstallOutcome.AlreadyInstalled => string.Empty,
            ModlightInstallOutcome.Cancelled => Strings.Components_Cancelled,
            ModlightInstallOutcome.Offline => Strings.Components_ErrorOffline,
            ModlightInstallOutcome.ChecksumFailed => Strings.Components_ErrorChecksum,
            ModlightInstallOutcome.NoSpace => Helpers.SafeStringFormat(Strings.Components_ErrorNoSpace, result.Message),
            ModlightInstallOutcome.DownloadFailed => Helpers.SafeStringFormat(Strings.Components_ErrorDownload, result.Message),
            ModlightInstallOutcome.Refused => Helpers.SafeStringFormat(Strings.Components_ErrorRefused, result.Message),
            _ => Helpers.SafeStringFormat(Strings.Components_ErrorFailed, result.Message)
        };
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// The component list, used by the settings page and by the window a failed feature opens.
/// </summary>
/// <remarks>
/// One list in one place, deliberately. Unticking a component during installation has to be
/// undoable without reinstalling, and the only way that stays true is if the thing the
/// installer asked about and the thing the settings page shows are the same list, drawn by
/// the same code, from the same catalogue.
/// </remarks>
public sealed class ComponentsViewModel : INotifyPropertyChanged
{
    public ComponentsViewModel(string? requiredId = null, string? headline = null)
    {
        Headline = headline ?? string.Empty;
        RequiredId = requiredId ?? string.Empty;

        IEnumerable<ModlightComponent> components = ModlightComponents.All;

        if (RequiredId.Length > 0)
        {
            // The one that is in the way goes first. Everything else stays on the list,
            // because "while you are here" is cheaper than a second trip.
            components = components.OrderBy(component =>
                string.Equals(component.Id, RequiredId, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }

        foreach (ModlightComponent component in components)
        {
            bool required = RequiredId.Length > 0 &&
                string.Equals(component.Id, RequiredId, StringComparison.OrdinalIgnoreCase);

            Rows.Add(new ComponentRowViewModel(component, OnRowChanged, required));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ComponentRowViewModel> Rows { get; } = [];

    /// <summary>Set when a feature sent the user here, e.g. "Screen recording needs FFmpeg".</summary>
    public string Headline { get; }

    public bool HasHeadline => Headline.Length > 0;

    public string RequiredId { get; }

    public string Intro => Strings.Components_Intro;

    public string FolderNote => Helpers.SafeStringFormat(Strings.Components_Folder, Program.ToolsFolder);

    /// <summary>True when anything in the list follows a publisher, i.e. yt-dlp is here.</summary>
    public bool ShowUpdates => Rows.Any(row => row.TracksUpdates);

    public string AutoUpdateNote => Strings.Components_AutoUpdateNote;

    /// <summary>Whether a newer published build may be installed without being asked.</summary>
    public bool AutoUpdate
    {
        get => ModlightComponentUpdates.AutoUpdate;
        set
        {
            if (value == ModlightComponentUpdates.AutoUpdate)
            {
                return;
            }

            ModlightComponentUpdates.AutoUpdate = value;
            OnPropertyChanged(nameof(AutoUpdate));
        }
    }

    public bool Checking
    {
        get => checking;
        private set
        {
            if (checking == value)
            {
                return;
            }

            checking = value;
            OnPropertyChanged(nameof(Checking));
            OnPropertyChanged(nameof(CanCheck));
        }
    }

    public bool CanCheck => !checking && !AnyBusy;

    /// <summary>When the last check that actually completed was.</summary>
    /// <remarks>
    /// Local time and only the date and hour, because it answers one question - "is this
    /// list telling me something recent" - and a timestamp to the second invites the reader
    /// to look for a precision that is not there: the check is scheduled to the day.
    /// </remarks>
    public string LastCheckText
    {
        get
        {
            DateTime? stamp = ModlightComponentUpdates.LastCheckUtc;

            return stamp == null
                ? Strings.Components_NeverChecked
                : Helpers.SafeStringFormat(Strings.Components_LastCheck, stamp.Value.ToLocalTime().ToString("g"));
        }
    }

    /// <summary>
    /// Read what is installed, and ask the publisher if it is time to ask.
    /// </summary>
    /// <remarks>
    /// Called when the page appears rather than from the constructor: this starts a process
    /// per component to read its version and may make a network request, and neither
    /// belongs on the thread that is building the window. The check is gated the same way
    /// the daily timer is, so opening the page ten times in an afternoon is one request.
    /// </remarks>
    public async Task StartAsync()
    {
        if (started)
        {
            return;
        }

        started = true;

        foreach (ComponentRowViewModel row in Rows.ToArray())
        {
            await row.LoadVersionAsync();
        }

        await ModlightComponentUpdates.RunScheduledIfDueAsync(CancellationToken.None);

        NotifyChecked();
    }

    /// <summary>The button: ask now, whatever the schedule says.</summary>
    public async Task CheckNowAsync()
    {
        if (checking)
        {
            return;
        }

        Checking = true;

        try
        {
            foreach (ComponentRowViewModel row in Rows.ToArray())
            {
                if (row.TracksUpdates)
                {
                    await ModlightComponentUpdates.CheckAsync(row.Component, CancellationToken.None);
                }

                await row.LoadVersionAsync();
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: the update check failed.");
        }
        finally
        {
            Checking = false;
            NotifyChecked();
        }
    }

    private void NotifyChecked()
    {
        OnPropertyChanged(nameof(LastCheckText));

        foreach (ComponentRowViewModel row in Rows)
        {
            row.Refresh();
        }
    }

    public bool AnythingMissing => Rows.Any(row => row.CanInstall && row.IsWanted);

    public bool AnyBusy => Rows.Any(row => row.IsBusy);

    private bool checking;

    private bool started;

    /// <summary>True once the component that sent the user here is installed.</summary>
    public bool RequiredSatisfied =>
        RequiredId.Length == 0 || ModlightComponents.IsInstalled(RequiredId);

    /// <summary>Install everything that is ticked and not here, one after another.</summary>
    public async Task InstallMissingAsync()
    {
        foreach (ComponentRowViewModel row in Rows.ToArray())
        {
            if (row.IsWanted && row.CanInstall)
            {
                await row.InstallAsync();
            }
        }
    }

    public void CancelAll()
    {
        foreach (ComponentRowViewModel row in Rows)
        {
            row.Cancel();
        }
    }

    public void RefreshAll()
    {
        foreach (ComponentRowViewModel row in Rows)
        {
            row.Refresh();
        }
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OnRowChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnythingMissing)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnyBusy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCheck)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequiredSatisfied)));
    }
}
