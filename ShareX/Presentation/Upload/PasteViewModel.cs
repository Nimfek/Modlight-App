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

using ShareX.HelpersLib;
using ShareX.Localization;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShareX;

/// <summary>
/// A paste being written: its text, its language, and what those two make of a filename.
/// </summary>
/// <remarks>
/// <b>How this reaches the server, and what it costs.</b> The panel creates a paste with
/// <c>createPaste</c> in <c>app/actions/pastes.ts</c>, which is a Next.js <i>Server
/// Action</i>: a function the browser invokes through a framework-private protocol,
/// addressed by a build-time id that changes on every deployment. There is no HTTP route
/// for it - the whole of <c>app/api</c> was read, and pastes appear nowhere in it - so a
/// C# client cannot call it at all, and pretending otherwise would produce a feature that
/// broke on the panel's next build.
///
/// So this sends the text the way ShareX has always sent text: as a FILE, through
/// <c>POST /upload</c>, named with the extension the chosen language implies. What that
/// buys, and what it does not:
///
///   works    - the bytes, the link, the account's copy_raw preference deciding what
///              lands on the clipboard, the folder My space is standing in, expiry,
///              view limits and passwords (they are columns on the upload row, set from
///              the tile's own Link settings), the preview page, and - because the panel
///              reads the extension when a row has no language of its own - the right
///              syntax label on the website. A paste written as Python arrives as
///              text/x-python and is shown as Python.
///   does not - the row is stored with kind='file', not kind='paste', and its
///              <c>language</c> column stays null. On the website it is filed under
///              "Documents" rather than under the "Pastes" filter, and it gets the text
///              icon rather than the clipboard one.
///
/// That difference is a label, not a capability, and it is the honest price: the
/// alternative is no paste in the app at all. The day the server grows a paste endpoint,
/// <see cref="PasteLanguage.Key"/> is already carried here and <see cref="TrySend"/>
/// becomes a call to it.
/// </remarks>
internal sealed class PasteViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// The most text one paste may carry.
    /// </summary>
    /// <remarks>
    /// The web panel's own ceiling, copied so the two agree. Nothing on this path would
    /// enforce it - a file upload has no size limit here by design - but a window that
    /// accepts what the website refuses teaches the wrong thing about the service, and
    /// five million characters is already far past any paste anybody writes.
    /// </remarks>
    public const int MaxCharacters = 5_000_000;

    private string title = "";
    private string content = "";
    private string languageFilter = "";
    private PasteLanguage language = PasteLanguages.Default;
    private string problem = "";

    public PasteViewModel()
    {
        foreach (PasteLanguage entry in PasteLanguages.All)
        {
            Languages.Add(entry);
        }
    }

    /// <summary>The languages the search box is currently letting through.</summary>
    public ObservableCollection<PasteLanguage> Languages { get; } = new();

    /// <summary>What the file will be called, minus its extension. Optional.</summary>
    public string Title
    {
        get => title;
        set
        {
            SetField(ref title, value ?? "");
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(FooterText));
        }
    }

    /// <summary>The paste itself.</summary>
    public string Content
    {
        get => content;
        set
        {
            SetField(ref content, value ?? "");
            Problem = "";
            OnPropertyChanged(nameof(FooterText));
        }
    }

    /// <summary>The chosen language.</summary>
    public PasteLanguage Language
    {
        get => language;
        set
        {
            SetField(ref language, value ?? PasteLanguages.Default);
            OnPropertyChanged(nameof(LanguageName));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(FooterText));
        }
    }

    /// <summary>The name on the button that opens the language list.</summary>
    public string LanguageName => Language.Name;

    /// <summary>What has been typed into the language search box.</summary>
    public string LanguageFilter
    {
        get => languageFilter;
        set
        {
            SetField(ref languageFilter, value ?? "");
            ApplyFilter();
        }
    }

    /// <summary>True when the search has excluded everything, so the list can say so.</summary>
    public bool NoLanguageMatches => Languages.Count == 0;

    /// <summary>The one refusal this window makes on its own, or empty.</summary>
    public string Problem
    {
        get => problem;
        set
        {
            SetField(ref problem, value ?? "");
            OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => Problem.Length > 0;

    /// <summary>
    /// The name the file goes up under.
    /// </summary>
    /// <remarks>
    /// Sanitised the way the web panel sanitises a paste title - anything outside
    /// letters, digits, dot, dash and underscore collapses to a dash, and the whole is
    /// cut to eighty characters - so the same title typed in either place produces the
    /// same name. An empty title becomes "paste", which is what the website calls one.
    /// </remarks>
    public string FileName => SanitizeTitle(Title) + "." + Language.Extension;

    /// <summary>The line under the editor: how much there is, and what it will become.</summary>
    public string FooterText =>
        string.Format(CultureInfo.CurrentCulture, Strings.Paste_Footer,
            Content.Length.ToString("N0", CultureInfo.CurrentCulture), FileName);

    /// <summary>
    /// Start the upload, or explain why not.
    /// </summary>
    /// <returns>True when a task was started and the window may close.</returns>
    /// <remarks>
    /// Goes through <see cref="TaskManager"/> like every other upload rather than talking
    /// to the uploader directly, which is what makes a paste behave as one: it appears in
    /// the strip at the bottom of the window while it goes, the toast comes up when it
    /// lands, the link reaches the clipboard by whichever rule the account chose, and the
    /// row joins the history in this tab. A private HTTP call here would have to
    /// reimplement all four, and would drift from them.
    /// </remarks>
    public bool TrySend()
    {
        string text = Content;

        if (text.Trim().Length == 0)
        {
            Problem = Strings.Paste_Empty;
            return false;
        }

        if (text.Length > MaxCharacters)
        {
            Problem = string.Format(CultureInfo.CurrentCulture, Strings.Paste_TooLarge,
                MaxCharacters.ToString("N0", CultureInfo.CurrentCulture));
            return false;
        }

        // No byte-order mark: the server sniffs the first bytes to decide what it was
        // sent, and a BOM in front of a shebang is one more thing for it to see through.
        // GetBytes never emits one, which is also how WorkerTask builds its text buffer.
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        // Handed over, not borrowed: WorkerTask disposes Data when the task ends.
        MemoryStream stream = new(bytes);

        TaskSettings settings = TaskSettings.GetDefaultTaskSettings();

        // EDataType.File, not EDataType.Text. Text would be routed by the TEXT
        // destination and named from the naming pattern plus the global text extension,
        // which is one setting shared by every paste - the language picked in this window
        // would be dropped on the way out. As a file the name is ours, and the name is
        // the only place the language survives the trip.
        WorkerTask task = WorkerTask.CreateDataUploaderTask(EDataType.File, stream, FileName, settings);
        TaskManager.Start(task);
        return true;
    }

    /// <summary>Everything the search box lets through, or everything when it is empty.</summary>
    private void ApplyFilter()
    {
        string query = LanguageFilter.Trim();

        Languages.Clear();

        foreach (PasteLanguage entry in PasteLanguages.All)
        {
            if (PasteLanguages.Matches(entry, query))
            {
                Languages.Add(entry);
            }
        }

        OnPropertyChanged(nameof(NoLanguageMatches));
    }

    /// <summary>A title turned into a filename stem, or "paste" when nothing usable is left.</summary>
    private static string SanitizeTitle(string value)
    {
        StringBuilder builder = new();
        bool lastWasDash = false;

        foreach (char c in value.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-')
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                // Runs collapse rather than repeat, so a title with spaces around a dash
                // becomes one dash and not three.
                builder.Append('-');
                lastWasDash = true;
            }

            if (builder.Length >= 80)
            {
                break;
            }
        }

        string name = builder.ToString().Trim('-');
        return name.Length > 0 ? name : "paste";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
