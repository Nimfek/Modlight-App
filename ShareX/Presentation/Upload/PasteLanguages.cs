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

using ShareX.Localization;
using System;
using System.Collections.Generic;

namespace ShareX;

/// <summary>One entry in the language menu: what it is called, and what it makes the file.</summary>
internal sealed class PasteLanguage
{
    public PasteLanguage(string key, string extension, string name)
    {
        Key = key;
        Extension = extension;
        Name = name;
    }

    /// <summary>
    /// The panel's own identifier for this language, from <c>lib/paste-languages.ts</c>.
    /// </summary>
    /// <remarks>
    /// Carried even though nothing sends it today, because it is the thing that would be
    /// sent the moment the server grows a paste endpoint - see
    /// <see cref="PasteViewModel"/> for why it cannot be sent now. Keeping the two lists
    /// keyed the same way is what makes that a one-line change rather than a re-mapping.
    /// </remarks>
    public string Key { get; }

    /// <summary>What the uploaded file is called, which is the ONLY part the server sees.</summary>
    public string Extension { get; }

    /// <summary>The name a person reads.</summary>
    public string Name { get; }

    public override string ToString() => Name;
}

/// <summary>
/// The languages a paste can be written in.
/// </summary>
/// <remarks>
/// The same set as the web panel's <c>lib/paste-languages.ts</c>, in the same order, with
/// the same keys and the same extensions - deliberately copied rather than shortened to
/// the ones that "matter", because a language offered on the website and missing here is
/// a paste the owner cannot make from the app he is being asked to use instead.
///
/// The EXTENSION is the load-bearing half. Nothing tells this server which language a
/// file is written in, so the panel works it out from the name: an upload called
/// <c>notes.py</c> is stored as text/x-python and labelled "Python" in the preview
/// because <c>lib/media/sniff.ts</c> and <c>lib/media/source-preview.ts</c> read the
/// extension. Every extension in this table is one both of those tables know, which is
/// what makes the choice visible on the website rather than only in this window.
///
/// One of them is a trap worth naming: <c>.ts</c> is TypeScript here and MPEG transport
/// stream in the extension table. It survives because the server sniffs the bytes first
/// and text has no signature to argue with - <c>resolveUploadType</c> keeps text/plain
/// rather than promoting the file to video, exactly as the panel's own paste path
/// documents. So a TypeScript paste is a text file, not a dead video player.
/// </remarks>
internal static class PasteLanguages
{
    /// <summary>Every language, in the panel's order. The first is the default.</summary>
    public static IReadOnlyList<PasteLanguage> All { get; } = new PasteLanguage[]
    {
        // Localized, unlike the rest: "Plain text" is a description, while "Python" and
        // "TOML" are names and a translated name is a name nobody can search for.
        new("text", "txt", Strings.Paste_LanguagePlainText),
        new("ts", "ts", "TypeScript"),
        new("tsx", "tsx", "TypeScript (TSX)"),
        new("js", "js", "JavaScript"),
        new("jsx", "jsx", "JavaScript (JSX)"),
        new("json", "json", "JSON"),
        new("py", "py", "Python"),
        new("go", "go", "Go"),
        new("rs", "rs", "Rust"),
        new("java", "java", "Java"),
        new("c", "c", "C"),
        new("cpp", "cpp", "C++"),
        new("cs", "cs", "C#"),
        new("php", "php", "PHP"),
        new("rb", "rb", "Ruby"),
        new("sh", "sh", "Shell"),
        new("yml", "yml", "YAML"),
        new("toml", "toml", "TOML"),
        new("sql", "sql", "SQL"),
        new("html", "html", "HTML"),
        new("css", "css", "CSS"),
        new("md", "md", "Markdown"),
        new("xml", "xml", "XML"),
        new("kt", "kt", "Kotlin"),
        new("swift", "swift", "Swift"),
        new("dockerfile", "dockerfile", "Dockerfile"),
        new("ini", "ini", "INI"),
        new("diff", "diff", "Diff"),
    };

    /// <summary>Plain text, which is what an unanswered question should mean.</summary>
    public static PasteLanguage Default => All[0];

    /// <summary>The language with this key, or plain text when there is no such key.</summary>
    public static PasteLanguage Find(string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            foreach (PasteLanguage language in All)
            {
                if (string.Equals(language.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return language;
                }
            }
        }

        return Default;
    }

    /// <summary>
    /// Does this entry answer what was typed into the search box?
    /// </summary>
    /// <remarks>
    /// Name, key and extension all match, because people reach for all three: someone
    /// looking for C# types "cs" as often as "C#", and the extension is what they will
    /// see on the finished file. Contains rather than StartsWith, so "script" finds both
    /// JavaScript and TypeScript.
    /// </remarks>
    public static bool Matches(PasteLanguage language, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        return language.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || language.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || language.Extension.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
