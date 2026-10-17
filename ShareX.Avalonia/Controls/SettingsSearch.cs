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
using Avalonia.LogicalTree;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace ShareX.AvaloniaUI.Controls;

public sealed class SettingsSearch : AvaloniaObject
{
    public static readonly AttachedProperty<string?> PageIdProperty =
        AvaloniaProperty.RegisterAttached<SettingsSearch, Control, string?>("PageId");

    public static readonly AttachedProperty<bool> IsPageTitleProperty =
        AvaloniaProperty.RegisterAttached<SettingsSearch, Control, bool>("IsPageTitle");

    public static readonly AttachedProperty<bool> IsPanelProperty =
        AvaloniaProperty.RegisterAttached<SettingsSearch, Control, bool>("IsPanel");

    public static readonly AttachedProperty<bool> IsAvailabilityContainerProperty =
        AvaloniaProperty.RegisterAttached<SettingsSearch, Control, bool>("IsAvailabilityContainer");

    /// <summary>
    /// Set to false on a list whose CONTENTS should not be searchable.
    /// </summary>
    /// <remarks>
    /// One list in this window enumerates every capture task there is, and three more
    /// pick a task from the same enumeration. Indexing them made the two pages holding
    /// them answer nearly every query: "screen" returned sixteen of the twenty sections,
    /// "folder" thirteen, and "OCR" put "Language and tray icon" in the results. A result
    /// covering most of the list is not a result.
    ///
    /// It excludes the ITEMS, never the labels around them - the page still answers for
    /// its own headings, its checkboxes and its section sentence, which is what somebody
    /// searching for that page types. Inherited by descendants, so one flag covers a list
    /// and whatever it draws inside itself.
    /// </remarks>
    public static readonly AttachedProperty<bool> IsListIndexedProperty =
        AvaloniaProperty.RegisterAttached<SettingsSearch, Control, bool>("IsListIndexed", true);

    private static readonly string[] SearchablePropertyNames = ["Text", "Content", "Header", "Watermark"];
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> SearchableProperties = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> SearchableItemProperties = new();

    private SettingsSearch()
    {
    }

    public static string? GetPageId(Control control) => control.GetValue(PageIdProperty);
    public static void SetPageId(Control control, string? value) => control.SetValue(PageIdProperty, value);

    public static bool GetIsPageTitle(Control control) => control.GetValue(IsPageTitleProperty);
    public static void SetIsPageTitle(Control control, bool value) => control.SetValue(IsPageTitleProperty, value);

    public static bool GetIsPanel(Control control) => control.GetValue(IsPanelProperty);
    public static void SetIsPanel(Control control, bool value) => control.SetValue(IsPanelProperty, value);

    public static bool GetIsAvailabilityContainer(Control control) => control.GetValue(IsAvailabilityContainerProperty);
    public static void SetIsAvailabilityContainer(Control control, bool value) => control.SetValue(IsAvailabilityContainerProperty, value);

    public static bool GetIsListIndexed(Control control) => control.GetValue(IsListIndexedProperty);
    public static void SetIsListIndexed(Control control, bool value) => control.SetValue(IsListIndexedProperty, value);

