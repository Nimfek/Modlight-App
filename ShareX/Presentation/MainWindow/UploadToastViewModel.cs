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

namespace ShareX;

/// <summary>
/// One finished upload, said once and then gone.
/// </summary>
/// <remarks>
/// This replaces the "this session" strip that used to sit along the bottom of My space.
/// The owner's objection to that strip was not how it looked: a permanent list of the
/// files you have sent is a second, worse copy of the library that is already the whole
/// point of the tab, and it answered a question - "did that go up?" - that stops being
/// interesting about four seconds after it is asked. A notice that appears, says which
/// of the two things happened, and leaves answers the same question without keeping a
/// list nobody asked for. The Upload tab remains the place where transfers are watched
/// and history is read; that is a different question and it keeps its own screen.
///
/// A view over the finished task rather than a copy of it, with one exception: the
/// folder name is resolved at construction. By the time a large upload finishes, My
/// space may be showing somewhere else entirely, so an id looked up later would name the
/// wrong folder - or none, if the shelf has moved on.
/// </remarks>
internal sealed class UploadToastViewModel : INotifyPropertyChanged
{
    /// <summary>How long the copy button admits to having copied something.</summary>
    private static readonly TimeSpan copyAcknowledgement = TimeSpan.FromSeconds(2);

    private string copyIcon = LucideIcons.copy;
    private string copyTooltip = Strings.Upload_CopyLink;
    private int copyVersion;

    public UploadToastViewModel(WorkerTask task, string folderName)
    {
        TaskInfo? info = task.Info;

        Task = task;
        Name = UploadRow.FirstNonEmpty(info?.FileName, Path.GetFileName(info?.FilePath ?? ""),
            Strings.ThumbnailItemViewModel_UntitledTask);
        Link = UploadRow.FirstNonEmpty(info?.Result?.ShortenedURL, info?.Result?.URL);
        IsFailed = task.Status == TaskStatus.Failed || Link.Length == 0;

        if (IsFailed)
        {
            Icon = LucideIcons.triangle_alert;
            Title = Strings.MainWindow_ToastFailed;

            // The uploader puts one sentence a person can act on at the head of the
            // error list precisely so this is the line that gets shown; the exception
            // dump underneath it belongs in the log, not in the corner of a window.
            Detail = UploadRow.ReasonFor(info);
        }
        else
        {
            Icon = LucideIcons.circle_check;

            // Naming the folder is what makes this worth putting on screen at all: it
            // is the answer to "where did that go", which since the ambient upload
            // target exists is a question with more than one possible answer.
            Title = folderName.Length > 0
                ? string.Format(Strings.MainWindow_ToastUploadedTo, folderName)
                : Strings.MainWindow_ToastUploaded;

            Detail = Link;
        }
    }

    /// <summary>
    /// Plain notice: something happened that no upload produced.
    /// </summary>
    /// <remarks>
    /// The other half of what this corner is for. Everything the library tab can now do -
    /// moving files between folders, publishing them, converting one and saving it to
    /// disk, emptying a folder into the trash - finishes somewhere the user may not be
    /// looking, and each of those needs the same three lines this already draws. A modal
    /// per action would be the alternative, and twenty message boxes in a row is not a
    /// report, it is a punishment.
    /// </remarks>
    public static UploadToastViewModel Notice(string icon, string title, string detail, bool failed) =>
        new(icon, title, detail, failed);

    private UploadToastViewModel(string icon, string title, string detail, bool failed)
    {
        Task = null;
        Name = "";
        Link = "";
        Icon = icon;
        Title = title;
        Detail = detail;
        IsFailed = failed;
    }

    /// <summary>The task this came from, or null for a notice that no task produced.</summary>
    public WorkerTask? Task { get; }

    public string Icon { get; }

    /// <summary>What happened, and where it went. The line that is read first.</summary>
    public string Title { get; }

    public string Name { get; }

    /// <summary>The link on a success, the reason on a failure.</summary>
    public string Detail { get; }

    public bool IsFailed { get; }

    /// <summary>
    /// The other face of <see cref="IsFailed"/>.
    /// </summary>
    /// <remarks>
    /// A property rather than a <c>!</c> in the binding, for the reason the rest of this
    /// window states: the layout is not compile-checked, and a binding that silently fails
    /// would paint the tick and the warning glyph in the same colour.
    /// </remarks>
    public bool IsDone => !IsFailed;

    public string Link { get; }

    public bool HasLink => Link.Length > 0;

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
    /// Put the link on the clipboard and say so on the button.
    /// </summary>
    /// <remarks>
    /// The acknowledgement is versioned so that copying twice in quick succession does
    /// not have the first timer reset the tick while the second copy is still fresh.
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
