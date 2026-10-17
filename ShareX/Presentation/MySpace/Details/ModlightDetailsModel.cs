#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The right-hand details drawer: one file, everything about it, and the actions.
/// </summary>
/// <remarks>
/// This replaces a properties DIALOG, which is the shape a desktop app usually gives
/// this job and the shape the owner rejected by name. His objection is the correct one:
/// a modal window is a place you go and come back from, and inspecting a library means
/// looking at one file after another. The drawer stays put while the selection moves
/// under it, which turns "open, read, close, click the next one, open again" into
/// clicking the next card.
///
/// Three rules follow from that, and each of them is a bug if broken:
///
/// 1. IT FOLLOWS THE SELECTION. Clicking another card retargets the drawer. It does not
///    open a second one and it does not have to be closed first.
///
/// 2. CLOSING IT LEAVES THE SELECTION ALONE. The drawer is a view of the selection, not
///    the owner of it. Dismissing a view must not throw away what the user had marked -
///    that is the one thing which would make people afraid to close it.
///
/// 3. IT SPENDS NOTHING. Every figure comes from the row the grid was drawn from, and
///    the only request it makes is for a preview at a resize URL, which the serving layer
///    treats as a preview: no view counted, no one-time link burned. A drawer that spent
///    a one-time link by being LOOKED at would be a destructive way to inspect a file.
/// </remarks>
internal sealed class ModlightDetailsModel : INotifyPropertyChanged
{
    /// <summary>How long the drawer takes to travel, matching Border.dd-shell.</summary>
    private static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(180);

    private MySpaceViewModel? space;
    private CloudFileViewModel? file;

    private bool isPresent;
    private bool isOpen;
    private bool isInstant;
    private bool primaryCopied;

    /// <summary>Which open or close the current movement belongs to, so a reversal cancels it.</summary>
    private int step;

    private Bitmap? preview;
    private CancellationTokenSource? previewWork;

    private IReadOnlyList<DetailsFact> facts = Array.Empty<DetailsFact>();
    private IReadOnlyList<DetailsLink> links = Array.Empty<DetailsLink>();
    private IReadOnlyList<DetailsGate> gates = Array.Empty<DetailsGate>();

    /* ---------------------------------------------------------------------- */
    /* What the window does on the drawer's behalf                            */
    /* ---------------------------------------------------------------------- */

    /// <summary>Toggle published. The window owns the request, the retry and the toast.</summary>
    public Action<CloudFileViewModel>? VisibilityRequested { get; set; }

    /// <summary>Offer the folder list, anchored on the button that asked for it.</summary>
    public Action<CloudFileViewModel, Control>? FolderRequested { get; set; }

    public Action<CloudFileViewModel>? LinkSettingsRequested { get; set; }

    public Action<CloudFileViewModel>? DownloadRequested { get; set; }

    public Action<CloudFileViewModel>? TrashRequested { get; set; }

    public Action<CloudFileViewModel>? RestoreRequested { get; set; }

    public Action<CloudFileViewModel>? DeleteForeverRequested { get; set; }

    /* ---------------------------------------------------------------------- */
    /* Wiring                                                                 */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Watch the tab this drawer belongs to. Called once.
    /// </summary>
    /// <remarks>
    /// Two subscriptions, answering different questions. The selection count is how the
    /// drawer FOLLOWS a click on another card - it is the only signal the tab raises for
    /// "the marked files changed", and a plain click emits it twice (down to nothing, then
    /// up to one), which is harmless because retargeting to the file already on show is a
    /// no-op. The collection is how the drawer notices that the row it is describing has
    /// been deleted, moved out of the folder on screen, or replaced by a reload - all of
    /// which otherwise end with a drawer describing something that no longer exists.
    /// </remarks>
    public void Attach(MySpaceViewModel tab)
    {
        if (space != null)
        {
            return;
        }

        space = tab;
        tab.PropertyChanged += OnTabChanged;
        tab.Files.CollectionChanged += OnFilesChanged;
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (isPresent && e.PropertyName == nameof(MySpaceViewModel.SelectedFileCount))
        {
            Follow();
        }
    }