    internal static void Apply(Control root, IEnumerable<SettingsNavigationItem> navigationItems, string? query)
    {
        query ??= string.Empty;
        SettingsNavigationItem[] flattenedItems = Flatten(navigationItems).ToArray();

        foreach (Control page in root.GetLogicalDescendants().OfType<Control>().Where(x => !string.IsNullOrEmpty(GetPageId(x))))
        {
            string pageId = GetPageId(page)!;
            SettingsNavigationItem? navigationItem = flattenedItems.FirstOrDefault(x => x.Id == pageId);
            if (navigationItem == null)
            {
                continue;
            }

            string pageTitle = page.GetLogicalDescendants()
                .OfType<Control>()
                .FirstOrDefault(GetIsPageTitle) is { } titleControl
                ? GetDisplayedSearchText(titleControl)
                : navigationItem.Title;

            Control[] panels = page.GetLogicalDescendants()
                .OfType<Control>()
                .Where(GetIsPanel)
                .Where(x => !HasPanelAncestor(x, page))
                .ToArray();

            List<string> pageSearchText = [pageTitle, GetItemsSourceSearchText(page)];

            foreach (Control panel in panels)
            {
                bool isAvailable = IsPanelAvailable(panel, page);
                string panelSearchText = GetDisplayedSearchText(panel);

                if (isAvailable)
                {
                    pageSearchText.Add(panelSearchText);
                }

                // Everything the LIST entry answers to, so that a query which finds the
                // entry also finds something on its page. Three separate things, and each
                // was a bug on its own before it was here:
                //
                // navigationItem.Title is the name on the row. The pages kept the titles
                // their old dialogs gave them and the list renames them, so typing the
                // name you can SEE - "Zaznaczanie obszaru" for a page whose own hidden
                // title still says "Przechwytywanie regionu" - left the row selected and
                // the page blank. Renaming the pages was the point of the redesign, which
                // made this miss most of the window.
                //
                // Keywords is the sentence under the heading, carrying the vocabulary
                // somebody coming from ShareX types; Aliases carries the English ShareX
                // terms themselves, in any UI language.
                panel.IsVisible = isAvailable &&
                    Matches(
                        string.Join(
                            ' ',
                            pageTitle,
                            navigationItem.Title,
                            navigationItem.Keywords,
                            navigationItem.Aliases,
                            panelSearchText),
                        query);
            }

            navigationItem.UpdateSearchText(string.Join(' ', pageSearchText));
        }
    }

    private static bool HasPanelAncestor(Control panel, Control page)
    {
        ILogical? ancestor = panel.GetLogicalParent();

        while (ancestor != null && ancestor != page)
        {
            if (ancestor is Control control && GetIsPanel(control))
            {
                return true;
            }

            ancestor = ancestor.GetLogicalParent();
        }

        return false;
    }

    private static bool IsPanelAvailable(Control panel, Control page)
    {
        ILogical? ancestor = panel.GetLogicalParent();

        while (ancestor != null && ancestor != page)
        {
            if (ancestor is Control control && GetIsAvailabilityContainer(control) && !control.IsVisible)
            {
                return false;
            }

            ancestor = ancestor.GetLogicalParent();
        }

        return true;
    }

    private static string GetDisplayedSearchText(Control root)
    {
        List<string> values = [];
        AddDisplayedText(root, values);

        if (IsListIndexed(root, root))
        {
            AddItemsSourceText(root, values);
        }

        foreach (Control control in root.GetLogicalDescendants().OfType<Control>().Where(x => IsSearchAvailable(x, root)))
        {
            AddDisplayedText(control, values);

            if (IsListIndexed(control, root))
            {
                AddItemsSourceText(control, values);
            }
        }

        return string.Join(' ', values);
    }

    private static void AddDisplayedText(Control control, List<string> values)
    {
        PropertyInfo[] properties = SearchableProperties.GetOrAdd(control.GetType(), static type => SearchablePropertyNames
            .Select(name => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public))
            .Where(property => property is { CanRead: true } && property.GetIndexParameters().Length == 0)
            .Cast<PropertyInfo>()
            .ToArray());

