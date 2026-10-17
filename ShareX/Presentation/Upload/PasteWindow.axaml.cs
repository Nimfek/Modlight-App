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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using System;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// Writing a paste: the text, the language it is written in, and the button that sends it.
/// </summary>
/// <remarks>
/// The web panel's <c>/dashboard/paste</c>, in a window. What it does with what it
/// collects - and the one thing it cannot do, because the panel creates pastes through a
/// Server Action that no HTTP client can call - is set out on
/// <see cref="PasteViewModel"/>.
///
/// Two keyboard decisions worth stating, because both are easy to get wrong in a window
/// whose middle is one large text box:
///
/// <b>Tab indents, and never traps.</b> <c>AcceptsTab</c> is false, so Avalonia does not
/// swallow the key; a bare Tab is handled here and inserts one, while Shift+Tab and
/// Ctrl+Tab are left alone and move focus the way they do everywhere else. The
/// alternative - letting the box keep the key - leaves somebody who is not using a mouse
/// with no way out of it, and a modal window that cannot be left by keyboard is a modal
/// window that cannot be dismissed.
///
/// <b>Enter is a newline, so Ctrl+Enter sends.</b> The Upload button is deliberately NOT
/// <c>IsDefault</c>: in a window built around a code editor, an Enter that submits is an
/// Enter that throws away the line somebody was in the middle of writing.
/// </remarks>
public partial class PasteWindow : Window
{
    public PasteWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        DataContext = Model;

        // The same behaviour ShareX's own text-upload window has always had, and the
        // reason the owner asked for this at all is that the text is usually already on
        // the clipboard. It is selected on open, so the first keystroke replaces it - the
        // prefill helps the common case without standing in the way of the other one.
        if (ClipboardHelpers.ContainsText())
        {
            string clipboard = ClipboardHelpers.GetText();

            if (!string.IsNullOrEmpty(clipboard))
            {
                Model.Content = clipboard;
                PrefilledFromClipboard = true;
            }
        }

        Opened += OnOpened;
    }

    internal PasteViewModel Model { get; } = new();

    private bool PrefilledFromClipboard { get; }

    /// <summary>Open the window over <paramref name="owner"/> and wait for it to close.</summary>
    public static async Task ShowAsync(Window owner)
    {
        PasteWindow window = new();
        await window.ShowDialog(owner);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Editor.Focus();

        if (PrefilledFromClipboard)
        {
            Editor.SelectAll();
        }
    }

    private void OnSendClick(object? sender, RoutedEventArgs e) => Send();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Hand the paste to the upload pipeline, or stay open and say what is wrong.
    /// </summary>
    /// <remarks>
    /// Closing on success rather than reporting here: from this point a paste is an
    /// upload like any other, and it is reported the way all of them are - the strip at
    /// the bottom of the main window while it goes, the toast when it lands, the link on
    /// the clipboard, the row in this tab's history. A second report of its own would
    /// make it the only upload in the app that announced itself twice.
    /// </remarks>
    private void Send()
    {
        if (Model.TrySend())
        {
            Close();
        }
        else
        {
            Editor.Focus();
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Send();
            e.Handled = true;
            return;
        }

        // A BARE Tab only. Shift+Tab and Ctrl+Tab fall through to Avalonia and move
        // focus, which is what keeps this box escapable from the keyboard.
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None)
        {
            InsertTab();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Put a tab character where the caret is, replacing whatever was selected.
    /// </summary>
    /// <remarks>
    /// A real tab and not a run of spaces: this is a code box, the file goes up byte for
    /// byte, and a Makefile indented with spaces is a broken file rather than a
    /// differently styled one.
    /// </remarks>
    private void InsertTab()
    {
        string text = Editor.Text ?? string.Empty;
        int start = Math.Clamp(Math.Min(Editor.SelectionStart, Editor.SelectionEnd), 0, text.Length);
        int end = Math.Clamp(Math.Max(Editor.SelectionStart, Editor.SelectionEnd), start, text.Length);
        Editor.Text = text.Remove(start, end - start).Insert(start, "\t");
        Editor.CaretIndex = start + 1;
    }

    private void OnLanguageButtonClick(object? sender, RoutedEventArgs e)
    {
        if (LanguagePopup.IsOpen)
        {
            LanguagePopup.IsOpen = false;
            return;
        }

        // Opens showing everything, whatever was searched for last time: the list is a
        // menu, and a menu that remembers a filter is a menu with items missing for
        // reasons nobody can see.
        LanguageSearch.Text = string.Empty;
        HighlightCurrentLanguage();
        LanguagePopup.IsOpen = true;

        // After the popup has a visual tree of its own; focusing before it is up does
        // nothing and the caret lands nowhere.
        Dispatcher.UIThread.Post(() => LanguageSearch.Focus());
    }

    private void OnLanguageSearchChanged(object? sender, TextChangedEventArgs e)
    {
        Model.LanguageFilter = LanguageSearch.Text ?? string.Empty;
        HighlightCurrentLanguage();
    }

    private void OnLanguageSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Model.Languages.Count > 0)
        {
            if (LanguageList.SelectedIndex < 0)
            {
                LanguageList.SelectedIndex = 0;
            }

            LanguageList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Model.Languages.Count > 0)
        {
            // The first match, which is the one the search box is visibly pointing at.
            Model.Language = Model.Languages[0];
            CloseLanguagePopup();
            e.Handled = true;
        }
    }

    /// <summary>
    /// A click on the list picks what the click highlighted.
    /// </summary>
    /// <remarks>
    /// Tapped rather than SelectionChanged, and the difference matters: selection also
    /// moves under the arrow keys, so committing on it would mean a keyboard user could
    /// not look down the list without the window deciding for them at the first step.
    /// Here the arrows browse, and Enter or a click chooses.
    /// </remarks>
    private void OnLanguageListTapped(object? sender, TappedEventArgs e) => CommitHighlightedLanguage();

    private void OnLanguageListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHighlightedLanguage();
            e.Handled = true;
        }
    }

    private void OnPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Handled, so the window's own Cancel button does not read the same press as
            // "close the whole thing". Escape from a menu closes the menu.
            CloseLanguagePopup();
            e.Handled = true;
        }
    }

    private void CommitHighlightedLanguage()
    {
        if (LanguageList.SelectedItem is PasteLanguage picked)
        {
            Model.Language = picked;
        }

        CloseLanguagePopup();
    }

    private void CloseLanguagePopup()
    {
        LanguagePopup.IsOpen = false;
        Editor.Focus();
    }

    /// <summary>
    /// Put the highlight back on the chosen language after the list has been rebuilt.
    /// </summary>
    /// <remarks>
    /// The search box refills the collection on every keystroke, and refilling a ListBox
    /// clears its selection - so without this the highlight would fall off the chosen
    /// language at the first letter typed. Null when the search has filtered that
    /// language out, which is honest: it is not one of the things on offer right now.
    /// </remarks>
    private void HighlightCurrentLanguage() =>
        LanguageList.SelectedItem = Model.Languages.Contains(Model.Language) ? Model.Language : null;
}
