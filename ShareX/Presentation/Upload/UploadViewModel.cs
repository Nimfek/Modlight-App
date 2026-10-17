#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Threading;
using ShareX.HelpersLib;
using ShareX.HistoryLib;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ShareX;

/// <summary>
/// The Upload tab: what is going up right now, and what has gone up before.
/// </summary>
/// <remarks>
/// The owner's description of this tab is "upload control rather than a place people
/// add files from" - so it watches, it does not originate. Everything on it is a view
/// over work that already exists somewhere else:
///
///   in flight  - <see cref="TaskManager"/>'s own task list, the same list the strip at
///                the bottom of My space draws from. Nothing is duplicated and nothing
///                is polled; the task events carry the progress the uploader measured.
///   waiting    - <see cref="ModlightUploadQueue"/>: uploads that could not go out when
///                they were made and will go by themselves later. A third state, and
///                deliberately not drawn as another kind of failure - nothing has gone
///                wrong with these, they are simply not finished yet.
///   finished   - those same tasks once they end, plus the app's history store for the
///                sessions before this one, plus whatever the queue eventually made of
///                what it was holding. Three sources because none is complete: the store
///                outlives the process but only ever records successes, the session
///                records failures and cancellations but forgets at exit, and the
///                queue's endings happen when neither of the other two is looking.
///
/// UI thread only. The task events arrive on worker threads and are posted across.
/// </remarks>
internal sealed class UploadViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// How many finished uploads to keep on screen.
    /// </summary>
    /// <remarks>
    /// The owner's account has around 8700 uploads behind it, and this is a control
    /// panel rather than a library - My space is where the whole collection lives. A
    /// hundred is several screens of scrolling, which is as far as "what did I just
    /// send" ever reaches.
    /// </remarks>
    private const int MaxHistoryRows = 100;

    private bool attached;
    private DispatcherTimer? tick;
    private bool historyRead;
    private bool isLoadingHistory;
    private string historyError = "";
    private bool isSignedOut;

    /// <summary>Uploads still happening, newest first.</summary>
    public ObservableCollection<TransferViewModel> Transfers { get; } = new();

    /// <summary>
    /// Uploads waiting for a connection, oldest first.
    /// </summary>
    /// <remarks>
    /// Oldest first, unlike the other two lists, because this one is a queue: the order it
    /// is drawn in is the order it will be sent in, and a queue displayed backwards is a
    /// queue whose next item is at the bottom.
    /// </remarks>
    public ObservableCollection<QueueRowViewModel> Queue { get; } = new();

    /// <summary>Uploads that have ended, newest first.</summary>
    public ObservableCollection<UploadHistoryViewModel> History { get; } = new();

    public bool HasTransfers => Transfers.Count > 0;
    public bool HasQueue => Queue.Count > 0;
    public bool HasHistory => History.Count > 0;

    public bool IsLoadingHistory
    {
        get => isLoadingHistory;
        private set
        {
            SetField(ref isLoadingHistory, value);
            NotifyPanels();
        }
    }

    public bool HasHistoryError => historyError.Length > 0;

    public string HistoryErrorText => historyError;

    /// <summary>Nobody is signed in, so nothing can be uploaded until somebody is.</summary>
    public bool IsSignedOut
    {
        get => isSignedOut;
        private set
        {
            SetField(ref isSignedOut, value);
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyBody));
        }
    }

    /// <summary>
    /// The other face of <see cref="IsSignedOut"/>, as a property rather than a negated
    /// binding: Avalonia's <c>!</c> prefix is not compile-checked here, and a silently
    /// broken binding would put both of the empty state's buttons on screen at once.
    /// </summary>
    public bool IsSignedIn => !isSignedOut;

    /// <summary>
    /// Nothing to report at all.
    /// </summary>
    /// <remarks>
    /// Deliberately not shown while the history is still being read or after it failed
    /// to be read: an empty list and an unread list are different facts, and a screen
    /// that says "nothing has gone up yet" to somebody with a thousand uploads because
    /// a file was locked is the exact mistake this app has already made once.
    /// </remarks>
    public bool ShowEmptyPanel => !HasTransfers && !HasQueue && !HasHistory && !isLoadingHistory && !HasHistoryError;

    /// <summary>The empty state says which of the two nothings this is.</summary>
    public string EmptyTitle => isSignedOut ? Strings.Upload_SignedOutTitle : Strings.Upload_EmptyTitle;

    public string EmptyBody => isSignedOut ? Strings.Upload_SignedOutBody : Strings.Upload_EmptyBody;

    /// <summary>
    /// Start following the task list.
    /// </summary>
    /// <remarks>
    /// Called when the control joins the visual tree rather than from its constructor,
    /// so that the handlers are gone the moment the window is closed - a static event
    /// held by a dead window is the classic way a tab like this outlives its process.
    /// Everything is rebuilt from <see cref="TaskManager.Tasks"/> on the way in, which
    /// is also what covers whatever happened while it was detached.
    /// </remarks>
    public void Attach()
    {
        if (attached)
        {
            return;
        }

        attached = true;
        TaskManager.TaskAdded += OnTaskEvent;
        TaskManager.TaskChanged += OnTaskEvent;
        TaskManager.TaskRemoved += OnTaskRemoved;

        ModlightUploadQueue.Changed += OnQueueChanged;
        ModlightUploadQueue.Progressed += OnQueueProgressed;
        ModlightUploadQueue.Finished += OnQueueFinished;

        Resync();

        if (!historyRead)
        {
            historyRead = true;
            _ = LoadHistoryAsync();
        }
    }

    public void Detach()
    {
        if (!attached)
        {
            return;
        }

        attached = false;
        TaskManager.TaskAdded -= OnTaskEvent;
        TaskManager.TaskChanged -= OnTaskEvent;
        TaskManager.TaskRemoved -= OnTaskRemoved;

        ModlightUploadQueue.Changed -= OnQueueChanged;
        ModlightUploadQueue.Progressed -= OnQueueProgressed;
        ModlightUploadQueue.Finished -= OnQueueFinished;

        StopTicking();
    }

    /// <summary>Re-read both sources. The one action offered when the history could not be read.</summary>
    public void Refresh()
    {
        Resync();

        // The rows the store gave us are the only ones that can be stale here; the
        // session's own are answered by Resync. Dropping them first stops the reload
        // from having to reconcile with a list that already contains its own output.
        foreach (UploadHistoryViewModel stored in History.Where(row => row.Task == null).ToArray())
        {
            History.Remove(stored);
        }

        historyError = "";
        OnPropertyChanged(nameof(HasHistoryError));
        OnPropertyChanged(nameof(HistoryErrorText));
        NotifyHistory();

        historyRead = true;
        _ = LoadHistoryAsync();
    }

    /// <summary>Bring the rows in line with the task list and with who is signed in.</summary>
    public void Resync()
    {
        IsSignedOut = ModlightSignIn.Current == null;

        SyncQueue();

        WorkerTask[] tasks = TaskManager.Tasks.ToArray();

        // A task that has been cleared from the list is gone from this tab too. Rows
        // that came from the store are left alone: they never had a task to lose.
        foreach (TransferViewModel row in Transfers.Where(row => !tasks.Contains(row.Task)).ToArray())
        {
            Transfers.Remove(row);
        }

        foreach (UploadHistoryViewModel row in History
                     .Where(row => row.Task != null && !tasks.Contains(row.Task)).ToArray())
        {
            History.Remove(row);
        }

        NotifyTransfers();
        NotifyHistory();

        foreach (WorkerTask task in tasks)
        {
            Sync(task);
        }
    }

    /* ------------------------------------------------------------------ */
    /* The queue                                                           */
    /* ------------------------------------------------------------------ */

    private void OnQueueChanged() => Dispatcher.UIThread.Post(SyncQueue);

    /// <summary>
    /// Bytes moved on the item the queue is sending.
    /// </summary>
    /// <remarks>
    /// Only the sending row is touched, and only its own properties: rebuilding the list
    /// on a progress tick would replace the row several times a second and take the
    /// pointer out from under whatever button it was over.
    /// </remarks>
    private void OnQueueProgressed() => Dispatcher.UIThread.Post(() =>
    {
        foreach (QueueRowViewModel row in Queue)
        {
            if (row.Item.State == ModlightQueueState.Sending)
            {
                row.Refresh();
            }
        }
    });

    private void OnQueueFinished(ModlightQueueOutcome outcome) => Dispatcher.UIThread.Post(() =>
    {
        SyncQueue();
        Insert(new UploadHistoryViewModel(outcome));
    });

    /// <summary>
    /// Bring the waiting list in line with the queue.
    /// </summary>
    /// <remarks>
    /// Rows are matched to items by id and kept, rather than rebuilt. Two reasons, and
    /// the second is the one that bites: a rebuilt list loses the scroll position every
    /// time an item's countdown ticks over, and a rebuilt row is a different object, so
    /// the button the pointer is resting on is replaced under it mid-click.
    /// </remarks>
    private void SyncQueue()
    {
        ModlightQueueItem[] queued = ModlightUploadQueue.Snapshot();
        bool changed = false;

        for (int index = Queue.Count - 1; index >= 0; index--)
        {
            if (!queued.Any(item => item.Id == Queue[index].Id))
            {
                Queue.RemoveAt(index);
                changed = true;
            }
        }

        for (int index = 0; index < queued.Length; index++)
        {
            ModlightQueueItem item = queued[index];
            QueueRowViewModel? row = Queue.FirstOrDefault(existing => existing.Id == item.Id);

            if (row == null)
            {
                Queue.Insert(Math.Min(index, Queue.Count), new QueueRowViewModel(item));
                changed = true;
                continue;
            }

            row.Refresh();
        }

        if (changed)
        {
            OnPropertyChanged(nameof(HasQueue));
            NotifyPanels();
        }

        if (Queue.Count > 0)
        {
            StartTicking();
        }
        else
        {
            StopTicking();
        }
    }

    /// <summary>
    /// Keep the "next try in ..." counts honest.
    /// </summary>
    /// <remarks>
    /// The queue raises nothing while it waits - there is nothing to raise, the item is
    /// simply sitting there - so a countdown drawn once would freeze at whatever it said
    /// when the row was built and quietly become a lie. One second, and only while there
    /// is something to count down; the timer stops with the last row and when the tab is
    /// closed.
    /// </remarks>
    private void StartTicking()
    {
        if (tick != null)
        {
            return;
        }

        tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (QueueRowViewModel row in Queue)
            {
                row.Refresh();
            }
        });

        tick.Start();
    }

    private void StopTicking()
    {
        tick?.Stop();
        tick = null;
    }

    private void OnTaskEvent(WorkerTask task) => Dispatcher.UIThread.Post(() => Sync(task));

    private void OnTaskRemoved(WorkerTask task) => Dispatcher.UIThread.Post(() =>
    {
        RemoveTransfer(task);
        RemoveHistory(task);
    });

    /// <summary>
    /// Put one task on whichever of the two lists it now belongs to.
    /// </summary>
    /// <remarks>
    /// Idempotent, because it runs on every progress tick as well as on the two
    /// transitions that matter. The two lists are kept mutually exclusive here rather
    /// than by the layout: a task that appeared in both would be cancellable and
    /// retryable at the same time.
    /// </remarks>
    private void Sync(WorkerTask task)
    {
        if (task?.Info == null || !IsUpload(task))
        {
            return;
        }

        if (task.IsBusy)
        {
            RemoveHistory(task);

            TransferViewModel? row = Transfers.FirstOrDefault(x => ReferenceEquals(x.Task, task));

            if (row == null)
            {
                Transfers.Insert(0, new TransferViewModel(task));
                NotifyTransfers();
            }
            else
            {
                row.Refresh();
            }

            return;
        }

        RemoveTransfer(task);
        RemoveHistory(task);
        Insert(new UploadHistoryViewModel(task));
    }

    /// <summary>
    /// Whether this task is something this tab is about.
    /// </summary>
    /// <remarks>
    /// An upload job qualifies whatever became of it, which is what keeps failures on
    /// the list. Anything else qualifies only once it has produced a link - that is the
    /// case of the entries restored from the recent-task list at startup, which carry
    /// no job and no settings but are uploads all the same. A capture that was only
    /// ever saved to disk is neither, and does not belong here.
    /// </remarks>
    private static bool IsUpload(WorkerTask task)
    {
        TaskInfo info = task.Info;

        if (info.IsUploadJob)
        {
            return true;
        }

        return !string.IsNullOrEmpty(info.Result?.URL) || !string.IsNullOrEmpty(info.Result?.ShortenedURL);
    }

    private void RemoveTransfer(WorkerTask task)
    {
        TransferViewModel? row = Transfers.FirstOrDefault(x => ReferenceEquals(x.Task, task));

        if (row != null)
        {
            Transfers.Remove(row);
            NotifyTransfers();
        }
    }

    private void RemoveHistory(WorkerTask task)
    {
        UploadHistoryViewModel? row = History.FirstOrDefault(x => ReferenceEquals(x.Task, task));

        if (row != null)
        {
            History.Remove(row);
            NotifyHistory();
        }
    }

    /// <summary>
    /// File one finished upload into the list, newest first.
    /// </summary>
    /// <remarks>
    /// A session row displaces a stored row with the same link. The two describe the
    /// same upload - the store is appended to as each task completes, and the recent-task
    /// entries restored at startup are already in it - and the session's copy is the one
    /// that knows whether it failed.
    /// </remarks>
    private void Insert(UploadHistoryViewModel row)
    {
        if (row.HasLink)
        {
            UploadHistoryViewModel? duplicate = History.FirstOrDefault(existing =>
                !ReferenceEquals(existing, row) &&
                string.Equals(existing.Link, row.Link, StringComparison.OrdinalIgnoreCase) &&
                (existing.Task == null || row.Task == null));

            if (duplicate != null)
            {
                // The session's own row wins; a stored row arriving for an upload this
                // session already reported is dropped instead.
                if (duplicate.Task != null && row.Task == null)
                {
                    return;
                }

                History.Remove(duplicate);
            }
        }

        int index = 0;

        while (index < History.Count && History[index].When >= row.When)
        {
            index++;
        }

        History.Insert(index, row);

        while (History.Count > MaxHistoryRows)
        {
            History.RemoveAt(History.Count - 1);
        }

        NotifyHistory();
    }

    /// <summary>
    /// Read the sessions before this one out of the app's own history store.
    /// </summary>
    /// <remarks>
    /// The store is the app's, not the server's: it is what the History window shows and
    /// what every completed task is appended to, so reading it costs nothing and cannot
    /// disagree with the rest of the app. Asking the server for the same list would be a
    /// different question with a different answer - it holds what the account has, not
    /// what this machine sent.
    /// </remarks>
    private async System.Threading.Tasks.Task LoadHistoryAsync()
    {
        HistoryManager? manager = Program.HistoryManager;

        if (manager == null)
        {
            // Only reachable if the tab is somehow shown before the application config
            // has been loaded, which is what connects the store. Nothing to say about
            // it on screen; the session's own uploads still list, and Refresh retries.
            historyRead = false;
            return;
        }

        IsLoadingHistory = true;

        try
        {
            List<HistoryItem> items = await manager.GetHistoryItemsAsync();

            foreach (HistoryItem item in items
                         .Where(item => item != null && !string.IsNullOrEmpty(item.URL))
                         .OrderByDescending(item => item.DateTime)
                         .Take(MaxHistoryRows))
            {
                Insert(new UploadHistoryViewModel(item));
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);

            historyError = string.Format(Strings.Upload_HistoryUnreadable, exception.Message);
            OnPropertyChanged(nameof(HasHistoryError));
            OnPropertyChanged(nameof(HistoryErrorText));
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }

    private void NotifyTransfers()
    {
        OnPropertyChanged(nameof(HasTransfers));
        NotifyPanels();
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(HasHistory));
        NotifyPanels();
    }

    private void NotifyPanels() => OnPropertyChanged(nameof(ShowEmptyPanel));

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
