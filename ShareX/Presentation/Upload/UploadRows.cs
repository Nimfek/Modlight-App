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
using ShareX.HistoryLib;
using ShareX.Localization;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace ShareX;

/// <summary>
/// One transfer that is still happening: what it is, how far it has got, and the one
/// thing that can be done about it while it is in the air.
/// </summary>
/// <remarks>
/// A view over a live <see cref="WorkerTask"/> rather than a copy of it. Nothing here
/// owns any state that the task does not already hold, so <see cref="Refresh"/> can be
/// called from any task event without the two ever disagreeing - and the numbers come
/// from the uploader's own <see cref="ProgressManager"/>, which is the only place in
/// the app that knows the real byte count going over the wire.
/// </remarks>
internal sealed class TransferViewModel : INotifyPropertyChanged
{
    private string name = "";
    private string state = "";
    private string detail = "";
    private double progress;
    private bool isIndeterminate = true;
    private bool canCancel;

    public TransferViewModel(WorkerTask task)
    {
        Task = task;
        Icon = UploadRow.IconFor(task.Info?.FilePath ?? task.Info?.FileName);
        Refresh();
    }

    public WorkerTask Task { get; }

    /// <summary>The Lucide glyph for this kind of file. Fixed for the row's lifetime.</summary>
    public string Icon { get; }

    public string Name
    {
        get => name;
        private set => SetField(ref name, value);
    }

    /// <summary>What the task says it is doing, in the user's language.</summary>
    public string State
    {
        get => state;
        private set => SetField(ref state, value);
    }

    /// <summary>Bytes, speed and time left, as one line. Empty until the first byte moves.</summary>
    public string Detail
    {
        get => detail;
        private set
        {
            SetField(ref detail, value);
            OnPropertyChanged(nameof(HasDetail));
        }
    }

    /// <summary>
    /// There is a measurement worth printing.
    /// </summary>
    /// <remarks>
    /// A property rather than a converter on the binding, so that the row's height
    /// comes from what is in it rather than from an empty line reserving space for a
    /// number that has not been measured yet.
    /// </remarks>
    public bool HasDetail => detail.Length > 0;

    public double Progress
    {
        get => progress;
        private set => SetField(ref progress, value);
    }

    /// <summary>
    /// Nothing measurable is happening yet.
    /// </summary>
    /// <remarks>
    /// True while the task is queued, and while a capture is still being written to
    /// disk - both of which precede the upload and neither of which has a byte count.
    /// A bar sitting at 0% through those would read as a stalled upload.
    /// </remarks>
    public bool IsIndeterminate
    {
        get => isIndeterminate;
        private set => SetField(ref isIndeterminate, value);
    }

    public bool CanCancel
    {
        get => canCancel;
        private set => SetField(ref canCancel, value);
    }

    /// <summary>Read everything back off the task. Cheap, and safe to call on every event.</summary>
    public void Refresh()
    {
        TaskInfo? info = Task.Info;

        Name = UploadRow.FirstNonEmpty(info?.FileName, Path.GetFileName(info?.FilePath ?? ""),
            Strings.ThumbnailItemViewModel_UntitledTask);
        State = StateOf(Task);
        CanCancel = Task.IsBusy;

        ProgressManager? measured = info?.Progress;

        // Length is checked, not just null: Percentage divides by it, and a zero-byte
        // upload would otherwise put NaN into the bar's Value and blank the control.
        if (measured == null || measured.Length <= 0)
        {
            Progress = 0;
            IsIndeterminate = true;
            Detail = "";
            return;
        }

        bool binary = Program.Settings?.BinaryUnits ?? false;

        Progress = Math.Clamp(measured.Percentage, 0, 100);
        IsIndeterminate = false;
        Detail = string.Format(Strings.Upload_TransferProgress,
            measured.Position.ToSizeString(binary, 1),
            measured.Length.ToSizeString(binary, 1),
            ((long)measured.Speed).ToSizeString(binary, 1)) + Eta(measured.Remaining);
    }

    /// <summary>
    /// What the task calls its own state, falling back to a name for it.
    /// </summary>
    /// <remarks>
    /// <c>Info.Status</c> is set and localised by <see cref="WorkerTask"/> itself, so
    /// preferring it means this row says exactly what the rest of the app says. It is
    /// empty for the moments before the task has started, which is what the fallback
    /// covers.
    /// </remarks>
    private static string StateOf(WorkerTask task)
    {
        string? reported = task.Info?.Status;

        if (!string.IsNullOrEmpty(reported))
        {
            return reported!;
        }

        return task.Status == TaskStatus.InQueue ? Strings.Upload_StateQueued : Strings.Upload_StateWorking;
    }

    /// <summary>Time left, or nothing at all while there is no measured speed to divide by.</summary>
    private static string Eta(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "";
        }

