#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The My space tab: the account's folders, the files in the chosen one, and which of
/// the four things that can be true right now is true.
/// </summary>
/// <remarks>
/// UI thread only. Every field here is single-threaded by that rule, and the loads keep
/// it that way by awaiting on Avalonia's synchronisation context rather than posting
/// back by hand - the one exception being the thumbnails, which are deliberately pushed
/// onto worker threads and marshalled back, because decoding sixty images on the thread
/// that draws them is exactly the stutter this grid must not have.
///
/// The four states are mutually exclusive and each one names what happened, because an
/// empty grid and a broken connection looking alike is the specific mistake that made
/// the owner blame this app for a failure in his browser.
/// </remarks>
internal sealed class MySpaceViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// How many files one page holds.
    /// </summary>
    /// <remarks>
    /// A CHOICE now rather than a constant: the grid is paged the way the web panel is
    /// paged, with the panel's three sizes and the panel's arithmetic - see
    /// <see cref="LibraryPager"/> - and the size in force is remembered between runs.
    ///
    /// It used to be sixty and the grid used to APPEND. That is the part worth naming:
    /// scrolling to the end of the owner's library meant 6,926 cards realised at once, in
    /// a wrapping panel Avalonia 12.1.1 cannot virtualise, every one of them holding a
    /// place in the layout for as long as the window stayed on the tab. A page is now a
    /// page - <see cref="LoadPageAsync"/> replaces the cards instead of adding to them.
    /// </remarks>
    private int pageSize = LibraryPager.DefaultPageSize;

    /// <summary>
    /// How many preview images to fetch at once.
    /// </summary>
    /// <remarks>
    /// Enough to fill the visible rows quickly, few enough that arriving on the tab
    /// cannot open sixty sockets at once - and the first request for a given size makes
    /// the server render it with sharp, so a burst is paid for in its CPU as well as
    /// ours.
    /// </remarks>
    private const int ThumbnailSlots = 4;

    /// <summary>
    /// How long a folder tile says a file just landed in it.
    /// </summary>
    /// <remarks>
    /// Long enough to be seen by somebody who was looking elsewhere when the upload
    /// finished, short enough that it is over before it starts reading as a second
    /// selection mark on the shelf.
    /// </remarks>
    private static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(1.6);

    /// <summary>
    /// How wide to decode a preview, in pixels.
    /// </summary>
    /// <remarks>
    /// The server renders tiles at 480x480 and the card draws them into 178x126, so
    /// decoding what arrives at its full size spent about three times the memory the
    /// screen could use. 288 is chosen against the card: 178 logical pixels is 267 at the
    /// 150% scaling most laptops ship at, so anything up to that is drawn from more pixels
    /// than it shows. Only ever a REDUCTION - a picture that arrives smaller than this is
    /// kept as it came, because enlarging it would cost memory to add blur.
    /// </remarks>
    private const int ThumbnailDecodeWidth = 288;

    /// <summary>
    /// How many bytes of decoded pictures the grid may hold.
    /// </summary>
    /// <remarks>
    /// 64 MiB is roughly two hundred tiles at the size above, which is several screenfuls
    /// on any monitor this runs on. See <see cref="ThumbnailBudget{T}"/> for why there has
    /// to be a number here at all.
    /// </remarks>
    private const long ThumbnailBudgetBytes = 64L * 1024 * 1024;

    /// <summary>
    /// How many cards either side of the visible ones keep their pictures.
    /// </summary>
    /// <remarks>
    /// Wide enough that a flick of the wheel lands on tiles that already have their
    /// pictures rather than on a wave of icons filling in behind the pointer.
    /// </remarks>
    private const int VisibleBandMargin = 40;

    /// <summary>The current listing. Cancelled on a folder change, a reload or a sign-out.</summary>
    private CancellationTokenSource? listing;

    /// <summary>
    /// The ceiling on decoded pictures. One per listing lifetime, not one per page.
    /// </summary>
    private readonly ThumbnailBudget<CloudFileViewModel> pictures =
        new(ThumbnailBudgetBytes, file => file.ReleaseThumbnail());

    /// <summary>
    /// Which previews are fetched, in what order, and how many at once.
    /// </summary>
    /// <remarks>
    /// This was a bare <c>SemaphoreSlim</c>, and a semaphore is FIRST COME, FIRST SERVED.
    /// Every card that came within <see cref="VisibleBandMargin"/> of the viewport started
    /// a task that waited on it, so a fast scroll lined up every tile it went PAST ahead
    /// of the tiles it stopped on, and the tile under the pointer was served last. That is
    /// the whole of the "after scrolling quickly, thumbnails take a very long time to
    /// appear" report; <see cref="ThumbnailQueue{T}"/> carries the measurement.
    ///
    /// The delegate reads the account and the listing when the fetch STARTS rather than
    /// when it was queued, which is the point of queueing: something asked for before a
    /// page turn must not be fetched against the listing that replaced it.
    /// </remarks>
    private readonly ThumbnailQueue<CloudFileViewModel> thumbnails;

    /// <summary>Scratch buffer for the pinned set, so a scroll does not allocate a list per event.</summary>
    private readonly List<CloudFileViewModel> visibleBuffer = new();

    /// <summary>Scratch buffer for the want-set handed to <see cref="thumbnails"/>.</summary>
    private readonly List<CloudFileViewModel> wantBuffer = new();

    /// <summary>
    /// The one tile allowed to move, and everything that decides which.
    /// </summary>
    /// <remarks>
    /// Deliberately OUTSIDE <see cref="pictures"/>. That budget bounds the stills, which
    /// are held by every card in the band around the viewport - hundreds of them, each
    /// wanted again the moment it is scrolled back to, which is what a ceiling with an
    /// eviction order is for. This holds at most one file's frames, for as long as a
    /// pointer rests on one tile, and gives them back when it leaves; there is nothing to
    /// choose between and nothing to evict. Its worst case is one tile, and that number
    /// moved: the server's preview is a thirty-frame clip now, not an eight-frame spread,
    /// so a square tile is 9.49 MiB decoded and a portrait one would be 14.24 MiB -
    /// against 2.6 MiB when this was written. <see cref="AnimatedThumbnail"/> holds it to
    /// ten by dropping frames on the tall tiles, and the 64 MiB ceiling next door still
    /// does not have to move, because ONE reel exists at a time however many videos the
    /// account has.
    /// </remarks>
    private readonly CloudMotionPlayer motion = new();

    /// <summary>The page on screen. One-based, like the server's.</summary>
    private int page = 1;

    /// <summary>Whether the page size saved from last time has been taken up yet.</summary>
    private bool restoredPageSize;

    /// <summary>Host and token the files on screen belong to.</summary>
    private string identity = "";

    /// <summary>
    /// The class of file the grid is filtered to, or null for every class.
    /// </summary>
    /// <remarks>
    /// A filter over the WHOLE library, never a place inside it, which is what it is in
    /// the web panel: choosing "Images" there drops the folder from the query string on
    /// purpose. So this and <see cref="currentFolderId"/> are mutually exclusive by
    /// construction rather than by accident - every path that sets one clears the other.
    /// </remarks>
    private string? currentKind;

    /// <summary>The grid is listing the server's trash rather than the library.</summary>
    private bool trashed;

    /// <summary>The folder being browsed. Null is the top of the library, not a folder.</summary>
    /// <remarks>
    /// Never assigned directly - every write goes through <see cref="SetCurrentFolder"/>,
    /// which also publishes it as the app's upload target. Five places move this field and
    /// only one of them is navigation; the others are a sign-out, an account change, the
    /// folder restored at startup, and the fallback taken when the folder that was open
    /// has since been deleted in the panel. A publish attached to the one obvious caller
    /// would have been silently wrong in the other four.
    /// </remarks>
    private string? currentFolderId;

    /// <summary>
    /// What the server calls "in no folder", as it spelled it.
    /// </summary>
    /// <remarks>
    /// Read from GET /api/folders rather than hardcoded, because that endpoint exists in
    /// part to hand out its own sentinel; falling back to the constant only covers a
    /// deployment old enough not to send it.
    /// </remarks>
    private string rootValue = ModlightLibrary.RootFolder;

    /// <summary>Every real folder on the account, flat, in the order the panel lists them.</summary>
    private readonly List<CloudFolderViewModel> allFolders = new();

    /// <summary>The rows above the folder tree: everything, the six classes, the trash.</summary>
    private readonly List<LibraryNavViewModel> views = LibraryNavViewModel.BuildAll();

    /// <summary>The loose-files shelf, or null when the server did not report one.</summary>
    private CloudFolderViewModel? unfiled;

    /// <summary>The tile clicked once, waiting for a second click or a menu.</summary>
    private CloudFolderViewModel? selectedFolder;

    /// <summary>Where a Shift+click measures its range from.</summary>
    private CloudFileViewModel? selectionAnchor;

    /// <summary>Whether the folder saved from last time has been taken up yet.</summary>
    private bool restoredLastFolder;

    /// <summary>A "what is new" request is in the air.</summary>
    private bool merging;

    /// <summary>Something else finished while that request was in the air.</summary>
    private bool mergeQueued;

    /// <summary>Which flash is the current one, so a second upload cancels the first one's timer.</summary>
    private int flashVersion;

    private int total;

    private bool hasAccount;
    private bool isBusy;
    private bool hasError;
    private bool loadedOnce;
    private string errorText = "";
    private string countText = "";
    private int selectedFileCount;

    public MySpaceViewModel()
    {
        // Built here rather than as field initialisers because both of them close over
        // methods on this object, which a field initialiser may not do.
        thumbnails = new ThumbnailQueue<CloudFileViewModel>(
            ThumbnailSlots, FetchThumbnailAsync, action => Dispatcher.UIThread.Post(action));

        Pager = new LibraryPagerViewModel(GoToPage, SetPageSize);
    }

    /// <summary>
    /// The bar under the grid: which page is showing, and the way to another one.
    /// </summary>
    public LibraryPagerViewModel Pager { get; }

    /// <summary>
    /// The library panel's top half: All files, the six classes of file, and the trash.
    /// </summary>
    /// <remarks>
    /// Built once and then only marked, never rebuilt - the rows do not depend on the
    /// account, and replacing them on every listing would throw away the mark mid-click.
    /// </remarks>
    public IReadOnlyList<LibraryNavViewModel> Views => views;

    /// <summary>
    /// Every folder on the account, flat, indented by depth. The optional side list.
    /// </summary>
    /// <remarks>
    /// The shelf shows one LEVEL; this shows the lot, and it is the "in case you want it"
    /// list the owner allowed as an extra - never the only way to reach a folder. Both
    /// are fed from GET /api/folders and never from the files on screen, which is the
    /// whole reason an empty folder appears at all: it exists in the panel, and a list
    /// derived from files could not know that.
    /// </remarks>
    public ObservableCollection<CloudFolderViewModel> Folders { get; } = new();

    /// <summary>The folders sitting directly inside the one being browsed.</summary>
    public ObservableCollection<CloudFolderViewModel> VisibleFolders { get; } = new();

    /// <summary>The path to the folder being browsed. One entry (the root) means "no path".</summary>
    public ObservableCollection<FolderCrumbViewModel> Crumbs { get; } = new();

    public ObservableCollection<CloudFileViewModel> Files { get; } = new();

    /// <summary>Somebody has to look at the Capture tab. Set by the window that owns this.</summary>
    public Action? GoToCapture { get; set; }

    /// <summary>An account is configured. The folder row and the grid only exist then.</summary>
    public bool HasAccount
    {
        get => hasAccount;
        private set
        {
            SetField(ref hasAccount, value);
            OnPropertyChanged(nameof(IsSignedOut));
        }
    }

    /// <summary>
    /// The other face of <see cref="HasAccount"/>, as a property rather than a negated
    /// binding: Avalonia's <c>!</c> prefix is not compile-checked here, and a silently
    /// broken binding would leave two states drawn on top of each other.
    /// </summary>
    public bool IsSignedOut => !hasAccount;

    /// <summary>A request is in the air. Drives the hairline bar, never a modal.</summary>
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            SetField(ref isBusy, value);
            OnPropertyChanged(nameof(ShowLoadingPanel));
            OnPropertyChanged(nameof(ShowEmptyPanel));
        }
    }

    public string ErrorText
    {
        get => errorText;
        private set => SetField(ref errorText, value);
    }

    /// <summary>How many files the server says are in this folder.</summary>
    public string CountText
    {
        get => countText;
        private set => SetField(ref countText, value);
    }

    public bool HasFiles => Files.Count > 0;

    /// <summary>There are folders on the shelf at this level.</summary>
    public bool HasFolders => VisibleFolders.Count > 0;

    /// <summary>Anything at all to draw in the scrolled area: folders, files or both.</summary>
    public bool HasContent => HasFolders || HasFiles;

    /// <summary>
    /// This folder holds folders but no files of its own.
    /// </summary>
    /// <remarks>
    /// Said as a line under the shelf rather than with the full empty-state card, which
    /// would claim the folder is empty while its own subfolders are on screen above it.
    /// </remarks>
    public bool ShowNoFilesNote =>
        hasAccount && loadedOnce && !isBusy && !hasError && HasFolders && !HasFiles;

    /// <summary>What the grid is showing: a folder, a class of file, the trash, or everything.</summary>
    public string CurrentFolderName
    {
        get
        {
            if (trashed)
            {
                return Strings.MySpace_Trash;
            }

            if (currentFolderId != null)
            {
                return CurrentFolder?.Name ?? Strings.MySpace_AllFiles;
            }

            return CurrentView?.Name ?? Strings.MySpace_AllFiles;
        }
    }

    /// <summary>The glyph beside that name.</summary>
    public string CurrentFolderIcon
    {
        get
        {
            if (currentFolderId != null)
            {
                return CurrentFolder?.Kind == CloudFolderKind.Unfiled
                    ? LucideIcons.inbox
                    : LucideIcons.folder_open;
            }

            return CurrentView?.Icon ?? LucideIcons.layout_grid;
        }
    }

    /// <summary>The library row the grid is showing, or null while a folder is open.</summary>
    private LibraryNavViewModel? CurrentView
    {
        get
        {
            if (currentFolderId != null)
            {
                return null;
            }

            foreach (LibraryNavViewModel view in views)
            {
                if (view.Kind == currentKind && view.IsTrash == trashed)
                {
                    return view;
                }
            }

            return null;
        }
    }

    /// <summary>The grid is listing the trash. Menus and uploads change shape when it is.</summary>
    public bool IsTrash => trashed;

    /// <summary>
    /// Files may be sent from here.
    /// </summary>
    /// <remarks>
    /// False in the trash, where "upload here" would have to mean putting a new file
    /// straight into the bin - which the server has no way to do and nobody would want.
    /// </remarks>
    public bool CanUpload => hasAccount && !trashed;

    /// <summary>There is somewhere above here to go.</summary>
    public bool CanGoUp => currentFolderId != null;

    /// <summary>The whole library is being shown, unfiltered. Marks the side list's first row.</summary>
    public bool IsAtRoot => currentFolderId == null && currentKind == null && !trashed;

    /// <summary>
    /// The path is worth drawing.
    /// </summary>
    /// <remarks>
    /// At the top of the library there is no path - one segment naming the library is not
    /// a trail, it is a title, and the header already says it.
    /// </remarks>
    public bool ShowCrumbs => Crumbs.Count > 1;

    /// <summary>
    /// The other face of <see cref="ShowCrumbs"/>: at the top of the library the header
    /// carries a title instead of a trail.
    /// </summary>
    /// <remarks>
    /// A property rather than a <c>!</c> in the binding, for the same reason
    /// <see cref="IsSignedOut"/> is one: the window is not compile-checked, and a binding
    /// that silently fails would draw a title and a breadcrumb on top of each other.
    /// </remarks>
    public bool ShowFolderTitle => Crumbs.Count <= 1;

    /// <summary>
    /// The folder a drop or an upload lands in when it is aimed at "here".
    /// </summary>
    /// <remarks>
    /// Null at the top of the library on purpose: that is "expressed no preference", which
    /// the server answers with the ACCOUNT'S default folder. Sending the root sentinel
    /// there instead would quietly override a default the owner set in the panel.
    /// </remarks>
    public string? UploadFolderId => currentFolderId;

    /// <summary>How many file cards are selected. Zero most of the time.</summary>
    public int SelectedFileCount
    {
        get => selectedFileCount;
        private set
        {
            SetField(ref selectedFileCount, value);
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasMultiSelection));
        }
    }

    public bool HasSelection => selectedFileCount > 0;

    /// <summary>More than one card is selected, so the menu speaks in plurals.</summary>
    public bool HasMultiSelection => selectedFileCount > 1;

    private CloudFolderViewModel? CurrentFolder => currentFolderId == null ? null : Find(currentFolderId);

    /// <summary>
    /// The first page has not arrived yet and there is nothing to show meanwhile.
    /// </summary>
    /// <remarks>
    /// Only for an empty grid. Once there are cards, waiting is said with the hairline
    /// bar and the footer note instead - replacing a screenful of files with a spinner
    /// because a further page is loading would be a worse answer than the files.
    /// </remarks>
    public bool ShowLoadingPanel => hasAccount && isBusy && !hasError && !HasContent;

    public bool ShowEmptyPanel => hasAccount && loadedOnce && !isBusy && !hasError && !HasContent;

    /// <summary>Nothing loaded and the reason why. Carries the Retry.</summary>
    public bool ShowErrorPanel => hasAccount && hasError && !HasContent;

    /// <summary>
    /// The same failure when there is already something on screen.
    /// </summary>
    /// <remarks>
    /// A page that failed halfway must not throw away the pages that worked, and it must
    /// not silently stop either - the grid would simply appear to end, which reads as
    /// "that is all of them".
    /// </remarks>
    public bool ShowErrorFooter => hasAccount && hasError && HasContent;

    /// <summary>
    /// Bring the tab in line with whoever is signed in now.
    /// </summary>
    /// <remarks>
    /// Called when the window is shown and whenever the menus are rebuilt, which is what
    /// signing in or out does. A different account is always reloaded: the files on
    /// screen belong to the previous one, and leaving them under a new name would be a
    /// lie rather than a delay.
    /// </remarks>
    public void RefreshAccount()
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignedOut();
            return;
        }

        HasAccount = true;

        if (IdentityOf(account) != identity)
        {
            // A different account: the folder that was open belongs to the previous one,
            // and its id would name nothing here. Start at the top rather than resolving
            // a stranger's path.
            SetCurrentFolder(null);
            Reload();
            return;
        }

        if (!loadedOnce && !isBusy)
        {
            Reload();
        }
    }

    /// <summary>Throw away what is on screen and ask again, folders included.</summary>
    public void Reload()
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignedOut();
            return;
        }

        // Once per run, and before the first request rather than after it: asking for the
        // top of the library and then asking again for the saved folder would be two
        // round trips and a flash of the wrong screen on every launch. The id is taken on
        // trust here and checked against the real folder list when it arrives, which is
        // the only moment it CAN be checked.
        if (!restoredLastFolder)
        {
            restoredLastFolder = true;

            string saved = Program.Settings?.ModlightLastFolderId ?? "";
            SetCurrentFolder(saved.Length > 0 ? saved : null);
        }

        // Read here for the reason the folder is: once per run, before the first request,
        // rather than in a field initialiser that would run before the settings file has
        // been loaded. Clamped on the way in, so a size written by an older build - or
        // typed into the file by hand - can only ever be one this build has a button for.
        if (!restoredPageSize)
        {
            restoredPageSize = true;
            pageSize = LibraryPager.ClampPageSize(
                Program.Settings?.ModlightPageSize ?? LibraryPager.DefaultPageSize);
            Pager.Update(1, pageSize, 0);
        }

        StartListing(account, reloadFolders: true);
    }

    /// <summary>Step into a folder: its subfolders on the shelf, its files below them.</summary>
    public void OpenFolder(CloudFolderViewModel folder) => NavigateTo(folder.Id);

    /// <summary>
    /// Show one row of the library panel: everything, one class of file, or the trash.
    /// </summary>
    /// <remarks>
    /// Leaves whatever folder was open, which is the panel's behaviour and not an
    /// oversight: its sidebar links carry only the view, the sort and the page size
    /// across, and drop the folder. "Images" means every image on the account.
    /// </remarks>
    public void SelectView(LibraryNavViewModel view)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignedOut();
            return;
        }

        // Re-picking the row already shown is a no-op only when there is something to
        // show: after a failure it has to be another attempt, or the panel would be a set
        // of dead rows on exactly the screen that says "try again".
        if (currentFolderId == null && currentKind == view.Kind && trashed == view.IsTrash &&
            loadedOnce && !hasError)
        {
            return;
        }

        currentKind = view.Kind;
        trashed = view.IsTrash;

        SetCurrentFolder(null);
        RememberFolder();
        SelectFolderTile(null);
        RebuildShelf();
        StartListing(account, reloadFolders: false);
    }

    /// <summary>Go back to the folder this one sits in, or to the top of the library.</summary>
    public void GoUp()
    {
        if (currentFolderId == null)
        {
            return;
        }

        NavigateTo(CurrentFolder?.ParentId);
    }

    /// <summary>
    /// Show one folder, or the whole library when <paramref name="folderId"/> is null.
    /// </summary>
    /// <remarks>
    /// An id that no longer names anything falls back to the top of the library rather
    /// than showing an empty shelf under a name that is gone - a folder deleted in the
    /// panel while this window sat in the tray is an ordinary Tuesday, not an error.
    /// </remarks>
    public void NavigateTo(string? folderId)
    {
        if (folderId != null && Find(folderId) == null)
        {
            folderId = null;
        }

        // Re-entering the folder already shown is a no-op only when there is something to
        // show: after a failure it has to be another attempt, or the breadcrumb would be a
        // set of dead links on exactly the screen that says "try again".
        if (currentFolderId == folderId && loadedOnce && !hasError)
        {
            return;
        }

        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignedOut();
            return;
        }

        // A folder is a place and a class of file is a filter over every place, so
        // entering one clears the other. The panel does the same by omission - its folder
        // links carry no `kind` - and having both at once would mean a folder that appears
        // to be missing most of its contents.
        currentKind = null;
        trashed = false;

        SetCurrentFolder(folderId);
        RememberFolder();
        SelectFolderTile(null);
        RebuildShelf();
        StartListing(account, reloadFolders: false);
    }

    /// <summary>
    /// Move to a folder, and tell the rest of the app that uploads go there now.
    /// </summary>
    /// <remarks>
    /// The publish is the whole point of this method existing. Aiming an upload was
    /// previously something only My space's own two gestures did - the tile's send button
    /// and "Upload here" - so a file dropped on the window, pasted from the clipboard,
    /// handed over by the tray or produced by a capture named no folder at all and went
    /// to the account default, while the breadcrumb overhead said the user was inside
    /// "CV". See <see cref="ModlightUploadTarget"/>.
    /// </remarks>
    private void SetCurrentFolder(string? folderId)
    {
        currentFolderId = folderId;
        ModlightUploadTarget.Set(folderId);
    }

    /// <summary>Where to come back to next time the window opens.</summary>
    private void RememberFolder()
    {
        if (Program.Settings != null)
        {
            Program.Settings.ModlightLastFolderId = currentFolderId ?? "";
        }
    }

    /// <summary>One click on a tile: mark it, do not move.</summary>
    public void SelectFolderTile(CloudFolderViewModel? folder)
    {
        if (ReferenceEquals(selectedFolder, folder))
        {
            return;
        }

        if (selectedFolder != null)
        {
            selectedFolder.IsSelected = false;
        }

        selectedFolder = folder;

        if (folder != null)
        {
            folder.IsSelected = true;

            // The two selections are exclusive the way they are in any file manager:
            // clicking a folder is not a way to add to a set of files.
            ClearFileSelection();
        }
    }

    /// <summary>The folder one click has marked, if any.</summary>
    public CloudFolderViewModel? SelectedFolder => selectedFolder;

    /// <summary>
    /// A plain click on a file card: this one, and nothing else.
    /// </summary>
    /// <remarks>
    /// The three selection gestures follow the platform rather than the web panel, which
    /// put multi-select on the RIGHT button (Round 4). On Windows the right button means
    /// "what can I do with this", and a desktop app that swallows the context menu reads
    /// as broken. So: click replaces, Ctrl+click toggles, Shift+click extends.
    /// </remarks>
    public void SelectFile(CloudFileViewModel file)
    {
        SelectFolderTile(null);
        ClearFileSelection();

        file.IsSelected = true;
        selectionAnchor = file;
        SelectedFileCount = 1;
    }

    /// <summary>Ctrl+click: add this card to the selection, or take it out again.</summary>
    public void ToggleFile(CloudFileViewModel file)
    {
        SelectFolderTile(null);

        file.IsSelected = !file.IsSelected;

        // The anchor follows the last card TOUCHED, including one just removed, because
        // that is where the user's attention is and where a following Shift+click means
        // to measure from.
        selectionAnchor = file;
        SelectedFileCount = CountSelected();
    }

    /// <summary>
    /// Shift+click: everything between the anchor and this card.
    /// </summary>
    /// <remarks>
    /// Replaces the selection rather than adding to it, which is what Explorer does and
    /// what makes a range correctable by simply Shift+clicking somewhere else.
    /// </remarks>
    public void SelectFileRange(CloudFileViewModel file)
    {
        int to = Files.IndexOf(file);

        if (to < 0)
        {
            return;
        }

        int from = selectionAnchor == null ? to : Files.IndexOf(selectionAnchor);

        if (from < 0)
        {
            from = to;
        }

        SelectFolderTile(null);

        int first = Math.Min(from, to);
        int last = Math.Max(from, to);

        for (int index = 0; index < Files.Count; index++)
        {
            Files[index].IsSelected = index >= first && index <= last;
        }

        SelectedFileCount = last - first + 1;
    }

    /// <summary>Ctrl+A: every file on screen. Not every file in the folder - the rest are not loaded.</summary>
    public void SelectAllFiles()
    {
        if (Files.Count == 0)
        {
            return;
        }

        SelectFolderTile(null);

        foreach (CloudFileViewModel file in Files)
        {
            file.IsSelected = true;
        }

        selectionAnchor = Files[0];
        SelectedFileCount = Files.Count;
    }

    /// <summary>Esc: nothing is selected. Never changes folder.</summary>
    public bool ClearSelection()
    {
        bool had = selectedFileCount > 0 || selectedFolder != null;

        SelectFolderTile(null);
        ClearFileSelection();

        return had;
    }

    /// <summary>
    /// The selected cards, in the order they are on screen.
    /// </summary>
    /// <remarks>
    /// A fresh list rather than a live collection: every caller is about to act on it -
    /// copy, delete - and acting on a live view of a collection that the action itself
    /// mutates is how a bulk delete ends up skipping every other file.
    /// </remarks>
    public List<CloudFileViewModel> SelectedFiles()
    {
        List<CloudFileViewModel> selected = new();

        foreach (CloudFileViewModel file in Files)
        {
            if (file.IsSelected)
            {
                selected.Add(file);
            }
        }

        return selected;
    }

    private void ClearFileSelection()
    {
        if (selectedFileCount == 0)
        {
            selectionAnchor = null;
            return;
        }

        foreach (CloudFileViewModel file in Files)
        {
            file.IsSelected = false;
        }

        selectionAnchor = null;
        SelectedFileCount = 0;
    }

    private int CountSelected()
    {
        int count = 0;

        foreach (CloudFileViewModel file in Files)
        {
            if (file.IsSelected)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Show another page of this listing.
    /// </summary>
    /// <remarks>
    /// A page turn goes through <see cref="StartListing"/>, which is to say it is handled
    /// exactly like opening another folder: the cards are thrown away, the listing's
    /// cancellation source is replaced, and everything still in the air for the page being
    /// left is abandoned by the machinery that already existed for folder changes. Adding
    /// a second, gentler way to change what the grid is showing would mean a preview
    /// arriving for a card that is no longer on screen, from a page it is not on.
    /// </remarks>
    public void GoToPage(int target)
    {
        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignedOut();
            return;
        }

        StartListing(account, reloadFolders: false, target);
    }

    /// <summary>
    /// Change how many files a page holds, and stay roughly where you were.
    /// </summary>
    /// <remarks>
    /// Where it lands is the panel's rule, <see cref="LibraryPager.Anchor"/>: anchor on
    /// the first file currently on screen, so going from 30 to 120 while looking at file
    /// 3,001 leaves that file on screen. Jumping to page one would be simpler and would
    /// throw away where somebody was.
    ///
    /// Saved before the request rather than after it, deliberately: this is a preference,
    /// not a result, and it should survive a listing that fails.
    /// </remarks>
    public void SetPageSize(int size)
    {
        int wanted = LibraryPager.ClampPageSize(size);

        if (wanted == pageSize)
        {
            return;
        }

        int anchor = LibraryPager.Anchor(LibraryPager.First(page, pageSize), wanted);

        pageSize = wanted;
        RememberPageSize();

        ModlightAccount? account = ModlightSignIn.Current;

        if (account == null)
        {
            SignedOut();
            return;
        }

        StartListing(account, reloadFolders: false, anchor);
    }

    private void RememberPageSize()
    {
        if (Program.Settings != null)
        {
            Program.Settings.ModlightPageSize = pageSize;
        }
    }

    /// <summary>
    /// Say which cards the user can actually see, so their pictures are the last to go.
    /// </summary>
    /// <remarks>
    /// Called from the scroll handler with an ESTIMATE of the index range, worked out from
    /// how far down the content the view is rather than by asking the cards where they
    /// are. That is exact enough because every card is the same size: the only thing the
    /// estimate does not account for is the strip of padding and the footer note inside
    /// the scrolled area, which is eighty pixels against an extent of tens of thousands.
    /// <see cref="VisibleBandMargin"/> is a good deal wider than the error.
    ///
    /// Asking the cards instead would mean either virtualising the panel - which Avalonia
    /// cannot do for a wrapping layout, there is no VirtualizingWrapPanel - or attaching a
    /// viewport listener to every one of several thousand realised controls, which is the
    /// cost this is here to avoid.
    /// </remarks>
    public void SetVisibleRange(int first, int last)
    {
        if (Files.Count == 0)
        {
            return;
        }

        int visibleFirst = Math.Clamp(first, 0, Files.Count - 1);
        int visibleLast = Math.Clamp(last, visibleFirst, Files.Count - 1);

        visibleBuffer.Clear();

        for (int index = visibleFirst; index <= visibleLast; index++)
        {
            visibleBuffer.Add(Files[index]);
        }

        pictures.Pin(visibleBuffer);

        int bandFirst = Math.Max(0, visibleFirst - VisibleBandMargin);
        int bandLast = Math.Min(Files.Count - 1, visibleLast + VisibleBandMargin);

        // Outwards from the middle of the band, so that when the ceiling does bite it
        // takes the far edge of the band rather than what is under the pointer.
        for (int index = bandFirst; index <= bandLast; index++)
        {
            pictures.Touch(Files[index]);
        }

        // The want-set, nearest first, and that ORDER is the point: the queue serves the
        // front of it, so the tiles the scroll came to rest on are fetched before the ones
        // it went past. Anything outside the band is dropped by the same call - a card
        // forty places off screen was worth asking for while it was near, and is not any
        // more, and paying for it would be paying with the tiles that are.
        wantBuffer.Clear();
        VisibleBand.Order(Files.Count, visibleFirst, visibleLast, VisibleBandMargin,
            index => Consider(Files[index]));
        thumbnails.Want(wantBuffer);
    }

    /// <summary>
    /// Add a card to the want-set, if it is missing a picture it could have.
    /// </summary>
    /// <remarks>
    /// Almost always a decode rather than a download when it does get fetched:
    /// <see cref="CloudThumbnailCache"/> keeps four hundred sets of encoded bytes, which
    /// is wider than this band, so a card whose picture the budget took back usually gets
    /// it again without a round trip.
    /// </remarks>
    private void Consider(CloudFileViewModel file)
    {
        if (file.Thumbnail == null && file.ThumbnailUrl.Length > 0)
        {
            wantBuffer.Add(file);
        }
    }

    /// <summary>
    /// The pointer has come to rest somewhere on a card.
    /// </summary>
    /// <remarks>
    /// Nothing is asked for here. It only tells <see cref="CloudMotionPlayer"/> where the
    /// pointer is; that decides whether the pause is long enough to be worth a request,
    /// and refuses outright for the great majority of rows, which the server has already
    /// said have no moving preview to give.
    ///
    /// Signed out or between listings, this does nothing rather than falling back to some
    /// other account's credentials - the same guard <see cref="SetVisibleRange"/> uses.
    /// </remarks>
    public void HoverFile(CloudFileViewModel file)
    {
        ModlightAccount? account = ModlightSignIn.Current;
        CancellationTokenSource? source = listing;

        if (account == null || source == null)
        {
            return;
        }

        motion.Enter(file, account.Host, account.Token, source.Token);
    }

    /// <summary>The pointer has left a card, so it goes back to its still.</summary>
    public void UnhoverFile(CloudFileViewModel file) => motion.Leave(file);

    /// <summary>
    /// The name of a folder, for saying out loud. Empty when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Null - "the account's own default folder" - deliberately answers empty rather than
    /// inventing a name for it. This client is never told which folder that is, and naming
    /// the wrong one is worse than naming none.
    /// </remarks>
    public string FolderNameFor(string? folderId) =>
        folderId == null ? "" : Find(folderId)?.Name ?? "";

    /// <summary>
    /// An upload has landed. Show it, count it, and point at where it went.
    /// </summary>
    /// <remarks>
    /// The tab used to sit there stale until somebody pressed Refresh, which is what the
    /// owner called infuriating and was right to. Three things are stale after an upload
    /// and they are fixed three different ways, deliberately:
    ///
    /// The GRID gets the new row from the server, merged into what is already on screen
    /// rather than reloaded. A reload would empty the grid, throw away every decoded
    /// thumbnail, scroll back to the top and lose the selection - on every single upload.
    ///
    /// The TILE of the folder that received the file has its count corrected locally.
    /// See <see cref="CloudFolderViewModel.AdjustDirect"/> for why that is not a fetch.
    ///
    /// The FLASH answers "where did it go", and it is the reason the count is corrected
    /// rather than re-fetched: re-fetching replaces the tile in the middle of its flash.
    /// </remarks>
    public void NoteUploadLanded(string? folderId, long bytes)
    {
        if (!hasAccount)
        {
            return;
        }

        AdjustFolderTally(folderId, 1, bytes);
        Flash(folderId);
        MergeNewest();
    }

    /// <summary>One file has gone from the library, by whatever route.</summary>
    public void NoteFileRemoved(CloudFileViewModel file)
    {
        // A row already in the trash was never counted against a folder's tally, so
        // deleting it for good must not take one off - the folder would end up claiming
        // fewer files than it holds until the next reload.
        if (!file.IsTrashed)
        {
            AdjustFolderTally(file.FolderId, -1, -file.Size);
        }

        Remove(file);
    }

    /// <summary>One file has come back out of the trash.</summary>
    /// <remarks>
    /// The mirror of <see cref="NoteFileRemoved"/>, and it exists for the same reason: a
    /// trashed row is not counted against its folder, so putting it back has to put the
    /// count back too. Without this the tile stayed one file and one file's bytes short
    /// until somebody happened to reload, which on a shelf of folder tiles reads as the
    /// restore not having worked.
    ///
    /// Off the screen afterwards, because the list this is called from is the trash and a
    /// restored file is no longer in it.
    /// </remarks>
    public void NoteFileRestored(CloudFileViewModel file)
    {
        if (file.IsTrashed)
        {
            AdjustFolderTally(file.FolderId, 1, file.Size);
        }

        Remove(file);
    }

    /// <summary>
    /// Redraw the cards the server has just written, without disturbing anything else.
    /// </summary>
    /// <remarks>
    /// Every write endpoint answers with the affected rows in the listing's own shape, and
    /// this is what that is for. The alternative - reloading the folder after each edit -
    /// empties the grid, throws away every decoded thumbnail, loses the selection and
    /// scrolls back to the top, which is a heavy price for having ticked "Make private".
    ///
    /// A row the grid is not showing is simply skipped: a selection can go stale while a
    /// menu is open, and a client that treated that as a failure would report an edit as
    /// broken when it had in fact landed.
    /// </remarks>
    public void ApplyRows(IReadOnlyList<ModlightUpload>? rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return;
        }

        Dictionary<string, CloudFileViewModel> byId = new(Files.Count, StringComparer.Ordinal);

        foreach (CloudFileViewModel file in Files)
        {
            byId[file.Id] = file;
        }

        foreach (ModlightUpload row in rows)
        {
            if (!string.IsNullOrEmpty(row.Id) && byId.TryGetValue(row.Id!, out CloudFileViewModel? file))
            {
                file.Apply(row);
            }
        }
    }

    /// <summary>
    /// Files have changed folder: correct the tiles, redraw the cards, drop what left.
    /// </summary>
    /// <remarks>
    /// The order inside is the whole of the method's difficulty. The tallies have to be
    /// worked out from the folder the cards still SAY they are in, so they are done
    /// before the rows are applied; the cards that no longer belong on screen can only be
    /// identified afterwards, from where they now are.
    ///
    /// A card is dropped only when the grid is showing one particular folder. Under "All
    /// files" or a class filter the file is still in view - it has merely moved - and
    /// making it vanish would look like a delete.
    /// </remarks>
    public void NoteFilesMoved(IReadOnlyList<CloudFileViewModel> files, string? targetFolderId,
        IReadOnlyList<ModlightUpload>? rows)
    {
        foreach (CloudFileViewModel file in files)
        {
            if (file.FolderId == targetFolderId)
            {
                continue;
            }

            AdjustFolderTally(file.FolderId, -1, -file.Size);
            AdjustFolderTally(targetFolderId, 1, file.Size);
        }

        ApplyRows(rows);

        if (currentFolderId != null)
        {
            List<CloudFileViewModel> leaving = new();

            foreach (CloudFileViewModel file in files)
            {
                if (file.FolderId != currentFolderId)
                {
                    leaving.Add(file);
                }
            }

            foreach (CloudFileViewModel file in leaving)
            {
                Remove(file);
            }
        }

        Flash(targetFolderId);
    }

    /// <summary>
    /// Ask the server for the folder list again, keeping the files on screen.
    /// </summary>
    /// <remarks>
    /// For coming back to the window, where a folder made or deleted in the panel in the
    /// meantime is exactly the sort of thing to have happened. Not called after every
    /// upload: this replaces every tile on the shelf, and an upload creates no folders.
    ///
    /// Does nothing until the first listing has arrived, which is what stops the window's
    /// first appearance asking for the folder list twice: showing the window signs the tab
    /// in, that starts a listing which fetches the folders itself, and this would then
    /// fetch them again a millisecond later.
    /// </remarks>
    public void RefreshFolders()
    {
        ModlightAccount? account = ModlightSignIn.Current;
        CancellationTokenSource? source = listing;

        if (account == null || source == null || !hasAccount || !loadedOnce)
        {
            return;
        }

        _ = LoadFoldersAsync(account.Host, account.Token, source);
    }

    /// <summary>Correct one folder's own tally, and every folder above it.</summary>
    private void AdjustFolderTally(string? folderId, int files, long bytes)
    {
        if (folderId == null)
        {
            return;
        }

        CloudFolderViewModel? folder = Find(folderId);

        if (folder == null)
        {
            return;
        }

        folder.AdjustDirect(files, bytes);

        // The loose-files shelf is not in allFolders and has nothing above it, so it is
        // rolled up on its own - passing it through the main list would give it a Depth
        // and a place in a tree it is not part of.
        CloudFolderViewModel.RollUp(folder.Kind == CloudFolderKind.Unfiled
            ? new List<CloudFolderViewModel> { folder }
            : allFolders);
    }

    /// <summary>Make the tile of the folder that took the file say so, briefly.</summary>
    private void Flash(string? folderId)
    {
        int version = ++flashVersion;

        foreach (CloudFolderViewModel folder in VisibleFolders)
        {
            folder.JustReceived = folder.Id == folderId;
        }

        DispatcherTimer.RunOnce(() =>
        {
            // A later upload has its own flash running; letting this timer clear it would
            // cut the second one short.
            if (version != flashVersion)
            {
                return;
            }

            foreach (CloudFolderViewModel folder in VisibleFolders)
            {
                folder.JustReceived = false;
            }
        }, FlashDuration);
    }

    /// <summary>
    /// Ask for the first page again and put whatever is new at the top of the grid.
    /// </summary>
    /// <remarks>
    /// Always the folder that is OPEN, never the folder the upload was aimed at: if the
    /// file did not land here the server simply does not return it, which is the correct
    /// answer and costs one small request to get. Deciding locally whether it "belongs"
    /// would mean this client re-implementing the server's idea of a default folder.
    ///
    /// Coalesced rather than queued: twenty files dropped at once finish twenty times, and
    /// twenty listings of one folder would be nineteen wasted round trips. A request made
    /// while one is in flight is noted, and one further pass runs when the first returns.
    /// </remarks>
    private void MergeNewest()
    {
        ModlightAccount? account = ModlightSignIn.Current;
        CancellationTokenSource? source = listing;

        // Page one only. On any other page the newest file is not on screen and does not
        // belong there: merging it in would put a row at the top of page 4 that the server
        // says is on page 1, and the two would then disagree about what page 4 is. The
        // count is refreshed by the next page turn, which is one click away.
        if (account == null || source == null || !loadedOnce || trashed || page != 1)
        {
            return;
        }

        if (merging)
        {
            mergeQueued = true;
            return;
        }

        _ = MergeNewestAsync(account.Host, account.Token, source);
    }

    private async Task MergeNewestAsync(string host, string token, CancellationTokenSource source)
    {
        // Taken before the first await, for the reason LoadFoldersAsync spells out: Cancel
        // disposes the source, and reading Token after that throws on a worker thread.
        CancellationToken cancellationToken = source.Token;

        merging = true;

        try
        {
            do
            {
                mergeQueued = false;

                ModlightUploadPage result = await ModlightLibrary.GetUploadsAsync(
                    host, token, 1, pageSize, currentFolderId, null, currentKind, trashed, cancellationToken);

                if (!Owns(source))
                {
                    return;
                }

                Merge(result, host, token, cancellationToken);
            }
            while (mergeQueued && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // Deliberately silent on screen. This runs behind an upload that has already
            // succeeded, and turning the grid's error panel on over it would report a
            // failure that did not happen. The Refresh button is still right there.
            DebugHelper.WriteException(exception);
        }
        finally
        {
            merging = false;
        }
    }

    /// <summary>
    /// Put the rows the grid has not seen at the front of it.
    /// </summary>
    /// <remarks>
    /// The first page arrives newest first, so anything new is at the head of it and the
    /// first row already on screen is where the old listing starts. Stopping there rather
    /// than walking the whole page keeps this proportional to what actually arrived and,
    /// more to the point, stops it reinserting a file that paging has already placed
    /// further down the grid.
    /// </remarks>
    private void Merge(ModlightUploadPage result, string host, string token, CancellationToken cancellationToken)
    {
        HashSet<string> known = new(Files.Count, StringComparer.Ordinal);

        foreach (CloudFileViewModel existing in Files)
        {
            known.Add(existing.Id);
        }

        List<CloudFileViewModel> arrived = new();
        int at = 0;

        foreach (ModlightUpload upload in result.Items ?? new List<ModlightUpload>())
        {
            if (string.IsNullOrEmpty(upload.Id) || known.Contains(upload.Id!))
            {
                break;
            }

            CloudFileViewModel file = new(upload);
            Files.Insert(at++, file);
            arrived.Add(file);
        }

        if (arrived.Count == 0)
        {
            return;
        }

        total = Math.Max(result.Total, Files.Count);

        // A page stays a page. Two files landing pushes two off the bottom, which is
        // exactly where the server would have put them, and without this the first page
        // would quietly grow every time somebody uploaded - the unbounded grid this
        // replaced, arrived at by a different road.
        while (Files.Count > pageSize)
        {
            CloudFileViewModel dropped = Files[Files.Count - 1];

            Files.RemoveAt(Files.Count - 1);
            pictures.Forget(dropped);
            motion.Forget(dropped);

            if (dropped.IsSelected)
            {
                SelectedFileCount = Math.Max(0, selectedFileCount - 1);
            }

            if (ReferenceEquals(selectionAnchor, dropped))
            {
                selectionAnchor = null;
            }

            dropped.Dispose();
        }

        Pager.Update(page, pageSize, total);
        UpdateCount();
        NotifyFileStates();
        LoadThumbnails(arrived);
    }

    /// <summary>Take one file off the grid after the server has agreed it is gone.</summary>
    public void Remove(CloudFileViewModel file)
    {
        if (!Files.Remove(file))
        {
            return;
        }

        pictures.Forget(file);
        motion.Forget(file);

        if (file.IsSelected)
        {
            SelectedFileCount = Math.Max(0, selectedFileCount - 1);
        }

        if (ReferenceEquals(selectionAnchor, file))
        {
            selectionAnchor = null;
        }

        file.Dispose();

        if (total > 0)
        {
            total--;
        }

        Pager.Update(page, pageSize, total);
        UpdateCount();
        NotifyFileStates();
    }

    private void SignedOut()
    {
        Cancel();
        ClearFiles();

        allFolders.Clear();
        Folders.Clear();
        unfiled = null;
        selectedFolder = null;
        currentKind = null;
        trashed = false;
        SetCurrentFolder(null);
        RebuildShelf();

        identity = "";
        loadedOnce = false;
        total = 0;
        page = 1;
        Pager.Reset();
        CountText = "";
        SetError(false, "");
        IsBusy = false;
        HasAccount = false;
    }

    /// <summary>
    /// Throw away what is on screen and ask for one page of the current listing.
    /// </summary>
    /// <remarks>
    /// Every route that changes WHAT the grid is showing comes through here - another
    /// folder, another view, another page, another page size - so there is one place that
    /// replaces the cancellation source, and one moment at which everything in flight for
    /// the old contents stops being wanted. A page turn that took a shortcut past this
    /// would be the way a preview arrives for a card from the page before.
    /// </remarks>
    private void StartListing(ModlightAccount account, bool reloadFolders, int target = 1)
    {
        Cancel();
        ClearFiles();

        identity = IdentityOf(account);
        total = 0;
        page = Math.Max(1, target);
        loadedOnce = false;

        CountText = "";
        SetError(false, "");

        CancellationTokenSource source = new();
        listing = source;

        if (reloadFolders)
        {
            _ = LoadFoldersAsync(account.Host, account.Token, source);
        }

        _ = LoadPageAsync(account.Host, account.Token, page, source);
    }

    private void Cancel()
    {
        CancellationTokenSource? source = listing;
        listing = null;

        if (source != null)
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private void ClearFiles()
    {
        // Before the cards go: a frame timer still ticking against one of them would be
        // writing pictures onto an object nothing is showing, and holding their pixels for
        // as long as it ran.
        motion.Reset();

        // Nothing on the way out is worth fetching. What is already in flight carries the
        // listing's token, which Cancel has just tripped; this is about the hundreds that
        // were merely waiting.
        thumbnails.Clear();

        // Emptied first: the budget's job is to dispose pictures on cards that are still
        // in the listing, and every one of these is about to be disposed by name.
        pictures.Clear();

        foreach (CloudFileViewModel file in Files)
        {
            file.Dispose();
        }

        Files.Clear();
        visibleBuffer.Clear();
        wantBuffer.Clear();
        selectionAnchor = null;
        SelectedFileCount = 0;
        NotifyFileStates();
    }

    private async Task LoadFoldersAsync(string host, string token, CancellationTokenSource source)
    {
        // Taken off the source before the first await, and never read from it again.
        // Cancel() disposes the source, and CancellationTokenSource.Token throws once it
        // has been - on a worker thread, inside a lambda, where the exception would be
        // swallowed as "the folder list failed" and nobody would ever find it.
        CancellationToken cancellationToken = source.Token;

        try
        {
            ModlightFolderList result = await ModlightLibrary.GetFoldersAsync(host, token, cancellationToken);

            if (!Owns(source))
            {
                return;
            }

            rootValue = string.IsNullOrEmpty(result.RootValue) ? ModlightLibrary.RootFolder : result.RootValue!;

            allFolders.Clear();

            foreach (ModlightFolder folder in result.Folders ?? new List<ModlightFolder>())
            {
                if (!string.IsNullOrEmpty(folder.Id))
                {
                    allFolders.Add(new CloudFolderViewModel(folder));
                }
            }

            // Direct counts in, subtree counts out. Once per folder list, never per tile.
            CloudFolderViewModel.RollUp(allFolders);

            // Only when the server reported it AND there is anything loose. A shelf saying
            // "no loose files" is a row of furniture answering a question nobody asked; on
            // an older deployment that does not send the figure at all, the absence is
            // honest and inventing a zero would not be.
            unfiled = result.Unfiled is { FileCount: > 0 } loose
                ? CloudFolderViewModel.CreateUnfiled(rootValue, loose.FileCount, loose.Size)
                : null;

            Folders.Clear();

            if (unfiled != null)
            {
                Folders.Add(unfiled);
            }

            foreach (CloudFolderViewModel folder in allFolders)
            {
                Folders.Add(folder);
            }

            // A folder open when the list was refreshed may be gone from it - deleted in
            // the panel, or saved from a previous run and since removed. RebuildShelf
            // sends the view back to the top when it cannot find the id, and when it does
            // that, the files already on their way belong to a folder that is not being
            // shown any more, so they have to be asked for again.
            string? before = currentFolderId;
            RebuildShelf();

            ModlightAccount? account = ModlightSignIn.Current;

            if (currentFolderId != before && account != null)
            {
                RememberFolder();

                // Cancels the listing this method was started by, which is exactly right:
                // its pages are for a folder that is no longer on screen.
                StartListing(account, reloadFolders: false);
            }
        }
        catch (OperationCanceledException)
        {
            // Another folder was picked, or the account changed. Nothing to say.
        }
        catch (Exception exception)
        {
            // A folder list that failed is not a reason to hide the files: the grid can
            // still show everything, which is what it defaults to. Logged, not shown.
            DebugHelper.WriteException(exception);
        }
    }

    /// <summary>
    /// Redraw the shelf and the path for whichever folder is open.
    /// </summary>
    /// <remarks>
    /// Called on every move and on every folder refresh, and it is the only place either
    /// collection is written. Both are derived entirely from the flat list the server
    /// sent, never from the files on screen - which is what makes an EMPTY folder appear:
    /// it exists in the panel, and a shelf built from files could not know that.
    /// </remarks>
    private void RebuildShelf()
    {
        CloudFolderViewModel? current = currentFolderId == null ? null : Find(currentFolderId);

        // A folder that vanished from the panel takes the view back to the top rather than
        // leaving it inside a name that no longer exists.
        if (currentFolderId != null && current == null)
        {
            SetCurrentFolder(null);
        }

        VisibleFolders.Clear();

        // A class filter and the trash are not places, so there is no shelf to draw for
        // them: the grid is showing every image on the account, or everything in the bin,
        // and a row of folders above that would invite a click that means nothing.
        bool showShelf = currentKind == null && !trashed;

        // The loose-files shelf sits with the top-level folders and only there: it is not
        // inside anything, so it cannot be a child of a folder.
        if (showShelf && currentFolderId == null && unfiled != null)
        {
            VisibleFolders.Add(unfiled);
        }

        // Nothing nests under the loose-files shelf, by definition.
        if (showShelf && current?.Kind != CloudFolderKind.Unfiled)
        {
            foreach (CloudFolderViewModel folder in allFolders)
            {
                if (folder.ParentId == currentFolderId)
                {
                    VisibleFolders.Add(folder);
                }
            }
        }

        foreach (CloudFolderViewModel folder in allFolders)
        {
            folder.IsCurrent = folder.Id == currentFolderId;
        }

        if (unfiled != null)
        {
            unfiled.IsCurrent = unfiled.Id == currentFolderId;
        }

        // At most one row of the panel is marked, and while a folder is open it is one of
        // the folder rows rather than one of these.
        foreach (LibraryNavViewModel view in views)
        {
            view.IsCurrent = currentFolderId == null && view.Kind == currentKind && view.IsTrash == trashed;
        }

        RebuildCrumbs(current);

        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ShowNoFilesNote));
        OnPropertyChanged(nameof(CurrentFolderName));
        OnPropertyChanged(nameof(CurrentFolderIcon));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(IsAtRoot));
        OnPropertyChanged(nameof(IsTrash));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(UploadFolderId));
        NotifyFileStates();
    }

    private void RebuildCrumbs(CloudFolderViewModel? current)
    {
        List<CloudFolderViewModel> path = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        // Upwards from where we are, then reversed: the parent chain is the only direction
        // the data can be walked in, and the guard is not tidiness - parentId is a string
        // off the network, and a cycle in it would hang the UI thread.
        for (CloudFolderViewModel? folder = current; folder != null && seen.Add(folder.Id); folder = Parent(folder))
        {
            path.Add(folder);
        }

        path.Reverse();

        Crumbs.Clear();
        Crumbs.Add(new FolderCrumbViewModel(null, Strings.MySpace_AllFiles, path.Count == 0, showSeparator: false));

        for (int index = 0; index < path.Count; index++)
        {
            Crumbs.Add(new FolderCrumbViewModel(path[index].Id, path[index].Name,
                isCurrent: index == path.Count - 1, showSeparator: true));
        }

        OnPropertyChanged(nameof(ShowCrumbs));
        OnPropertyChanged(nameof(ShowFolderTitle));
    }

    private CloudFolderViewModel? Parent(CloudFolderViewModel folder) =>
        folder.ParentId == null ? null : Find(folder.ParentId);

    /// <summary>The folder with this id, the loose-files shelf included, or null.</summary>
    private CloudFolderViewModel? Find(string id)
    {
        if (unfiled != null && unfiled.Id == id)
        {
            return unfiled;
        }

        foreach (CloudFolderViewModel folder in allFolders)
        {
            if (folder.Id == id)
            {
                return folder;
            }
        }

        return null;
    }

    private async Task LoadPageAsync(string host, string token, int target, CancellationTokenSource source)
    {
        CancellationToken cancellationToken = source.Token;
        IsBusy = true;

        try
        {
            ModlightUploadPage result = await ModlightLibrary.GetUploadsAsync(
                host, token, target, pageSize, currentFolderId, null, currentKind, trashed, cancellationToken);

            if (!Owns(source))
            {
                return;
            }

            List<CloudFileViewModel> arrived = new();
            List<ModlightUpload> items = result.Items ?? new List<ModlightUpload>();

            // Straight in. The grid used to have to skip rows it already held, because a
            // file uploaded while the listing was several pages deep was merged in at the
            // top and pushed every later row one place along - so the next page the server
            // sent began with a card already on screen. Pages cannot do that: the cards
            // are replaced on every turn, so the page on screen is exactly the page the
            // server just sent.
            foreach (ModlightUpload upload in items)
            {
                CloudFileViewModel file = new(upload);
                Files.Add(file);
                arrived.Add(file);
            }

            // Believe the server, not what was asked for: it clamps both the page and the
            // size, and a bar counting pages in a size the server refused would number
            // pages that do not exist.
            page = result.Page > 0 ? result.Page : target;
            total = Math.Max(result.Total, Files.Count);

            if (result.PerPage > 0)
            {
                pageSize = LibraryPager.ClampPageSize(result.PerPage);
            }

            Pager.Update(page, pageSize, total);

            loadedOnce = true;
            SetError(false, "");
            UpdateCount();
            NotifyFileStates();

            LoadThumbnails(arrived);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ModlightUnauthorizedException)
        {
            if (Owns(source))
            {
                // Its own message because it is the one failure with a different remedy:
                // every other error asks the person to try again, this one asks them to
                // sign in, and a Retry button here could only fail.
                loadedOnce = true;
                SetError(true, Strings.MySpace_TokenRejected);
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);

            if (Owns(source))
            {
                loadedOnce = true;
                SetError(true, string.Format(Strings.ModlightSignIn_Unreachable, host, exception.Message));
            }
        }
        finally
        {
            if (Owns(source))
            {
                IsBusy = false;
                NotifyFileStates();
            }
        }
    }

    /// <summary>
    /// A page has arrived; ask for its pictures, top of the page first.
    /// </summary>
    /// <remarks>
    /// Top-first is the right guess exactly once, before anything has been scrolled: the
    /// first scroll event replaces this order with the band around wherever the viewport
    /// actually is. Passing the whole page and letting the queue reorder it is cheaper
    /// than trying to know here which rows will be visible, which needs a layout pass that
    /// has not run yet.
    /// </remarks>
    private void LoadThumbnails(List<CloudFileViewModel> arrived)
    {
        wantBuffer.Clear();

        foreach (CloudFileViewModel file in arrived)
        {
            Consider(file);
        }

        // Then everything else on the page, behind them. For a page that has just arrived
        // this adds nothing - the two lists are the same cards, and the queue drops the
        // repeats. It matters after an upload merges rows in at the top: without it the
        // want-set would shrink to the two new cards and the queue would abandon every
        // picture the page was still waiting for.
        foreach (CloudFileViewModel file in Files)
        {
            Consider(file);
        }

        thumbnails.Want(wantBuffer);
    }

    /// <summary>
    /// Get one card its picture, against whatever listing is current NOW.
    /// </summary>
    /// <remarks>
    /// The account and the listing are read here rather than captured when the card was
    /// queued, because minutes may separate the two: a card queued on page 3 and reached
    /// after a page turn must not be fetched against the token of a listing that has been
    /// thrown away. Anything that has stopped being true simply answers "nothing to do",
    /// which frees the slot for a card that is still wanted.
    /// </remarks>
    private Task FetchThumbnailAsync(CloudFileViewModel file)
    {
        ModlightAccount? account = ModlightSignIn.Current;
        CancellationTokenSource? source = listing;

        if (account == null || source == null || file.Thumbnail != null || file.ThumbnailUrl.Length == 0)
        {
            return Task.CompletedTask;
        }

        return LoadThumbnailAsync(file, account.Host, account.Token, source.Token);
    }

    /// <summary>
    /// Get one card its picture, from the cache if the bytes are still there.
    /// </summary>
    /// <remarks>
    /// Callable twice for the same card, which is the point: a card whose picture the
    /// budget took back asks again when it scrolls into view, and the encoded bytes are
    /// usually still in <see cref="CloudThumbnailCache"/>, so the second time costs a
    /// decode rather than a round trip.
    ///
    /// There is no "already pending" flag here any more, and no semaphore either. Both
    /// belonged to a scheme where a card started its own task and then waited its turn;
    /// <see cref="thumbnails"/> owns both jobs now, which is what makes the turn
    /// re-orderable - and a flag on the card could only ever say "wait", never "stop
    /// wanting this".
    /// </remarks>
    private async Task LoadThumbnailAsync(CloudFileViewModel file, string host, string token,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[]? bytes = CloudThumbnailCache.Get(file.Code);

            if (bytes == null)
            {
                bytes = await Task.Run(() => ModlightLibrary.GetThumbnailAsync(host, token, file.ThumbnailUrl, cancellationToken));

                if (bytes != null)
                {
                    CloudThumbnailCache.Put(file.Code, bytes);
                }
            }

            if (bytes == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            byte[] encoded = bytes;
            Bitmap? bitmap = await Task.Run(() => Decode(encoded));

            if (bitmap == null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // The card owns its picture from here. Cancelled in the meantime means
                // the card is already disposed, so the bitmap goes with it rather than
                // onto a control nothing is showing.
                if (cancellationToken.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }

                file.SetThumbnail(bitmap);

                // Registered only once it is actually on a card, so the ceiling counts
                // what is being held rather than what has been decoded. Adding it can
                // evict the oldest picture in the listing, which is the whole mechanism.
                pictures.Add(file, CloudFileViewModel.ThumbnailCost(bitmap));
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // One picture that will not load is not a reason to stop the grid; the card
            // keeps the icon its kind earned it.
            DebugHelper.WriteException(exception);
        }
    }

    /// <summary>
    /// Turn encoded bytes into a picture no larger than the card can show.
    /// </summary>
    /// <remarks>
    /// The size cap is applied AFTER decoding rather than by asking the decoder for a
    /// width, and deliberately: DecodeToWidth is told a number, not a limit, and the
    /// server does not enlarge a small original, so a 96-pixel tile asked for at 288 would
    /// come back as a 288-pixel blur costing nine times the memory of the picture it was
    /// made from. Decoding first and shrinking only what is too big cannot do that. The
    /// full-size copy exists for a moment on a worker thread; it is the ones the cards
    /// KEEP that were the problem.
    /// </remarks>
    private static Bitmap? Decode(byte[] bytes)
    {
        Bitmap? decoded = null;

        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            decoded = new Bitmap(stream);

            if (decoded.PixelSize.Width <= ThumbnailDecodeWidth || decoded.PixelSize.Width <= 0)
            {
                return decoded;
            }

            int height = Math.Max(1, (int)Math.Round(
                decoded.PixelSize.Height * (double)ThumbnailDecodeWidth / decoded.PixelSize.Width));

            Bitmap scaled = decoded.CreateScaledBitmap(
                new PixelSize(ThumbnailDecodeWidth, height), BitmapInterpolationMode.HighQuality);

            decoded.Dispose();
            return scaled;
        }
        catch (Exception exception)
        {
            decoded?.Dispose();
            DebugHelper.WriteException(exception);
            return null;
        }
    }

    /// <summary>
    /// Whether this answer is still the one being waited for.
    /// </summary>
    /// <remarks>
    /// A folder change or a sign-out while the request was in the air replaces the token
    /// source. Without this check the old folder's files would land in the new one's grid.
    /// </remarks>
    private bool Owns(CancellationTokenSource source) =>
        ReferenceEquals(listing, source) && !source.IsCancellationRequested;

    private void UpdateCount() =>
        CountText = total > 0 ? string.Format(Strings.MySpace_FileCount, total) : "";

    private void SetError(bool failed, string text)
    {
        hasError = failed;
        ErrorText = text;
        OnPropertyChanged(nameof(ShowErrorPanel));
        OnPropertyChanged(nameof(ShowErrorFooter));
        OnPropertyChanged(nameof(ShowLoadingPanel));
        OnPropertyChanged(nameof(ShowEmptyPanel));
    }

    private void NotifyFileStates()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ShowNoFilesNote));
        OnPropertyChanged(nameof(ShowLoadingPanel));
        OnPropertyChanged(nameof(ShowEmptyPanel));
        OnPropertyChanged(nameof(ShowErrorPanel));
        OnPropertyChanged(nameof(ShowErrorFooter));
    }

    private static string IdentityOf(ModlightAccount account) => account.Host + "\n" + account.Token;

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
