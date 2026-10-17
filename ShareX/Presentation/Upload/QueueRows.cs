#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ShareX;

/// <summary>
/// One upload that has not gone out yet: what it is waiting for, and the two things a
/// person can do about it.
/// </summary>
/// <remarks>
/// The third state on the Upload tab, and the one that had to be got right. A queued
/// upload is not a failure - nothing has gone wrong, the file is safe and it will go by
/// itself - so it does not borrow the failure row's warning triangle or its red. It gets
/// the quiet clock, the ordinary text colour, and a sentence saying what it is waiting
/// for. The only rows here that look like trouble are the ones that ARE trouble: an item
/// the server refused, which has stopped trying and says so.
///
/// A row that is being sent right now stays in this section rather than moving up to
/// "In progress". Those rows are live tasks with a Cancel that stops a task; these are
/// queue entries with a Cancel that drops a queued upload, and an item that hopped
/// between the two lists every time the network flickered would be a row whose buttons
/// changed meaning while the pointer was over them.
///
/// A view over the item, not a copy of it: <see cref="Refresh"/> re-reads everything, so
/// the queue's own state is the only truth and the row cannot drift from it.
/// </remarks>
internal sealed class QueueRowViewModel : INotifyPropertyChanged
{
    private string name = "";
    private string stateText = "";
    private string detail = "";
    private string icon = LucideIcons.file;
    private string badge = LucideIcons.clock;
    private double progress;
    private bool isIndeterminate = true;
    private bool showProgress;
    private bool isStopped;
    private bool canSendNow = true;

    public QueueRowViewModel(ModlightQueueItem item)
    {
        Item = item;
        Refresh();
    }

    public ModlightQueueItem Item { get; }

    public string Id => Item.Id;

    /// <summary>The Lucide glyph for this kind of file.</summary>
    public string Icon
    {
        get => icon;
        private set => SetField(ref icon, value);
    }

    /// <summary>The glyph in the badge, which is what carries the state at a glance.</summary>
    public string Badge
    {
        get => badge;
        private set => SetField(ref badge, value);
    }

    public string Name
    {
        get => name;
        private set => SetField(ref name, value);
    }

    public string StateText
    {
        get => stateText;
        private set => SetField(ref stateText, value);
    }

    /// <summary>What it is waiting for, or how far it has got.</summary>
    public string Detail
    {
        get => detail;
        private set
        {
            SetField(ref detail, value);
            OnPropertyChanged(nameof(HasDetail));
        }
    }

    public bool HasDetail => detail.Length > 0;

    public double Progress
    {
        get => progress;
        private set => SetField(ref progress, value);
    }

    public bool IsIndeterminate
    {
        get => isIndeterminate;
        private set => SetField(ref isIndeterminate, value);
    }

    /// <summary>A bar is only drawn while there is something for it to say.</summary>
    public bool ShowProgress
    {
        get => showProgress;
        private set => SetField(ref showProgress, value);
    }

    /// <summary>
    /// The queue has stopped trying this one.
    /// </summary>
    /// <remarks>
    /// The one thing on this row that is allowed to look like a warning. Everything else
    /// in the section is going to happen on its own; this will not happen until somebody
    /// does something.
    /// </remarks>
    public bool IsStopped
    {
        get => isStopped;
        private set
        {
            SetField(ref isStopped, value);
            OnPropertyChanged(nameof(IsGoing));
        }
    }

    /// <summary>
    /// The other face of <see cref="IsStopped"/>, as a property rather than a negated
    /// binding - the same choice the sign-in panel on this tab makes, for the same reason:
    /// Avalonia's <c>!</c> prefix is not compile-checked, and a binding that silently
    /// broke would put both badges on the row at once.
    /// </summary>
    public bool IsGoing => !isStopped;

    public bool CanSendNow
    {
        get => canSendNow;
        private set => SetField(ref canSendNow, value);
    }

    /// <summary>Read everything back off the item. Cheap, and safe on every tick.</summary>
    public void Refresh()
    {
        Name = string.IsNullOrEmpty(Item.FileName) ? Strings.ThumbnailItemViewModel_UntitledTask : Item.FileName;
        Icon = UploadRow.IconFor(Item.FileName);

        bool sending = Item.State == ModlightQueueState.Sending;
        bool stopped = Item.State == ModlightQueueState.Blocked;

        IsStopped = stopped;
        CanSendNow = !sending;

        if (sending)
        {
            StateText = Strings.UploadQueue_StateSending;
            Badge = LucideIcons.cloud_upload;
            ShowMeasurement(Item.Progress);
            return;
        }

        ShowProgress = false;
        IsIndeterminate = true;
        Progress = 0;

        if (stopped)
        {
            StateText = Strings.UploadQueue_StateStopped;
            Badge = LucideIcons.octagon_alert;
            Detail = ModlightQueueText.Sentence(Item.Reason, Item.Detail);
            return;
        }

        StateText = Strings.UploadQueue_StateWaiting;

        // No network gets its own glyph. It is by far the most common reason to be here
        // and it is the one a person can act on without reading anything.
        Badge = Item.Reason == ModlightQueueReason.NoNetwork ? LucideIcons.wifi_off : LucideIcons.clock;

        string why = ModlightQueueText.Sentence(Item.Reason, Item.Detail);
        string when = ModlightQueueText.Countdown(Item.NextAttemptUtc);

        Detail = why.Length > 0 ? why + UploadRow.Separator + when : when;
    }

    /// <summary>
    /// Bytes, speed and time left, exactly as the in-flight rows put it.
    /// </summary>
    /// <remarks>
    /// Same strings, same units, same shape, because a resumed transfer showing its
    /// numbers differently from a fresh one would suggest they are different things.
    /// A resumed transfer starts at the offset the server confirmed, so the bar comes up
    /// at ninety percent rather than at nothing - which is the visible proof that the
    /// queue did the thing it exists to do.
    /// </remarks>
    private void ShowMeasurement(ProgressManager? measured)
    {
        if (measured == null || measured.Length <= 0)
        {
            ShowProgress = true;
            IsIndeterminate = true;
            Progress = 0;
            Detail = "";
            return;
        }

        bool binary = Program.Settings?.BinaryUnits ?? false;

        ShowProgress = true;
        IsIndeterminate = false;
        Progress = Math.Clamp(measured.Percentage, 0, 100);

        string text = string.Format(Strings.Upload_TransferProgress,
            measured.Position.ToSizeString(binary, 1),
            measured.Length.ToSizeString(binary, 1),
            ((long)measured.Speed).ToSizeString(binary, 1));

        TimeSpan remaining = measured.Remaining;

        if (remaining > TimeSpan.Zero)
        {
            text += UploadRow.Separator + (remaining.TotalSeconds < 90
                ? string.Format(Strings.Upload_EtaSeconds, (int)Math.Ceiling(remaining.TotalSeconds))
                : string.Format(Strings.Upload_EtaMinutes, (int)Math.Ceiling(remaining.TotalMinutes)));
        }

        Detail = text;
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
