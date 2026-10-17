#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>What the screen is currently able to show.</summary>
internal enum AdminDiskState
{
    /// <summary>Nothing asked for yet.</summary>
    Idle,
    Loading,
    Ready,

    /// <summary>No account on this installation, so there is nobody to ask as.</summary>
    SignedOut,

    /// <summary>
    /// The server will not act on this credential at all (401).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Refused"/>, which used to swallow it. "Your token was
    /// revoked" and "your token is too narrow" are one status apart and a whole remedy
    /// apart: the first is fixed by signing in again and the second is made no better
    /// by it, so the one sentence that used to cover both sent half of its readers to
    /// do the useless thing.
    /// </remarks>
    Unauthorized,

    /// <summary>The credential is real, and too narrow for this screen (403).</summary>
    Refused,

    /// <summary>
    /// The server answered, and this account is not one it reports to (404).
    /// </summary>
    /// <remarks>
    /// Not a fault and not an outage. Reached only after a SECOND question is asked:
    /// the panel gives a non-administrator the same 404 as a build with no such route,
    /// deliberately, so that the status cannot be used to find out who the
    /// administrators are. See <see cref="LoadAsync"/>.
    /// </remarks>
    NotAllowed,

    /// <summary>The server is older than this screen (404, and we are still admin).</summary>
    Unsupported,

    /// <summary>
    /// The server did not answer inside this screen's deadline.
    /// </summary>
    /// <remarks>
    /// Its own state because it used to be no state at all: the deadline surfaced as an
    /// <see cref="OperationCanceledException"/>, which the catch in <see cref="LoadAsync"/>
    /// swallowed along with the deliberate cancellations, and the screen then sat on
    /// "Reading the server..." for ever. A spinner that never stops is the one failure
    /// a person cannot even report, because nothing on screen admits anything happened.
    /// </remarks>
    TimedOut,

    /// <summary>Something stopped the request from completing.</summary>
    Failed
}

