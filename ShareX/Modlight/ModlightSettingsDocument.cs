#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ShareX;

/// <summary>
/// What travels to the account's cloud settings, and what does not.
/// </summary>
/// <remarks>
/// <para>
/// The owner's requirement (docs/decyzje-modlight-app.md, "Ustawienia w chmurze") is that
/// the configuration - hotkeys above all - belongs to the ACCOUNT, so signing in on a new
/// machine puts everything back. The server deliberately knows nothing about the shape of
/// this document (see lib/client-settings.ts in the panel), which means every decision
/// about what is in it is made here.
/// </para>
/// <para>
/// ALLOWLIST WHERE THE RISK IS DISCLOSURE, DENYLIST WHERE THE RISK IS ERGONOMICS.
/// Five people are extending these config classes at once, and the next credential
/// somebody adds will land inside a node nobody remembers to put on a denylist - so the
/// set of things that travel is a list of NAMES written down here, and anything new stays
/// home until it is named. That rule already earned itself: <c>ToolsSettings.AIOptions</c>
/// carries <c>OpenAIAPIKey</c>, <c>GeminiAPIKey</c> and <c>OpenRouterAPIKey</c>, and
/// <c>ProxySettings</c> carries <c>Password</c>. Neither would have looked like a secret
/// from the outside. File PATHS are the other way round: a path is not a secret, it is
/// merely useless on another machine, so those are removed by name and a new one that
/// slips through costs somebody a wrong folder rather than a leaked key.
/// </para>
/// <para>
/// WHAT IS NOT HERE AT ALL. The API token and every other uploader credential live in
/// <c>UploadersConfig.json</c>, which this file never reads - the token is a bearer key to
/// the whole library and syncing it would hand every machine on the account a credential
/// nobody granted it. The YouTube sign-in is a cookie jar in its own folder
/// (<c>YouTubeSignIn/cookies.txt</c>, see <see cref="ShareX.Presentation.Download"/>) and
/// is not in any config file, so it cannot travel through here even by accident. The skin
/// is excluded too, for a different reason: it has its own endpoint and its own confirmed
/// marker in <see cref="ModlightTheme"/>, and two syncs writing one setting would fight.
/// </para>
/// </remarks>
public static class ModlightSettingsDocument
{
    /// <summary>
    /// The shape of the document. Bumped when a change would confuse an older client.
    /// </summary>
    /// <remarks>
    /// A client that meets a schema it does not know does NOTHING - it neither adopts the
    /// document nor pushes over it. Adopting would apply fields it cannot interpret;
    /// pushing would destroy a newer client's configuration on every machine on the
    /// account. Refusing to act is the only option that cannot lose anything.
    /// </remarks>
    public const int Schema = 1;

