#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using ShareX.Localization;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The "where do uploads made with these settings land" control on the Upload page.
/// </summary>
/// <remarks>
/// Its own file rather than another two hundred lines inside the page builder, because
/// unlike every other control there this one talks to the server: the list of folders is
/// the account's, not a fixed set of enum values, and it can fail to arrive.
///
/// Three rules it exists to keep:
///
///   The list never has to load for the setting to work. What is stored is the id; the
///   name is a cached label. Offline, signed out, or with the server down, the picker
///   still shows the folder that is set, by name, and changes nothing.
///
///   A folder that is no longer there is SHOWN as missing rather than quietly dropped.
///   Deleting a folder in the panel cannot reach into a config file on somebody's PC, so
///   an id can outlive its folder; uploads still succeed (the server files them in the
///   account default and says so in folder_warning), and this is where the owner finds
///   out why their captures stopped arriving where they expected.
///
///   The first entry is the ambient target - the folder My space is standing in - so
///   picking any other entry is visibly picking NOT to follow the window.
/// </remarks>
internal static class ModlightFolderPicker
{
    /// <summary>How deep a parent chain may be walked before it is assumed to be a loop.</summary>
    private const int MaxDepth = 16;

    /// <summary>One line in the drop-down.</summary>
    private sealed class Choice
    {
        public Choice(string id, string label)
        {
            Id = id;
            Label = label;
        }

        /// <summary>What gets stored: "" for the ambient target, the root sentinel, or a folder id.</summary>
        public string Id { get; }

        public string Label { get; }

        /// <summary>The name on its own, for the cached label. Empty for the two fixed entries.</summary>
        public string FolderName { get; init; } = "";

        public override string ToString() => Label;
    }

    public static Control Create(TaskSettingsUpload upload)
    {
        ComboBox combo = new();
        combo.Classes.Add("form-control");

        TextBlock state = new();
        state.Classes.Add("hint");

        // What the server calls "no folder". Replaced by the value the folder list hands
        // out when it arrives - the endpoint publishes its own sentinel precisely so a
        // client cannot drift from it - and this constant is only the fallback for a
        // deployment too old to send one.
        string rootValue = ModlightLibrary.RootFolder;

        // Set while the items are being rebuilt: assigning ItemsSource clears the
        // selection, and the handler must not read that as the user choosing the first
        // entry and write "" over their folder.
        bool repopulating = false;

        void Apply(IReadOnlyList<Choice> choices, string selectedId)
        {
            repopulating = true;
            combo.ItemsSource = choices;
            combo.SelectedItem = choices.FirstOrDefault(x => x.Id == selectedId) ?? choices[0];
            repopulating = false;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (repopulating || combo.SelectedItem is not Choice choice)
            {
                return;
            }

            upload.ModlightFolderId = choice.Id;
            upload.ModlightFolderName = choice.FolderName;
        };

        Apply(Offline(upload, rootValue), upload.ModlightFolderId ?? "");

        Button refresh = new() { Content = Strings.TaskSettingsWindow_ModlightFolderRefresh };
        refresh.Classes.Add("compact");

        async Task LoadAsync()
        {
            ModlightAccount? account = ModlightSignIn.Current;

            if (account == null)
            {
                state.Text = Strings.TaskSettingsWindow_ModlightFolderSignedOut;
                return;
            }

            refresh.IsEnabled = false;
            state.Text = Strings.TaskSettingsWindow_ModlightFolderLoading;

            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
                ModlightFolderList list = await ModlightLibrary.GetFoldersAsync(account.Host, account.Token, timeout.Token);

                string selected = upload.ModlightFolderId ?? "";

                if (!string.IsNullOrEmpty(list.RootValue))
                {
                    rootValue = list.RootValue!;
                }

                List<Choice> choices = Build(list, rootValue, selected, upload.ModlightFolderName);
                Apply(choices, selected);

                Choice? current = choices.FirstOrDefault(x => x.Id == selected);

                // The cached label is refreshed from the list every time it loads, so
                // renaming a folder in the panel shows up here rather than leaving the
                // picker naming a folder by a name it no longer has.
                if (current != null && !string.IsNullOrEmpty(current.FolderName))
                {
                    upload.ModlightFolderName = current.FolderName;
                }

                state.Text = IsFolder(selected, rootValue) && (current == null || string.IsNullOrEmpty(current.FolderName))
                    ? Strings.TaskSettingsWindow_ModlightFolderMissing
                    : "";
            }
            catch (ModlightUnauthorizedException)
            {
                state.Text = Strings.TaskSettingsWindow_ModlightFolderRejected;
            }
            catch (Exception)
            {
                // Including a timeout: from here a folder list that did not arrive is one
                // thing whatever stopped it, and the setting is unaffected either way.
                state.Text = Strings.TaskSettingsWindow_ModlightFolderFailed;
            }
            finally
            {
                refresh.IsEnabled = true;
            }
        }