    private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!isPresent)
        {
            return;
        }

        if (file == null || space == null || !space.Files.Contains(file))
        {
            // The row went. Closed rather than moved on to a neighbour: the drawer shows
            // what the user pointed at, and silently swapping a different file in under a
            // heading full of buttons is how somebody publishes the wrong thing.
            Close();
            return;
        }

        // Still here. Only its PLACE in the listing can have moved, so only that is
        // redone: a full rebuild would make new link rows, which would throw away the
        // tick on one just copied and flicker the whole section every time a page of
        // sixty files arrived.
        RefreshPosition();
    }

    /// <summary>
    /// Point the drawer at whatever is marked now.
    /// </summary>
    /// <remarks>
    /// One marked file retargets. A marked GROUP retargets only when the file on show is
    /// not part of it, because a Shift+click that extends a range around the current file
    /// is not a request to look at a different one. Nothing marked changes nothing at all:
    /// Escape clears the selection, and it must not also empty the drawer or the two
    /// gestures become one.
    /// </remarks>
    private void Follow()
    {
        if (space == null)
        {
            return;
        }

        List<CloudFileViewModel> marked = space.SelectedFiles();

        if (marked.Count == 0)
        {
            return;
        }

        if (marked.Count == 1 || file == null || !marked.Contains(file))
        {
            Show(marked[0]);
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Opening and closing                                                    */
    /* ---------------------------------------------------------------------- */

    /// <summary>Show this file, sliding in if the drawer was not already out.</summary>
    /// <remarks>
    /// The test is "already out AND staying out", not "already out", and the difference is
    /// a drawer that never comes back. Between <see cref="Close"/> and the
    /// <see cref="Retire"/> that follows it a fifth of a second later, the drawer is still
    /// present and is travelling away - so a plain isPresent test would take that as
    /// nothing to do, retarget the contents, leave it sliding out, and then let the retire
    /// already in flight put it away. Asking for details on another file in that fifth of
    /// a second would appear to do nothing at all. Falling through instead bumps
    /// <see cref="step"/>, which cancels that retire, and sends it back the way it came.
    /// </remarks>
    public void Open(CloudFileViewModel target)
    {
        Show(target);

        if (isPresent && isOpen)
        {
            return;
        }

        // Read at the moment it matters rather than remembered from startup: the system
        // switch exists to take motion away from people who asked for that, and they are
        // entitled to have flipped it since the app opened.
        IsInstant = !ClientAreaAnimation.Enabled;

        int mine = ++step;
        IsPresent = true;

        if (isInstant)
        {
            IsOpen = true;
            return;
        }

        // Posted rather than set here, for the reason the library panel states at its own
        // slide: a Border realised into the tree already carrying the open class has no
        // previous value to animate FROM, so it would appear rather than arrive.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (step == mine)
                {
                    IsOpen = true;
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Put the drawer away. THE SELECTION IS NOT TOUCHED.
    /// </summary>
    /// <remarks>
    /// Stated this loudly because it is the one thing about this control the owner would
    /// notice being wrong: the cards stay marked, so closing the drawer to get the width
    /// back does not cost the group somebody spent a minute Ctrl+clicking together.
    /// </remarks>
    public void Close()
    {
        if (!isPresent)
        {
            return;
        }

        int mine = ++step;
        IsOpen = false;

        if (isInstant)
        {
            Retire();
            return;
        }

        // A little longer than the travel, because a control removed on the same frame the
        // transition ends occasionally clips its own last frame.
        DispatcherTimer.RunOnce(
            () =>
            {
                if (step == mine)
                {
                    Retire();
                }
            },
            Slide + TimeSpan.FromMilliseconds(40));
    }

    /// <summary>Out of the tree, and let go of everything it was holding.</summary>
    private void Retire()
    {
        IsPresent = false;

        previewWork?.Cancel();
        previewWork?.Dispose();
        previewWork = null;

        Watch(null);
        SetPreview(null);
        Rebuild();
    }

    /* ---------------------------------------------------------------------- */
    /* Which file                                                             */
    /* ---------------------------------------------------------------------- */

    private void Show(CloudFileViewModel target)
    {
        if (ReferenceEquals(file, target))
        {
            return;
        }

        Watch(target);
        Rebuild();
        StartPreview(target);
    }

    /// <summary>
    /// Listen to the file itself, so an edit redraws the drawer.
    /// </summary>
    /// <remarks>
    /// Every write endpoint answers with the affected row and the tab feeds it back
    /// through <see cref="CloudFileViewModel.Apply"/>, which is what keeps a card correct
    /// after "Make private" without reloading the folder. The drawer has to hear the same
    /// news, or it goes on claiming a file is public while the card behind it says
    /// otherwise. Code is the pulse it listens for, because Apply raises that one
    /// unconditionally on every call - one rebuild per edit rather than one per property.
    /// </remarks>
    private void Watch(CloudFileViewModel? target)
    {
        if (file != null)
        {
            file.PropertyChanged -= OnFileChanged;
        }

        file = target;

        if (file != null)
        {
            file.PropertyChanged += OnFileChanged;
        }
    }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloudFileViewModel.Code))
        {
            Rebuild();
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Paging                                                                 */
    /* ---------------------------------------------------------------------- */

    public void Previous() => Step(-1);

    public void Next() => Step(1);

    /// <summary>
    /// Walk the grid without leaving the drawer.
    /// </summary>
    /// <remarks>
    /// It moves the SELECTION rather than only the drawer's target, which is what keeps
    /// the two from disagreeing: the file the drawer describes is the card drawn as
    /// marked, so the arrows and the grid tell one story. It also feeds the tab's own
    /// machinery, so the anchor a following Shift+click measures from moves with it.
    /// </remarks>
    private void Step(int delta)
    {
        if (space == null || file == null)
        {
            return;
        }

        int at = space.Files.IndexOf(file);

        if (at < 0)
        {
            return;
        }

        int next = at + delta;

        if (next < 0 || next >= space.Files.Count)
        {
            return;
        }

        CloudFileViewModel target = space.Files[next];
        space.SelectFile(target);
        Show(target);
    }

    /* ---------------------------------------------------------------------- */
    /* Acting on the file                                                     */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// The account's own shape of link on the clipboard, and a tick to say so.
    /// </summary>
    /// <remarks>
    /// Which shape that is comes from the server - the account's copy_raw switch decides
    /// it, exactly as it decides what a ShareX upload puts on the clipboard - so the one
    /// switch in the web panel governs this button too, with nothing to keep in step.
    ///
    /// The tick rather than a message in the corner: a copy is not news, it is a
    /// confirmation, and the button that was pressed is where the eye already is.
    /// </remarks>
    public void CopyPrimaryLink()
    {
        if (file == null || !file.HasLink)
        {
            return;
        }

        ClipboardHelpers.CopyText(file.CopyLink);

        PrimaryCopied = true;
        DispatcherTimer.RunOnce(() => PrimaryCopied = false, TimeSpan.FromMilliseconds(1400));
    }

    public void CopyRow(DetailsLink row)
    {
        if (row.Value.Length == 0)
        {
            return;
        }

        ClipboardHelpers.CopyText(row.Value);
        row.NoteCopied();
    }

    public void OpenInBrowser()
    {
        if (file != null && file.CanOpen)
        {
            URLHelpers.OpenURL(file.OpenLink);
        }
    }

    public void Download()
    {
        if (file != null)
        {
            DownloadRequested?.Invoke(file);
        }
    }

    public void ToggleVisibility()
    {
        if (file != null)
        {
            VisibilityRequested?.Invoke(file);
        }
    }

    public void ChangeFolder(Control anchor)
    {
        if (file != null)
        {
            FolderRequested?.Invoke(file, anchor);
        }
    }

    public void EditLinkSettings()
    {
        if (file != null)
        {
            LinkSettingsRequested?.Invoke(file);
        }
    }

    public void Trash()
    {
        if (file != null)
        {
            TrashRequested?.Invoke(file);
        }
    }

    public void Restore()
    {
        if (file != null)
        {
            RestoreRequested?.Invoke(file);
        }
    }

    public void DeleteForever()
    {
        if (file != null)
        {
            DeleteForeverRequested?.Invoke(file);
        }
    }

    /* ---------------------------------------------------------------------- */
    /* The preview                                                            */
    /* ---------------------------------------------------------------------- */

    private void StartPreview(CloudFileViewModel target)
    {
        previewWork?.Cancel();
        previewWork?.Dispose();
        previewWork = null;
        SetPreview(null);

        if (target.ThumbnailUrl.Length == 0)
        {
            return;
        }

        CancellationTokenSource work = new();
        previewWork = work;
        _ = LoadPreviewAsync(target, work);
    }

    /// <summary>
    /// The tile first, the full-size picture when it arrives.
    /// </summary>
    /// <remarks>
    /// Two steps, because they answer at different speeds and the drawer opens NOW. The
    /// warm one is already in memory, so there is a picture on the frame the drawer
    /// appears; the sharp one costs a round trip and quietly replaces it. Both are checked
    /// against the current job before they are shown - by the time either finishes the
    /// user may be three files further on, and a preview landing on the wrong file is
    /// worse than no preview at all.
    /// </remarks>
    private async Task LoadPreviewAsync(CloudFileViewModel target, CancellationTokenSource work)
    {
        try
        {
            Bitmap? warm = await ModlightDetailsPreview.WarmAsync(target);

            if (Stale(work))
            {
                warm?.Dispose();
                return;
            }

            if (warm != null)
            {
                SetPreview(warm);
            }

            Bitmap? full = await ModlightDetailsPreview.LoadAsync(target, work.Token);

            if (full == null)
            {
                return;
            }

            if (Stale(work))
            {
                full.Dispose();
                return;
            }

            SetPreview(full);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // One picture that will not load is not worth saying out loud. The drawer keeps
            // the placeholder its kind earned it, exactly as a card does.
            DebugHelper.WriteException(exception);
        }
    }

    /// <summary>This job has been overtaken, so whatever it produced belongs to nobody.</summary>
    private bool Stale(CancellationTokenSource work) =>
        work.IsCancellationRequested || !ReferenceEquals(previewWork, work);

    private void SetPreview(Bitmap? bitmap)
    {
        if (ReferenceEquals(preview, bitmap))
        {
            return;
        }

        Bitmap? old = preview;
        preview = bitmap;

        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasNoPreview));

        // After the notification, never before: the Image is still pointing at it until
        // the binding has been told otherwise, and disposing first draws a dead handle.
        old?.Dispose();
    }

    /* ---------------------------------------------------------------------- */
    /* Everything the drawer draws                                            */
    /* ---------------------------------------------------------------------- */

    /// <summary>Whether the drawer is in the tree at all. False once it has finished leaving.</summary>
    public bool IsPresent
    {
        get => isPresent;
        private set => SetField(ref isPresent, value);
    }

    /// <summary>Whether it is at its open position. Drives the render transform.</summary>
    public bool IsOpen
    {
        get => isOpen;
        private set => SetField(ref isOpen, value);
    }

    /// <summary>The system asked for no animations, so the drawer arrives without travelling.</summary>
    public bool IsInstant
    {
        get => isInstant;
        private set => SetField(ref isInstant, value);
    }

    public bool HasFile => file != null;

    public Bitmap? Preview => preview;

    public bool HasPreview => preview != null;

    public bool HasNoPreview => preview == null;

    public string Name { get; private set; } = "";

    /// <summary>The kind badge - PNG, MP4, PASTE - which the panel puts in the same corner.</summary>
    public string TypeBadge { get; private set; } = "";

    public bool HasBadge => TypeBadge.Length > 0;

    /// <summary>The glyph on the primary copy button: a tick for a moment, a copy otherwise.</summary>
    public string PrimaryCopyIcon => primaryCopied ? LucideIcons.check : LucideIcons.copy;

    private bool PrimaryCopied
    {
        get => primaryCopied;
        set
        {
            if (SetIfChanged(ref primaryCopied, value))
            {
                OnPropertyChanged(nameof(PrimaryCopyIcon));
            }
        }
    }

    public string SizeText { get; private set; } = "";

    public string DimensionsText { get; private set; } = "";

    public bool HasDimensions => DimensionsText.Length > 0;

    public string PlaceholderIcon { get; private set; } = LucideIcons.file;

    public string PositionText { get; private set; } = "";

    public bool CanPrevious { get; private set; }

    public bool CanNext { get; private set; }

    public bool CanCopy { get; private set; }

    public bool CanOpen { get; private set; }

    public bool CanDownload { get; private set; }

    /// <summary>A soft-deleted row: no links, and the last section offers the way back.</summary>
    public bool IsInTrash { get; private set; }

    public bool IsLive => !IsInTrash;

    public IReadOnlyList<DetailsFact> Facts => facts;

    public IReadOnlyList<DetailsLink> Links => links;

    public IReadOnlyList<DetailsGate> Gates => gates;

    public bool HasGates => gates.Count > 0;

    public bool HasNoGates => gates.Count == 0;

    public string FolderName { get; private set; } = "";

    public string FolderIcon { get; private set; } = LucideIcons.inbox;

    public string VisibilityText { get; private set; } = "";

    public string VisibilityIcon { get; private set; } = LucideIcons.lock_keyhole;

    public string VisibilityAction { get; private set; } = "";

    public string VisibilityActionIcon { get; private set; } = LucideIcons.globe;

    public string ViewsText { get; private set; } = "";

    public string DownloadsText { get; private set; } = "";

    /// <summary>
    /// Work out everything on screen from the row, in one pass.
    /// </summary>
    /// <remarks>
    /// One method rather than a property per fact reading <c>file</c> in its getter: the
    /// drawer redraws on three separate signals - an edit, a move, a reload - and a dozen
    /// independent getters is a dozen chances for one of them to be left out of a
    /// notification list and go stale on screen on its own.
    /// </remarks>
    private void Rebuild()
    {
        CloudFileViewModel? row = file;

        if (row == null)
        {
            Name = "";
            TypeBadge = "";
            SizeText = "";
            DimensionsText = "";
            PositionText = "";
            CanPrevious = false;
            CanNext = false;
            CanCopy = false;
            CanOpen = false;
            CanDownload = false;
            IsInTrash = false;
            facts = Array.Empty<DetailsFact>();
            links = Array.Empty<DetailsLink>();
            gates = Array.Empty<DetailsGate>();
            NotifyAll();
            return;
        }

        ModlightUpload upload = row.Row;

        Name = row.Name;
        TypeBadge = BadgeFor(upload);
        SizeText = ModlightFormat.CloudSize(upload.Size);
        DimensionsText = upload.Width > 0 && upload.Height > 0
            ? upload.Width + " x " + upload.Height
            : "";
        PlaceholderIcon = row.Icon;

        IsInTrash = row.IsTrashed;
        CanCopy = row.HasLink;
        CanOpen = row.CanOpen;
        CanDownload = row.HasMediaBase;

        RefreshPosition();

        List<DetailsFact> newFacts = new()
        {
            new DetailsFact(Strings.MySpace_DetailsType,
                string.IsNullOrEmpty(upload.MimeType) ? "-" : upload.MimeType!, mono: true),
            new DetailsFact(Strings.MySpace_DetailsSize, SizeText)
        };

        // Only when both are known. Half a pair of dimensions is not a dimension.
        if (upload.Width > 0 && upload.Height > 0)
        {
            newFacts.Add(new DetailsFact(Strings.MySpace_DetailsDimensions, DimensionsText + " px"));
        }

        if (upload.CreatedAt != null)
        {
            newFacts.Add(new DetailsFact(Strings.MySpace_DetailsUploaded,
                upload.CreatedAt.Value.LocalDateTime.ToString("f", CultureInfo.CurrentCulture)));
        }

        newFacts.Add(new DetailsFact(Strings.MySpace_DetailsCode, row.Code, mono: true));
        facts = newFacts;

        // A trashed row is given no links rather than five that 404, which is the same
        // choice the server makes when it withholds every URL for one.
        links = row.IsTrashed
            ? Array.Empty<DetailsLink>()
            : new DetailsLink[]
            {
                new(Strings.MySpace_LinkFormatDirect, row.LinkFormat(CloudLinkFormat.Direct)),
                new(Strings.MySpace_LinkFormatRaw, row.LinkFormat(CloudLinkFormat.Raw)),
                new(Strings.MySpace_LinkFormatMarkdown, row.LinkFormat(CloudLinkFormat.Markdown)),
                new(Strings.MySpace_LinkFormatBBCode, row.LinkFormat(CloudLinkFormat.BBCode)),
                new(Strings.MySpace_LinkFormatHtml, row.LinkFormat(CloudLinkFormat.Html))
            };

        string folder = space?.FolderNameFor(row.FolderId) ?? "";
        FolderName = folder.Length > 0 ? folder : Strings.MySpace_Unfiled;
        FolderIcon = folder.Length > 0 ? LucideIcons.folder : LucideIcons.inbox;

        VisibilityText = row.Published ? Strings.MySpace_VisibilityPublic : Strings.MySpace_VisibilityPrivate;
        VisibilityIcon = row.Published ? LucideIcons.globe : LucideIcons.lock_keyhole;
        VisibilityAction = row.Published ? Strings.MySpace_MakePrivate : Strings.MySpace_MakePublic;
        VisibilityActionIcon = row.Published ? LucideIcons.globe_lock : LucideIcons.globe;

        ViewsText = upload.Views.ToString("N0", CultureInfo.CurrentCulture);
        DownloadsText = upload.Downloads.ToString("N0", CultureInfo.CurrentCulture);

        List<DetailsGate> newGates = new();

        if (upload.ExpiresAt != null)
        {
            newGates.Add(new DetailsGate(LucideIcons.clock,
                string.Format(CultureInfo.CurrentCulture, Strings.MySpace_BadgeExpires,
                    upload.ExpiresAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))));
        }

        if (upload.MaxViews != null)
        {
            newGates.Add(new DetailsGate(LucideIcons.flame,
                string.Format(CultureInfo.CurrentCulture, Strings.MySpace_BadgeMaxViews,
                    upload.Views, upload.MaxViews.Value)));
        }

        if (upload.HasPassword)
        {
            newGates.Add(new DetailsGate(LucideIcons.key_round, Strings.MySpace_BadgePassword));
        }

        gates = newGates;
        NotifyAll();
    }

    /// <summary>Where this file sits in the listing, and whether the arrows have anywhere to go.</summary>
    private void RefreshPosition()
    {
        CloudFileViewModel? row = file;
        int at = row == null || space == null ? -1 : space.Files.IndexOf(row);
        int total = space?.Files.Count ?? 0;

        PositionText = at >= 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.MySpace_DetailsPosition, at + 1, total)
            : "";
        CanPrevious = at > 0;
        CanNext = at >= 0 && at < total - 1;

        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
    }

    /// <summary>The extension in capitals, or the server's own word for the kind.</summary>
    private static string BadgeFor(ModlightUpload upload)
    {
        if (!string.IsNullOrEmpty(upload.Ext))
        {
            return upload.Ext!.ToUpperInvariant();
        }

        return string.IsNullOrEmpty(upload.Kind) ? "" : upload.Kind!.ToUpperInvariant();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TypeBadge));
        OnPropertyChanged(nameof(HasBadge));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(DimensionsText));
        OnPropertyChanged(nameof(HasDimensions));
        OnPropertyChanged(nameof(PlaceholderIcon));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(IsInTrash));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(Facts));
        OnPropertyChanged(nameof(Links));
        OnPropertyChanged(nameof(Gates));
        OnPropertyChanged(nameof(HasGates));
        OnPropertyChanged(nameof(HasNoGates));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(FolderIcon));
        OnPropertyChanged(nameof(VisibilityText));
        OnPropertyChanged(nameof(VisibilityIcon));
        OnPropertyChanged(nameof(VisibilityAction));
        OnPropertyChanged(nameof(VisibilityActionIcon));
        OnPropertyChanged(nameof(ViewsText));
        OnPropertyChanged(nameof(DownloadsText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) =>
        SetIfChanged(ref field, value, propertyName);

    /// <summary>Set, notify, and say whether anything happened - for properties with dependents.</summary>
    private bool SetIfChanged<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
