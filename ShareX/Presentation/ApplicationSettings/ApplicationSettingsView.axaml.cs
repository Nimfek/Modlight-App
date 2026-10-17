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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ShareX;

/// <summary>The application half of the one settings window, as a page rather than a dialog.</summary>
/// <remarks>
/// A <see cref="UserControl"/> and not a <see cref="Window"/> because these pages are now
/// sections of <see cref="SettingsWindow"/> next to the task, shortcut and account sections,
/// which is the whole point of the consolidation: the person looking for a setting picks it
/// out of one list instead of guessing which of three dialogs ShareX filed it under.
/// The section list lives in the shell, so this control no longer carries one.
/// </remarks>
public partial class ApplicationSettingsView : UserControl
{
    private ApplicationSettingsViewModel ViewModel => (ApplicationSettingsViewModel)DataContext!;

    /// <summary>The pickers need a top level; a page has one only once it is in a window.</summary>
    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;
    private ClipboardFormatItem? _editedClipboardFormat;

    private static readonly string[] ClipboardResultTokens =
    [
        "$result", "$url", "$shorturl", "$thumbnailurl", "$deletionurl", "$filepath", "$filename",
        "$filenamenoext", "$thumbnailfilename", "$thumbnailfilenamenoext", "$folderpath", "$foldername", "$uploadtime"
    ];

    public ApplicationSettingsView()
    {
        InitializeComponent();
        DataContext = new ApplicationSettingsViewModel();
        AttachClipboardFormatMenu();
        ClipboardFormatSupportedVariablesText.Text = string.Format(
            Strings.ClipboardFormatForm_ClipboardFormatForm_Supported_variables___0__and_other_variables_such_as__1__etc_,
            string.Join(", ", ClipboardResultTokens),
            "%y, %mo, %d");
        KeyDown += OnWindowKeyDown;
    }

    /// <summary>Called by the shell when the settings window closes.</summary>
    public void Release() => ViewModel.Dispose();

    /// <summary>Show one page and hide the rest. The shell's section list decides which.</summary>
    public void SelectPage(string pageId) => ViewModel.SelectPage(pageId);