        // Minutes above ninety seconds rather than sixty: "1 min left" appearing for a
        // transfer that has 61 seconds to run rounds the wrong way every time.
        string text = remaining.TotalSeconds < 90
            ? string.Format(Strings.Upload_EtaSeconds, (int)Math.Ceiling(remaining.TotalSeconds))
            : string.Format(Strings.Upload_EtaMinutes, (int)Math.Ceiling(remaining.TotalMinutes));

        return UploadRow.Separator + text;
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

/// <summary>
/// One upload that has finished, however it finished.
/// </summary>
/// <remarks>
/// Built either from a task this session ran or from a row in the app's own history
/// store, because those are the only two places the answer exists and neither has all
/// of it. The store keeps successes for ever and knows nothing about failures - it is
/// appended to only when a task produced something to record - while the session knows
/// both but forgets at exit. Presenting them as one list is the whole point: "did that
/// go up, and what is the link" is one question, not two.
///
/// Immutable apart from the copy button's acknowledgement, which is the only thing on
/// the row that can change after it is built.
/// </remarks>
internal sealed class UploadHistoryViewModel : INotifyPropertyChanged
{
    /// <summary>How long the copy button admits to having copied something.</summary>
    private static readonly TimeSpan copyAcknowledgement = TimeSpan.FromSeconds(2);

    private string copyIcon = LucideIcons.copy;
    private string copyTooltip = Strings.Upload_CopyLink;
    private int copyVersion;

    /// <summary>An upload this session ran, in whatever state it ended.</summary>
    public UploadHistoryViewModel(WorkerTask task)
    {
        TaskInfo? info = task.Info;

        Task = task;
        Name = UploadRow.FirstNonEmpty(info?.FileName, Path.GetFileName(info?.FilePath ?? ""),
            Strings.ThumbnailItemViewModel_UntitledTask);
        Link = UploadRow.FirstNonEmpty(info?.Result?.ShortenedURL, info?.Result?.URL);

        // TaskEndTime is default for a task that never ran to completion - a queued
        // upload cancelled before it started, for one. Now is the honest answer there:
        // it is when this row came to exist, and sorting it to 0001 would bury it under
        // everything the store remembers.
        When = info != null && info.TaskEndTime != default ? info.TaskEndTime : DateTime.Now;

        IsFailed = task.Status == TaskStatus.Failed;
        IsCanceled = task.Status == TaskStatus.Stopped;
        Reason = IsFailed ? UploadRow.ReasonFor(info) : "";

        // Retry re-runs the ordinary file upload, which needs the file still to be
        // there. A capture whose local copy was deleted after uploading, or a text or
        // clipboard upload that never had a file, cannot be offered one.
        RetryPath = FileFor(info?.FilePath);

        // UploaderHost is where the app records this having gone: the destination's
        // own name. Not the server-side folder - the upload response names that as a
        // bare id, and the history store this list is also built from has no column
        // for it at all, so a folder shown here would be blank on most rows.
        Meta = MetaFor(When, info?.UploaderHost ?? "");
        StateText = StateTextFor(IsFailed, IsCanceled);
    }

    /// <summary>An upload the offline queue finished with, however it finished.</summary>
    /// <remarks>
    /// A third source for one list, for the same reason the other two are pooled: "did that
    /// go up, and what is the link" is one question. An upload that waited out an outage and
    /// went at four in the morning belongs in the same list, in time order, as one that went
    /// straight away - and a cancellation from the queue has to appear here for the same
    /// reason a cancelled task does, because a row that simply vanishes reads as success.
    ///
    /// The reason is built here rather than carried on the outcome: the queue stores codes,
    /// not sentences, precisely so that they can be put into words in whatever language is
    /// current when somebody reads them.
    /// </remarks>
    public UploadHistoryViewModel(ModlightQueueOutcome outcome)
    {
        ModlightQueueItem item = outcome.Item;

        Task = null;
        Name = UploadRow.FirstNonEmpty(item.FileName, Strings.ThumbnailItemViewModel_UntitledTask);
        Link = outcome.Link ?? "";
        When = outcome.When;

        IsFailed = !outcome.Succeeded && !outcome.Canceled;
        IsCanceled = outcome.Canceled;
        Reason = IsFailed ? ModlightQueueText.Sentence(outcome.Reason, outcome.Detail) : "";

        // Only when the file is the user's own. A spooled capture has just been deleted -
        // it was a copy the queue made because nothing else held those bytes - so offering
        // to send it again would offer a file that is no longer there.
        RetryPath = FileFor(item.OwnsSource ? null : item.SourcePath);

        Meta = MetaFor(When, "Modlight");
        StateText = StateTextFor(IsFailed, IsCanceled);
    }

