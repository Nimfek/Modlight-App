#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>Where one queued download has got to.</summary>
internal enum DownloadState
{
    /// <summary>Nothing has happened yet; something else is downloading.</summary>
    Queued,

    /// <summary>yt-dlp is running but has not reported a byte. Resolving the address, usually.</summary>
    Starting,

    Downloading,

    /// <summary>Bytes are in; ffmpeg is merging them, or a post-processing step is running.</summary>
    Working,

    /// <summary>Stopped on purpose, with the partial file kept. Resuming continues it.</summary>
    Paused,

    Completed,
    Failed,
    Canceled
}

/// <summary>
/// One row of the download queue, and the thing that owns its yt-dlp process.
/// </summary>
/// <remarks>
/// UI thread only. Everything yt-dlp reports arrives on a worker thread and is posted here
/// rather than being written from there - a queue row is bound to controls that are being
/// drawn while the numbers change.
///
/// The row owns its cancellation source and nothing else may cancel it, because the same
/// stop means two different things: a pause keeps the partial file and expects to be
/// resumed, a cancel does not. Getting that backwards would either lose a half-finished
/// download or leave a cancelled one looking resumable.
/// </remarks>
internal sealed class DownloadItemViewModel : INotifyPropertyChanged
{
    private readonly DownloadLink link;
    private readonly DownloadDestination destination;

    /// <summary>
    /// What was asked for, frozen at the moment this row was queued.
    /// </summary>
    /// <remarks>
    /// Not read from the settings when the row starts, which matters twice over. The picker
    /// above the queue is a setting for the NEXT download, so five queued rows keep the five
    /// answers they were given rather than all changing shape because somebody moved a
    /// dropdown. And a pause is a kill whose resume is the identical command run again - a
    /// format that could differ between the two attempts would turn a resume into a second,
    /// different download that cannot use the .part file the first one left.
    /// </remarks>
    private readonly DownloadFormat format;

    /// <summary>Where yt-dlp is told to write. Fixed for the row's whole life, so a resume finds its own .part.</summary>
    private readonly string folder;

    /// <summary>The folder is ours, not the user's - a Modlight-only download has nowhere else to land.</summary>
    private readonly bool folderIsScratch;

    private CancellationTokenSource? cancellation;

    /// <summary>This stop is a pause. Read by the continuation to tell the two apart.</summary>
    private bool pausing;

    /// <summary>The last failure was one a YouTube sign-in would plausibly fix.</summary>
    private bool needsSignIn;

    /// <summary>The last failure looked like a site this yt-dlp no longer understands.</summary>
    private bool needsUpdate;

    /// <summary>A newer yt-dlp is being fetched for this row right now.</summary>
    private bool updatingTool;

    private DownloadState state = DownloadState.Queued;
    private string title;
    private string detail = "";
    private double percent;
    private string sizeText = "";
    private string speedText = "";
    private string etaText = "";
    private string? filePath;

    /// <summary>What was asked for, until yt-dlp says what it found; then what it found.</summary>
    private string formatText;

    /// <summary>The asked-for label, kept so a retry can put the row back the way it started.</summary>
    private readonly string requestedFormatText;

    private string note = "";

    public DownloadItemViewModel(DownloadLink link, string folder, bool folderIsScratch,
        DownloadDestination destination, DownloadFormat format, string formatLabel)
    {
        this.link = link;
        this.folder = folder;
        this.folderIsScratch = folderIsScratch;
        this.destination = destination;
        this.format = format;

        requestedFormatText = formatLabel;
        formatText = formatLabel;

        // The address until yt-dlp says what the thing is called, which takes a second or
        // two. An empty title would make every fresh row look identical.
        title = link.Url;
        detail = Strings.Download_StateQueued;
    }

    /// <summary>The queue wants to know a row changed, so it can start the next one.</summary>
    public Action? StateChanged { get; set; }

    public string Url => link.Url;

    /// <summary>The site, so a queue of ten rows can be read without opening any of them.</summary>
    public string Source => link.Source;

