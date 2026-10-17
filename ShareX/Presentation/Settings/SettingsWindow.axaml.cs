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
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Controls;
using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ShareX;

/// <summary>
/// The one settings window: a list of sections on the left, one section on the right.
/// </summary>
/// <remarks>
/// <para>
/// ShareX had three of these - Application settings, Task settings and Hotkey settings -
/// and the first thing anybody looking for a setting had to do was guess which. The
/// pages behind those dialogs are unchanged and still live in their own views; what
/// changed is that they are now SECTIONS of one window, listed together and searched
/// together, with the filing decided by <see cref="SettingsSections"/>.
/// </para>
/// <para>
/// Every host view is built when the window opens rather than when its first section is
/// picked, and that is deliberate: the search box indexes the tree it is given, so a view
/// that has not been built yet is a set of settings that searching cannot find. Paying
/// for all of them at once costs what opening all three old dialogs used to.
/// </para>
/// <para>
/// The same window in a second shape is a single hotkey's overrides - same shell, same
/// search, a section list holding only the task pages. See
/// <see cref="SettingsSections.ForTaskOverrides"/>.
/// </para>
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly Dictionary<string, SettingsSection> _sections = [];
    private readonly Dictionary<string, string> _groupOf = [];
    private readonly Dictionary<SettingsHost, Control> _hosts = [];
    private readonly Action? _closed;
    private ObservableCollection<SettingsNavigationItem> _items = [];
    private SettingsNavigationItem? _nearest;
    private bool _redirecting;

    /// <summary>
    /// For the XAML runtime loader and the previewer, which need a public parameterless
    /// constructor. Everything else goes through one of the two below.
    /// </summary>
    public SettingsWindow() : this((IHotkeySettingsService?)null, null)
    {
    }

    /// <summary>The settings window proper: account, capture, uploading, shortcuts, application.</summary>
    internal SettingsWindow(IHotkeySettingsService? hotkeys, Action? closed)
        : this(SettingsSections.All(), Program.DefaultTaskSettings, true, hotkeys, closed)
    {
        Title = Strings.SettingsWindow_WindowTitle;
    }

    /// <summary>The same shell holding one hotkey's overrides.</summary>
    internal SettingsWindow(TaskSettings settings, Action? closed)
        : this(SettingsSections.ForTaskOverrides(), settings, false, null, closed)
    {
        Title = string.Format(Strings.TaskSettingsWindow_ShareXTaskSettingsFor0, settings);
    }

    private SettingsWindow(
        IReadOnlyList<SettingsGroup> groups,
        TaskSettings taskSettings,
        bool isDefault,
        IHotkeySettingsService? hotkeys,
        Action? closed)
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        _closed = closed;

        BuildHosts(groups, taskSettings, isDefault, hotkeys);
        BuildNavigation(groups);

        ThemeManager.ThemeChanged += OnThemeChanged;
        SizeChanged += (_, _) => ResizeNavigation();
        ResizeNavigation();
        Opened += (_, _) =>
        {
            Activate();
            Navigation.RefreshFilter();
        };
        Closed += OnClosed;
    }

    /// <summary>Show a section by its id, opening its group on the way.</summary>
    /// <remarks>
    /// Ignores an id this window does not carry rather than throwing: the hotkey override
    /// shell has no account sections, and a caller asking for one has made a mistake that
    /// is not worth crashing somebody's settings window over.
    /// </remarks>
    public void ShowSection(string sectionId)
    {
        SettingsNavigationItem? item = Flatten(_items).FirstOrDefault(x => x.Id == sectionId);

        if (item != null)
        {
            Navigation.SelectedItem = item;
        }
    }

    private void BuildHosts(
        IReadOnlyList<SettingsGroup> groups,
        TaskSettings taskSettings,
        bool isDefault,
        IHotkeySettingsService? hotkeys)
    {
        HashSet<SettingsHost> wanted = groups.SelectMany(x => x.Sections).Select(x => x.Host).ToHashSet();

        if (wanted.Contains(SettingsHost.Account))
        {
            // Not stamped: this view is ours and writes the prefixed page ids itself.
            AccountSettingsView account = new();
            Add(SettingsHost.Account, account);
        }

        if (wanted.Contains(SettingsHost.Application))
        {
            ApplicationSettingsView application = new();
            StampPageIds(application, SettingsHost.Application);
            Add(SettingsHost.Application, application);
        }

        if (wanted.Contains(SettingsHost.Task))
        {
            TaskSettingsView task = new(taskSettings, isDefault);
            StampPageIds(task, SettingsHost.Task);
            Add(SettingsHost.Task, task);
        }

        if (wanted.Contains(SettingsHost.Hotkeys) && hotkeys != null)
        {
            HotkeySettingsView view = new(hotkeys);
            SettingsSearch.SetPageId(view, SettingsSections.HotkeysSectionId);
            Add(SettingsHost.Hotkeys, view);
        }

        void Add(SettingsHost host, Control view)
        {
            view.IsVisible = false;
            HidePageTitles(view);
            _hosts[host] = view;
            Sections.Children.Add(view);
        }
    }

    /// <summary>
    /// Give every page in a host view the id the merged section list knows it by.
    /// </summary>
    /// <remarks>
    /// The three inherited views each name a page "general", "upload" and "advanced".
    /// The search index looks a page up by the id stamped on the control, so without a
    /// prefix the task settings' "general" page and the application settings' "general"
    /// page would both claim the same list entry - and whichever the index reached second
    /// would overwrite the first one's searchable text. Stamping here rather than editing
    /// eighteen page ids inside two files keeps the views readable on their own terms.
    /// </remarks>
    private static void StampPageIds(Control host, SettingsHost kind)
    {
        string prefix = kind.ToString().ToLowerInvariant() + ".";

        foreach (Control page in host.GetLogicalDescendants().OfType<Control>())
        {
            string? id = SettingsSearch.GetPageId(page);

            if (!string.IsNullOrEmpty(id) && !id.StartsWith(prefix, StringComparison.Ordinal))
            {
                SettingsSearch.SetPageId(page, prefix + id);
            }
        }
    }

    /// <summary>
    /// Take the title off every page, because the shell draws one header for all of them.
    /// </summary>
    /// <remarks>
    /// Hidden rather than deleted: the search index reads the page's own title control to
    /// work out what the page is called (<see cref="SettingsSearch"/>), and it reads text,
    /// not pixels, so a hidden title still indexes. Deleting the titles instead would mean
    /// editing every page in three files to gain nothing.
    ///
    /// Set as a local value rather than through a style so it does not depend on which of
    /// the nested style hosts wins - the pages carry their own "page-title" styles.
    /// </remarks>
    private static void HidePageTitles(Control host)
    {
        foreach (Control control in host.GetLogicalDescendants().OfType<Control>())
        {
            if (SettingsSearch.GetIsPageTitle(control))
            {
                control.IsVisible = false;
            }
        }
    }

    /// <summary>
    /// Keep the section list roughly a quarter of the window, within reason.
    /// </summary>
    /// <remarks>
    /// A fixed pane is the first thing a narrow window breaks: at the smallest size the
    /// list was taking a third of the width away from the page it is there to open, and
    /// on a maximised window it stayed narrow enough to clip its own longest titles.
    /// </remarks>
    private void ResizeNavigation()
    {
        // Bounds is empty until the window is laid out; Width is what the XAML asked for.
        double window = Bounds.Width > 0 ? Bounds.Width : Width;
        double width = Math.Clamp(window * 0.26, 220, 276);
        Shell.ColumnDefinitions[0].Width = new GridLength(width);
    }

    private void ShowHeader(SettingsSection section)
    {
        string group = _groupOf.TryGetValue(section.Id, out string? title) ? title : string.Empty;

        HeaderIcon.Text = section.Icon;
        HeaderTitle.Text = section.Title;
        HeaderDescription.Text = section.Description;
        HeaderDescription.IsVisible = !string.IsNullOrWhiteSpace(section.Description);

        // A group of one is drawn as a single row and carries its section's name, so
        // printing it above that same name would be the title twice.
        HeaderGroup.Text = group;
        HeaderGroup.IsVisible = !string.IsNullOrWhiteSpace(group) &&
            !string.Equals(group, section.Title, StringComparison.CurrentCulture);
    }

    private void BuildNavigation(IReadOnlyList<SettingsGroup> groups)
    {
        foreach (SettingsGroup group in groups)
        {
            foreach (SettingsSection section in group.Sections)
            {
                _groupOf[section.Id] = group.Title;
            }
        }

        foreach (SettingsSection section in groups.SelectMany(x => x.Sections))
        {
            // A section whose host could not be built - shortcuts with no hotkey manager -
            // is left out of the list rather than listed and dead.
            if (_hosts.ContainsKey(section.Host))
            {
                _sections[section.Id] = section;
            }
        }

        IReadOnlyList<SettingsGroup> live = groups
            .Select(group => group with { Sections = group.Sections.Where(x => _sections.ContainsKey(x.Id)).ToArray() })
            .Where(group => group.Sections.Count > 0)
            .ToArray();

        _items = SettingsSections.ToNavigation(live);
        Navigation.Items = _items;
        Navigation.PropertyChanged += OnNavigationPropertyChanged;

        ShowSection(_sections.ContainsKey(SettingsSections.DefaultSectionId)
            ? SettingsSections.DefaultSectionId
            : live[0].Sections[0].Id);
    }

    private void OnNavigationPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == SettingsNavigation.SelectedItemProperty)
        {
            Select(Navigation.SelectedItem);
        }
        else if (e.Property == SettingsNavigation.HasResultsProperty)
        {
            if (Navigation.HasResults)
            {
                Select(Navigation.SelectedItem);
            }
            else
            {
                ShowNoResults();
            }
        }
        else if (e.Property == SettingsNavigation.NoResultsMessageProperty && !Navigation.HasResults)
        {
            // Typing on past one dead query into another is still a change worth showing.
            // Driven off the MESSAGE rather than off the query, because the message is set
            // after the filter has run - reacting to the keystroke itself would repaint the
            // pane with the previous query's answer, which is the bug this replaced: the
            // list said "zaznaczxxx" while the pane beside it still said "domena".
            ShowNoResults();
        }
    }

    /// <summary>
    /// Say that the query found nothing, instead of leaving the pane blank.
    /// </summary>
    /// <remarks>
    /// The whole window used to go quiet here: an empty section list on the left, and on
    /// the right the heading of whatever page had been open before, over nothing. Two
    /// different queries produced pixel-identical screenshots of a program that looks
    /// broken, and the only way out was to guess that the search field had to be emptied.
    ///
    /// The suggestion is a prefix match rather than anything cleverer, because that is
    /// the case worth answering: somebody typing a name they half-remember, or stopping
    /// one letter into a word that does not end there.
    /// </remarks>
    private void ShowNoResults()
    {
        string query = Navigation.SearchText ?? string.Empty;

        HeaderIcon.Text = string.Empty;
        HeaderTitle.Text = string.Empty;
        HeaderGroup.Text = string.Empty;
        HeaderGroup.IsVisible = false;
        HeaderDescription.Text = string.Empty;
        HeaderDescription.IsVisible = false;

        foreach (Control view in _hosts.Values)
        {
            view.IsVisible = false;
        }

        NoResultsTitle.Text = string.Format(Strings.SettingsWindow_NoResultsTitle, query);
        NoResultsHint.Text = Strings.SettingsWindow_NoResultsHint;

        SettingsNavigationItem? nearest = Nearest(query);
        _nearest = nearest;
        NoResultsNearest.Content = nearest == null
            ? string.Empty
            : string.Format(Strings.SettingsWindow_NoResultsNearest, nearest.Title);
        NoResultsNearest.IsVisible = nearest != null;
        NoResults.IsVisible = true;
    }

    /// <summary>The section whose name shares the longest opening with the query.</summary>
    private SettingsNavigationItem? Nearest(string query)
    {
        string word = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        if (word.Length < 3)
        {
            return null;
        }

        SettingsNavigationItem? best = null;
        int bestShared = 0;

        foreach (SettingsNavigationItem item in Flatten(_items).Where(x => x.Children.Count == 0))
        {
            int shared = 0;

            while (shared < word.Length && shared < item.Title.Length &&
                char.ToLowerInvariant(word[shared]) == char.ToLowerInvariant(item.Title[shared]))
            {
                shared++;
            }

            if (shared >= 3 && shared > bestShared)
            {
                best = item;
                bestShared = shared;
            }
        }

        return best;
    }

    private void OnClearSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Navigation.SearchText = string.Empty;

    private void OnNearestClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SettingsNavigationItem? nearest = _nearest;
        Navigation.SearchText = string.Empty;

        if (nearest != null)
        {
            Navigation.SelectedItem = nearest;
        }
    }

    private void Select(SettingsNavigationItem? item)
    {
        if (_redirecting)
        {
            return;
        }

        // Nothing selected is a state the window can genuinely be in - a query that
        // matched nothing leaves it there - and returning here used to leave the header
        // describing a page that was no longer on screen.
        if (item == null)
        {
            if (!Navigation.HasResults)
            {
                ShowNoResults();
            }

            return;
        }

        // A group heading is not a page. CLICKING one lands on its first visible section,
        // because a heading answers nothing; arrowing onto one does not, because the
        // redirect is a wall - Up from the first child selects the heading and is thrown
        // straight back down, and with one group open at a time everything above the open
        // group becomes unreachable. Selecting a group with the keyboard just leaves the
        // current page up, and Left/Right open and close it like any other tree.
        if (item.Children.Count > 0)
        {
            if (!Navigation.SelectionFromPointer)
            {
                return;
            }

            SettingsNavigationItem? child = item.Children.FirstOrDefault(x => x.IsVisible) ?? item.Children[0];
            _redirecting = true;
            Navigation.SelectedItem = child;
            _redirecting = false;
            Select(child);
            return;
        }

        if (!_sections.TryGetValue(item.Id, out SettingsSection? section))
        {
            return;
        }

        NoResults.IsVisible = false;
        ShowHeader(section);

        foreach ((SettingsHost host, Control view) in _hosts)
        {
            view.IsVisible = host == section.Host;
        }

        switch (section.Host)
        {
            case SettingsHost.Account:
                ((AccountSettingsView)_hosts[SettingsHost.Account]).SelectPage(section.PageId);
                break;
            case SettingsHost.Application:
                ((ApplicationSettingsView)_hosts[SettingsHost.Application]).SelectPage(section.PageId);
                break;
            case SettingsHost.Task:
                ((TaskSettingsView)_hosts[SettingsHost.Task]).SelectPage(section.PageId);
                break;
        }
    }

    private void OnThemeChanged(object? sender, Avalonia.Styling.ThemeVariant theme) =>
        Dispatcher.UIThread.Post(() => RequestedThemeVariant = theme);

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        Navigation.PropertyChanged -= OnNavigationPropertyChanged;

        foreach (Control view in _hosts.Values)
        {
            switch (view)
            {
                case ApplicationSettingsView application:
                    application.Release();
                    break;
                case HotkeySettingsView hotkeys:
                    hotkeys.Release();
                    break;
                case AccountSettingsView account:
                    account.Release();
                    break;
            }
        }

        _closed?.Invoke();
    }

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