        foreach (PropertyInfo property in properties)
        {
            if (control is TextBox && property.Name == "Text")
            {
                continue;
            }

            try
            {
                if (property.GetValue(control) is string text && !string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text);
                }
            }
            catch (TargetInvocationException)
            {
                // A custom control property should not be able to break settings search.
            }
        }
    }

    private static string GetItemsSourceSearchText(Control root)
    {
        List<string> values = [];

        if (IsListIndexed(root, root))
        {
            AddItemsSourceText(root, values);
        }

        foreach (ItemsControl itemsControl in root.GetLogicalDescendants().OfType<ItemsControl>()
            .Where(x => IsSearchAvailable(x, root) && IsListIndexed(x, root)))
        {
            AddItemsSourceText(itemsControl, values);
        }

        return string.Join(' ', values);
    }

    /// <summary>Whether this control's items may go into the index - see IsListIndexed.</summary>
    private static bool IsListIndexed(Control control, Control root)
    {
        ILogical? current = control;

        while (current != null)
        {
            if (current is Control candidate && !GetIsListIndexed(candidate))
            {
                return false;
            }

            if (current == root)
            {
                return true;
            }

            current = current.GetLogicalParent();
        }

        return true;
    }

    private static bool IsSearchAvailable(Control control, Control root)
    {
        ILogical? current = control;

        while (current != null && current != root)
        {
            if (current is Control candidate && GetIsAvailabilityContainer(candidate) && !candidate.IsVisible)
            {
                return false;
            }

            current = current.GetLogicalParent();
        }

        return true;
    }

    private static void AddItemsSourceText(Control control, List<string> values)
    {
        if (control is ItemsControl { ItemsSource: IEnumerable items })
        {
            AddSearchableItemText(items, values, 0);
        }
    }

    private static void AddSearchableItemText(object? value, List<string> values, int depth)
    {
        if (value == null || depth > 4)
        {
            return;
        }

        if (value is string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }

            return;
        }

        if (value is IEnumerable items)
        {
            foreach (object? item in items)
            {
                AddSearchableItemText(item, values, depth + 1);
            }

            return;
        }

        PropertyInfo[] properties = SearchableItemProperties.GetOrAdd(value.GetType(), static type => type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0 &&
                (property.PropertyType == typeof(string) || typeof(IEnumerable).IsAssignableFrom(property.PropertyType)))
            .ToArray());

        foreach (PropertyInfo property in properties)
        {
            try
            {
                AddSearchableItemText(property.GetValue(value), values, depth + 1);
            }
            catch (TargetInvocationException)
            {
                // A custom item property should not be able to break settings search.
            }
        }
    }

    /// <summary>
    /// Whether every word of the query is in the text, allowing for Polish inflection.
    /// </summary>
    /// <remarks>
    /// A raw substring test is what this used to be, and in an inflected language it is
    /// a trap: "nazwa" did not find the page literally called "Nazwy plikow" - the two
    /// words share a stem and differ in the last letter, and Contains does not care that
    /// they are the same word. Only "nazw" worked, which is not something a person types.
    ///
    /// So: substring first, because that is the cheap case and the one that keeps
    /// mid-word matches like "kran" finding "ekran" working; then a stem comparison,
    /// word by word. The stem is the crude one on purpose - drop the last two letters of
    /// a long word, the last one of a medium word - because a real Polish stemmer is a
    /// dictionary, and the failure mode of guessing here is one extra row in a list, not
    /// a wrong answer. Four characters is the floor for the comparison: below that a
    /// stem is a syllable and would match half the window.
    ///
    /// Widening a search never hides anything, which is why this can be layered on top
    /// of the old test rather than replacing it.
    /// </remarks>
    internal static bool Matches(string searchText, string query)
    {
        string[] terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (terms.Length == 0)
        {
            return true;
        }

        string[]? stems = null;

        foreach (string term in terms)
        {
            if (searchText.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            stems ??= Words(searchText).Select(Stem).ToArray();
            string stem = Stem(term);

            if (!stems.Any(word => SameStem(word, stem)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameStem(string word, string term) =>
        Math.Min(word.Length, term.Length) >= 4 &&
        (word.StartsWith(term, StringComparison.CurrentCultureIgnoreCase) ||
            term.StartsWith(word, StringComparison.CurrentCultureIgnoreCase));

    private static string Stem(string word) => word.Length >= 6
        ? word[..^2]
        : word.Length >= 5 ? word[..^1] : word;

    private static IEnumerable<string> Words(string text)
    {
        int start = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool letter = i < text.Length && char.IsLetterOrDigit(text[i]);

            if (letter && start < 0)
            {
                start = i;
            }
            else if (!letter && start >= 0)
            {
                yield return text[start..i];
                start = -1;
            }
        }
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