    /// <summary>
    /// The chip beside the site: "1080p", "Best", "MP3".
    /// </summary>
    /// <remarks>
    /// It starts as what was asked for and becomes what arrived, which is the whole point of
    /// having it on the row rather than only in the picker. A queue built up over an hour
    /// holds rows that were queued under different answers, and after the fact the only
    /// place that record survives is here.
    /// </remarks>
    public string FormatText
    {
        get => formatText;
        private set => SetField(ref formatText, value);
    }

    /// <summary>
    /// One line about the gap between what was asked for and what is coming, or empty.
    /// </summary>
    /// <remarks>
    /// Its own line rather than folded into <see cref="Detail"/>, because Detail is a
    /// running commentary that is overwritten every second while bytes arrive - and this is
    /// a fact about the download that has to stay legible from the moment it is known until
    /// the row is cleared.
    /// </remarks>
    public string Note
    {
        get => note;
        private set
        {
            SetField(ref note, value);
            OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => note.Length > 0;

    public string Title
    {
        get => title;
        private set => SetField(ref title, value);
    }

    /// <summary>The second line: what is happening, or what went wrong, or where it landed.</summary>
    public string Detail
    {
        get => detail;
        private set => SetField(ref detail, value);
    }

    public double Percent
    {
        get => percent;
        private set => SetField(ref percent, value);
    }

    /// <summary>"12.4 MB / 88.1 MB", or empty before the server has said how big it is.</summary>
    public string SizeText
    {
        get => sizeText;
        private set => SetField(ref sizeText, value);
    }

    public string SpeedText
    {
        get => speedText;
        private set => SetField(ref speedText, value);
    }

    public string EtaText
    {
        get => etaText;
        private set => SetField(ref etaText, value);
    }

    public DownloadState State
    {
        get => state;
        private set
        {
            if (state == value)
            {
                return;
            }

            state = value;
            OnPropertyChanged();
            NotifyState();
            StateChanged?.Invoke();
        }
    }

    /// <summary>A process of ours is running for this row. The queue starts one row at a time.</summary>
    public bool IsActive => state is DownloadState.Starting or DownloadState.Downloading or DownloadState.Working;

    /// <summary>Nothing more will happen to this row unless somebody asks.</summary>
    public bool IsFinished => state is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled;

    public bool ShowProgress => state is DownloadState.Starting or DownloadState.Downloading
        or DownloadState.Working or DownloadState.Paused;

    /// <summary>Running without a figure to show: resolving the address, or merging.</summary>
    public bool IsIndeterminate => state is DownloadState.Starting or DownloadState.Working;

    public bool CanPause => state is DownloadState.Downloading or DownloadState.Starting or DownloadState.Queued;
    public bool CanResume => state == DownloadState.Paused;
    public bool CanCancel => !IsFinished;
    public bool CanRetry => state is DownloadState.Failed or DownloadState.Canceled;

    /// <summary>
    /// This row failed for want of a signed-in YouTube account, so it offers one.
    /// </summary>
    /// <remarks>
    /// On the row rather than only in the strip above it, because the row is where the
    /// person is looking when they read why it failed - and a remedy stated one line away
    /// from the problem is a remedy that gets acted on.
    /// </remarks>
    public bool CanSignIn => state == DownloadState.Failed && needsSignIn;

    /// <summary>
    /// This row failed the way an out-of-date downloader fails, so it offers to fix that.
    /// </summary>
    /// <remarks>
    /// The offer survives a failed attempt on purpose. Somebody who pressed it on a train
    /// and got "nothing answered" is going to want it still there when the train comes out
    /// of the tunnel; it goes away only when the row runs again, which is the moment it
    /// stops describing anything.
    /// </remarks>
    public bool CanUpdateTool => state == DownloadState.Failed && needsUpdate && !updatingTool;

    /// <summary>There is a file on this PC to show. False for a Modlight-only download, whose copy is scratch.</summary>
    public bool CanReveal => state == DownloadState.Completed && !folderIsScratch && filePath != null;

    public bool HasNumbers => speedText.Length > 0 || sizeText.Length > 0 || etaText.Length > 0;

    public bool IsFailed => state == DownloadState.Failed;
    public bool IsDone => state == DownloadState.Completed;

    /// <summary>The glyph on the left of the row, which is the state at a glance.</summary>
    public string Icon => state switch
    {
        DownloadState.Queued => LucideIcons.clock,
        DownloadState.Paused => LucideIcons.circle_pause,
        DownloadState.Completed => LucideIcons.circle_check,
        DownloadState.Failed => LucideIcons.triangle_alert,
        DownloadState.Canceled => LucideIcons.ban,
        _ => LucideIcons.arrow_down_to_line
    };

    /// <summary>Where the finished file is, or null.</summary>
    public string? FilePath => filePath;

    /// <summary>
    /// Begin, or begin again after a pause.
    /// </summary>
    /// <remarks>
    /// Called by the queue and only by the queue, so that one download runs at a time. The
    /// arguments yt-dlp gets are identical to the previous run's, which is what makes a
    /// resume a resume rather than a second download of the same video.
    /// </remarks>
    public void Start()
    {
        if (IsActive)
        {
            return;
        }

        pausing = false;

        // The offer belongs to one failure, not to the row. Left standing it would sit
        // under a download that is running again and has not failed at all.
        needsSignIn = false;
        needsUpdate = false;

        // Same rule for the format verdict: it described the last attempt. ffmpeg may have
        // been installed since, or the site may answer differently, so the row goes back to
        // stating what was asked for until this run says otherwise.
        Note = "";
        FormatText = requestedFormatText;

        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();

        State = DownloadState.Starting;
        Detail = Strings.Download_StateStarting;
        SpeedText = "";
        EtaText = "";
        NotifyNumbers();

        _ = RunAsync(cancellation.Token);
    }

    /// <summary>Stop, keep what has been fetched, and expect to be asked again.</summary>
    public void Pause()
    {
        if (state == DownloadState.Queued)
        {
            // Never started, so there is nothing to kill - but "pause" on a waiting row
            // has to mean something, or the queue would simply start it a moment later.
            State = DownloadState.Paused;
            Detail = Strings.Download_StatePaused;
            return;
        }

        if (!IsActive)
        {
            return;
        }

        pausing = true;
        cancellation?.Cancel();
    }

    public void Resume()
    {
        if (state != DownloadState.Paused)
        {
            return;
        }

        State = DownloadState.Queued;
        Detail = Strings.Download_StateQueued;
    }

    public void Cancel()
    {
        if (IsFinished)
        {
            return;
        }

        pausing = false;

        if (IsActive)
        {
            cancellation?.Cancel();
            return;
        }

        // Queued or paused: no process to stop, so the row settles here and now.
        Stopped(Strings.Download_StateCanceled, DownloadState.Canceled);
    }

    public void Retry()
    {
        if (!CanRetry)
        {
            return;
        }

        Percent = 0;
        SizeText = "";
        SpeedText = "";
        EtaText = "";
        NotifyNumbers();

        State = DownloadState.Queued;
        Detail = Strings.Download_StateQueued;
    }

    /// <summary>
    /// Fetch a newer yt-dlp and try this same address again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The remedy stated where the problem is. What it does NOT do is retry regardless: if
    /// the publisher cannot be reached, or if the build here is already the newest one
    /// published, the row says so and stays failed. Retrying an unchanged downloader
    /// against a site it has already failed on would fail identically, and the second
    /// identical failure teaches the reader that the button does nothing.
    /// </para>
    /// <para>
    /// "Already the newest" is worth saying out loud rather than hiding: it means the site
    /// is broken further upstream than anything Modlight can fix, which is a different
    /// piece of news from "you are out of date" and leads somewhere else - waiting a day,
    /// or reporting it where yt-dlp's own message was pointing.
    /// </para>
    /// </remarks>
    public async Task UpdateToolAndRetryAsync()
    {
        if (!CanUpdateTool)
        {
            return;
        }

        updatingTool = true;
        OnPropertyChanged(nameof(CanUpdateTool));
        Detail = Strings.Download_UpdatingTool;

        ModlightComponent? component = ModlightComponentCatalog.Find(ModlightComponentCatalog.YtDlp);
        ModlightComponentUpdateResult result;

        if (component == null)
        {
            result = new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.NotTracked, "", "");
        }
        else
        {
            try
            {
                // onlyWhenNewer: false - the person asked. See ModlightComponentUpdates on
                // why the automatic path is the one that must not act on a version it could
                // not read.
                result = await ModlightComponentUpdates.UpdateAsync(component, null, onlyWhenNewer: false,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception, "Download: updating yt-dlp threw.");
                result = new ModlightComponentUpdateResult(ModlightComponentUpdateOutcome.InstallFailed,
                    "", exception.Message);
            }
        }

        updatingTool = false;

        if (result.Installed)
        {
            Detail = string.Format(Strings.Download_UpdatedTool, result.Version);

            // Back into the queue, which starts it when nothing else is running. Retry
            // clears the offer through Start, so the row does not come back still holding
            // a remedy it has already taken.
            Retry();
            return;
        }

        Detail = result.Outcome switch
        {
            ModlightComponentUpdateOutcome.AlreadyCurrent =>
                string.Format(Strings.Download_UpdateToolCurrent, result.Version),
            ModlightComponentUpdateOutcome.CheckFailed => Strings.Download_UpdateToolOffline,
            // The path is what makes this sentence worth reading, so without one it is not
            // this sentence. Only reachable if yt-dlp went missing between the failure and
            // the click, which is somebody uninstalling it mid-download.
            ModlightComponentUpdateOutcome.NotOurs when result.Detail.Length > 0 =>
                string.Format(Strings.Download_UpdateToolNotOurs, result.Detail),
            ModlightComponentUpdateOutcome.Cancelled => Strings.Download_UpdateToolFailedPlain,
            _ => result.Detail.Length > 0
                ? string.Format(Strings.Download_UpdateToolFailed, result.Detail)
                : Strings.Download_UpdateToolFailedPlain
        };

        OnPropertyChanged(nameof(CanUpdateTool));
    }

    /// <summary>Called when the row is dropped from the list, so nothing is left running.</summary>
    public void Dispose()
    {
        pausing = false;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        YtDlpRun run = new(link, folder, format,
            progress => Dispatcher.UIThread.Post(() => Apply(progress)),
            found => Dispatcher.UIThread.Post(() => Named(found)),
            chosen => Dispatcher.UIThread.Post(() => Chose(chosen)));

        YtDlpOutcome outcome;

        try
        {
            outcome = await run.RunAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            outcome = new YtDlpOutcome { Error = string.Format(Strings.Download_ErrorRaw, exception.Message) };
        }

        await Dispatcher.UIThread.InvokeAsync(() => Finish(outcome));
    }

    private void Named(string found)
    {
        if (found.Length > 0)
        {
            Title = found;
        }
    }

    /// <summary>
    /// yt-dlp has picked a format. Say what it is, and say so if it is not what was asked for.
    /// </summary>
    /// <remarks>
    /// This arrives before the first byte, which is why it is worth showing at all: a person
    /// who wanted 1080p and is about to spend forty minutes fetching 360p can still stop it.
    /// </remarks>
    private void Chose(YtDlpFormatResult chosen)
    {
        if (chosen.Resolution > 0)
        {
            FormatText = string.Format(Strings.Download_QualityHeight, chosen.Resolution);
        }

        Note = chosen.Note;
    }

    private void Apply(YtDlpProgress progress)
    {
        if (!IsActive)
        {
            // A last update that crossed with a pause. Writing it would leave a paused row
            // showing a live speed.
            return;
        }

        if (progress.IsWorking)
        {
            State = DownloadState.Working;
            Detail = Strings.Download_StateWorking;
            SpeedText = "";
            EtaText = "";
            NotifyNumbers();
            return;
        }

        State = DownloadState.Downloading;
        Detail = Strings.Download_StateDownloading;
        Percent = progress.Percent;

        SizeText = progress.Total > 0
            ? string.Format(Strings.Download_SizeFormat, Size(progress.Downloaded), Size(progress.Total))
            : Size(progress.Downloaded);

        SpeedText = progress.Speed > 0
            ? string.Format(Strings.Download_SpeedFormat, Size((long)progress.Speed))
            : "";

        EtaText = progress.Eta >= 0 ? string.Format(Strings.Download_EtaFormat, Clock(progress.Eta)) : "";

        NotifyNumbers();
    }

    private void Finish(YtDlpOutcome outcome)
    {
        if (outcome.Title != null)
        {
            Named(outcome.Title);
        }

        if (outcome.Canceled)
        {
            if (pausing)
            {
                pausing = false;
                State = DownloadState.Paused;
                Detail = Strings.Download_StatePaused;
                SpeedText = "";
                EtaText = "";
                NotifyNumbers();
                return;
            }

            Stopped(Strings.Download_StateCanceled, DownloadState.Canceled);
            return;
        }

        if (!outcome.Success || outcome.FilePath == null)
        {
            // Before the state change, because that is what raises CanSignIn.
            needsSignIn = outcome.NeedsSignIn;
            needsUpdate = outcome.NeedsUpdate;

            Stopped(outcome.Error.Length > 0 ? outcome.Error : Strings.Download_ErrorUnknown, DownloadState.Failed);
            return;
        }

        filePath = outcome.FilePath;
        Percent = 100;
        SpeedText = "";
        EtaText = "";
        SizeText = Size(SizeOnDisk(outcome.FilePath));
        NotifyNumbers();

        Title = Path.GetFileName(outcome.FilePath);
        Detail = Deliver(outcome.FilePath);
        State = DownloadState.Completed;
        OnPropertyChanged(nameof(CanReveal));
    }

    /// <summary>
    /// Do whatever the destination says, and describe it.
    /// </summary>
    /// <remarks>
    /// The upload goes through <see cref="UploadManager"/>, the same route a screenshot
    /// takes: it makes a real task, it appears in the strip at the bottom of My space, it
    /// obeys the account's own after-upload settings and it puts the link on the clipboard
    /// because that is what those settings say by default. Writing a second uploader here
    /// would have been a second thing to keep in step with the server.
    ///
    /// The upload is handed over rather than waited for, which is why the wording says it
    /// is on its way rather than that it arrived. A failure surfaces on the task strip,
    /// where every other upload failure in this app already surfaces.
    /// </remarks>
    private string Deliver(string path)
    {
        if (destination == DownloadDestination.Disk)
        {
            ShowSavedToast(path);
            return string.Format(Strings.Download_SavedTo, folder);
        }

        try
        {
            UploadManager.UploadFile(path, UploadTaskSettings());
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return string.Format(Strings.Download_ErrorRaw, exception.Message);
        }

        return destination == DownloadDestination.Both
            ? string.Format(Strings.Download_SavedAndSent, folder)
            : Strings.Download_Sent;
    }

    /// <summary>
    /// The settings this row's upload runs under: the user's own, with the finished-task
    /// toast rebuilt around what actually happened to the file.
    /// </summary>
    /// <remarks>
    /// The buttons on that toast are a preference, and the shipped set includes Upload -
    /// which on a file that has just BEEN uploaded means a second copy on the server for
    /// one click on the most inviting icon in the notification. That is the reported bug,
    /// and it is not a labelling problem: the offer itself is wrong once the file is
    /// there, so it is not made.
    ///
    /// The three click actions are pinned for the same reason. Leaving them on the user's
    /// defaults would leave the duplicate one configurable left-click away, so the card
    /// opens the link, copies it, or does nothing.
    /// </remarks>
    private TaskSettings UploadTaskSettings()
    {
        TaskSettings settings = TaskSettings.GetDefaultTaskSettings();

        // That call has already copied the defaults in, so this GeneralSettings instance
        // is ours to write on and nothing global moves. The flag comes off because
        // GetSafeTaskSettings discards every override from a caller that "is using default
        // settings" - see the comment on it - and these buttons are exactly such an
        // override.
        settings.UseDefaultGeneralSettings = false;

        TaskSettingsGeneral general = settings.GeneralSettings;
        general.ToastWindowButtons = UploadedButtons();
        general.ToastWindowLeftClickAction = ToastClickAction.OpenUrl;
        general.ToastWindowMiddleClickAction = ToastClickAction.CopyUrl;
        general.ToastWindowRightClickAction = ToastClickAction.CloseNotification;

        return settings;
    }

    /// <summary>
    /// What the toast offers once the file is on the server.
    /// </summary>
    /// <remarks>
    /// "Open folder" only when a copy was kept. A Modlight-only download lands in a
    /// scratch folder that is cleaned up afterwards, so the button would be pointing at
    /// somewhere the file is about to leave.
    ///
    /// No "Open link" button, deliberately, even though this is the toast for a file that
    /// now has one: the left click on the toast body is already OpenUrl (see
    /// <see cref="UploadTaskSettings"/> a few lines up), so a button for it would be a
    /// second control for the thing the whole surface already does. Same rule the capture
    /// toast follows.
    /// </remarks>
    private List<NotificationActionButton> UploadedButtons()
    {
        List<NotificationActionButton> buttons = [];

        if (destination == DownloadDestination.Both)
        {
            buttons.Add(new(ToastClickAction.OpenFolder, Strings.Download_ToastOpenFolder));
        }

        buttons.Add(new(ToastClickAction.CopyUrl, Strings.Download_ToastCopyLink));

        return buttons;
    }

    /// <summary>
    /// What the toast offers for a file that is only on this PC.
    /// </summary>
    /// <remarks>
    /// Its own method, next to <see cref="UploadedButtons"/>, so the two lists can be read
    /// against each other. The whole fix is the difference between them.
    /// </remarks>
    private static List<NotificationActionButton> SavedButtons() =>
    [
        new(ToastClickAction.OpenFile, Strings.Download_ToastOpen),
        new(ToastClickAction.OpenFolder, Strings.Download_ToastOpenFolder),
        new(ToastClickAction.Upload, Strings.Download_ToastUpload)
    ];

    /// <summary>
    /// The toast for a download that stayed on this PC.
    /// </summary>
    /// <remarks>
    /// Raised here because nothing else will: a local-only download never becomes a task,
    /// so the finished-task notification every upload gets does not exist for it and the
    /// tab was finishing in silence unless you were watching the queue.
    ///
    /// Upload IS offered on this one. It is the same button that is wrong in the two cases
    /// above; what makes it right or wrong is whether the file is already on the server.
    ///
    /// The user's own toast preferences still decide whether a notification appears at all
    /// and how it looks - only the actions are ours, because only we know where the file
    /// went.
    /// </remarks>
    private void ShowSavedToast(string path)
    {
        TaskSettingsGeneral general = TaskSettings.GetDefaultTaskSettings().GeneralSettings;

        if (!general.ShowToastNotificationAfterTaskCompleted ||
            (general.DisableNotificationsOnFullscreen && CaptureHelpers.IsActiveWindowFullscreen()))
        {
            return;
        }

        NotificationWindow.Show(new NotificationWindowConfig
        {
            Duration = (int)(general.ToastWindowDuration * 1000),
            FadeDuration = (int)(general.ToastWindowFadeDuration * 1000),
            Placement = general.ToastWindowPlacement,
            Size = general.ToastWindowSize,
            ActionButtonSize = general.ToastWindowButtonSize,
            LeftClickAction = ToastClickAction.OpenFile,
            MiddleClickAction = ToastClickAction.OpenFolder,
            RightClickAction = ToastClickAction.CloseNotification,
            ActionButtons = SavedButtons(),
            FilePath = path,
            Title = Strings.Download_ToastSavedTitle,
            Text = Path.GetFileName(path)
        });
    }

    private void Stopped(string reason, DownloadState final)
    {
        SpeedText = "";
        EtaText = "";
        NotifyNumbers();

        Detail = reason;
        State = final;
    }

    private static long SizeOnDisk(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// A local file's size, spelled the way the rest of Windows spells it.
    /// </summary>
    /// <remarks>
    /// Not <see cref="ModlightFormat"/>: that one exists to agree with a number the web
    /// panel also prints. These bytes are on this PC and have no counterpart on a web page
    /// to disagree with, so they follow the user's own units preference like every other
    /// local size in the app.
    /// </remarks>
    private static string Size(long bytes) =>
        bytes > 0 ? bytes.ToSizeString(Program.Settings?.BinaryUnits ?? false, 1) : "";

    private static string Clock(int seconds)
    {
        TimeSpan left = TimeSpan.FromSeconds(Math.Min(seconds, 359999));

        return left.TotalHours >= 1
            ? left.ToString(@"h\:mm\:ss")
            : left.ToString(@"m\:ss");
    }

    private void NotifyNumbers()
    {
        OnPropertyChanged(nameof(HasNumbers));
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(CanUpdateTool));
        OnPropertyChanged(nameof(CanReveal));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(Icon));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