    /// <summary>
    /// ApplicationConfig members that travel with the account.
    /// </summary>
    /// <remarks>
    /// Everything absent from this list stays on the machine, and the notable absences are
    /// deliberate:
    /// <list type="bullet">
    /// <item>ProxySettings - contains a password, and describes this network anyway.</item>
    /// <item>Every path (CustomScreenshotsPath, CustomScreenshotsPath2, DownloadFolder,
    /// FileUploadDefaultDirectory, BrowserPath, CustomUploadersConfigPath,
    /// CustomHotkeysConfigPath) - a folder that exists on one machine and not the next.</item>
    /// <item>Window position and size, AutoCaptureRegion, ScreenRecordRegion - screen
    /// coordinates, and the other machine has different screens.</item>
    /// <item>PrintSettings - names a printer.</item>
    /// <item>RecentTasks, ModlightLastFolderId - this machine's recent activity, not a
    /// setting, and rewritten constantly.</item>
    /// <item>DisableUpload and UseMachineSpecificUploadersConfig - policy for one machine.
    /// Carrying "uploads are off" from a locked-down PC to a normal one would disable
    /// uploading somewhere nobody asked.</item>
    /// <item>ThemeOptions - owned by /api/theme, see the class remarks.</item>
    /// <item>ApplicationVersion, FirstTimeRunDate, ToastButtonsVersion, CloudSettings -
    /// per-installation bookkeeping. ToastButtonsVersion especially: it records which
    /// one-time migrations THIS installation has run, and carrying it would make a fresh
    /// machine skip a migration it has never done.</item>
    /// </list>
    /// </remarks>
    private static readonly string[] AppMembers =
    {
        "DefaultTaskSettings",
        "QuickTaskPresets",
        "Language",
        "ShowTray",
        "SilentRun",
        "TrayIconProgressEnabled",
        "TaskbarProgressEnabled",
        "TrayLeftClickAction",
        "TrayLeftDoubleClickAction",
        "TrayMiddleClickAction",
        "AutoCheckUpdate",
        "SaveImageSubFolderPattern",
        "SaveImageSubFolderPatternWindow",
        "AutoCleanupBackupFiles",
        "AutoCleanupLogFiles",
        "CleanupKeepFileCount",
        "ShowThumbnailTitle",
        "ThumbnailTitleLocation",
        "ThumbnailSize",
        "ThumbnailClickAction",
        "ModlightLibraryPanel",
        "ModlightFoldersSection",
        "ModlightFilesSection",
        "ClipboardContentFormats",
        "UploadLimit",
        "BufferSizePower",
        "MaxUploadFailRetry",
        "HistorySaveTasks",
        "HistoryCheckURL",
        "RecentTasksSave",
        "RecentTasksMaxCount",
        "RecentTasksShowInMainWindow",
        "RecentTasksShowInTrayMenu",
        "RecentTasksTrayMenuMostRecentFirst",
        "HistorySettings",
        "ImageHistorySettings",
        "DontShowPrintSettingsDialog",
        "AutoCaptureRepeatTime",
        "AutoCaptureMinimizeToTray",
        "AutoCaptureWaitUpload",
        "RecentColors",
        "DownloadDestination",
        "DownloadMediaKind",
        "DownloadVideoQuality",
        "DownloadAudioFormat",
        "BinaryUnits",
        "ShowMostRecentTaskFirst",
        "WorkflowsOnlyShowEdited",
        "TrayAutoExpandCaptureMenu",
        "SaveSettingsAfterTaskCompleted",
        "DevMode",
        "DisableHotkeys",
        "DisableHotkeysOnFullscreen",
        "HotkeyRepeatLimit",
        "ShowClipboardContentViewer",
        "DefaultClipboardCopyImageFillBackground",
        "UseAlternativeClipboardCopyImage",
        "UseAlternativeClipboardGetImage",
        "RotateImageByExifOrientationData",
        "PNGStripColorSpaceInformation",
        "URLEncodeIgnoreEmoji",
        "ShowMultiUploadWarning",
        "ShowLargeFileSizeWarning",
        "DropSize",
        "DropOffset",
        "DropAlignment",
        "DropOpacity",
        "DropHoverOpacity"
    };

