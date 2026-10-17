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

using ShareX.AvaloniaUI.Controls;
using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ShareX;

/// <summary>Which of the four views inside the settings window draws a section.</summary>
internal enum SettingsHost
{
    Account,
    Application,
    Task,
    Hotkeys
}

/// <summary>One entry in the settings window's section list.</summary>
/// <param name="Host">The view that draws it.</param>
/// <param name="PageId">What that view calls the page - unprefixed, as the view knows it.</param>
/// <param name="Description">
/// The sentence under the page title. It is shown to a person AND fed to the search
/// index, which is why the old ShareX words a returning user would type - "region
/// capture", "effects", "thumbnail", "clipboard formats" - are written into these
/// sentences on purpose rather than hidden in a keyword list nobody can read.
/// </param>
internal sealed record SettingsSection(
    SettingsHost Host,
    string PageId,
    string Title,
    string Icon,
    string Description = "")
{
    /// <summary>
    /// The id the section list and the search index use.
    /// </summary>
    /// <remarks>
    /// Prefixed because the inherited views were written as separate dialogs and each has
    /// a page called "general", "upload" and "advanced". Merging their lists without a
    /// prefix would make the search index attach one page's contents to another page's
    /// entry - silently, and only for the ids that happen to collide.
    /// </remarks>
    public string Id => Host.ToString().ToLowerInvariant() + "." + PageId;
}

/// <summary>A heading in the section list, with the sections filed under it.</summary>
internal sealed record SettingsGroup(string Title, string Icon, IReadOnlyList<SettingsSection> Sections);

/// <summary>
/// The one list of settings sections, and the only place that decides what goes where.
/// </summary>
/// <remarks>
/// <para>
/// ShareX filed configuration under three dialogs - Application settings, Task settings
/// and Hotkey settings - so every question started with "which of the three is it in".
/// The grouping here answers a different question: WHAT ARE YOU TRYING TO DO. The pages
/// themselves are untouched; only their filing and their names are.
/// </para>
/// <para>
/// Names are the second half of that. ShareX's page titles were written from the
/// program's point of view - "Image", "Effects", "Thumbnail", "Clipboard formats",
/// "Region capture" - and each of them describes a part of ShareX rather than a thing a
/// person wants. Every title here is meant to answer "what would I type into the search
/// box to find this", so "Image" became "Format and quality", "Effects" became "Effects
/// and watermark", and the two different things ShareX both called thumbnails - the file
/// saved next to a capture and the tiles in the main window - are now named apart.
/// </para>
/// <para>
/// Groups of one section are drawn as a single row (see <see cref="ToNavigation"/>), and
/// four things earned that: screen recording, OCR, shortcuts, and sounds. Each is a
/// thing people hunt for by name, and a row you can read without opening anything is
/// found faster than a heading you have to guess at. Sounds in particular used to sit
/// under "Tools and actions", which is where nobody looks for "turn the beep off".
/// </para>
/// <para>
/// Three ids from the inherited dialogs are deliberately NOT listed here. The task
/// settings "general" and "upload" pages hold nothing but an override switch, which does
/// not apply to the defaults - <see cref="TaskSettingsPageBuilder"/> hides it when
/// <c>isDefault</c> - so listing them would give two entries that open a blank page. The
/// task settings "upload-modlight" page moved into the account group, which is where the
/// question "where do my uploads go" is actually asked.
/// </para>
/// </remarks>
internal static class SettingsSections
{
    /// <summary>Where the window opens when nothing more specific was asked for.</summary>
    public const string DefaultSectionId = "account.profile";

    /// <summary>The account chip's door.</summary>
    public const string AccountSectionId = "account.profile";

    /// <summary>Where the "shortcut did not register" banner sends people.</summary>
    public const string HotkeysSectionId = "hotkeys.list";

    /// <summary>What the old "Application settings" menu entry pointed at.</summary>
    public const string ApplicationSectionId = "application.general";

    /// <summary>What the old "Task settings" menu entry pointed at.</summary>
    public const string TaskSectionId = "task.capture";

    /// <summary>The first page of a single hotkey's overrides.</summary>
    public const string TaskOverrideSectionId = "task.task";