/// <summary>
/// The administrator's view of the server's storage.
/// </summary>
/// <remarks>
/// <para>
/// The owner's words: "as an admin I still cannot simply see the disk usage - I see it
/// in the web panel, but not in the app". So this is a deliberate MIRROR of one web
/// page, <c>components/admin/ServerDisk.tsx</c>, and not a new opinion about the same
/// data. Every number arrives already aggregated from
/// <see cref="ModlightServerDisk.GetAsync"/>; nothing here recomputes anything the
/// server has already answered, because the moment two screens compute the same total
/// from different halves of the same table, one of them starts being wrong and nobody
/// can tell which.
/// </para>
/// <para>
/// A MEASUREMENT AND A COUNT ARE NOT THE SAME THING, and this screen never lets them
/// look alike. The filesystem card only appears when the server actually measured a
/// filesystem (<c>statfs</c> on a local driver). On an object store there is no disk to
/// fill, so the card is replaced by the database's own totals with a sentence saying
/// exactly that, and saying that what the bucket really holds can only be MORE -
/// thumbnails, cached conversions and the remains of interrupted uploads are objects
/// with no row to their name.
/// </para>
/// <para>
/// Sizes go through <see cref="ModlightFormat.CloudSize"/> - 1024 steps with decimal
/// labels - because that is what the panel prints, and this whole screen exists so the
/// two agree.
/// </para>
/// <para>
/// UI thread only. The fetch is awaited on Avalonia's synchronisation context, so every
/// mutation below happens where the bindings live.
/// </para>
/// </remarks>
internal sealed class AdminDiskViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// How long a report is treated as still current.
    /// </summary>
    /// <remarks>
    /// Re-read when the tab is opened after this long, and not otherwise. There is no
    /// timer: a disk fills over days, so a screen nobody is looking at has nothing to
    /// keep up with, and polling would only spend six aggregate queries a minute on the
    /// database that serves the actual website.
    /// </remarks>
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(2);

    private CancellationTokenSource? inflight;
    private AdminDiskState state = AdminDiskState.Idle;
    private string failure = "";
    private ModlightDiskReport? report;
    private DateTimeOffset readAt;

    public event PropertyChangedEventHandler? PropertyChanged;

    // -----------------------------------------------------------------------
    // Lists
    // -----------------------------------------------------------------------

    /// <summary>What the bar above it is made of, in the same order.</summary>
    public ObservableCollection<AdminLegendViewModel> Legend { get; } = new();

    /// <summary>Live bytes by kind of file, heaviest first.</summary>
    public ObservableCollection<AdminShareViewModel> Kinds { get; } = new();

    /// <summary>What each account holds, biggest first, tail folded into one row.</summary>
    public ObservableCollection<AdminShareViewModel> Accounts { get; } = new();

    /// <summary>The heaviest live files.</summary>
    public ObservableCollection<AdminFileViewModel> Biggest { get; } = new();

    /// <summary>The longest-untouched live files.</summary>
    public ObservableCollection<AdminFileViewModel> Cold { get; } = new();

    // -----------------------------------------------------------------------
    // Which panel is on screen
    // -----------------------------------------------------------------------

    public bool IsLoading => state == AdminDiskState.Loading;

    public bool HasReport => state == AdminDiskState.Ready && report != null;

    /// <summary>Anything that is not a report: refusal, absence, or a dead connection.</summary>
    public bool HasProblem => state is AdminDiskState.SignedOut or AdminDiskState.Unauthorized
        or AdminDiskState.Refused or AdminDiskState.NotAllowed or AdminDiskState.Unsupported
        or AdminDiskState.TimedOut or AdminDiskState.Failed;

    /// <summary>
    /// Whether trying again could help.
    /// </summary>
    /// <remarks>
    /// Offered where a second attempt can genuinely land differently: a connection that
    /// broke, and a server that ran out of time while a cold database read its pages off
    /// disk. Not offered next to "sign in first", "this server has no such endpoint",
    /// "your token is too narrow" or "this account is not an administrator" - those are
    /// answers, not accidents, and a Retry button beside one can only fail. A person who
    /// presses it twice learns to distrust the screen rather than to read it.
    /// </remarks>
    public bool CanRetry => state is AdminDiskState.Failed or AdminDiskState.TimedOut;

    public string ProblemTitle => state switch
    {
        AdminDiskState.SignedOut => Strings.Admin_SignedOutTitle,
        AdminDiskState.Unauthorized => Strings.Admin_UnauthorizedTitle,
        AdminDiskState.Refused => Strings.Admin_RefusedTitle,
        AdminDiskState.NotAllowed => Strings.Admin_NotAllowedTitle,
        AdminDiskState.Unsupported => Strings.Admin_UnsupportedTitle,
        AdminDiskState.TimedOut => Strings.Admin_TimedOutTitle,
        AdminDiskState.Failed => Strings.Admin_FailedTitle,
        _ => ""
    };

    public string ProblemBody => state switch
    {
        AdminDiskState.SignedOut => Strings.Admin_SignedOutBody,
        AdminDiskState.Unauthorized => Strings.Admin_UnauthorizedBody,
        AdminDiskState.Refused => Strings.Admin_RefusedBody,
        AdminDiskState.NotAllowed => Strings.Admin_NotAllowedBody,
        AdminDiskState.Unsupported => string.Format(CultureInfo.CurrentCulture,
            Strings.Admin_UnsupportedBody, ModlightServerDisk.Path),
        AdminDiskState.TimedOut => string.Format(CultureInfo.CurrentCulture,
            Strings.Admin_TimedOutBody, HostText, (int)ModlightServerDisk.Timeout.TotalSeconds),
        AdminDiskState.Failed => failure,
        _ => ""
    };

    /// <summary>The icon for the problem panel: a refusal is not an outage.</summary>
    public string ProblemIcon => state switch
    {
        AdminDiskState.SignedOut => LucideIcons.circle_user,
        // A credential the server no longer knows is a sign-in problem, and it wears the
        // sign-in icon rather than the shield: the shield says "you were kept out", and
        // this says "we could not tell it was you".
        AdminDiskState.Unauthorized => LucideIcons.circle_user,
        AdminDiskState.Refused => LucideIcons.shield,
        AdminDiskState.NotAllowed => LucideIcons.shield,
        AdminDiskState.Unsupported => LucideIcons.circle_help,
        _ => LucideIcons.triangle_alert
    };

    // -----------------------------------------------------------------------
    // The machine
    // -----------------------------------------------------------------------

    /// <summary>Which server this is about, so a two-server user is never in doubt.</summary>
    public string HostText { get; private set; } = "";

    /// <summary>When the numbers were read. They are a moment, not a live feed.</summary>
    public string CheckedText { get; private set; } = "";

    /// <summary>True when a real filesystem was measured, false for an object store.</summary>
    public bool HasFilesystem { get; private set; }

    /// <summary>One sentence saying where these numbers come from and what they miss.</summary>
    public string SourceText { get; private set; } = "";

    /// <summary>Occupied on the whole filesystem, us and every neighbour.</summary>
    public string UsedText { get; private set; } = "";

    public string OfTotalText { get; private set; } = "";

    public string FreeBadgeText { get; private set; } = "";

    /// <summary>ok / low / critical - the style class the badge and border take.</summary>
    public string Pressure { get; private set; } = "ok";

    public bool IsPressureOk => Pressure == "ok";

    public bool IsPressureLow => Pressure == "low";

    public bool IsPressureCritical => Pressure == "critical";

    public bool HasPressureNote => IsPressureLow || IsPressureCritical;

    public string PressureNote { get; private set; } = "";

    /// <summary>
    /// The stacked bar, as four star weights.
    /// </summary>
    /// <remarks>
    /// Star weights rather than percentages because the bar has to fill whatever width
    /// the window happens to be, and a percentage would have to be recomputed on every
    /// resize by something that knows the width - which a view model must not. A segment
    /// of zero bytes gets a zero weight and disappears entirely, which is the right
    /// answer for an empty trash.
    /// </remarks>
    public GridLength LiveWeight { get; private set; } = new GridLength(0, GridUnitType.Star);

    public GridLength TrashWeight { get; private set; } = new GridLength(0, GridUnitType.Star);

    public GridLength OtherWeight { get; private set; } = new GridLength(0, GridUnitType.Star);

    public GridLength FreeWeight { get; private set; } = new GridLength(1, GridUnitType.Star);

    // -----------------------------------------------------------------------
    // The trash
    // -----------------------------------------------------------------------

    public string TrashTitle => Strings.Admin_TrashTitle;

    public string TrashDesc { get; private set; } = "";

    public string TrashHeldText { get; private set; } = "";

    public string TrashHeldHint { get; private set; } = "";

    public string TrashDueSoonLabel { get; private set; } = "";

    public string TrashDueSoonText { get; private set; } = "";

    public string TrashDueSoonHint { get; private set; } = "";

    public string TrashOldestText { get; private set; } = "";

    /// <summary>
    /// Files past the retention window that are somehow still stored.
    /// </summary>
    /// <remarks>
    /// This should always be false. When it is not, the sweeper has stopped, and this
    /// screen is the only place an operator would ever be told - so it is shown as a
    /// warning with the words "the purge job is not running", not as another statistic.
    /// </remarks>
    public bool HasTrashOverdue { get; private set; }

    public string TrashOverdueText { get; private set; } = "";

    // -----------------------------------------------------------------------
    // The breakdowns
    // -----------------------------------------------------------------------

    public string InsightsText { get; private set; } = "";

    public bool HasKinds => Kinds.Count > 0;

    public bool HasAccounts => Accounts.Count > 0;

    public bool HasBiggest => Biggest.Count > 0;

    public bool HasCold => Cold.Count > 0;

    public string ColdTitle { get; private set; } = "";

    public string ColdSummary { get; private set; } = "";

    public string ColdEmptyText { get; private set; } = "";

    // -----------------------------------------------------------------------
    // Fetching
    // -----------------------------------------------------------------------

    /// <summary>
    /// Read the report if what we hold has gone stale.
    /// </summary>
    /// <remarks>
    /// Called when the tab becomes visible. Cheap on a second visit, which is the point:
    /// switching tabs must not cost the website's database six aggregate queries every
    /// time somebody looks for their files and comes back.
    /// </remarks>
    public void Show()
    {
        if (state == AdminDiskState.Loading)
        {
            return;
        }

        if (HasReport && DateTimeOffset.Now - readAt < Freshness)
        {
            return;
        }

        Refresh();
    }

    /// <summary>Read the report now, whatever is on screen.</summary>
    public void Refresh()
    {
        ModlightAccount? account = ModlightSignIn.Current;

        Cancel();

        if (account == null)
        {
            // Clear the address too. A header still naming the server the last account
            // was on, above a panel saying nobody is signed in, is two claims that cannot
            // both be true.
            HostText = "";
            OnPropertyChanged(nameof(HostText));
            SetState(AdminDiskState.SignedOut);
            return;
        }

        CancellationTokenSource source = new();
        inflight = source;

        HostText = account.Host;
        OnPropertyChanged(nameof(HostText));
        SetState(AdminDiskState.Loading);

        _ = LoadAsync(account.Host, account.Token, source);
    }

    /// <summary>Drop anything in the air. The screen is going away or the account changed.</summary>
    public void Detach()
    {
        Cancel();
    }

    /// <summary>
    /// Throw away a report that belonged to somebody else.
    /// </summary>
    /// <remarks>
    /// Signing out, or signing in as a different account, has to clear this rather than
    /// leave one server's totals on screen under another server's name. The tab is
    /// hidden for a non-administrator anyway, but "hidden" is a UI state and this is the
    /// data - and data left lying around is what gets shown by the next bug.
    /// </remarks>
    public void Forget()
    {
        Cancel();
        report = null;
        Legend.Clear();
        Kinds.Clear();
        Accounts.Clear();
        Biggest.Clear();
        Cold.Clear();
        SetState(AdminDiskState.Idle);
    }

    private void Cancel()
    {
        CancellationTokenSource? source = inflight;
        inflight = null;

        if (source != null)
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private bool Owns(CancellationTokenSource source) =>
        ReferenceEquals(inflight, source) && !source.IsCancellationRequested;

    private async Task LoadAsync(string host, string token, CancellationTokenSource source)
    {
        try
        {
            ModlightDiskReport result = await ModlightServerDisk.GetAsync(host, token, source.Token);

            if (!Owns(source))
            {
                return;
            }

            report = result;
            readAt = DateTimeOffset.Now;
            Rebuild(result);
            SetState(AdminDiskState.Ready);
        }
        catch (OperationCanceledException) when (!source.IsCancellationRequested)
        {
            // OUR deadline, not the caller's. The two arrive as the same exception type
            // and mean opposite things: this one is the server failing to answer, and
            // the one below is the screen or the account going away while it did. Told
            // apart by which token asked for the cancellation - the pattern
            // ModlightLibrary.CheckConversionAsync already uses.
            //
            // Before this guard existed, both landed in an empty catch and the screen
            // stayed on "Reading the server..." for ever.
            SetState(AdminDiskState.TimedOut);
        }
        catch (OperationCanceledException)
        {
            // Deliberate: Detach, Forget, or a second Refresh overtaking this one. There
            // is nobody left to tell.
        }
        catch (ModlightUnauthorizedException)
        {
            if (Owns(source))
            {
                SetState(AdminDiskState.Unauthorized);
            }
        }
        catch (ModlightAdminForbiddenException)
        {
            if (Owns(source))
            {
                SetState(AdminDiskState.Refused);
            }
        }
        catch (ModlightNotSupportedException)
        {
            if (Owns(source))
            {
                AdminDiskState verdict = await DiagnoseNotFoundAsync(host, token, source.Token);

                // Checked again: the diagnosis is a second round trip, and the screen
                // may have been closed or the account changed while it was in the air.
                if (Owns(source))
                {
                    SetState(verdict);
                }
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);

            if (Owns(source))
            {
                failure = string.Format(CultureInfo.CurrentCulture,
                    Strings.ModlightSignIn_Unreachable, host, exception.Message);
                SetState(AdminDiskState.Failed);
            }
        }
        finally
        {
            if (ReferenceEquals(inflight, source))
            {
                inflight = null;
                source.Dispose();
            }
        }
    }

    /// <summary>
    /// Work out which of the two things a 404 meant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The panel answers 404 both to "this build has no such route" and to "you are not
    /// an administrator here", and that is a decision rather than an oversight: a 403
    /// would confirm to any account that the endpoint exists and that it is one rank
    /// away, which is exactly the thing an administrators' endpoint should not publish.
    /// So the status cannot be read for an answer, and the only honest way to get one is
    /// to ask a different question - <c>/api/me</c>, which tells an account its own role
    /// and nobody else's.
    /// </para>
    /// <para>
    /// Which is worth one extra request on a path that only runs when something is
    /// already wrong. The alternative is a hedged sentence covering both causes, and a
    /// screen that hedges teaches people to stop reading it.
    /// </para>
    /// <para>
    /// A failure of the second call falls back to <see cref="AdminDiskState.Unsupported"/>:
    /// the server has already given a definite 404 to a route the tab was offered for,
    /// so "the endpoint is not there" is the standing explanation, and a hiccup while
    /// confirming it must not turn a correct diagnosis into a fault report.
    /// </para>
    /// </remarks>
    private static async Task<AdminDiskState> DiagnoseNotFoundAsync(string host, string token,
        CancellationToken cancellationToken)
    {
        try
        {
            // Note the side effect, and that it is wanted: this call is the one place
            // ModlightAdmin learns the role, so an account that has just lost it will
            // also lose the tab, moments after being told why the screen is empty.
            ModlightAccountInfo? account = await ModlightApi.GetAccountAsync(host, token,
                cancellationToken);

            if (account == null)
            {
                // /api/me refused the same credential the report did. Whatever else is
                // true, this token is no longer one the server acts on.
                return AdminDiskState.Unauthorized;
            }

            return string.Equals(account.Role, "admin", StringComparison.Ordinal)
                ? AdminDiskState.Unsupported
                : AdminDiskState.NotAllowed;
        }
        catch (Exception exception)
        {
            // A cancellation lands here too, and deliberately so: this runs inside a
            // catch block of a fire-and-forget task, where rethrowing would leave an
            // unobserved exception behind rather than reaching anybody. The caller
            // re-checks ownership before it uses this answer, so a verdict computed for
            // a request nobody is waiting on is simply discarded.
            DebugHelper.WriteException(exception);
            return AdminDiskState.Unsupported;
        }
    }

    // -----------------------------------------------------------------------
    // Turning the report into words
    // -----------------------------------------------------------------------

    private void Rebuild(ModlightDiskReport r)
    {
        string driver = string.IsNullOrEmpty(r.Driver) ? "" : r.Driver!.ToUpperInvariant();

        // A clock time rather than "just now": these numbers are a moment the server was
        // asked about, not a live feed, and "just now" would still say "just now" an hour
        // later unless something kept re-rendering it. The clock is the reader's own.
        CheckedText = string.Format(CultureInfo.CurrentCulture, Strings.Admin_Checked,
            DateTime.Now.ToString("t", CultureInfo.CurrentCulture));

        BuildMachine(r, driver);
        BuildTrash(r);

        InsightsText = r.IsLocal && r.Disk != null
            ? Strings.Admin_InsightsDesc
            : string.Format(CultureInfo.CurrentCulture, Strings.Admin_InsightsDescObject, driver);

        BuildKinds(r);
        BuildAccounts(r);
        BuildFiles(r);

        OnPropertyChanged(nameof(CheckedText));
        OnPropertyChanged(nameof(InsightsText));
    }

    private void BuildMachine(ModlightDiskReport r, string driver)
    {
        ModlightDiskSpace? disk = r.Disk;

        HasFilesystem = disk != null && disk.TotalBytes > 0;

        long appBytes = r.LiveBytes + r.TrashedBytes;

        Legend.Clear();

        if (HasFilesystem && disk != null)
        {
            long total = disk.TotalBytes;
            long free = Math.Max(0, disk.FreeBytes);
            long used = Math.Max(0, disk.UsedBytes);

            // Everything on this filesystem that is not ours. Clamped at zero because
            // the two halves come from different sources - statfs and a sum of recorded
            // sizes - and on a driver that stores its files elsewhere the count can
            // legitimately exceed what this filesystem holds. A negative "other data"
            // would be arithmetic on screen rather than a fact.
            long other = Math.Max(0, used - appBytes);

            SourceText = Strings.Admin_DescLocal;
            UsedText = ModlightFormat.CloudSize(used);
            OfTotalText = string.Format(CultureInfo.CurrentCulture, Strings.Admin_OfTotal,
                ModlightFormat.CloudSize(total));
            FreeBadgeText = string.Format(CultureInfo.CurrentCulture, Strings.Admin_FreeBadge,
                (long)Math.Round(Share(free, total)));

            Pressure = PressureOf(free, total, r.MinFreeBytes);
            PressureNote = NoteFor(Pressure, r.MinFreeBytes);

            LiveWeight = Weight(r.LiveBytes);
            TrashWeight = Weight(r.TrashedBytes);
            OtherWeight = Weight(other);
            FreeWeight = Weight(free);

            Legend.Add(new AdminLegendViewModel("live", Strings.Admin_Live,
                ModlightFormat.CloudSize(r.LiveBytes), FilesText(r.LiveFiles)));
            Legend.Add(new AdminLegendViewModel("trash", Strings.Admin_Trash,
                ModlightFormat.CloudSize(r.TrashedBytes),
                string.Format(CultureInfo.CurrentCulture, Strings.Admin_TrashHint, r.TrashRetentionDays)));
            Legend.Add(new AdminLegendViewModel("other", Strings.Admin_Other,
                ModlightFormat.CloudSize(other), Strings.Admin_OtherHint));
            Legend.Add(new AdminLegendViewModel("free", Strings.Admin_Free,
                ModlightFormat.CloudSize(free), Strings.Admin_FreeHint));
        }
        else
        {
            // No filesystem was measured. Say which of the two reasons it is, and then
            // show only what the database can honestly account for.
            SourceText = string.IsNullOrEmpty(driver) || r.IsLocal
                ? Strings.Admin_DescUnmeasured
                : string.Format(CultureInfo.CurrentCulture, Strings.Admin_DescObject, driver);

            UsedText = ModlightFormat.CloudSize(appBytes);
            OfTotalText = "";
            FreeBadgeText = "";
            Pressure = "ok";
            PressureNote = "";

            Legend.Add(new AdminLegendViewModel("live", Strings.Admin_Live,
                ModlightFormat.CloudSize(r.LiveBytes), FilesText(r.LiveFiles)));
            Legend.Add(new AdminLegendViewModel("trash", Strings.Admin_Trash,
                ModlightFormat.CloudSize(r.TrashedBytes),
                string.Format(CultureInfo.CurrentCulture, Strings.Admin_TrashHint, r.TrashRetentionDays)));
            Legend.Add(new AdminLegendViewModel("app", Strings.Admin_AppTotal,
                ModlightFormat.CloudSize(appBytes), FilesText(r.LiveFiles + r.TrashedFiles)));
        }

        OnPropertyChanged(nameof(HasFilesystem));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(UsedText));
        OnPropertyChanged(nameof(OfTotalText));
        OnPropertyChanged(nameof(FreeBadgeText));
        OnPropertyChanged(nameof(Pressure));
        OnPropertyChanged(nameof(IsPressureOk));
        OnPropertyChanged(nameof(IsPressureLow));
        OnPropertyChanged(nameof(IsPressureCritical));
        OnPropertyChanged(nameof(HasPressureNote));
        OnPropertyChanged(nameof(PressureNote));
        OnPropertyChanged(nameof(LiveWeight));
        OnPropertyChanged(nameof(TrashWeight));
        OnPropertyChanged(nameof(OtherWeight));
        OnPropertyChanged(nameof(FreeWeight));
    }

    private void BuildTrash(ModlightDiskReport r)
    {
        ModlightDiskTrash trash = r.Trash ?? new ModlightDiskTrash();

        TrashDesc = string.Format(CultureInfo.CurrentCulture, Strings.Admin_TrashDesc, r.TrashRetentionDays);
        TrashHeldText = ModlightFormat.CloudSize(trash.Bytes);
        TrashHeldHint = FilesText(trash.Files);

        TrashDueSoonLabel = string.Format(CultureInfo.CurrentCulture,
            Strings.Admin_TrashDueSoon, r.TrashDueSoonDays);
        TrashDueSoonText = ModlightFormat.CloudSize(trash.DueSoonBytes);
        TrashDueSoonHint = FilesText(trash.DueSoonFiles);

        TrashOldestText = trash.Oldest == null
            ? Strings.Admin_TrashOldestNone
            : CloudFileViewModel.AgeOf(trash.Oldest);

        HasTrashOverdue = trash.OverdueFiles > 0;
        TrashOverdueText = HasTrashOverdue
            ? string.Format(CultureInfo.CurrentCulture, Strings.Admin_TrashOverdue,
                ModlightFormat.CloudSize(trash.OverdueBytes), FilesText(trash.OverdueFiles),
                r.TrashRetentionDays)
            : "";

        OnPropertyChanged(nameof(TrashDesc));
        OnPropertyChanged(nameof(TrashHeldText));
        OnPropertyChanged(nameof(TrashHeldHint));
        OnPropertyChanged(nameof(TrashDueSoonLabel));
        OnPropertyChanged(nameof(TrashDueSoonText));
        OnPropertyChanged(nameof(TrashDueSoonHint));
        OnPropertyChanged(nameof(TrashOldestText));
        OnPropertyChanged(nameof(HasTrashOverdue));
        OnPropertyChanged(nameof(TrashOverdueText));
    }

    private void BuildKinds(ModlightDiskReport r)
    {
        Kinds.Clear();

        // The server's own total for these rows, not a sum taken here: if a future
        // server ever folds a kind away, a locally recomputed denominator would make the
        // remaining shares add up to 100% of something that is not the whole.
        long total = r.KindBytes;

        foreach (ModlightDiskKind kind in r.Kinds ?? new List<ModlightDiskKind>())
        {
            Kinds.Add(new AdminShareViewModel(
                KindLabel(kind.Kind),
                FilesText(kind.Files),
                ModlightFormat.CloudSize(kind.Bytes),
                Share(kind.Bytes, total)));
        }

        OnPropertyChanged(nameof(HasKinds));
    }

    private void BuildAccounts(ModlightDiskReport r)
    {
        Accounts.Clear();

        List<ModlightDiskAccount> rows = r.Accounts ?? new List<ModlightDiskAccount>();

        // The denominator is what these rows themselves hold, trash included. The tail is
        // folded into a row rather than dropped precisely so this still adds up.
        long total = 0;

        foreach (ModlightDiskAccount row in rows)
        {
            total += row.LiveBytes + row.TrashedBytes;
        }

        foreach (ModlightDiskAccount row in rows)
        {
            long bytes = row.LiveBytes + row.TrashedBytes;
            string hint = row.TrashedBytes > 0
                ? FilesText(row.LiveFiles) + " · " + string.Format(CultureInfo.CurrentCulture,
                    Strings.Admin_AccountsTrashed, ModlightFormat.CloudSize(row.TrashedBytes))
                : FilesText(row.LiveFiles);

            Accounts.Add(new AdminShareViewModel(
                AccountLabel(row),
                hint,
                ModlightFormat.CloudSize(bytes),
                Share(bytes, total)));
        }

        OnPropertyChanged(nameof(HasAccounts));
    }

    private void BuildFiles(ModlightDiskReport r)
    {
        Biggest.Clear();

        foreach (ModlightDiskFile file in r.Biggest ?? new List<ModlightDiskFile>())
        {
            string meta = OwnerLabel(file);

            if (file.CreatedAt != null)
            {
                meta += " · " + string.Format(CultureInfo.CurrentCulture,
                    Strings.Admin_Uploaded, CloudFileViewModel.AgeOf(file.CreatedAt));
            }

            meta += " · " + ViewsText(file.Views);

            Biggest.Add(new AdminFileViewModel(NameOf(file), meta, ModlightFormat.CloudSize(file.Size)));
        }

        Cold.Clear();

        foreach (ModlightDiskFile file in r.Cold ?? new List<ModlightDiskFile>())
        {
            // "No recorded access" is NOT "never opened", and the note under the list
            // says so. Access has only been counted since the row was created on this
            // server, and older or imported files carry no history at
            // all - so the wording has to describe the record, not the file.
            string meta = OwnerLabel(file) + " · " + (file.LastAccessedAt == null
                ? Strings.Admin_NeverFetched
                : string.Format(CultureInfo.CurrentCulture, Strings.Admin_LastFetched,
                    CloudFileViewModel.AgeOf(file.LastAccessedAt)));

            Cold.Add(new AdminFileViewModel(NameOf(file), meta, ModlightFormat.CloudSize(file.Size)));
        }

        ModlightDiskCold cold = r.ColdTotals ?? new ModlightDiskCold();

        ColdTitle = string.Format(CultureInfo.CurrentCulture, Strings.Admin_ColdTitle, cold.Days);
        ColdSummary = string.Format(CultureInfo.CurrentCulture, Strings.Admin_ColdSummary,
            ModlightFormat.CloudSize(cold.Bytes), FilesText(cold.Files));
        ColdEmptyText = string.Format(CultureInfo.CurrentCulture, Strings.Admin_ColdEmpty, cold.Days);

        OnPropertyChanged(nameof(HasBiggest));
        OnPropertyChanged(nameof(HasCold));
        OnPropertyChanged(nameof(ColdTitle));
        OnPropertyChanged(nameof(ColdSummary));
        OnPropertyChanged(nameof(ColdEmptyText));
    }

    // -----------------------------------------------------------------------
    // Small answers
    // -----------------------------------------------------------------------

    private static GridLength Weight(long bytes) =>
        new GridLength(Math.Max(0, bytes), GridUnitType.Star);

    private static double Share(long part, long total) =>
        total > 0 ? Math.Min(100, Math.Max(0, part * 100.0 / total)) : 0;

    /// <summary>
    /// The same two triggers the panel uses, and for the same reasons.
    /// </summary>
    /// <remarks>
    /// Duplicated here rather than sent over the wire because it is a JUDGEMENT about
    /// two numbers that are on the wire, and both screens have to reach the same one:
    /// the ratio catches a slow fill on a disk far larger than the reserve, and the
    /// reserve catches the line past which the server is already refusing uploads with a
    /// 507. Kept in step with lib/upload/disk.ts by name - LOW_FREE_RATIO 0.1,
    /// CRITICAL_FREE_RATIO 0.05.
    /// </remarks>
    private static string PressureOf(long free, long total, long minFree)
    {
        if (total <= 0 || free < 0)
        {
            return "ok";
        }

        double ratio = (double)free / total;

        if (ratio <= 0.05 || (minFree > 0 && free <= minFree))
        {
            return "critical";
        }

        if (ratio <= 0.10 || (minFree > 0 && free <= minFree * 2))
        {
            return "low";
        }

        return "ok";
    }

    private static string NoteFor(string pressure, long minFree)
    {
        if (pressure == "ok")
        {
            return "";
        }

        string note = pressure == "critical" ? Strings.Admin_CriticalNote : Strings.Admin_LowNote;

        return minFree > 0
            ? note + " " + string.Format(CultureInfo.CurrentCulture, Strings.Admin_ReserveNote,
                ModlightFormat.CloudSize(minFree))
            : note;
    }

    private static string KindLabel(string? kind) => kind switch
    {
        "image" => Strings.Admin_KindImage,
        "video" => Strings.Admin_KindVideo,
        "audio" => Strings.Admin_KindAudio,
        "text" => Strings.Admin_KindText,
        "pdf" => Strings.Admin_KindPdf,
        "archive" => Strings.Admin_KindArchive,
        _ => Strings.Admin_KindOther
    };

    private static string AccountLabel(ModlightDiskAccount row)
    {
        if (row.Rest)
        {
            return string.Format(CultureInfo.CurrentCulture, AccountsForm(row.Accounts), row.Accounts);
        }

        if (row.Orphaned)
        {
            return Strings.Admin_AccountsOrphaned;
        }

        if (!string.IsNullOrWhiteSpace(row.Name))
        {
            return row.Name!;
        }

        if (!string.IsNullOrWhiteSpace(row.Email))
        {
            return row.Email!;
        }

        return string.IsNullOrWhiteSpace(row.Tag) ? Strings.Admin_AccountsUnknown : row.Tag!;
    }

    private static string OwnerLabel(ModlightDiskFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.OwnerName))
        {
            return file.OwnerName!;
        }

        return string.IsNullOrWhiteSpace(file.OwnerTag)
            ? Strings.Admin_AccountsOrphaned
            : file.OwnerTag!;
    }

    private static string NameOf(ModlightDiskFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.Filename))
        {
            return file.Filename!;
        }

        return string.IsNullOrWhiteSpace(file.Code) ? "?" : file.Code!;
    }

    private static string FilesText(long count) =>
        string.Format(CultureInfo.CurrentCulture, PluralForm(count,
            Strings.Admin_FilesOne, Strings.Admin_FilesFew, Strings.Admin_FilesMany), count);

    private static string ViewsText(long count) =>
        string.Format(CultureInfo.CurrentCulture, PluralForm(count,
            Strings.Admin_ViewsOne, Strings.Admin_ViewsFew, Strings.Admin_ViewsMany), count);

    private static string AccountsForm(long count) =>
        PluralForm(count, Strings.Admin_AccountsRestOne, Strings.Admin_AccountsRestFew,
            Strings.Admin_AccountsRestMany);

    /// <summary>
    /// Which of the three wordings a count takes.
    /// </summary>
    /// <remarks>
    /// The Polish rule, exactly as <c>CloudFileViewModel.YearForm</c> applies it to
    /// years: one, then two-to-four outside the teens, then everything else. English
    /// needs no rule and gets the right answer from this one anyway, because its "few"
    /// and "many" resources are the same string and only "one" differs - which is the
    /// case this rule separates out. One rule, two languages, no per-culture branch
    /// waiting to be got wrong by a third.
    /// </remarks>
    private static string PluralForm(long count, string one, string few, string many)
    {
        if (count == 1)
        {
            return one;
        }

        long lastTwo = Math.Abs(count) % 100;
        long last = Math.Abs(count) % 10;

        return last >= 2 && last <= 4 && (lastTwo < 12 || lastTwo > 14) ? few : many;
    }

    private void SetState(AdminDiskState next)
    {
        state = next;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(ProblemTitle));
        OnPropertyChanged(nameof(ProblemBody));
        OnPropertyChanged(nameof(ProblemIcon));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One entry beneath the stacked bar: what a colour on it means.</summary>
