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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Localization;
using System.ComponentModel;

namespace ShareX.AvaloniaUI.Controls;

public partial class SettingsNavigation : UserControl
{
    public static readonly StyledProperty<IEnumerable<SettingsNavigationItem>?> ItemsProperty =
        AvaloniaProperty.Register<SettingsNavigation, IEnumerable<SettingsNavigationItem>?>(nameof(Items));

    public static readonly StyledProperty<SettingsNavigationItem?> SelectedItemProperty =
        AvaloniaProperty.Register<SettingsNavigation, SettingsNavigationItem?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> SearchTextProperty =
        AvaloniaProperty.Register<SettingsNavigation, string>(
            nameof(SearchText),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<Control?> SearchRootProperty =
        AvaloniaProperty.Register<SettingsNavigation, Control?>(nameof(SearchRoot));

    /// <summary>Whether the current query left anything in the list.</summary>
    public static readonly StyledProperty<bool> HasResultsProperty =
        AvaloniaProperty.Register<SettingsNavigation, bool>(nameof(HasResults), true);

    public static readonly StyledProperty<bool> HasQueryProperty =
        AvaloniaProperty.Register<SettingsNavigation, bool>(nameof(HasQuery));

    public static readonly StyledProperty<string> NoResultsMessageProperty =
        AvaloniaProperty.Register<SettingsNavigation, string>(nameof(NoResultsMessage), string.Empty);

    private bool _filterUpdateQueued;
    private bool _syncingExpansion;
    private bool _hadQuery;
    private bool _fromPointer = true;
    private SettingsNavigationItem[] _watched = [];

    public IEnumerable<SettingsNavigationItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public SettingsNavigationItem? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public string SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public Control? SearchRoot
    {
        get => GetValue(SearchRootProperty);
        set => SetValue(SearchRootProperty, value);
    }

    public bool HasResults
    {
        get => GetValue(HasResultsProperty);
        private set => SetValue(HasResultsProperty, value);
    }

    public bool HasQuery
    {
        get => GetValue(HasQueryProperty);
        private set => SetValue(HasQueryProperty, value);
    }

    /// <summary>What the pane says when a query matched nothing.</summary>
    public string NoResultsMessage
    {
        get => GetValue(NoResultsMessageProperty);
        private set => SetValue(NoResultsMessageProperty, value);
    }

    /// <summary>
    /// Whether the last selection change came from the pointer rather than the keyboard.
    /// </summary>
    /// <remarks>
    /// The window redirects a selected GROUP onto its first section, which is right for a
    /// click - a heading is not a page - and a trap for the arrow keys: pressing Up from
    /// the first child selects the heading, the redirect throws the selection straight
    /// back down, and there is no way past an open group. Only one group is open at a
    /// time, so landing in "Advanced" made everything above it unreachable.
    ///
    /// Read from SettingsWindow.Select. Defaults to true so that a programmatic
    /// selection - ShowSection, the account chip - behaves exactly as a click does.
    /// </remarks>
    public bool SelectionFromPointer => _fromPointer;

    public SettingsNavigation()
    {
        InitializeComponent();
        SearchTextProperty.Changed.AddClassHandler<SettingsNavigation>((control, _) => control.QueueFilterUpdate());
        ItemsProperty.Changed.AddClassHandler<SettingsNavigation>((control, _) =>
        {
            control.WatchGroups();
            control.QueueFilterUpdate();
        });
        SelectedItemProperty.Changed.AddClassHandler<SettingsNavigation>((control, _) =>
        {
            control.OpenGroupOfSelection();
            control.QueueFilterUpdate();
        });
        SearchRootProperty.Changed.AddClassHandler<SettingsNavigation>((control, _) => control.QueueFilterUpdate());
        AttachedToLogicalTree += (_, _) => QueueFilterUpdate();

        // Tunnelled, so the flag is set before the TreeView has decided anything. Both
        // handlers are on this control, which is what keeps typing in the search box -
        // a KeyDown - from being read as "the user is arrowing through the list".
        AddHandler(PointerPressedEvent, (_, _) => _fromPointer = true, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, _) => _fromPointer = false, RoutingStrategies.Tunnel);
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e) => SearchText = string.Empty;