    /// <summary>The whole window, in the order the list draws it.</summary>
    public static IReadOnlyList<SettingsGroup> All()
    {
        return
        [
            new SettingsGroup(Strings.SettingsWindow_GroupAccount, LucideIcons.circle_user,
            [
                new SettingsSection(SettingsHost.Account, "profile", Strings.SettingsWindow_AccountProfile,
                    LucideIcons.circle_user_round, Strings.SettingsWindow_HintProfile),
                new SettingsSection(SettingsHost.Account, "links", Strings.SettingsWindow_AccountLinks,
                    LucideIcons.link, Strings.SettingsWindow_HintLinks),
                new SettingsSection(SettingsHost.Account, "storage", Strings.SettingsWindow_AccountStorage,
                    LucideIcons.hard_drive, Strings.SettingsWindow_HintStorage)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupCapture, LucideIcons.camera,
            [
                new SettingsSection(SettingsHost.Task, "capture-region", Strings.SettingsWindow_PageRegion,
                    LucideIcons.crop, Strings.SettingsWindow_HintRegion),
                new SettingsSection(SettingsHost.Task, "capture", Strings.SettingsWindow_PageScreenshots,
                    LucideIcons.camera, Strings.SettingsWindow_HintScreenshots),
                new SettingsSection(SettingsHost.Task, "image", Strings.SettingsWindow_PageImageFormat,
                    LucideIcons.image, Strings.SettingsWindow_HintImageFormat),
                new SettingsSection(SettingsHost.Task, "image-effects", Strings.SettingsWindow_PageEffects,
                    LucideIcons.wand_sparkles, Strings.SettingsWindow_HintEffects),
                new SettingsSection(SettingsHost.Task, "image-thumbnail", Strings.SettingsWindow_PageFileThumbnails,
                    LucideIcons.images, Strings.SettingsWindow_HintFileThumbnails),
                // Filed with capture, not with uploading. The pattern here names the file
                // the moment it is TAKEN - the copy saved on this PC carries it too - and
                // somebody asking "what are my screenshots called" opens this group,
                // reads five pages that are not about names, and gives up. Measured: it
                // was the one question of eight that nobody answered without the search
                // box.
                new SettingsSection(SettingsHost.Task, "upload-file-naming", Strings.SettingsWindow_PageFileNames,
                    LucideIcons.file_pen, Strings.SettingsWindow_HintFileNames)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupRecording, LucideIcons.video,
            [
                new SettingsSection(SettingsHost.Task, "capture-screen-recorder", Strings.SettingsWindow_GroupRecording,
                    LucideIcons.video, Strings.SettingsWindow_HintRecording)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupTextFromImage, LucideIcons.scan_text,
            [
                new SettingsSection(SettingsHost.Task, "capture-ocr", Strings.SettingsWindow_GroupTextFromImage,
                    LucideIcons.scan_text, Strings.SettingsWindow_HintOcr)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupSharing, LucideIcons.upload,
            [
                new SettingsSection(SettingsHost.Application, "clipboard-formats", Strings.SettingsWindow_PageCopyFormats,
                    LucideIcons.clipboard_list, Strings.SettingsWindow_HintCopyFormats),
                new SettingsSection(SettingsHost.Task, "upload-clipboard", Strings.SettingsWindow_PageFromClipboard,
                    LucideIcons.clipboard, Strings.SettingsWindow_HintFromClipboard),
                new SettingsSection(SettingsHost.Application, "upload", Strings.SettingsWindow_PageUploadLimits,
                    LucideIcons.upload, Strings.SettingsWindow_HintUploadLimits),
                new SettingsSection(SettingsHost.Application, "history", Strings.SettingsWindow_PageHistory,
                    LucideIcons.history, Strings.SettingsWindow_HintHistory)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupShortcuts, LucideIcons.keyboard,
            [
                new SettingsSection(SettingsHost.Hotkeys, "list", Strings.SettingsWindow_GroupShortcuts,
                    LucideIcons.keyboard, Strings.SettingsWindow_HintShortcuts)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupSounds, LucideIcons.bell,
            [
                new SettingsSection(SettingsHost.Task, "general-notifications", Strings.SettingsWindow_GroupSounds,
                    LucideIcons.bell, Strings.SettingsWindow_HintSounds)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupTools, LucideIcons.wrench,
            [
                new SettingsSection(SettingsHost.Task, "tools", Strings.SettingsWindow_PageEditor,
                    LucideIcons.wrench, Strings.SettingsWindow_HintEditor),
                new SettingsSection(SettingsHost.Task, "actions", Strings.SettingsWindow_PageExternalPrograms,
                    LucideIcons.zap, Strings.SettingsWindow_HintExternalPrograms),
                new SettingsSection(SettingsHost.Task, "watch-folders", Strings.TaskSettingsWindow_WatchFolders,
                    LucideIcons.folder_search, Strings.SettingsWindow_HintWatchFolders)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupAppearance, LucideIcons.palette,
            [
                new SettingsSection(SettingsHost.Application, "theme", Strings.SettingsWindow_PageThemeColours,
                    LucideIcons.palette, Strings.SettingsWindow_HintThemeColours),
                new SettingsSection(SettingsHost.Application, "main-window", Strings.SettingsWindow_PageMainWindowTiles,
                    LucideIcons.monitor, Strings.SettingsWindow_HintMainWindowTiles)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupApplication, LucideIcons.settings,
            [
                new SettingsSection(SettingsHost.Application, "general", Strings.SettingsWindow_PageLanguageTray,
                    LucideIcons.settings, Strings.SettingsWindow_HintLanguageTray),
                new SettingsSection(SettingsHost.Application, "integration", Strings.SettingsWindow_PageWindowsIntegration,
                    LucideIcons.plug, Strings.SettingsWindow_HintWindowsIntegration),
                new SettingsSection(SettingsHost.Application, "components", Strings.SettingsWindow_PageComponents,
                    LucideIcons.package, Strings.SettingsWindow_HintComponents),
                new SettingsSection(SettingsHost.Application, "paths", Strings.SettingsWindow_PageLocalFolders,
                    LucideIcons.folder, Strings.SettingsWindow_HintLocalFolders),
                new SettingsSection(SettingsHost.Application, "settings", Strings.SettingsWindow_PageBackup,
                    LucideIcons.database_backup, Strings.SettingsWindow_HintBackup),
                new SettingsSection(SettingsHost.Application, "print", Strings.ApplicationSettingsWindow_Print,
                    LucideIcons.printer, Strings.SettingsWindow_HintPrint),
                new SettingsSection(SettingsHost.Application, "proxy", Strings.SettingsWindow_PageProxy,
                    LucideIcons.network, Strings.SettingsWindow_HintProxy)
            ]),

            new SettingsGroup(Strings.SettingsWindow_GroupAdvanced, LucideIcons.sliders_horizontal,
            [
                new SettingsSection(SettingsHost.Application, "advanced", Strings.SettingsWindow_PageAllApplication,
                    LucideIcons.settings, Strings.SettingsWindow_HintAllApplication),
                new SettingsSection(SettingsHost.Task, "advanced", Strings.SettingsWindow_AdvancedTasks,
                    LucideIcons.zap, Strings.SettingsWindow_HintAllCapture)
            ])
        ];
    }

    /// <summary>
    /// The list for one hotkey's overrides, which is a different window with the same shell.
    /// </summary>
    /// <remarks>
    /// Only task sections, because that is all a hotkey overrides - it has no account, no
    /// shortcuts of its own and no application settings. The account group is absent for a
    /// sharper reason than tidiness: an account setting shown inside "settings for THIS
    /// shortcut" reads as belonging to the shortcut, and changing it there would silently
    /// change it everywhere.
    ///
    /// The "general" and "upload" pages ARE listed here, unlike in <see cref="All"/>: in
    /// override mode they carry the switch that decides whether this hotkey follows the
    /// defaults at all, which is the first thing anybody opening this window wants.
    /// </remarks>
    public static IReadOnlyList<SettingsGroup> ForTaskOverrides()
    {
        List<SettingsGroup> groups =
        [
            new SettingsGroup(Strings.TaskSettingsWindow_Task, LucideIcons.keyboard,
            [
                new SettingsSection(SettingsHost.Task, "task", Strings.TaskSettingsWindow_Task,
                    LucideIcons.keyboard, Strings.SettingsWindow_HintTask),
                new SettingsSection(SettingsHost.Task, "general", Strings.TaskSettingsWindow_General,
                    LucideIcons.settings, Strings.SettingsWindow_HintTaskGeneral),
                new SettingsSection(SettingsHost.Task, "upload", Strings.TaskSettingsWindow_Upload,
                    LucideIcons.upload, Strings.SettingsWindow_HintTaskUpload),
                new SettingsSection(SettingsHost.Task, "upload-modlight", Strings.TaskSettingsWindow_ModlightDestination,
                    LucideIcons.folder_up, Strings.SettingsWindow_HintTaskFolder)
            ])
        ];

        foreach (SettingsGroup group in All())
        {
            SettingsSection[] tasks = group.Sections.Where(x => x.Host == SettingsHost.Task).ToArray();

            if (tasks.Length > 0)
            {
                groups.Add(group with { Sections = tasks });
            }
        }

        return groups;
    }

    /// <summary>
    /// ShareX's own name for each section, in English, whatever the UI language is.
    /// </summary>
    /// <remarks>
    /// The section sentences were written to carry a returning user's vocabulary, and that
    /// only half worked: they carry it in Strings.resx, so on a Polish install "hotkey",
    /// "clipboard" and "effects" found NOTHING. The words that did work - "theme",
    /// "thumbnail" - worked because those two pages had not been translated yet, which is
    /// an accident waiting to be fixed rather than a design.
    ///
    /// Keyed by <see cref="SettingsSection.Id"/> and fed into the search index only, never
    /// drawn: they are for the person who already knows the name they want, not the person
    /// reading the page. Anything named here also has to survive being renamed - which is
    /// the point, since renaming the pages is what this window was for.
    /// </remarks>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["account.profile"] = "account profile token api key sign in",
        ["account.links"] = "url shortener short links copy url",
        ["account.storage"] = "storage quota disk space",
        ["task.capture-region"] = "region capture rectangle capture crop",
        ["task.capture"] = "capture screenshot screen capture window capture cursor",
        ["task.image"] = "image format quality png jpeg jpg gif",
        ["task.image-effects"] = "effects image effects watermark",
        ["task.image-thumbnail"] = "thumbnail thumbnails",
        ["task.upload-file-naming"] = "file naming name pattern name format filename",
        ["task.capture-screen-recorder"] = "screen recorder recording ffmpeg gif",
        ["task.capture-ocr"] = "ocr text capture",
        ["application.clipboard-formats"] = "clipboard formats copy formats",
        ["task.upload-clipboard"] = "clipboard upload clipboard",
        ["application.upload"] = "upload uploader retry buffer",
        ["application.history"] = "history",
        ["hotkeys.list"] = "hotkey hotkeys shortcut keys keyboard",
        ["task.general-notifications"] = "sound sounds notification toast",
        ["task.tools"] = "image editor tools",
        ["task.actions"] = "actions external programs",
        ["task.watch-folders"] = "watch folders watch folder",
        ["application.theme"] = "theme skin accent color dark light",
        ["application.main-window"] = "main window thumbnail view tiles",
        ["application.general"] = "general language tray tray icon",
        ["application.integration"] = "integration windows startup context menu send to steam chrome extension firefox addon",
        ["application.components"] = "components ffmpeg yt-dlp ytdlp exiftool",
        ["application.paths"] = "paths personal folder screenshots folder sub folder pattern",
        ["application.settings"] = "settings backup export import reset",
        ["application.print"] = "print printer",
        ["application.proxy"] = "proxy",
        ["application.advanced"] = "advanced",
        ["task.advanced"] = "advanced",
        ["task.task"] = "task job",
        ["task.general"] = "general",
        ["task.upload"] = "upload",
        ["task.upload-modlight"] = "modlight destination folder"
    };

    private static string AliasesFor(SettingsSection section, SettingsGroup group)
    {
        // The group's name rides along with every section under it, so that searching for
        // a heading - "Appearance" - leaves its pages showing rather than leaving the rows
        // in the list and every panel on them hidden.
        string aliases = Aliases.TryGetValue(section.Id, out string? value) ? value : string.Empty;
        return string.Join(' ', aliases, group.Title);
    }

    /// <summary>Turn a group list into the tree the navigation control draws.</summary>
    /// <remarks>
    /// Groups arrive collapsed. Expanding everything - which is what this used to do -
    /// puts thirty rows in a pane that fits fifteen, so the list scrolls and the last
    /// groups are below the fold before anybody has clicked anything. Orientation is the
    /// one job this pane has, and a map you have to scroll is not a map.
    /// <see cref="SettingsNavigation"/> opens the group holding the current page and
    /// keeps one open at a time, which is what bounds the list at
    /// "every group + the biggest group" rows for good.
    /// </remarks>
    public static ObservableCollection<SettingsNavigationItem> ToNavigation(IReadOnlyList<SettingsGroup> groups)
    {
        ObservableCollection<SettingsNavigationItem> items = [];

        foreach (SettingsGroup group in groups)
        {
            // A group holding one section IS that section: a heading you have to open to
            // find a single entry named the same thing is a step that answers nothing.
            if (group.Sections.Count == 1)
            {
                items.Add(new SettingsNavigationItem(
                    group.Sections[0].Id,
                    group.Title,
                    group.Icon,
                    group.Sections[0].Description,
                    aliases: AliasesFor(group.Sections[0], group)));
                continue;
            }

            items.Add(new SettingsNavigationItem(
                "group." + group.Title,
                group.Title,
                group.Icon,
                children: group.Sections.Select(x =>
                    new SettingsNavigationItem(
                        x.Id, x.Title, x.Icon, x.Description, aliases: AliasesFor(x, group)))));
        }

        return items;
    }
}