/// <remarks>
/// <paramref name="key"/> is a style class, not a colour. The app wears fourteen skins
/// in two modes each, and a brush written down here would be the one thing on the screen
/// that does not change with them.
/// </remarks>
internal sealed class AdminLegendViewModel
{
    public AdminLegendViewModel(string key, string label, string value, string hint)
    {
        Key = key;
        Label = label;
        Value = value;
        Hint = hint;
    }

    /// <summary>live / trash / other / free / app.</summary>
    public string Key { get; }

    public bool IsLive => Key == "live";

    public bool IsTrash => Key == "trash";

    public bool IsOther => Key == "other";

    public bool IsFree => Key == "free";

    public bool IsApp => Key == "app";

    public string Label { get; }

    public string Value { get; }

    public string Hint { get; }
}

/// <summary>One row of a breakdown: a name, a size, and its share of the whole.</summary>
internal sealed class AdminShareViewModel
{
    public AdminShareViewModel(string label, string hint, string value, double percent)
    {
        Label = label;
        Hint = hint;
        Value = value;
        Percent = percent;
    }

    public string Label { get; }

    public string Hint { get; }

    public string Value { get; }

    /// <summary>Share of the breakdown this row belongs to, 0-100.</summary>
    public double Percent { get; }
}

/// <summary>One named file on the administration screen.</summary>
internal sealed class AdminFileViewModel
{
    public AdminFileViewModel(string name, string meta, string size)
    {
        Name = name;
        Meta = meta;
        Size = size;
    }

    public string Name { get; }

    /// <summary>Owner, age, and whatever the list it is in cares about.</summary>
    public string Meta { get; }

    public string Size { get; }
}
