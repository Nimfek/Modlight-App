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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ShareX.AvaloniaUI.Controls;

public sealed class SettingsNavigationItem : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private bool _isExpanded;
    private string _searchText;

    public string Id { get; }
    public string Title { get; }
    public string Icon { get; }
    public bool HasIcon => !string.IsNullOrEmpty(Icon);

    /// <summary>
    /// Words that must find this entry even though its title does not contain them - the
    /// sentence shown under the page heading, which carries the vocabulary somebody
    /// coming from ShareX would type.
    /// </summary>
    /// <remarks>
    /// Kept as its own property rather than folded once into <see cref="SearchText"/>,
    /// because the index is rebuilt from the page's own controls on every keystroke
    /// (<see cref="SettingsSearch.Apply"/>) and anything merged in at construction time
    /// would be overwritten the first time somebody typed. The panel filter reads it too:
    /// an entry that survives the query while every panel on its page is hidden is a
    /// search result that opens an empty page.
    /// </remarks>
    public string Keywords { get; }

    /// <summary>
    /// Words that find this entry and are never shown - ShareX's own names for the thing.
    /// </summary>
    /// <remarks>
    /// The section sentences were written to carry a returning user's vocabulary, and
    /// that half worked: it carries it in ENGLISH, in Strings.resx. On a Polish install
    /// the sentences are Polish, so "hotkey", "clipboard" and "effects" returned nothing
    /// at all - the only old ShareX words that still worked were the ones that happened
    /// to be left untranslated on a page, which is an accident, not a design.
    ///
    /// Hidden rather than appended to the sentence in brackets, because these words are
    /// not for the person reading the page - they are for the person who already knows
    /// what they want and types the name they learned somewhere else.
    /// </remarks>
    public string Aliases { get; }

    /// <summary>
    /// What hovering a row says: its name, and the sentence the page header shows.
    /// </summary>
    /// <remarks>
    /// Two jobs in one string. On a narrow window the pane is 220px and the longest
    /// titles are trimmed with an ellipsis, so the name has to be readable some other
    /// way; and on any window, a one-line answer to "what is behind this row" is worth
    /// more than the row's name repeated.
    /// </remarks>
    public string Tooltip => string.IsNullOrWhiteSpace(Keywords) ? Title : Title + "\n" + Keywords;

    public string SearchText
    {
        get => _searchText;
        private set => SetField(ref _searchText, value);
    }
    public ObservableCollection<SettingsNavigationItem> Children { get; } = [];

    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetField(ref _isVisible, value);
    }

    /// <summary>Whether this group's sections are showing. Groups start closed.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public SettingsNavigationItem(
        string id,
        string title,
        string icon = "",
        string? keywords = null,
        IEnumerable<SettingsNavigationItem>? children = null,
        string? aliases = null)
    {
        Id = id;
        Title = title;
        Icon = icon;
        Keywords = keywords ?? string.Empty;
        Aliases = aliases ?? string.Empty;
        _searchText = string.Join(' ', title, Keywords, Aliases);

        if (children != null)
        {
            foreach (SettingsNavigationItem child in children)
            {
                Children.Add(child);
            }
        }
    }

    public void UpdateSearchText(string? searchText)
    {
        SearchText = string.Join(' ', Title, Keywords, Aliases, searchText ?? string.Empty);
    }

    internal bool ApplyFilter(string query)
    {
        query ??= string.Empty;

        bool childMatches = false;

        foreach (SettingsNavigationItem child in Children)
        {
            childMatches |= child.ApplyFilter(query);
        }

        // The same test the panels are filtered with, called rather than copied: two
        // implementations of "does this match" is how a query ends up keeping a row in
        // the list whose page it then empties.
        bool selfMatches = SettingsSearch.Matches(SearchText, query);

        IsVisible = selfMatches || childMatches;

        if (!string.IsNullOrWhiteSpace(query) && childMatches)
        {
            IsExpanded = true;
        }

        return IsVisible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
