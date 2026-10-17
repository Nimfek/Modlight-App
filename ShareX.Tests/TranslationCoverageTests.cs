#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ShareX.Tests;

/// <summary>
/// Every string the settings window draws has to exist in Polish, not just in English.
/// </summary>
/// <remarks>
/// <para>
/// The window was consolidated, its sections were renamed and its navigation was
/// translated - and the CONTENT of those sections was not. A hundred and ten keys under
/// <c>ApplicationSettingsWindow_</c> still held their English value in Strings.pl.resx,
/// so a Polish install read "Aplikacja / Jezyk i zasobnik" as a heading and then
/// "Show tray icon", "Minimize to tray on start", "Tray actions" underneath it. Somebody
/// scanning for a Polish phrase stops reading exactly where half the settings are.
/// </para>
/// <para>
/// This is not a spell checker and cannot be: nothing here can tell a good translation
/// from a bad one. What it can tell is that a value was never touched, and that is the
/// failure that happened. So: every key whose Polish value is byte-identical to its
/// English one has to be listed below as a deliberate decision. Thirty-six are - proper
/// nouns, format strings, and words Polish spells the same way - and a new one appears
/// only when somebody adds a key and does not translate it, which is the moment to say
/// so rather than three rounds later.
/// </para>
/// <para>
/// Scope is ShareX/Localization, which is where the settings window's own text lives.
/// The other assemblies have identical-value keys of their own (UploadersLib is mostly
/// service names); pulling them in would mean auditing them first, and a gate switched on
/// over a backlog nobody has read is a gate people learn to ignore.
/// </para>
/// </remarks>
internal static class TranslationCoverageTests
{
    /// <summary>
    /// Keys whose Polish value is meant to be the English one, and why.
    /// </summary>
    private static readonly HashSet<string> SameInBothLanguages = new(StringComparer.Ordinal)
    {
        // Format strings: punctuation and placeholders, no words.
        "Admin_ColdSummary",
        "Download_ErrorRaw",
        "Download_QualityHeight",
        "Download_SizeFormat",
        "Download_SpeedFormat",
        "MySpace_PageOf",
        "UploadQueue_InMinutes",
        "UploadQueue_InSeconds",
        "TaskSettingsWindow_X",
        "TaskSettingsWindow_Y",
        "ApplicationSettingsWindow_HostLabel",
        "ApplicationSettingsWindow_PortLabel",

        // Names of products, formats and programs - translating them would be wrong.
        "Admin_KindPdf",
        "ApplicationSettingsWindow_Steam",
        "ApplicationSettingsWindow_Windows",
        "Components_ExifTool",
        "Components_FFmpeg",
        "Components_YtDlp",
        "Download_AudioM4a",
        "Download_AudioMp3",
        "Download_SignInLabel",
        "MainMenuBuilder_Discord",
        "MainWindow_CaptureGifTitle",
        "MySpace_LinkFormatBBCode",
        "MySpace_LinkFormatHtml",
        "MySpace_LinkFormatMarkdown",
        "TaskSettingsWindow_OCR",

        // Words Polish spells the same way, or borrows unchanged.
        "ActionsToolbarEditorWindow_Separator",
        "ApplicationSettingsWindow_Format",
        "Download_FolderLabel",
        "FirstTimeConfigWindow_OK",
        "MainMenuBuilder_Monitor",
        "MainWindow_Folder",
        "MySpace_DetailsFolder",
        "MySpace_KindAudio",
        "QuickTaskMenuEditorWindow_Separator",
        "SignInWindow_Token",
        "TaskSettingsWindow_Format",
        "TaskSettingsWindow_ModlightFolder"
    };

    public static int Run()
    {
        Console.WriteLine("Translation coverage");
        int failures = 0;

        string? root = RepositoryRoot();

        if (root == null)
        {
            Console.WriteLine("  FAIL: could not find the repository root from " + AppContext.BaseDirectory);
            return 1;
        }

        Dictionary<string, string> english = Load(Path.Combine(root, "ShareX", "Localization", "Strings.resx"));
        Dictionary<string, string> polish = Load(Path.Combine(root, "ShareX", "Localization", "Strings.pl.resx"));

        failures += Check("every English key has a Polish row",
            english.Keys.Where(key => !polish.ContainsKey(key)).ToArray());

        failures += Check("no key is left holding its English value",
            polish.Where(pair =>
                    english.TryGetValue(pair.Key, out string? value) &&
                    value == pair.Value &&
                    value.Trim().Length > 0 &&
                    !SameInBothLanguages.Contains(pair.Key))
                .Select(pair => pair.Key)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray());

        // The allow list is part of the gate, so it has to describe reality too: a stale
        // entry silently widens the hole it was written to keep narrow.
        failures += Check("the allow list holds no key that is now translated",
            SameInBothLanguages.Where(key =>
                    !polish.TryGetValue(key, out string? pl) ||
                    !english.TryGetValue(key, out string? en) ||
                    pl != en)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray());

        return failures;
    }

    private static int Check(string what, IReadOnlyList<string> offenders)
    {
        if (offenders.Count == 0)
        {
            Console.WriteLine("  ok: " + what);
            return 0;
        }

        Console.WriteLine("  FAIL: " + what + " (" + offenders.Count + ")");

        foreach (string key in offenders.Take(20))
        {
            Console.WriteLine("        " + key);
        }

        if (offenders.Count > 20)
        {
            Console.WriteLine("        ... and " + (offenders.Count - 20) + " more");
        }

        return offenders.Count;
    }

    private static Dictionary<string, string> Load(string path)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (XElement element in XDocument.Load(path).Root!.Elements("data"))
        {
            string? name = element.Attribute("name")?.Value;
            XElement? value = element.Element("value");

            if (name != null && value != null)
            {
                values[name] = value.Value;
            }
        }

        return values;
    }

    /// <summary>
    /// Walk up from the test binary until the resource files are underfoot.
    /// </summary>
    /// <remarks>
    /// The suite runs from bin/Release/net10.0 and the files it reads are checked in, not
    /// copied: reading them where they live is what makes this measure the repository
    /// rather than a stale copy of it.
    /// </remarks>
    private static string? RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ShareX", "Localization", "Strings.resx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