    private void QueueFilterUpdate()
    {
        if (_filterUpdateQueued)
        {
            return;
        }

        _filterUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _filterUpdateQueued = false;
            ApplyFilter();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyFilter()
    {
        if (Items == null)
        {
            return;
        }

        if (SearchRoot != null)
        {
            SettingsSearch.Apply(SearchRoot, Items, SearchText);
        }

        foreach (SettingsNavigationItem item in Items)
        {
            item.ApplyFilter(SearchText);
        }

        if (SelectedItem?.IsVisible != true)
        {
            SelectedItem = Flatten(Items).FirstOrDefault(x => x.IsVisible && x.Children.Count == 0) ??
                Flatten(Items).FirstOrDefault(x => x.IsVisible);
        }

        // Said out loud rather than left as an empty pane. Nothing at all is how this
        // used to answer a query with no hits, and an empty list beside a page that
        // still showed the PREVIOUS section's heading reads as a program that broke.
        HasQuery = !string.IsNullOrEmpty(SearchText);
        HasResults = Flatten(Items).Any(x => x.IsVisible);
        NoResultsMessage = HasResults
            ? string.Empty
            : string.Format(Strings.SettingsNavigation_NoResults, SearchText);

        // A query opens every group that has a hit in it, which is what makes searching
        // work. Clearing the query has to undo that, or the pane is left holding the
        // wide-open list the redesign exists to get rid of.
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchText);

        if (_hadQuery && !hasQuery)
        {
            OpenGroupOfSelection();
        }

        _hadQuery = hasQuery;
    }

    /// <summary>
    /// Open the group holding the current page and close the rest.
    /// </summary>
    /// <remarks>
    /// One group at a time is what keeps the whole map on screen: the list can never be
    /// taller than "every group + the biggest group", however much clicking happens,
    /// whereas letting groups pile up open ends where this started - thirty rows in a
    /// pane that fits fifteen, with the last groups below the fold.
    ///
    /// Selection drives it rather than the chevron alone, because clicking a closed group
    /// also selects its first page (see the settings window), so the two gestures a
    /// person actually makes - clicking a group, clicking a page - both land here.
    /// </remarks>
    private void OpenGroupOfSelection()
    {
        if (Items == null || !string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        SettingsNavigationItem? selected = SelectedItem;
        _syncingExpansion = true;

        foreach (SettingsNavigationItem item in Items)
        {
            if (item.Children.Count > 0)
            {
                item.IsExpanded = selected != null && (item == selected || item.Children.Contains(selected));
            }
        }

        _syncingExpansion = false;
    }

    private void WatchGroups()
    {
        foreach (SettingsNavigationItem item in _watched)
        {
            item.PropertyChanged -= OnGroupPropertyChanged;
        }

        _watched = Items?.Where(x => x.Children.Count > 0).ToArray() ?? [];

        foreach (SettingsNavigationItem item in _watched)
        {
            item.PropertyChanged += OnGroupPropertyChanged;
        }
    }

    /// <summary>Opening a group by its chevron closes the others, same as selecting one.</summary>
    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingExpansion ||
            e.PropertyName != nameof(SettingsNavigationItem.IsExpanded) ||
            sender is not SettingsNavigationItem opened ||
            !opened.IsExpanded ||
            !string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        _syncingExpansion = true;

        foreach (SettingsNavigationItem item in _watched)
        {
            if (item != opened)
            {
                item.IsExpanded = false;
            }
        }

        _syncingExpansion = false;
    }

    public void RefreshFilter() => QueueFilterUpdate();

    private static IEnumerable<SettingsNavigationItem> Flatten(IEnumerable<SettingsNavigationItem> items)
    {
        foreach (SettingsNavigationItem item in items)
        {
            yield return item;

            foreach (SettingsNavigationItem child in Flatten(item.Children))
            {
                yield return child;
            }
        }
    }
}