    /// <summary>An upload from an earlier session, as the history store kept it.</summary>
    public UploadHistoryViewModel(HistoryItem item)
    {
        Task = null;
        Name = UploadRow.FirstNonEmpty(item.FileName, Path.GetFileName(item.FilePath ?? ""),
            Strings.ThumbnailItemViewModel_UntitledTask);
        Link = UploadRow.FirstNonEmpty(item.ShortenedURL, item.URL);
        When = item.DateTime;

        // The store only ever holds uploads that produced something, so every row from
        // it is a success. There is nowhere in it for a failure to have been written.
        IsFailed = false;
        IsCanceled = false;
        Reason = "";
        RetryPath = FileFor(item.FilePath);

        Meta = MetaFor(When, item.Host ?? "");
        StateText = StateTextFor(false, false);
    }

    /// <summary>The task this row came from, or null when it came from the store.</summary>
    public WorkerTask? Task { get; }

    public string Name { get; }

    /// <summary>When it finished. The list is ordered on this, newest first.</summary>
    public DateTime When { get; }

    /// <summary>Time and destination, as one line.</summary>
    public string Meta { get; }

    public string Link { get; }
    public bool HasLink => Link.Length > 0;

    public bool IsFailed { get; }
    public bool IsCanceled { get; }
    public bool IsDone => !IsFailed && !IsCanceled;

    public string StateText { get; }

    /// <summary>The server's or the uploader's own sentence about the failure.</summary>
    public string Reason { get; }

    public bool HasReason => Reason.Length > 0;

    public string RetryPath { get; }
    public bool CanRetry => RetryPath.Length > 0 && !IsDone;

    public string CopyIcon
    {
        get => copyIcon;
        private set => SetField(ref copyIcon, value);
    }

    public string CopyTooltip
    {
        get => copyTooltip;
        private set => SetField(ref copyTooltip, value);
    }

    /// <summary>
    /// Put the link on the clipboard and say so on the button for a moment.
    /// </summary>
    /// <remarks>
    /// The acknowledgement is on the button itself rather than a notification because
    /// copying a link is the most repeated action on this screen, and a toast for it
    /// thirty times a day is noise. The version counter means a second click restarts
    /// the two seconds instead of the first click's timer cutting the second one short.
    /// </remarks>
    public void Copy()
    {
        if (!HasLink)
        {
            return;
        }

        ClipboardHelpers.CopyText(Link);

        int version = ++copyVersion;
        CopyIcon = LucideIcons.check;
        CopyTooltip = Strings.Upload_Copied;

        DispatcherTimer.RunOnce(() =>
        {
            if (version == copyVersion)
            {
                CopyIcon = LucideIcons.copy;
                CopyTooltip = Strings.Upload_CopyLink;
            }
        }, copyAcknowledgement);
    }

    private static string StateTextFor(bool failed, bool canceled)
    {
        if (failed)
        {
            return Strings.Upload_StateFailed;
        }

        return canceled ? Strings.Upload_StateCanceled : Strings.Upload_StateDone;
    }

    private static string MetaFor(DateTime when, string destination)
    {
        string text = when.ToString("g", CultureInfo.CurrentCulture);

        return destination.Length > 0 ? text + UploadRow.Separator + destination : text;
    }

    private static string FileFor(string? path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path! : "";
        }
        catch (Exception exception)
        {
            // A path from the history store is whatever was on this machine months ago;
            // it can be malformed, or on a drive that is no longer there. Not being able
            // to answer "does this still exist" only costs the row its Retry button.
            DebugHelper.WriteException(exception);
            return "";
        }
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

/// <summary>The handful of things both kinds of row in this tab decide the same way.</summary>
internal static class UploadRow
{
    /// <summary>What separates two facts on one line, matching the My space cards.</summary>
    public const string Separator = "  ·  ";

    public static string IconFor(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return LucideIcons.file;
        }

        if (FileHelpers.IsVideoFile(path))
        {
            return LucideIcons.file_video;
        }

        if (FileHelpers.IsImageFile(path))
        {
            return LucideIcons.file_image;
        }

        if (FileHelpers.IsTextFile(path))
        {
            return LucideIcons.file_text;
        }

        return LucideIcons.file;
    }

    /// <summary>
    /// One sentence about why an upload failed.
    /// </summary>
    /// <remarks>
    /// The first error only, and only its first line. The Modlight uploader puts a
    /// sentence a person can act on at the front of that list precisely so that this
    /// is the one shown; everything underneath it is the exception dump a bug report
    /// needs, which belongs in the log rather than on a row in a list.
    ///
    /// Shared by the history row and the completion toast rather than written twice:
    /// they are two renderings of one fact, and the only thing worse than a failure
    /// with no reason is two surfaces giving different reasons for the same one.
    /// </remarks>
    public static string ReasonFor(TaskInfo? info)
    {
        UploaderErrorManager? errors = info?.Result?.Errors;

        if (errors == null || errors.Count == 0)
        {
            return Strings.Upload_NoReason;
        }

        string text = errors.Errors[0].Text ?? "";
        int newline = text.IndexOf('\n');

        if (newline >= 0)
        {
            text = text.Substring(0, newline);
        }

        text = text.Trim();

        return text.Length > 0 ? text : Strings.Upload_NoReason;
    }

    public static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate!;
            }
        }

        return "";
    }
}
