#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>One copy inside a set, with a tick box against it.</summary>
/// <remarks>
/// The row carries its own <see cref="IsSelected"/> rather than the window holding a set
/// of ids, because the two numbers the footer shows - how many files and how many bytes -
/// have to follow every individual tick, and a selection kept beside the list is a
/// selection that can disagree with the boxes on screen.
/// </remarks>
internal sealed class DuplicateFileViewModel : INotifyPropertyChanged
{
    private readonly Action changed;
    private bool isSelected;

    internal DuplicateFileViewModel(ModlightDuplicateFile row, bool isOldest, Action changed)
    {
        Row = row;
        IsOldest = isOldest;
        this.changed = changed;
    }

    internal ModlightDuplicateFile Row { get; }

    internal string Id => Row.Id ?? "";

    public string Name => string.IsNullOrEmpty(Row.Filename) ? Row.Code ?? "" : Row.Filename!;

    /// <summary>
    /// The oldest copy in the set, which is the one the default selection keeps.
    /// </summary>
    /// <remarks>
    /// The server sorts a set oldest first and this reads position 0, which is the same
    /// rule the web panel's "keep the oldest" default hangs on. It is a default and not a
    /// law: the box is tickable like any other, because the oldest copy is not always the
    /// one in the folder somebody wants to keep.
    /// </remarks>
    public bool IsOldest { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            OnPropertyChanged();
            changed();
        }
    }

    public string SizeText => ModlightFormat.CloudSize(Row.Size);

    public string AgeText => CloudFileViewModel.AgeOf(ParsedDate);

    /// <summary>Where the copy lives, so a decision can be made without opening anything.</summary>
    /// <remarks>
    /// The folder is the single most useful thing to know about two identical files: the
    /// one in a named folder was filed on purpose and the one at the top of the library
    /// usually was not. A copy in no folder says so rather than showing a blank.
    /// </remarks>
    public string FolderText =>
        string.IsNullOrEmpty(Row.FolderName) ? Strings.MySpace_DuplicatesNoFolder : Row.FolderName!;

    /// <summary>The badge on the oldest row. Empty everywhere else, so nothing is drawn.</summary>
    public string OldestText => IsOldest ? Strings.MySpace_DuplicatesOldest : "";

    private DateTimeOffset? ParsedDate =>
        DateTimeOffset.TryParse(Row.CreatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One set of byte-for-byte identical files.</summary>
internal sealed class DuplicateGroupViewModel : INotifyPropertyChanged
{
    private readonly Action changed;
    private bool dismissed;
    private bool busy;

    internal DuplicateGroupViewModel(ModlightDuplicateGroup group, Action changed)
    {
        Group = group;
        this.changed = changed;
        dismissed = group.Dismissed;

        List<ModlightDuplicateFile> rows = group.Files ?? new List<ModlightDuplicateFile>();

        for (int i = 0; i < rows.Count; i++)
        {
            Files.Add(new DuplicateFileViewModel(rows[i], i == 0, changed));
        }
    }

    internal ModlightDuplicateGroup Group { get; }

    internal string Checksum => Group.Checksum ?? "";

    public ObservableCollection<DuplicateFileViewModel> Files { get; } = new();

    /// <summary>"4 copies of 12.1 MB - 36.3 MB to reclaim".</summary>
    public string HeaderText => string.Format(CultureInfo.CurrentCulture,
        Strings.MySpace_DuplicatesGroup,
        Group.Copies,
        ModlightFormat.CloudSize(Group.Size),
        ModlightFormat.CloudSize(Group.Reclaimable));

    /// <summary>
    /// True when the set lists fewer copies than exist.
    /// </summary>
    /// <remarks>
    /// The server lists at most fifty per set and reports the true count separately, so a
    /// set of three hundred shows fifty rows above a sentence saying so. Silently showing
    /// fifty of three hundred would make "select everything but the oldest" look like it
    /// had missed most of the work.
    /// </remarks>
    public bool IsPartial => Group.Copies > Files.Count;

    public string PartialText => IsPartial
        ? string.Format(CultureInfo.CurrentCulture, Strings.MySpace_DuplicatesGroupPartial,
            Files.Count, Group.Copies)
        : "";

    /// <summary>The owner has said these copies are meant to exist.</summary>
    public bool Dismissed
    {
        get => dismissed;
        set
        {
            if (dismissed == value)
            {
                return;
            }

            dismissed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeepLabel));
            changed();
        }
    }

    /// <summary>False while this set's own decision is in flight, so it cannot be sent twice.</summary>
    public bool IsIdle => !busy;

    internal void SetBusy(bool value)
    {
        if (busy == value)
        {
            return;
        }

        busy = value;
        OnPropertyChanged(nameof(IsIdle));
    }

    /// <summary>One button, both directions: waving a set off and taking it back.</summary>
    public string KeepLabel =>
        Dismissed ? Strings.MySpace_DuplicatesCountAgain : Strings.MySpace_DuplicatesKeep;

    public int SelectedCount => Files.Count(f => f.IsSelected);

    public long SelectedBytes => Files.Where(f => f.IsSelected).Sum(f => f.Row.Size);

    /// <summary>
    /// Tick everything but the oldest copy - the default this screen opens with.
    /// </summary>
    /// <remarks>
    /// Never applied to a set the owner has waved off. "These copies are meant to exist"
    /// is an answer, and re-proposing the delete every time the window opens would make
    /// the answer worthless.
    /// </remarks>
    internal void SelectExtras()
    {
        if (Dismissed)
        {
            ClearSelection();
            return;
        }

        foreach (DuplicateFileViewModel file in Files)
        {
            file.IsSelected = !file.IsOldest;
        }
    }

    internal void ClearSelection()
    {
        foreach (DuplicateFileViewModel file in Files)
        {
            file.IsSelected = false;
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// The duplicate scan, as the trash presents it.
/// </summary>
/// <remarks>
/// Everything factual here comes from the server: which files are duplicates of which,
/// how much a clean-up would free, which sets have been waved off, and whether the
/// account is over the thresholds that make the reminder speak. None of it is recomputed,
/// because the same feature exists in the web panel and two implementations of "what
/// counts as a duplicate" would eventually answer differently on the same library.
///
/// What IS decided here is presentation and intent: the default selection, the batching
/// of the delete, and the rule that a dismissed set is never pre-ticked.
/// </remarks>
internal sealed class DuplicatesViewModel : INotifyPropertyChanged
{
    private readonly ModlightAccount account;

    private bool busy;
    private string status = "";
    private bool failed;
    private ModlightDuplicateScan? scan;

    internal DuplicatesViewModel(ModlightAccount account) => this.account = account;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    /// <summary>Files this window moved to the trash, so the caller knows to reload.</summary>
    internal int Trashed { get; private set; }

    public bool IsBusy
    {
        get => busy;
        private set
        {
            if (busy == value)
            {
                return;
            }

            busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanTrash));
            // ShowEmpty reads this flag as well as the scan's result, and the scan
            // finishes with the flag still set - the answer arrives inside the try and
            // the flag drops in the finally. Without this line "nothing is stored twice"
            // is computed while the window still counts itself busy, comes out false, and
            // is never asked again: an account with no duplicates would show a blank
            // panel with no sentence in it.
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    public bool IsIdle => !busy;

    /// <summary>The last thing that happened, in a line under the title. Never a dialog.</summary>
    /// <remarks>
    /// A modal error over a modal window is two things to dismiss for one piece of news,
    /// and the news here is rarely fatal: a set that would not dismiss leaves the list
    /// perfectly usable.
    /// </remarks>
    public string Status
    {
        get => status;
        private set
        {
            status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => Status.Length > 0;

    public bool Failed
    {
        get => failed;
        private set
        {
            failed = value;
            OnPropertyChanged();
        }
    }

    public bool HasGroups => Groups.Count > 0;

    public bool ShowEmpty => !IsBusy && scan != null && Groups.Count == 0;

    /// <summary>"6 sets - 14 extra copies - 1.2 GB to reclaim", or the empty answer.</summary>
    public string SummaryText
    {
        get
        {
            if (scan == null)
            {
                return "";
            }

            ModlightDuplicateTotals totals = scan.Active ?? new ModlightDuplicateTotals();

            return string.Format(CultureInfo.CurrentCulture, Strings.MySpace_DuplicatesSummary,
                totals.Groups, totals.Redundant, ModlightFormat.CloudSize(totals.Reclaimable));
        }
    }

    /// <summary>
    /// The caveat that makes "no duplicates" honest.
    /// </summary>
    /// <remarks>
    /// Rows with no checksum cannot be compared at all, and on a server whose library
    /// was imported that is most of it. Leaving this out would turn "nothing found"
    /// into a claim about files the scan never looked at.
    /// </remarks>
    public string BlindText => scan is { Unchecksummed: > 0 }
        ? string.Format(CultureInfo.CurrentCulture, Strings.MySpace_DuplicatesBlind, scan.Unchecksummed)
        : "";

    public bool HasBlindText => BlindText.Length > 0;

    /// <summary>Shown only when the server had more sets than it was willing to list.</summary>
    public string TruncatedText => scan is { Truncated: true }
        ? string.Format(CultureInfo.CurrentCulture, Strings.MySpace_DuplicatesTruncated,
            Groups.Count, scan.TotalGroups)
        : "";

    public bool HasTruncatedText => TruncatedText.Length > 0;

    /// <summary>
    /// Whether the account is over one of the server's two thresholds.
    /// </summary>
    /// <remarks>
    /// The thresholds themselves are quoted from the answer rather than written here, so
    /// the day the server changes either number this sentence changes with it instead of
    /// going quietly out of date.
    /// </remarks>
    public string DueText
    {
        get
        {
            if (scan is not { Due: true })
            {
                return "";
            }

            ModlightDuplicateThresholds limits = scan.Thresholds ?? new ModlightDuplicateThresholds();

            return string.Format(CultureInfo.CurrentCulture, Strings.MySpace_DuplicatesDue,
                limits.Redundant, ModlightFormat.CloudSize(limits.Bytes));
        }
    }

    public bool HasDueText => DueText.Length > 0;

    public int SelectedCount => Groups.Sum(g => g.SelectedCount);

    public long SelectedBytes => Groups.Sum(g => g.SelectedBytes);

    public bool CanTrash => !IsBusy && SelectedCount > 0;

    /// <summary>"Move 14 copies (1.2 GB) to the trash".</summary>
    public string TrashLabel => SelectedCount == 0
        ? Strings.MySpace_DuplicatesTrashNone
        : string.Format(CultureInfo.CurrentCulture, Strings.MySpace_DuplicatesTrash,
            SelectedCount, ModlightFormat.CloudSize(SelectedBytes));

    /// <summary>Ask the server what is duplicated, and lay out the answer.</summary>
    /// <param name="keepStatus">
    /// Put the line that is on screen back when the scan finishes, instead of clearing it.
    /// </param>
    /// <remarks>
    /// The parameter exists for the re-scan that follows a delete. Reading the library
    /// again is the honest way to redraw - trashing a copy can empty a set and always
    /// changes the totals in the header - but a scan that ends by clearing the status line
    /// would wipe "moved to the trash: 37" a moment after writing it, and the only
    /// confirmation left would be a list that is quietly shorter than it was.
    /// </remarks>
    internal async Task ScanAsync(bool keepStatus = false)
    {
        if (IsBusy)
        {
            return;
        }

        string carried = keepStatus ? Status : "";

        IsBusy = true;
        Failed = false;
        Status = Strings.MySpace_DuplicatesScanning;

        try
        {
            ModlightDuplicateScan answer = await ModlightLibrary.GetDuplicatesAsync(
                account.Host, account.Token, CancellationToken.None);

            scan = answer;
            Groups.Clear();

            foreach (ModlightDuplicateGroup group in answer.Groups ?? new List<ModlightDuplicateGroup>())
            {
                DuplicateGroupViewModel model = new(group, SelectionChanged);
                model.SelectExtras();
                Groups.Add(model);
            }

            Status = carried;
            RefreshAll();
        }
        catch (ModlightUnauthorizedException)
        {
            Fail(Strings.MySpace_TokenRejected);
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Wave a set off, or start counting it again - whichever it is not now.</summary>
    internal async Task ToggleKeepAsync(DuplicateGroupViewModel group)
    {
        if (IsBusy || group.Checksum.Length == 0)
        {
            return;
        }

        group.SetBusy(true);

        try
        {
            if (group.Dismissed)
            {
                await ModlightLibrary.CountDuplicateSetsAgainAsync(
                    account.Host, account.Token, new[] { group.Checksum }, CancellationToken.None);

                group.Dismissed = false;
                // Back in the running, so it gets the default proposal again.
                group.SelectExtras();
                Status = Strings.MySpace_DuplicatesCountingAgain;
            }
            else
            {
                await ModlightLibrary.KeepDuplicateSetsAsync(
                    account.Host, account.Token, new[] { group.Group }, CancellationToken.None);

                group.Dismissed = true;
                // A set that is meant to exist must not be sitting there pre-ticked for
                // deletion the moment after it was spared.
                group.ClearSelection();
                Status = Strings.MySpace_DuplicatesKept;
            }

            Failed = false;
            RefreshAll();
        }
        catch (ModlightUnauthorizedException)
        {
            Fail(Strings.MySpace_TokenRejected);
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
        finally
        {
            group.SetBusy(false);
        }
    }

    internal void SelectExtras()
    {
        foreach (DuplicateGroupViewModel group in Groups)
        {
            group.SelectExtras();
        }

        RefreshAll();
    }

    internal void ClearSelection()
    {
        foreach (DuplicateGroupViewModel group in Groups)
        {
            group.ClearSelection();
        }

        RefreshAll();
    }

    /// <summary>
    /// Move every ticked copy to the trash.
    /// </summary>
    /// <remarks>
    /// The trash, never a permanent delete. This is the one screen in the app where a
    /// mistake is both easy - one mis-read default and the wrong copy goes - and total,
    /// because the file being deleted is by definition identical to another one somebody
    /// may have meant to keep. Everything it removes has to be sitting in the thirty-day
    /// trash afterwards, one click from coming back.
    ///
    /// Batched, because a default selection on a large library can name more ids than one
    /// request may carry: the server refuses a list longer than
    /// <see cref="ModlightLibrary.MaxDeleteIds"/> outright, and a screen that can propose
    /// two thousand deletions has to send them in the shape the endpoint accepts rather
    /// than discover the ceiling as a 400.
    /// </remarks>
    internal async Task<int> TrashSelectedAsync()
    {
        if (IsBusy)
        {
            return 0;
        }

        List<string> ids = Groups
            .SelectMany(g => g.Files)
            .Where(f => f.IsSelected && f.Id.Length > 0)
            .Select(f => f.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
        {
            return 0;
        }

        IsBusy = true;
        Failed = false;
        Status = Strings.MySpace_DuplicatesTrashing;

        try
        {
            int affected = 0;

            for (int from = 0; from < ids.Count; from += ModlightLibrary.MaxDeleteIds)
            {
                List<string> batch = ids.GetRange(from,
                    Math.Min(ModlightLibrary.MaxDeleteIds, ids.Count - from));

                ModlightDeleteResult result = await ModlightLibrary.DeleteUploadsAsync(
                    account.Host, account.Token, batch, "trash", CancellationToken.None);

                affected += result.Affected;
            }

            Trashed += affected;

            if (affected == 0)
            {
                // The server answers the same "nothing affected" for "already gone",
                // "already in the trash" and "not yours", deliberately, so the message
                // can only say that nothing happened.
                Fail(Strings.MySpace_DeleteNothingHappened);
                return 0;
            }

            Status = string.Format(CultureInfo.CurrentCulture,
                Strings.MySpace_DuplicatesTrashed, affected);

            return affected;
        }
        catch (ModlightUnauthorizedException)
        {
            Fail(Strings.MySpace_TokenRejected);
            return 0;
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
            return 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(string message)
    {
        Failed = true;
        Status = message;
    }

    private void SelectionChanged()
    {
        foreach (DuplicateGroupViewModel group in Groups)
        {
            group.Refresh();
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(CanTrash));
        OnPropertyChanged(nameof(TrashLabel));
    }

    private void RefreshAll()
    {
        SelectionChanged();
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(BlindText));
        OnPropertyChanged(nameof(HasBlindText));
        OnPropertyChanged(nameof(TruncatedText));
        OnPropertyChanged(nameof(HasTruncatedText));
        OnPropertyChanged(nameof(DueText));
        OnPropertyChanged(nameof(HasDueText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