        refresh.Click += (_, _) => _ = LoadAsync();

        // Once, when the settings window is built. One small GET, and only when there is
        // an account to ask - a signed-out install must not produce a request per window.
        _ = LoadAsync();

        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6
        };
        row.Children.Add(combo);
        Grid.SetColumn(refresh, 1);
        row.Children.Add(refresh);

        StackPanel panel = new() { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(row);
        panel.Children.Add(state);
        return panel;
    }

    /// <summary>Whether a stored value names an actual folder, as opposed to the two fixed entries.</summary>
    private static bool IsFolder(string id, string rootValue) =>
        !string.IsNullOrEmpty(id) && id != rootValue;

    /// <summary>
    /// The drop-down before - or without - a folder list: the two fixed entries, plus
    /// whatever is already set, by its remembered name.
    /// </summary>
    private static List<Choice> Offline(TaskSettingsUpload upload, string rootValue)
    {
        List<Choice> choices = Fixed(rootValue);
        string id = upload.ModlightFolderId ?? "";

        if (IsFolder(id, rootValue))
        {
            string name = upload.ModlightFolderName ?? "";
            choices.Add(new Choice(id, string.IsNullOrEmpty(name) ? Strings.TaskSettingsWindow_ModlightFolderUnknown : name)
            {
                FolderName = name
            });
        }

        return choices;
    }

    private static List<Choice> Fixed(string rootValue) =>
    [
        new Choice("", Strings.TaskSettingsWindow_ModlightFolderAmbient),
        new Choice(rootValue, Strings.TaskSettingsWindow_ModlightFolderNone)
    ];

    /// <summary>The two fixed entries, every folder the account has, and the set-but-gone one.</summary>
    private static List<Choice> Build(ModlightFolderList list, string rootValue, string selectedId, string selectedName)
    {
        Dictionary<string, ModlightFolder> byId = [];

        foreach (ModlightFolder folder in list.Folders ?? [])
        {
            if (!string.IsNullOrEmpty(folder.Id))
            {
                byId[folder.Id!] = folder;
            }
        }

        List<Choice> folders = byId.Values
            .Select(folder => new Choice(folder.Id!, PathOf(folder, byId)) { FolderName = folder.Name ?? "" })
            .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        List<Choice> choices = Fixed(rootValue);
        choices.AddRange(folders);

        // A folder that is set but is not in the account's list any more. Kept in the
        // drop-down rather than silently replaced by the first entry: the setting really
        // does still say that id, and showing the truth is what lets somebody fix it.
        if (IsFolder(selectedId, rootValue) && !byId.ContainsKey(selectedId))
        {
            string label = string.IsNullOrEmpty(selectedName)
                ? Strings.TaskSettingsWindow_ModlightFolderUnknown
                : string.Format(Strings.TaskSettingsWindow_ModlightFolderGone, selectedName);
            choices.Add(new Choice(selectedId, label));
        }

        return choices;
    }

    /// <summary>"Parent / Child", so two folders with the same name are told apart.</summary>
    private static string PathOf(ModlightFolder folder, Dictionary<string, ModlightFolder> byId)
    {
        List<string> parts = [folder.Name ?? ""];
        string? parentId = folder.ParentId;

        for (int depth = 0; depth < MaxDepth && !string.IsNullOrEmpty(parentId); depth++)
        {
            if (!byId.TryGetValue(parentId!, out ModlightFolder? parent))
            {
                break;
            }

            parts.Insert(0, parent.Name ?? "");
            parentId = parent.ParentId;
        }

        return string.Join(" / ", parts);
    }
}
