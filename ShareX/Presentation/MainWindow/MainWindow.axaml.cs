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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;

using ShareX.ScreenCaptureLib;
using ShareX.Localization;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;
using FormsCursor = System.Windows.Forms.Cursor;
using FormsDataFormats = System.Windows.Forms.DataFormats;
using FormsDataObject = System.Windows.Forms.DataObject;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace ShareX;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    /// <summary>How many completion notices may be stacked in the corner at once.</summary>
    private const int MaxToasts = 4;

    /// <summary>How long a successful upload's notice stays up.</summary>
    private static readonly TimeSpan DoneToastLife = TimeSpan.FromSeconds(6);

    /// <summary>How long a failed one does. Longer, because it has a reason to read.</summary>
    private static readonly TimeSpan FailedToastLife = TimeSpan.FromSeconds(14);

    private readonly MainForm _host;
    private readonly MainMenuBuilder _menuBuilder;
    private readonly MainMenuBuilder _trayMenuBuilder;
    private ContextMenu? _activeContextMenu;
    private Window? _trayMenuAnchor;
    private bool _allowClose;
    private bool _disposed;
    private AvaloniaBitmap? _titleBarLogo;
    private RunawayWindowController? _runaway;
    private bool _boundsSaveQueued;
    private MainWindowTab _selectedTab = MainWindowTab.MySpace;

    /// <summary>
    /// Finished uploads, waiting their few seconds in the bottom right corner.
    /// </summary>
    /// <remarks>
    /// What replaced the "this session" strip along the bottom of My space. That strip
    /// was a permanent second copy of the library the tab already shows, kept in order to
    /// answer a question - "did that go up?" - which stops being interesting seconds after
    /// it is asked. Watching transfers and reading history is a different question and it
    /// keeps its own screen, the Upload tab.
    /// </remarks>
    internal ObservableCollection<UploadToastViewModel> Toasts { get; } = new();

    /// <summary>The account's own files: the folder row and the grid under it.</summary>
    internal MySpaceViewModel MySpace { get; } = new();

    /// <summary>
    /// The four buttons on the Capture tab, built once because their hotkeys are
    /// re-read whenever the hotkey config changes (see RefreshCaptureTiles).
    /// </summary>
    internal ObservableCollection<CaptureTileViewModel> CaptureTiles { get; } = new();

    private bool _hasHotkeyProblem;
    private string _hotkeyProblemText = string.Empty;

    /// <summary>Some configured shortcut could not be taken from the system.</summary>
    public bool HasHotkeyProblem
    {
        get => _hasHotkeyProblem;
        private set
        {
            if (_hasHotkeyProblem == value) return;
            _hasHotkeyProblem = value;
            OnPropertyChanged();
        }
    }

    public string HotkeyProblemText
    {
        get => _hotkeyProblemText;
        private set
        {
            if (_hotkeyProblemText == value) return;
            _hotkeyProblemText = value;
            OnPropertyChanged();
        }
    }
    /// <summary>Which account the uploads go to and how much room is left on it.</summary>
    internal AccountStripViewModel Account { get; } = new();

    private bool _isMaximized;

    /// <summary>
    /// Drives the middle caption button, which has to say which way it goes.
    /// </summary>
    /// <remarks>
    /// A plain mirror of WindowState, kept as its own property because the window is
    /// its own DataContext and WindowState is an AvaloniaProperty: a binding to it
    /// would go looking for the INotifyPropertyChanged this class declares, which
    /// only ever fires for the hand-written properties below it.
    /// </remarks>
    public bool IsMaximized
    {
        get => _isMaximized;
        private set
        {
            if (_isMaximized == value) return;
            _isMaximized = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaximizeTooltip));
        }
    }

    public string MaximizeTooltip =>
        IsMaximized ? Strings.MainWindow_Restore : Strings.MainWindow_Maximize;

    /// <summary>
    /// The tab on show.
    /// </summary>
    /// <remarks>
    /// One property with five derived booleans rather than five independent flags: the
    /// tabs are exclusive, and a single field is the only shape that cannot end up
    /// showing two of them or none.
    /// </remarks>
    internal MainWindowTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab == value)
            {
                return;
            }

            _selectedTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMySpaceSelected));
            OnPropertyChanged(nameof(IsUploadSelected));
            OnPropertyChanged(nameof(IsDownloadSelected));
            OnPropertyChanged(nameof(IsCreateSelected));
            OnPropertyChanged(nameof(IsToolsSelected));

            // Raises IsAdminSelected and, when the Server tab is the one being opened,
            // reads the server. See MainWindow.Admin.cs.
            NoteAdminTabChanged();
        }
    }

    public bool IsMySpaceSelected => _selectedTab == MainWindowTab.MySpace;
    public bool IsUploadSelected => _selectedTab == MainWindowTab.Upload;
    public bool IsDownloadSelected => _selectedTab == MainWindowTab.Download;
    public bool IsCreateSelected => _selectedTab == MainWindowTab.Create;
    public bool IsToolsSelected => _selectedTab == MainWindowTab.Tools;

    /// <summary>
    /// Whether uploading is allowed at all on this installation.
    /// </summary>
    /// <remarks>
    /// <c>SystemOptions.DisableUpload</c> is an administrator's lock, not a preference,
    /// and the old left panel hid its Upload entry when it was set. The Upload tab and
    /// its button are hidden for the same reason: an entry that can only fail is worse
    /// than an absent one.
    /// </remarks>
    public bool UploadsEnabled => !SystemOptions.DisableUpload;

    public MainWindow() : this(Program.MainForm)
    {
    }

    public MainWindow(MainForm host)
    {
        _host = host;
        _menuBuilder = new MainMenuBuilder(host);
        _trayMenuBuilder = new MainMenuBuilder(host, trayMenu: true);

        // BEFORE InitializeComponent, because the panel's visibility is a plain field
        // read straight into a binding: setting it afterwards would draw the window once
        // with the panel closed and then open it, which reads as a glitch on every launch.
        RestoreLibraryPanel();

        InitializeComponent();
        DataContext = this;
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        Title = Program.Title;
        Icon = CreateWindowIcon();
        SetUpTitleBar();

        _host.TrayIconService.RightButtonDown += OnTrayIconRightButtonDown;
        _host.TrayIconService.RightButtonUp += OnTrayIconRightButtonUp;

        // Before Account.Refresh, which is what asks the server who this is: subscribing
        // after it would let the answer arrive while nothing was listening, and the
        // Server tab would stay hidden from an administrator until the next refresh.
        HookAdminVisibility();

        BuildToolsTab();
        RestoreWindowBounds();
        RefreshCaptureTiles();
        Account.Refresh();

        MySpace.GoToCapture = () => SelectedTab = MainWindowTab.Create;
        MySpace.RefreshAccount();

        TaskManager.TaskChanged += OnTaskChanged;
        TaskManager.TaskFinished += OnTaskFinished;

        // The other way a file can arrive. An upload the queue took over ended its task
        // quietly hours ago, so TaskFinished has already been and gone by the time the
        // bytes actually leave - and the library was never told at all. Same funnel, not
        // a second mechanism: both ends call NoteUploadLanded, so there is still exactly
        // one place that decides what happens when a file lands.
        ModlightUploadQueue.Finished += OnQueuedUploadLanded;
        ThemeManager.ThemeChanged += OnThemeChanged;

        Closing += OnClosing;
        PositionChanged += (_, _) => QueueWindowBoundsSave();
        Resized += (_, _) => QueueWindowBoundsSave();
        KeyDown += OnWindowKeyDown;

        Dispatcher.UIThread.Post(WarmUpTrayMenu, DispatcherPriority.Background);

        // The uploaders config is read on its own thread at startup, so this window can
        // be built before there is an account to read - and the navigation would then
        // say "Sign in" to somebody who is signed in. Correct it once the file is
        // actually there, on a worker so the wait never lands on the UI thread, and
        // only in the case where the answer genuinely was not available yet.
        if (Program.UploadersConfig == null)
        {
            Task.Run(() =>
            {
                SettingManager.WaitUploadersConfig();
                MainWindowIntegration.RefreshMenus();
            });
        }

        // The website holds the other copy of the skin setting, and this is the
        // once-per-run moment to find out what it says. On a worker because the
        // credential may still be being read off disk. ModlightTheme decides which side
        // wins - a local choice the server never confirmed is pushed rather than
        // overwritten - and does nothing at all when nobody is signed in.
        Task.Run(async () =>
        {
            SettingManager.WaitUploadersConfig();
            await ModlightTheme.SyncAsync(CancellationToken.None);
        });
    }

    /// <summary>
    /// Fills in the parts of the hand-drawn title bar that are not markup, and hangs the
    /// easter egg off the logo.
    /// </summary>
    private void SetUpTitleBar()
    {
        _titleBarLogo = CreateTitleBarLogo();
        TitleBarLogo.Source = _titleBarLogo;
        TitleBarLogo.Cursor = new Cursor(StandardCursorType.Hand);

        _runaway = new RunawayWindowController(this, _ => UpdateTitleBarText());
        IsMaximized = WindowState == Avalonia.Controls.WindowState.Maximized;
        UpdateTitleBarText();
    }

    /// <summary>
    /// The window menu is put back here rather than in the constructor because the
    /// backend has not applied the decoration style yet at that point - measured: the
    /// bit was still missing at construction and present after the first state change.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowSystemMenu.Ensure(this);
    }

    /// <summary>
    /// The window title, except while the window is running away - then it is the way to
    /// make it stop, because that is the only thing anybody wants to know at that point.
    /// </summary>
    private void UpdateTitleBarText()
    {
        // The markup sets Title while InitializeComponent is still running, so the first
        // time this is reached there is no title bar yet to write into. SetUpTitleBar
        // calls it again once there is.
        if (TitleBarText is null)
        {
            return;
        }

        TitleBarText.Text = _runaway?.IsRunning == true ? Strings.MainWindow_RunawayHint : Title;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = Avalonia.Controls.WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Double-click on the bar, the way a real title bar answers it.
    /// </summary>
    /// <remarks>
    /// Belt and braces with the platform: where the window manager treats a TitleBar role
    /// as its own caption it handles the double-click itself and this never runs, because
    /// the press never arrives as client input in the first place. Where it does not, this
    /// is the only thing that maximises the window from the bar.
    /// </remarks>
    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse || IsTitleBarControl(e.Source))
        {
            return;
        }

        ToggleMaximized();
        e.Handled = true;
    }

    /// <summary>
    /// Drags the window from the bar, for the same reason as above: only reached when the
    /// press arrived as ordinary client input, which means nobody else is going to move
    /// the window for us.
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount > 1 || IsTitleBarControl(e.Source) ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnTitleBarLogoPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(TitleBarLogo).Properties.IsLeftButtonPressed)
        {
            _runaway?.Nudge();
        }
    }

    /// <summary>
    /// Whether the press landed on something on the bar rather than on the bar itself.
    /// </summary>
    /// <remarks>
    /// The logo counts, and that is the point of it existing as a separate question from
    /// "is it a button": ten quick clicks on it are four double-clicks as far as the bar
    /// is concerned, so without this the easter egg would maximise and unmaximise the
    /// window on its way to going off, and the same ten clicks would each begin a window
    /// drag.
    /// </remarks>
    private bool IsTitleBarControl(object? source)
    {
        for (Visual? visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, TitleBarLogo))
            {
                return true;
            }

            if (visual is Button button && button.Classes.Contains("caption"))
            {
                return true;
            }
        }

        return false;
    }

    private void ToggleMaximized()
    {
        WindowState = WindowState == Avalonia.Controls.WindowState.Maximized
            ? Avalonia.Controls.WindowState.Normal
            : Avalonia.Controls.WindowState.Maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            IsMaximized = WindowState == Avalonia.Controls.WindowState.Maximized;
            QueueWindowBoundsSave();
            WindowSystemMenu.Ensure(this);
        }
        else if (change.Property == TitleProperty)
        {
            UpdateTitleBarText();
        }
    }

    public void ShowAndActivate()
    {
        if (_trayMenuAnchor != null)
        {
            CloseActiveContextMenu();
            CloseTrayMenuAnchor();
            Dispatcher.UIThread.Post(ShowAndActivate);
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == Avalonia.Controls.WindowState.Minimized)
        {
            WindowState = Avalonia.Controls.WindowState.Normal;
        }

        Activate();

        // The moment somebody looks at the window is the moment the storage figure has
        // to be true, and it is the cheapest place to ask: nothing else has happened.
        Account.Refresh();

        // Same reasoning, and it also catches the case the tab cannot see for itself:
        // signing in from another window while this one sat hidden in the tray.
        MySpace.RefreshAccount();

        // Folders are made and deleted in the web panel, never here, so coming back to
        // the window is exactly the moment one of them can have appeared or gone. Cheap
        // enough to be unconditional: one small request against a list of seventeen.
        MySpace.RefreshFolders();

        MainWindowIntegration.ReportVisibility(true);
    }

    public void HideToTray()
    {
        // Stopped first, so the bounds written below are the ones its owner chose rather
        // than wherever the easter egg had got to, and so nothing goes on animating a
        // window nobody can see.
        _runaway?.Stop();
        SaveWindowBounds();
        Hide();
        MainWindowIntegration.ReportVisibility(false);
    }

    public void SetTitle(string title)
    {
        Title = title;
    }

    public void ShowTrayMenu()
    {
        if (_trayMenuAnchor != null)
        {
            CloseActiveContextMenu();
            CloseTrayMenuAnchor();
            return;
        }

        CloseActiveContextMenu();

        System.Drawing.Point cursorPosition = FormsCursor.Position;
        PixelPoint anchorPosition = new(cursorPosition.X, cursorPosition.Y);
        Window anchor = CreateTrayMenuAnchor(anchorPosition);
        _trayMenuAnchor = anchor;
        anchor.Deactivated += OnTrayMenuAnchorDeactivated;
        anchor.Show();
        anchor.Position = anchorPosition;
        anchor.Activate();

        ContextMenu menu = BuildContextMenu(_trayMenuBuilder.BuildTrayMenu());
        menu.Closed += OnTrayMenuClosed;

        try
        {
            if (!TryOpenContextMenu(menu, anchor, PlacementMode.BottomEdgeAlignedLeft))
            {
                menu.Closed -= OnTrayMenuClosed;
                CloseTrayMenuAnchor();
            }
            else if (Program.Settings.TrayAutoExpandCaptureMenu && menu.Items.OfType<MenuItem>().FirstOrDefault() is MenuItem captureItem)
            {
                Dispatcher.UIThread.Post(captureItem.Open);
            }
        }
        catch
        {
            menu.Closed -= OnTrayMenuClosed;
            CloseTrayMenuAnchor();
            throw;
        }
    }

    private void WarmUpTrayMenu()
    {
        if (_disposed)
        {
            return;
        }

        Window anchor = CreateTrayMenuAnchor(new PixelPoint(-32000, -32000));
        anchor.ShowActivated = false;
        anchor.Topmost = false;
        ContextMenu menu = BuildContextMenu(_trayMenuBuilder.BuildTrayMenu());
        menu.Opacity = 0;
        menu.IsHitTestVisible = false;

        try
        {
            anchor.Show();
            menu.Open(anchor);

            // Leave the invisible popup attached for one layout pass so Avalonia
            // creates its popup host, templates and styles before the first click.
            Dispatcher.UIThread.Post(() =>
            {
                menu.Close();

                if (anchor.IsVisible)
                {
                    anchor.Close();
                }
            }, DispatcherPriority.Loaded);
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);

            if (anchor.IsVisible)
            {
                anchor.Close();
            }
        }
    }

    private static Window CreateTrayMenuAnchor(PixelPoint position) => new()
    {
        Width = 1,
        Height = 1,
        MinWidth = 1,
        MinHeight = 1,
        Position = position,
        WindowStartupLocation = WindowStartupLocation.Manual,
        WindowDecorations = Avalonia.Controls.WindowDecorations.None,
        ShowInTaskbar = false,
        CanResize = false,
        Topmost = true,
        Opacity = 0,
        Background = Brushes.Transparent,
        RequestedThemeVariant = ThemeManager.GetCurrentTheme()
    };

    public void RefreshMenus()
    {
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        BuildToolsTab();
        RefreshCaptureTiles();
        Account.Refresh();

        // This is what signing in or out calls, so it is where the file grid finds out
        // that the account it was showing is no longer the account.
        MySpace.RefreshAccount();
    }

    public void CloseFromHost()
    {
        if (_disposed)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MainWindowTab tab)
        {
            SelectedTab = tab;
        }
    }

    /// <summary>
    /// Fill the Tools tab from the same list the tray menu builds from.
    /// </summary>
    /// <remarks>
    /// Reading <see cref="MainMenuBuilder.BuildToolsMenu"/> rather than repeating it
    /// means a tool added or dropped there shows up here without a second edit, and the
    /// two can never disagree about what exists. Rebuilt on every refresh because that
    /// is when the labels can have changed language.
    /// </remarks>
    private void BuildToolsTab()
    {
        ToolsPanel.Children.Clear();

        foreach (MainMenuEntry entry in MainMenuBuilder.BuildToolsMenu())
        {
            // The separators group the menu; a wrap panel of cards has no rows to
            // separate, so they are dropped rather than drawn as gaps.
            if (entry.IsSeparator || !entry.IsVisible)
            {
                continue;
            }

            Button button = new()
            {
                Classes = { "tool-card" },
                Content = CreateToolContent(entry),
                IsEnabled = entry.IsEnabled,
                Tag = entry
            };
            button.Click += OnToolClick;
            ToolsPanel.Children.Add(button);
        }
    }

    private static Control CreateToolContent(MainMenuEntry entry)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("24,10,*") };
        grid.Children.Add(CreateAccentMenuIcon(entry.Icon, 19));

        TextBlock label = new()
        {
            Text = entry.Header,
            FontWeight = FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);

        return grid;
    }

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not MainMenuEntry entry || entry.ExecuteAsync == null)
        {
            return;
        }

        // Deferred for the same reason the menu items are: several of these open a modal
        // WinForms dialog or a full-screen capture overlay, and neither can be created
        // while Avalonia is still delivering this click.
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await entry.ExecuteAsync();
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception);
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// The account button: sign in when there is nobody, what can be done with the
    /// account when there is.
    /// </summary>
    private void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        if (ModlightSignIn.Current == null)
        {
            SignInWindowIntegration.Show();
            return;
        }

        OpenStripMenu(button, MainMenuBuilder.BuildAccountMenu());
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            OpenStripMenu(button, _menuBuilder.BuildSettingsMenu());
        }
    }

    private void OnHotkeySettingsClick(object? sender, RoutedEventArgs e)
    {
        // The same door the menu opens. Putting the fix next to the problem is
        // the whole point of moving this out of a startup dialog, which told the
        // user what was wrong and then left them to find the setting themselves.
        _host.ExecuteAvaloniaMainFormCommand(MainFormCommand.HotkeySettings);
    }

    private void OnCaptureTileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CaptureTileViewModel tile })
        {
            // Same call the menu entry makes. The tile is a shortcut to the
            // action, never a second implementation of it.
            tile.Invoke();
        }
    }

    /// <summary>
    /// Rebuild the Capture tab's tiles, picking up whatever hotkeys are bound now.
    /// </summary>
    private void RefreshCaptureTiles()
    {
        CaptureTiles.Clear();

        // Say what the keyboard is actually doing, and which program is doing it -
        // until now the only trace of a failed hotkey was a startup dialog and a
        // line in a log file. HotkeyProblemViewModel picks the culprit, because
        // the Snipping Tool and a running ShareX need opposite fixes.
        List<HotkeySettings> failed = Program.HotkeyManager?.FailedHotkeys ?? new List<HotkeySettings>();
        HasHotkeyProblem = failed.Count > 0;
        HotkeyProblemText = HotkeyProblemViewModel.Describe(failed);

        // Region first: it is the one people reach for, and the one bound to the
        // shortcut this app registers successfully at startup.
        CaptureTiles.Add(new CaptureTileViewModel(
            LucideIcons.scan,
            Strings.MainWindow_CaptureRegionTitle,
            Strings.MainWindow_CaptureRegionBody,
            HotkeyType.RectangleRegion,
            () => new CaptureRegion().Capture(true)));

        CaptureTiles.Add(new CaptureTileViewModel(
            LucideIcons.video,
            Strings.MainWindow_CaptureRecordTitle,
            Strings.MainWindow_CaptureRecordBody,
            HotkeyType.ScreenRecorder,
            () => TaskHelpers.StartScreenRecording(ScreenRecordOutput.FFmpeg, ScreenRecordStartMethod.Region)));

        CaptureTiles.Add(new CaptureTileViewModel(
            LucideIcons.film,
            Strings.MainWindow_CaptureGifTitle,
            Strings.MainWindow_CaptureGifBody,
            HotkeyType.ScreenRecorderGIF,
            () => TaskHelpers.StartScreenRecording(ScreenRecordOutput.GIF, ScreenRecordStartMethod.Region)));

        CaptureTiles.Add(new CaptureTileViewModel(
            LucideIcons.clipboard,
            Strings.MainWindow_CaptureClipboardTitle,
            Strings.MainWindow_CaptureClipboardBody,
            HotkeyType.ClipboardUpload,
            () => UploadManager.ClipboardUploadMainWindow()));
    }

    private void OnCaptureMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ContextMenu menu = BuildContextMenu(_menuBuilder.BuildCaptureMenu());
            TryOpenContextMenu(menu, button, PlacementMode.BottomEdgeAlignedLeft);
        }
    }

    private void OnUploadMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ContextMenu menu = BuildContextMenu(MainMenuBuilder.BuildUploadMenu());
            TryOpenContextMenu(menu, button, PlacementMode.BottomEdgeAlignedLeft);
        }
    }

    /// <summary>Right-aligned, because these two buttons are at the window's right edge.</summary>
    private void OpenStripMenu(Control target, IReadOnlyList<MainMenuEntry> entries)
    {
        ContextMenu menu = BuildContextMenu(entries);
        TryOpenContextMenu(menu, target, PlacementMode.BottomEdgeAlignedRight);
    }

    private ContextMenu BuildContextMenu(IEnumerable<MainMenuEntry> entries)
    {
        ContextMenu menu = new();

        foreach (Control item in BuildMenuControls(entries, menu))
        {
            menu.Items.Add(item);
        }

        return menu;
    }

    private bool TryOpenContextMenu(ContextMenu menu, Control placementTarget, PlacementMode placement)
    {
        if (_activeContextMenu != null)
        {
            return false;
        }

        _activeContextMenu = menu;
        menu.Placement = placement;
        menu.Closed += OnActiveContextMenuClosed;

        try
        {
            menu.Open(placementTarget);
            return true;
        }
        catch
        {
            menu.Closed -= OnActiveContextMenuClosed;
            _activeContextMenu = null;
            throw;
        }
    }

    private void OnActiveContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed -= OnActiveContextMenuClosed;

            if (ReferenceEquals(_activeContextMenu, menu))
            {
                _activeContextMenu = null;
            }
        }
    }

    private void CloseActiveContextMenu()
    {
        if (_activeContextMenu == null)
        {
            return;
        }

        ContextMenu menu = _activeContextMenu;
        _activeContextMenu = null;
        menu.Closed -= OnActiveContextMenuClosed;
        menu.Close();
    }

    private IEnumerable<Control> BuildMenuControls(IEnumerable<MainMenuEntry> entries, ContextMenu menu)
    {
        foreach (MainMenuEntry entry in entries.Where(x => x.IsVisible))
        {
            if (entry.IsSeparator)
            {
                Separator separator = new();
                separator.Classes.Add("compact-menu-separator");
                yield return separator;
                continue;
            }

            MenuItem item = new()
            {
                Header = entry.Header,
                InputGesture = entry.InputGesture,
                IsEnabled = entry.IsEnabled,
                IsChecked = entry.IsChecked,
                StaysOpenOnClick = entry.StaysOpenOnClick,
                ToggleType = entry.ToggleType switch
                {
                    MainMenuToggleType.CheckBox => MenuItemToggleType.CheckBox,
                    MainMenuToggleType.Radio => MenuItemToggleType.Radio,
                    _ => MenuItemToggleType.None
                }
            };
            item.Classes.Add("compact-menu-item");

            if (entry.BitmapIcon is { Length: > 0 })
            {
                item.Icon = CreateBitmapMenuIcon(entry.BitmapIcon, menu);
            }
            else if (!string.IsNullOrEmpty(entry.Icon))
            {
                item.Icon = CreateAccentMenuIcon(entry.Icon, 16);
            }

            if (entry.CreateChildren != null)
            {
                AddLazySubmenu(item, entry.CreateChildren, menu);
            }

            if (entry.ExecuteAsync != null)
            {
                item.Click += (_, _) =>
                {
                    if (!entry.StaysOpenOnClick)
                    {
                        // Modal WinForms dialogs and capture overlays cannot be opened while
                        // the Avalonia popup is still processing its click event. Dismiss the
                        // entire menu first, then run the command on the next dispatcher turn.
                        menu.Close();
                    }

                    Dispatcher.UIThread.Post(async () =>
                    {
                        try
                        {
                            await entry.ExecuteAsync();
                            if (!entry.StaysOpenOnClick)
                            {
                                RefreshMenus();
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugHelper.WriteException(ex);
                        }
                    }, DispatcherPriority.Background);
                };
            }

            yield return item;
        }
    }

    private void AddLazySubmenu(MenuItem item, Func<IReadOnlyList<MainMenuEntry>> createChildren, ContextMenu menu)
    {
        // Keep the submenu indicator without constructing every nested menu before
        // the root context menu can be displayed.
        item.Items.Add(new MenuItem { IsVisible = false });

        bool isPopulated = false;
        item.SubmenuOpened += (_, e) =>
        {
            if (isPopulated || !ReferenceEquals(e.Source, item))
            {
                return;
            }

            item.Items.Clear();

            foreach (Control child in BuildMenuControls(createChildren(), menu))
            {
                item.Items.Add(child);
            }

            isPopulated = true;
        };
    }

    private static TextBlock CreateLucideText(string glyph, double size)
    {
        TextBlock text = new()
        {
            Text = glyph,
            FontSize = size,
            FontWeight = FontWeight.Normal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        text.Classes.Add("icon");
        return text;
    }

    private static TextBlock CreateAccentMenuIcon(string glyph, double size)
    {
        TextBlock icon = CreateLucideText(glyph, size);
        icon.Classes.Add("accent-menu-icon");
        return icon;
    }

    private static Image CreateBitmapMenuIcon(byte[] data, ContextMenu menu)
    {
        using MemoryStream stream = new(data, writable: false);
        AvaloniaBitmap bitmap = new(stream);
        menu.Closed += (_, _) => bitmap.Dispose();

        return new Image
        {
            Source = bitmap,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform
        };
    }

    private void OnTaskChanged(WorkerTask task) => Dispatcher.UIThread.Post(() =>
    {
        // An upload that just landed changed the storage figure. This fires many times
        // per task, which is why the refresh throttles itself rather than being filtered
        // by task status here: "did this particular change move the quota" is a question
        // only the server can answer.
        Account.Refresh();
    });

    /// <summary>
    /// One upload has ended, one way or the other.
    /// </summary>
    /// <remarks>
    /// <see cref="TaskManager.TaskFinished"/> rather than the general TaskChanged event,
    /// because this has to happen exactly once per task: TaskChanged fires on every
    /// progress tick, and a toast per tick would be a wall of them.
    ///
    /// Only uploads. A capture that was saved to disk and never sent anywhere has nothing
    /// to report and no link to offer, and a cancellation is something the user did on
    /// purpose a second ago - being told about it is noise, not news.
    /// </remarks>
    private void OnTaskFinished(WorkerTask task) => Dispatcher.UIThread.Post(() => ReportFinished(task));

    private void ReportFinished(WorkerTask task)
    {
        if (_disposed || task.Info == null || !task.Info.IsUploadJob || task.Status == TaskStatus.Stopped)
        {
            return;
        }

        // What the SERVER said, and only then what we asked for. They differ on almost
        // every upload ShareX makes by itself: a hotkey capture asks for nothing, and the
        // server puts it in the account's default folder. Reading only the request left
        // the receiving tile's count stale and the notice unable to name where the file
        // went, on exactly the uploads a person makes most often.
        string? folderId = task.Info.ModlightLandedFolderId ?? task.Info.ModlightFolderId;

        // "A file arrived in the library", which is a narrower thing than "a task that
        // was going to produce a URL produced one". Shortening and sharing a URL are
        // upload jobs too and they set Result.URL, but nothing lands in a folder for
        // them - counting one would put a file on a folder tile that has no file in it.
        bool landed = task.Status != TaskStatus.Failed &&
                      task.Info.Job != TaskJob.ShortenURL &&
                      task.Info.Job != TaskJob.ShareURL &&
                      !string.IsNullOrEmpty(task.Info.Result?.URL);

        // Resolved before the toast is built and while the file is still on disk, because
        // both facts are about to stop being available: the target can move, and a
        // capture with "delete file after upload" set is about to be removed.
        ShowToast(new UploadToastViewModel(task, MySpace.FolderNameFor(folderId)));

        if (landed)
        {
            MySpace.NoteUploadLanded(folderId, SizeOf(task.Info));
        }
    }

    /// <summary>
    /// A file the offline queue finally got out. Show it the same way as any other.
    /// </summary>
    /// <remarks>
    /// Only successes. A cancellation is something the user did on purpose, and a failure
    /// leaves the item in the queue with its own row on the Upload tab, which is where the
    /// user goes to do something about it - neither of them put a file in the library.
    ///
    /// The folder is the one the item was queued against; the queue speaks tus, whose
    /// answer does not carry the folder the way the 201 does, so an item that named none
    /// still corrects nothing. The grid merge does not depend on it and runs regardless,
    /// which is the half that was missing entirely.
    /// </remarks>
    private void OnQueuedUploadLanded(ModlightQueueOutcome outcome) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || outcome == null || !outcome.Succeeded)
        {
            return;
        }

        MySpace.NoteUploadLanded(outcome.Item.FolderId, outcome.Item.Length);
        Account.Refresh();
    });

    /// <summary>
    /// How many bytes this upload was, for the folder tile's running total.
    /// </summary>
    /// <remarks>
    /// The file on disk first, the transfer's own byte count second. Neither is certain -
    /// a capture can have been deleted after uploading, and a text or clipboard upload
    /// never had a file - so a wrong answer has to be a harmless one, and zero is: the
    /// tile's count still goes up by one, and the exact figure is the server's on the
    /// next reload.
    /// </remarks>
    private static long SizeOf(TaskInfo info)
    {
        try
        {
            if (!string.IsNullOrEmpty(info.FilePath) && File.Exists(info.FilePath))
            {
                return new FileInfo(info.FilePath).Length;
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }

        return info.Progress?.Length ?? 0;
    }

    /// <summary>
    /// Put one notice up, and take it down again by itself.
    /// </summary>
    /// <remarks>
    /// Newest at the top of the stack and never more than <see cref="MaxToasts"/> of them:
    /// a folder of thirty files dropped at once must not turn the window into a column of
    /// notices reaching the title bar. A failure is left up more than twice as long as a
    /// success, because it has something to read - the success has already done its job by
    /// existing, and the link is on the clipboard anyway.
    /// </remarks>
    private void ShowToast(UploadToastViewModel toast)
    {
        Toasts.Insert(0, toast);

        while (Toasts.Count > MaxToasts)
        {
            Toasts.RemoveAt(Toasts.Count - 1);
        }

        DispatcherTimer.RunOnce(() => Toasts.Remove(toast),
            toast.IsFailed ? FailedToastLife : DoneToastLife);
    }

    private void OnToastCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UploadToastViewModel toast })
        {
            toast.Copy();
        }
    }

    private void OnToastOpenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UploadToastViewModel toast } && toast.HasLink)
        {
            URLHelpers.OpenURL(toast.Link);
        }
    }

    private void OnToastDismissClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UploadToastViewModel toast })
        {
            Toasts.Remove(toast);
        }
    }

    private void OnThemeChanged(object? sender, Avalonia.Styling.ThemeVariant theme) =>
        Dispatcher.UIThread.Post(() => RequestedThemeVariant = theme);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Every shortcut below acts on the selected items in the My space list, and the
        // selection survives a tab change. Without this the Delete key would remove
        // files from a list that is not even on screen.
        if (SelectedTab != MainWindowTab.MySpace)
        {
            return;
        }

        // My space is a folder explorer, so it answers an explorer's keys first. Kept
        // clear of text fields on principle - there is nothing to type into on this tab
        // today, and the day there is, Backspace has to keep meaning "delete a character".
        if (e.Source is not TextBox && HandleMySpaceKey(e))
        {
            e.Handled = true;
            return;
        }

        // Ctrl+V is the one shortcut here that was never about the session strip: it
        // uploads whatever is on the clipboard, into whichever folder the tab is showing.
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            UploadManager.ClipboardUploadMainWindow();
            e.Handled = true;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e) => UpdateDropOverlay(e);

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e) => UpdateDropOverlay(e);

    private void UpdateDropOverlay(DragEventArgs e)
    {
        // A drag of the account's own cards is a MOVE between folders, not an upload. It
        // carries their links as text only because the platform will not run a drag with an
        // empty transfer, and reading that text here is what used to promise "release to
        // upload text" over a picture the account already owned - see the note on
        // MainWindow.MySpace.DragDrop.
        if (CloudDragOwnsEvent(e))
        {
            return;
        }

        bool hasFiles = e.DataTransfer.TryGetFiles()?.Any() == true;
        bool hasText = !string.IsNullOrEmpty(e.DataTransfer.TryGetText());
        bool supported = hasFiles || hasText;

        e.DragEffects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = supported;

        if (hasFiles)
        {
            DropOverlayTitle.Text = Strings.MainWindow_ReleaseToUploadFiles;
            DropOverlayDescription.Text = Strings.MainWindow_FilesDroppedUploadDescription;
        }
        else if (hasText)
        {
            DropOverlayTitle.Text = Strings.MainWindow_ReleaseToUploadText;
            DropOverlayDescription.Text = Strings.MainWindow_TextDroppedUploadDescription;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Files or text released anywhere on the window.
    /// </summary>
    /// <remarks>
    /// No folder is named here, and that used to be the whole bug: this went to the
    /// account's default while the tab overhead plainly said the user was standing in
    /// "CV". It lands correctly now without this handler knowing anything about folders,
    /// because an upload that names none takes the ambient target - see
    /// <see cref="ModlightUploadTarget"/>. A drop onto a folder TILE still beats it: that
    /// handler marks the event handled, so this one never runs for it.
    /// </remarks>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;

        // The other half of the same gesture: releasing a card of ours anywhere that is not
        // a folder tile has to do nothing at all. Falling through here would take the links
        // out of the transfer and post them back to the server as a new paste - a copy made
        // by a gesture that meant to move the original.
        if (CloudDragOwnsEvent(e))
        {
            return;
        }

        FormsDataObject dataObject = new();
        string[] files = e.DataTransfer.TryGetFiles()?
            .Select(x => x.TryGetLocalPath())
            .Where(x => !string.IsNullOrEmpty(x))
            .Cast<string>().ToArray() ?? Array.Empty<string>();

        if (files.Length > 0)
        {
            dataObject.SetData(FormsDataFormats.FileDrop, files);
        }

        string? text = e.DataTransfer.TryGetText();
        if (!string.IsNullOrEmpty(text))
        {
            dataObject.SetText(text);
        }

        UploadManager.DragDropUpload(dataObject);
        e.Handled = true;
    }

    private void OnTrayIconRightButtonDown()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                CloseActiveContextMenu();
                CloseTrayMenuAnchor();
            }
        });
    }

    private void OnTrayIconRightButtonUp()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                ShowTrayMenu();
            }
        });
    }

    private void OnTrayMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed -= OnTrayMenuClosed;
        }

        CloseTrayMenuAnchor();
        SettingManager.SaveAllSettingsAsync();
    }

    private void OnTrayMenuAnchorDeactivated(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _trayMenuAnchor))
        {
            CloseActiveContextMenu();
            CloseTrayMenuAnchor();
        }
    }

    private void CloseTrayMenuAnchor()
    {
        Window? anchor = _trayMenuAnchor;
        _trayMenuAnchor = null;

        if (anchor != null)
        {
            anchor.Deactivated -= OnTrayMenuAnchorDeactivated;

            if (anchor.IsVisible)
            {
                anchor.Close();
            }
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_allowClose && Program.Settings.ShowTray)
        {
            e.Cancel = true;
            HideToTray();
            SettingManager.SaveAllSettingsAsync();

            if (Program.Settings.FirstTimeMinimizeToTray)
            {
                TaskHelpers.ShowNotificationTip(Strings.ShareXIsMinimizedToTheSystemTray, "Modlight", 8000);
                Program.Settings.FirstTimeMinimizeToTray = false;
            }

            return;
        }

        if (!_allowClose)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(_host.ForceClose);
            return;
        }

        DisposeWindowResources();
    }

    private void DisposeWindowResources()
    {
        if (_disposed)
        {
            return;
        }

        // Before the bounds are written down, not after: the window has to be back
        // where its owner left it, or the easter egg decides where it opens tomorrow.
        _runaway?.Dispose();
        _runaway = null;

        SaveWindowBounds();
        _disposed = true;
        TaskManager.TaskChanged -= OnTaskChanged;
        TaskManager.TaskFinished -= OnTaskFinished;
        ModlightUploadQueue.Finished -= OnQueuedUploadLanded;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        UnhookAdminVisibility();
        Account.Cancel();
        CloseActiveContextMenu();
        CloseTrayMenuAnchor();
        _host.TrayIconService.RightButtonDown -= OnTrayIconRightButtonDown;
        _host.TrayIconService.RightButtonUp -= OnTrayIconRightButtonUp;
        _host.TrayIconService.Visible = false;
        Toasts.Clear();
        _titleBarLogo?.Dispose();
        _titleBarLogo = null;
    }

    /// <summary>
    /// Puts the window back where it was last left, on a screen that still exists.
    /// </summary>
    /// <remarks>
    /// The clamp is the whole point. A position is remembered against a desktop that can
    /// have changed since - a laptop undocked, a second monitor unplugged, a resolution
    /// dropped - and a window restored onto a screen that is no longer there opens
    /// nowhere at all, which does not look like a lost setting, it looks like the
    /// application failed to start.
    ///
    /// Maximised is restored as a state on top of that, never as a size: remembering it
    /// as one would make "restore down" hand back a window the size of the screen, with
    /// no way to get the real one back short of resizing it by hand.
    /// </remarks>
    private void RestoreWindowBounds()
    {
        DrawingSize savedSize = Program.Settings.MainFormSize;
        DrawingPoint savedPosition = Program.Settings.MainFormPosition;
        bool wantSize = Program.Settings.RememberMainFormSize && !savedSize.IsEmpty;
        bool wantPosition = Program.Settings.RememberMainFormPosition && !savedPosition.IsEmpty;

        if (wantSize || wantPosition)
        {
            double width = wantSize ? Math.Max(MinWidth, savedSize.Width) : Width;
            double height = wantSize ? Math.Max(MinHeight, savedSize.Height) : Height;
            PixelPoint position = new(savedPosition.X, savedPosition.Y);
            Screen? screen = FindScreenFor(position, width, height);

            if (screen != null)
            {
                double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
                PixelRect area = screen.WorkingArea;

                width = Math.Min(width, Math.Max(MinWidth, area.Width / scaling));
                height = Math.Min(height, Math.Max(MinHeight, area.Height / scaling));

                int pixelWidth = (int)Math.Round(width * scaling);
                int pixelHeight = (int)Math.Round(height * scaling);
                position = new PixelPoint(
                    Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - pixelWidth)),
                    Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - pixelHeight)));
            }

            if (wantSize)
            {
                Width = width;
                Height = height;
            }

            if (wantPosition)
            {
                Position = position;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }

        if (Program.Settings.MainFormMaximized)
        {
            WindowState = Avalonia.Controls.WindowState.Maximized;
        }
    }

    /// <summary>
    /// The screen a window of this size at this position would mostly be on, or the
    /// primary one when the answer is "none of them".
    /// </summary>
    /// <remarks>
    /// Overlap rather than "which screen is the top-left corner on", because the corner
    /// is the one part of a window that is routinely a few pixels outside its screen -
    /// snapped windows and shadows both do it - and asking the corner would send those
    /// windows to the primary monitor on every launch.
    /// </remarks>
    private Screen? FindScreenFor(PixelPoint position, double width, double height)
    {
        Screens screens = Screens;
        if (screens == null || screens.ScreenCount == 0)
        {
            return null;
        }

        Screen? best = null;
        long bestArea = 0;

        foreach (Screen screen in screens.All)
        {
            double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
            PixelRect wanted = new(position,
                new PixelSize((int)Math.Round(width * scaling), (int)Math.Round(height * scaling)));
            PixelRect shared = screen.Bounds.Intersect(wanted);
            long area = (long)shared.Width * shared.Height;

            if (area > bestArea)
            {
                bestArea = area;
                best = screen;
            }
        }

        return best ?? screens.Primary ?? (screens.ScreenCount > 0 ? screens.All[0] : null);
    }

    /// <summary>
    /// Records the bounds once the window has finished whatever it is doing.
    /// </summary>
    /// <remarks>
    /// Not an optimisation - a correctness fix, and one this cost a full round of
    /// testing to find. Maximising raises Resized while WindowState still reads Normal,
    /// so a save taken there writes the maximised frame down as the window's ordinary
    /// size and position: 1920x1032 at -8,-8. The next launch then opens maximised
    /// (correct), and "restore down" hands back a window the size of the screen, with
    /// the real one gone for good.
    ///
    /// Posting it puts the read after the whole Win32 message has been dealt with, by
    /// which time WindowState is the state the window is actually in. The two callers
    /// that run at shutdown keep calling the synchronous version: by then nothing is
    /// changing, and a posted job would never be reached.
    /// </remarks>
    private void QueueWindowBoundsSave()
    {
        if (_disposed || _boundsSaveQueued)
        {
            return;
        }

        _boundsSaveQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _boundsSaveQueued = false;
            SaveWindowBounds();
        }, DispatcherPriority.Background);
    }

    private void SaveWindowBounds()
    {
        // While the easter egg is on, the window is somewhere neither of us chose.
        if (_disposed || _runaway?.IsRunning == true)
        {
            return;
        }

        Avalonia.Controls.WindowState state = WindowState;

        // Minimised is not a place. Whatever was recorded last still stands.
        if (state == Avalonia.Controls.WindowState.Minimized)
        {
            return;
        }

        Program.Settings.MainFormMaximized = state == Avalonia.Controls.WindowState.Maximized;

        if (state != Avalonia.Controls.WindowState.Normal)
        {
            return;
        }

        Program.Settings.MainFormPosition = new DrawingPoint(Position.X, Position.Y);
        Program.Settings.MainFormSize = new DrawingSize((int)Math.Round(ClientSize.Width), (int)Math.Round(ClientSize.Height));
    }

    private static WindowIcon CreateWindowIcon()
    {
        using MemoryStream stream = new();
        ShareXResources.Icon.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    private static AvaloniaBitmap CreateTitleBarLogo()
    {
        using System.Drawing.Bitmap logo = ShareXResources.Logo;
        using Stream stream = logo.GetStream();
        stream.Position = 0;
        return new AvaloniaBitmap(stream);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static MainMenuEntry Item(string header, string icon, Action execute, bool visible = true,
        KeyGesture? inputGesture = null) =>
        new(header, icon, execute, isVisible: visible, inputGesture: inputGesture);

    private static MainMenuEntry Item(string header, string icon, Func<Task> execute, bool visible = true,
        KeyGesture? inputGesture = null) =>
        new(header, icon, execute, isVisible: visible, inputGesture: inputGesture);

    private static MainMenuEntry Submenu(string header, string icon, Func<IReadOnlyList<MainMenuEntry>> children, bool visible = true) =>
        new(header, icon, createChildren: children, isVisible: visible);
}