    /// <summary>
    /// Nodes of <c>TaskSettings.ToolsSettings</c> that travel.
    /// </summary>
    /// <remarks>
    /// An allowlist rather than "everything except AIOptions", because this is the subtree
    /// where the API keys turned up. Excluded: <c>AIOptions</c> (three provider keys),
    /// <c>VideoConverterOptions</c> (input and output paths), <c>IndexerSettings</c>
    /// (a CSS file path) and <c>ImageEditorOptions</c> (a background image path and a
    /// recently-opened file list).
    /// </remarks>
    private static readonly HashSet<string> ToolsMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "ScreenColorPickerOptions",
        "PinToScreenOptions",
        "ImageCombinerOptions",
        "BorderlessWindowSettings",
        "BackgroundRemoverOptions",
        "UseLegacyImageEditor",
        "ShowImageEditorSelector"
    };

    /// <summary>Members of a TaskSettings node that name a place on one machine.</summary>
    private static readonly string[] TaskSettingsPaths =
    {
        "OverrideScreenshotsFolder",
        "ScreenshotsFolder"
    };

    /// <summary>Sound file paths, with the switches that turn them on.</summary>
    /// <remarks>
    /// The switch goes with the path deliberately. Carrying "use a custom capture sound"
    /// without the file it names leaves the other machine silent at every capture, and
    /// silently - which is a worse bug than not carrying the preference at all.
    /// </remarks>
    private static readonly string[] GeneralSoundPaths =
    {
        "UseCustomCaptureSound", "CustomCaptureSoundPath",
        "UseCustomTaskCompletedSound", "CustomTaskCompletedSoundPath",
        "UseCustomActionCompletedSound", "CustomActionCompletedSoundPath",
        "UseCustomErrorSound", "CustomErrorSoundPath"
    };

    /// <summary>
    /// The serializer the config FILES are written with.
    /// </summary>
    /// <remarks>
    /// Same contract resolver, same enum converter, same time zone handling as
    /// <c>SettingsBase.SaveToStream</c> - so what travels is exactly what would have been
    /// written to disk, rather than a second opinion about the same objects that could
    /// disagree with it in some corner nobody looks at. Unindented, because this one is
    /// hashed and put on a wire rather than read by a person.
    /// </remarks>
    private static JsonSerializer Writer()
    {
        JsonSerializer serializer = new JsonSerializer
        {
            ContractResolver = new WritablePropertiesOnlyResolver(),
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            Formatting = Formatting.None
        };

        serializer.Converters.Add(new StringEnumConverter());
        return serializer;
    }

    /// <summary>The mirror of <see cref="Writer"/>, for putting a document back.</summary>
    /// <remarks>
    /// <c>ObjectCreationHandling.Replace</c> matches <c>SettingsBase.LoadInternal</c>, and
    /// it is not a preference: the default (Auto) reuses an existing list and APPENDS to
    /// it, so every populate would double the length of QuickTaskPresets, RecentColors and
    /// the notification buttons.
    /// </remarks>
    private static JsonSerializer Reader()
    {
        JsonSerializer serializer = new JsonSerializer
        {
            ContractResolver = new WritablePropertiesOnlyResolver(),
            DateTimeZoneHandling = DateTimeZoneHandling.Local,
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

        serializer.Converters.Add(new StringEnumConverter());
        return serializer;
    }

    /// <summary>
    /// Everything this machine would send, or null when the settings are not loaded yet.
    /// </summary>
    public static JObject? Build()
    {
        ApplicationConfig? settings = Program.Settings;
        HotkeysConfig? hotkeys = Program.HotkeysConfig;

        if (settings == null)
        {
            return null;
        }

        JsonSerializer writer = Writer();

        JObject full = JObject.FromObject(settings, writer);
        JObject app = new JObject();

        foreach (string member in AppMembers)
        {
            JToken? value = full[member];

            if (value != null)
            {
                app[member] = value.DeepClone();
            }
        }

        if (app["DefaultTaskSettings"] is JObject defaults)
        {
            NormalizeTaskSettings(defaults);
        }

        JObject document = new JObject
        {
            ["schema"] = Schema,
            ["app"] = app
        };

        // Absent rather than empty when the hotkeys file has not finished loading. An
        // empty hotkey list is a real configuration - somebody who deleted every hotkey -
        // and sending one because a background load had not finished would delete
        // everybody's hotkeys on every machine.
        if (hotkeys != null)
        {
            JObject hotkeysDocument = JObject.FromObject(hotkeys, writer);

            if (hotkeysDocument["Hotkeys"] is JArray list)
            {
                foreach (JToken entry in list)
                {
                    if (entry is JObject hotkey && hotkey["TaskSettings"] is JObject taskSettings)
                    {
                        NormalizeTaskSettings(taskSettings);
                    }
                }
            }

            document["hotkeys"] = hotkeysDocument;
        }

        return document;
    }

    /// <summary>
    /// Strip a TaskSettings node down to what may travel, and to one canonical form.
    /// </summary>
    /// <remarks>
    /// The first half mirrors <see cref="TaskSettings.Cleanup"/> - the pass that runs
    /// before every save - ON THE COPY. Doing it here rather than by calling Cleanup is
    /// the point: Cleanup nulls fields on the LIVE objects, so a snapshot taken before a
    /// save and one taken after would differ, the digest would flip with save timing, and
    /// the sync would push on every tick forever.
    /// </remarks>
    private static void NormalizeTaskSettings(JObject taskSettings)
    {
        DropWhenDefault(taskSettings, "UseDefaultGeneralSettings", "GeneralSettings");
        DropWhenDefault(taskSettings, "UseDefaultImageSettings", "ImageSettings");
        DropWhenDefault(taskSettings, "UseDefaultCaptureSettings", "CaptureSettings");
        DropWhenDefault(taskSettings, "UseDefaultUploadSettings", "UploadSettings");
        DropWhenDefault(taskSettings, "UseDefaultActions", "ExternalPrograms");
        DropWhenDefault(taskSettings, "UseDefaultToolsSettings", "ToolsSettings");
        DropWhenDefault(taskSettings, "UseDefaultAdvancedSettings", "AdvancedSettings");

        foreach (string member in TaskSettingsPaths)
        {
            taskSettings.Remove(member);
        }

        if (taskSettings["GeneralSettings"] is JObject general)
        {
            foreach (string member in GeneralSoundPaths)
            {
                general.Remove(member);
            }
        }

        if (taskSettings["CaptureSettings"] is JObject capture &&
            capture["FFmpegOptions"] is JObject ffmpeg)
        {
            // Where ffmpeg.exe lives, and whether to look there. Both leave, together:
            // "override the path" pointing at a path that is not on this machine is a
            // screen recorder that fails at the moment somebody presses record.
            ffmpeg.Remove("OverrideCLIPath");
            ffmpeg.Remove("CLIPath");
        }

        if (taskSettings["ToolsSettings"] is JObject tools)
        {
            foreach (JProperty property in new List<JProperty>(tools.Properties()))
            {
                if (!ToolsMembers.Contains(property.Name))
                {
                    property.Remove();
                }
            }
        }
    }

    /// <summary>Remove <paramref name="member"/> when its "use the default" flag is set.</summary>
    private static void DropWhenDefault(JObject node, string flag, string member)
    {
        if (node[flag] is JValue value && value.Type == JTokenType.Boolean && (bool)value.Value!)
        {
            node.Remove(member);
        }
    }

    /// <summary>
    /// A short, stable name for the contents of a document.
    /// </summary>
    /// <remarks>
    /// Compared against the digest the server last confirmed, so it answers exactly one
    /// question - "is this the same document?" - and answers it without keeping a second
    /// copy of the configuration on disk to diff against. Newtonsoft writes members in
    /// declaration order, so the same values always produce the same bytes.
    /// </remarks>
    public static string Digest(JObject? document)
    {
        if (document == null)
        {
            return "";
        }

        byte[] bytes = Encoding.UTF8.GetBytes(document.ToString(Formatting.None));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>How many bytes the settings object would occupy on the server.</summary>
    public static int CanonicalLength(JObject document)
    {
        return Encoding.UTF8.GetByteCount(document.ToString(Formatting.None));
    }

    /// <summary>
    /// Put an adopted <c>app</c> document onto the live configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Merged onto THIS machine's values rather than applied on its own, and the base is
    /// the local document with nothing stripped out of it. That is what makes a field
    /// which never travels keep its local value instead of reverting to a factory default:
    /// the remote document simply has no opinion about <c>AIOptions</c> or the ffmpeg
    /// path, so the local one survives the merge.
    /// </para>
    /// <para>
    /// Only allowlisted members are in the merged document at all, which matters for more
    /// than tidiness: populating with <c>Replace</c> builds a NEW object for every member
    /// it touches, and <c>ThemeOptions</c> is an object the theme manager has subscribed
    /// to. Leaving it out of the document leaves the live instance alone.
    /// </para>
    /// </remarks>
    /// <returns>True when something was applied.</returns>
    public static bool ApplyApp(JObject? remoteApp)
    {
        ApplicationConfig? settings = Program.Settings;

        if (settings == null || remoteApp == null)
        {
            return false;
        }

        JsonSerializer writer = Writer();
        JObject full = JObject.FromObject(settings, writer);
        JObject merged = new JObject();

        JsonMergeSettings merge = new JsonMergeSettings
        {
            // Wholesale, never element-by-element: merging two lists of notification
            // buttons index by index would produce a set that exists on neither machine.
            MergeArrayHandling = MergeArrayHandling.Replace,
            // An explicit null in the remote document is a value, not an omission.
            MergeNullValueHandling = MergeNullValueHandling.Merge
        };

        foreach (string member in AppMembers)
        {
            JToken? local = full[member];
            JToken? remote = remoteApp[member];

            if (local == null && remote == null)
            {
                continue;
            }

            if (local is JObject localObject && remote is JObject remoteObject)
            {
                JObject combined = (JObject)localObject.DeepClone();
                combined.Merge(remoteObject, merge);
                merged[member] = combined;
            }
            else if (remote != null)
            {
                merged[member] = remote.DeepClone();
            }
        }

        using (JsonReader reader = merged.CreateReader())
        {
            Reader().Populate(reader, settings);
        }

        // DefaultTaskSettings is a new object after a populate with Replace, and Program
        // holds a second reference to the old one. Every capture reads that reference, so
        // leaving it pointing at the superseded object would apply the adopted settings to
        // nothing at all.
        Program.DefaultTaskSettings = settings.DefaultTaskSettings;

        return true;
    }

    /// <summary>
    /// Read the hotkey list out of an adopted document.
    /// </summary>
    /// <remarks>
    /// A fresh list rather than a mutation of the live one. <see cref="HotkeyManager"/>
    /// unregisters from the list it is holding and then takes the one it is handed, so
    /// handing it the same instance it already owns would empty it and register nothing.
    /// </remarks>
    public static List<HotkeySettings>? ReadHotkeys(JObject? remoteHotkeys)
    {
        if (remoteHotkeys?["Hotkeys"] is not JArray list)
        {
            return null;
        }

        using JsonReader reader = list.CreateReader();
        return Reader().Deserialize<List<HotkeySettings>>(reader);
    }
}