    private void OnRestartClick(object? sender, RoutedEventArgs e) => ViewModel.Restart();
    private void OnEditQuickTaskMenuClick(object? sender, RoutedEventArgs e) => ViewModel.EditQuickTaskMenu();
    private void OnOpenPersonalFolderClick(object? sender, RoutedEventArgs e) => ViewModel.OpenPersonalFolder();
    private void OnOpenScreenshotsFolderClick(object? sender, RoutedEventArgs e) => ViewModel.OpenScreenshotsFolder();
    private void OnResetThumbnailSizeClick(object? sender, RoutedEventArgs e) => ViewModel.ResetThumbnailSize();
    private void OnAddClipboardFormatClick(object? sender, RoutedEventArgs e) => ShowClipboardFormatEditor(null);
    private void OnEditClipboardFormatClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedClipboardFormat is { } format)
        {
            ShowClipboardFormatEditor(format);
        }
    }
    private void OnRemoveClipboardFormatClick(object? sender, RoutedEventArgs e) => ViewModel.RemoveSelectedClipboardFormat();
    private void OnImagePrintSettingsClick(object? sender, RoutedEventArgs e) => ViewModel.ShowImagePrintSettings((Window)TopLevel.GetTopLevel(this)!);
    private async void OnResetSettingsClick(object? sender, RoutedEventArgs e) => await ViewModel.ResetAsync();

    private async void OnBrowsePersonalFolderClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync(Strings.ApplicationSettingsWindow_ChooseShareXPersonalFolderPath);
        if (!string.IsNullOrEmpty(path))
        {
            ViewModel.PersonalFolderPath = path;
        }
    }

    private async void OnBrowseScreenshotsFolderClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync(Strings.ApplicationSettingsWindow_ChooseScreenshotsFolderPath);
        if (!string.IsNullOrEmpty(path))
        {
            ViewModel.CustomScreenshotsPath = path;
        }
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        string machineName = FileHelpers.SanitizeFileName(Environment.MachineName.ToLowerInvariant());
        IStorageFile? file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.ApplicationSettingsWindow_ExportShareXBackup,
            SuggestedFileName = $"Modlight-{Helpers.GetApplicationVersion()}-{machineName}-backup.sxb",
            DefaultExtension = "sxb",
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.ApplicationSettingsWindow_ShareXBackup) { Patterns = ["*.sxb"] },
                FilePickerFileTypes.All
            ]
        });

        string? path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            await ViewModel.ExportAsync(path);
        }
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.ApplicationSettingsWindow_ImportShareXBackup,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Strings.ApplicationSettingsWindow_ShareXBackup) { Patterns = ["*.sxb"] }]
        });

        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            await ViewModel.ImportAsync(path);
        }
    }

    private void ShowClipboardFormatEditor(ClipboardFormatItem? format)
    {
        _editedClipboardFormat = format;
        ClipboardFormatEditorTitle.Text = format == null
            ? Strings.ApplicationSettingsWindow_AddClipboardFormat
            : Strings.ApplicationSettingsWindow_EditClipboardFormat;
        ClipboardFormatDescriptionBox.Text = format?.Description ?? string.Empty;
        ClipboardFormatTextBox.Text = format?.Format ?? string.Empty;
        ClipboardFormatEditorOverlay.IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            ClipboardFormatDescriptionBox.Focus();
            ClipboardFormatDescriptionBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void HideClipboardFormatEditor()
    {
        ClipboardFormatEditorOverlay.IsVisible = false;
        _editedClipboardFormat = null;
    }

    private void OnSaveClipboardFormatClick(object? sender, RoutedEventArgs e)
    {
        string description = ClipboardFormatDescriptionBox.Text ?? string.Empty;
        string format = ClipboardFormatTextBox.Text ?? string.Empty;

        if (_editedClipboardFormat == null)
        {
            ViewModel.AddClipboardFormat(description, format);
        }
        else
        {
            _editedClipboardFormat.Description = description;
            _editedClipboardFormat.Format = format;
        }

        HideClipboardFormatEditor();
    }

    private void OnCancelClipboardFormatClick(object? sender, RoutedEventArgs e) => HideClipboardFormatEditor();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ClipboardFormatEditorOverlay.IsVisible)
        {
            HideClipboardFormatEditor();
            e.Handled = true;
        }
    }

    private void AttachClipboardFormatMenu()
    {
        List<MenuItem> menuItems = [];
        menuItems.Add(new MenuItem
        {
            Header = Strings.ApplicationSettingsWindow_UploadResult,
            ItemsSource = ClipboardResultTokens.Select(CreateClipboardTokenItem).ToList()
        });

        IEnumerable<CodeMenuEntryFilename> filenameEntries = typeof(CodeMenuEntryFilename)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(CodeMenuEntryFilename))
            .Select(field => field.GetValue(null))
            .OfType<CodeMenuEntryFilename>();

        foreach (IGrouping<string?, CodeMenuEntryFilename> group in filenameEntries.GroupBy(entry => entry.Category))
        {
            List<MenuItem> items = group.Select(entry => CreateClipboardTokenItem(entry.ToPrefixString(), entry.Description)).ToList();
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                menuItems.AddRange(items);
            }
            else
            {
                menuItems.Add(new MenuItem { Header = group.Key, ItemsSource = items });
            }
        }

        ClipboardFormatTextBox.ContextMenu = new ContextMenu { ItemsSource = menuItems };
    }

    private MenuItem CreateClipboardTokenItem(string token) => CreateClipboardTokenItem(token, null);

    private MenuItem CreateClipboardTokenItem(string token, string? description)
    {
        MenuItem item = new() { Header = token };
        if (!string.IsNullOrEmpty(description))
        {
            ToolTip.SetTip(item, description);
        }
        item.Click += (_, _) => InsertClipboardToken(token);
        return item;
    }

    private void InsertClipboardToken(string token)
    {
        string text = ClipboardFormatTextBox.Text ?? string.Empty;
        int start = Math.Clamp(Math.Min(ClipboardFormatTextBox.SelectionStart, ClipboardFormatTextBox.SelectionEnd), 0, text.Length);
        int end = Math.Clamp(Math.Max(ClipboardFormatTextBox.SelectionStart, ClipboardFormatTextBox.SelectionEnd), start, text.Length);
        ClipboardFormatTextBox.Text = text.Remove(start, end - start).Insert(start, token);
        ClipboardFormatTextBox.CaretIndex = start + token.Length;
        ClipboardFormatTextBox.Focus();
    }
}
