#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ShareX;

/// <summary>One cookie, in the four facts the file format has room for.</summary>
/// <remarks>
/// Deliberately not the browser's own cookie type. The writer below is the piece that has
/// to be exactly right and so the piece worth testing, and a test that has to start a
/// browser in order to make one cookie is a test nobody runs.
/// </remarks>
internal readonly struct NetscapeCookie
{
    public NetscapeCookie(string domain, string path, string name, string value, bool secure, long expiresUnix)
    {
        Domain = domain ?? "";
        Path = path ?? "";
        Name = name ?? "";
        Value = value ?? "";
        Secure = secure;
        ExpiresUnix = expiresUnix;
    }

    /// <summary>With the leading dot when it is a domain cookie, exactly as the browser stores it.</summary>
    public string Domain { get; }

    public string Path { get; }

    public string Name { get; }

    public string Value { get; }

    public bool Secure { get; }

    /// <summary>Seconds since the epoch, or zero or less for a session cookie.</summary>
    public long ExpiresUnix { get; }
}

/// <summary>
/// Writing a cookie jar in the Netscape format, which is the one yt-dlp reads.
/// </summary>
/// <remarks>
/// <para>
/// Seven TAB-separated columns and no quoting whatsoever: <c>domain</c>,
/// <c>include-subdomains</c>, <c>path</c>, <c>https-only</c>, <c>expires</c>, <c>name</c>,
/// <c>value</c>. Every mistake it is possible to make here is silent. yt-dlp's loader
/// drops any line whose column count is not exactly seven, and any line whose expiry
/// column is neither empty nor all digits, with a warning on stderr that nobody sees - so
/// a file with one column too many is a file with no cookies in it, and the only symptom
/// is the same 403 the sign-in was supposed to fix.
/// </para>
/// <para>
/// Three details are load-bearing, and none of them are guesses: they are what Python's
/// own <c>MozillaCookieJar.save</c> emits, which is what the loader on the other side
/// expects to read.
/// </para>
/// <list type="bullet">
/// <item><description>
/// The FIRST LINE must match <c>#( Netscape)? HTTP Cookie File</c> at position zero -
/// <c>MozillaCookieJar._really_load</c> reads it before anything else and raises
/// <c>LoadError</c> if it does not. That is also why this is written as UTF-8 with NO byte
/// order mark: three bytes in front of the <c>#</c> and the whole jar is refused.
/// </description></item>
/// <item><description>
/// Column two is TRUE exactly when the domain starts with a dot. It is not a separate fact
/// from the domain and must never be set independently of it.
/// </description></item>
/// <item><description>
/// A session cookie has an EMPTY expiry column, not a zero - a zero is a date in 1970.
/// yt-dlp loads with <c>ignore_expires</c> so both survive, but empty is the form the
/// format defines and the form the reference implementation produces.
/// </description></item>
/// </list>
/// <para>
/// Newlines are <c>\n</c> rather than the repository's usual CRLF. Python reads this in
/// text mode, where either works, and a person reads it when something has gone wrong,
/// where the single-byte form is the one every tool agrees about.
/// </para>
/// </remarks>
internal static class NetscapeCookieFile
{
    /// <summary>The magic first line. Nothing may precede it, a byte order mark included.</summary>
    private const string Magic = "# Netscape HTTP Cookie File";

    private const char Separator = '\t';

    /// <summary>
    /// What yt-dlp puts in front of the domain of an httpOnly cookie.
    /// </summary>
    /// <remarks>
    /// Not a comment, despite the <c>#</c>. Python's own reader skips every line starting
    /// with one; yt-dlp's subclass carves out exactly this prefix, writes it when it saves
    /// and strips it again when it loads. It matters here because Google's
    /// <c>__Secure-1PSID</c> - the cookie the sign-in check looks for - is httpOnly, so a
    /// reader that took every <c>#</c> line for a comment would report "signed out" for
    /// every jar yt-dlp has ever saved, which is every jar after the first download.
    /// </remarks>
    private const string HttpOnlyPrefix = "#HttpOnly_";

    /// <summary>A cookie jar is a few dozen lines. Past this it is not one, so stop reading.</summary>
    private const int MaxLines = 4000;

    public static string Build(IEnumerable<NetscapeCookie> cookies) => Build(cookies, out _);

    /// <summary>
    /// The whole file as text, and how many cookies survived into it.
    /// </summary>
    /// <remarks>
    /// The count is the caller's only honest way to say "signed in". A jar that came back
    /// empty still produces a perfectly valid file, one that authenticates nobody.
    /// </remarks>
    public static string Build(IEnumerable<NetscapeCookie> cookies, out int written)
    {
        StringBuilder builder = new();

        builder.Append(Magic).Append('\n');
        builder.Append("# Written by Modlight. Do not edit and do not share: these lines are a live login.\n");
        builder.Append('\n');

        written = 0;

        if (cookies == null)
        {
            return builder.ToString();
        }

        foreach (NetscapeCookie cookie in cookies)
        {
            if (!IsUsable(cookie))
            {
                continue;
            }

            builder.Append(cookie.Domain).Append(Separator);

            // Not an independent fact: it IS whether the domain carries a leading dot.
            builder.Append(Flag(cookie.Domain.StartsWith(".", StringComparison.Ordinal))).Append(Separator);

            builder.Append(cookie.Path.Length > 0 ? cookie.Path : "/").Append(Separator);
            builder.Append(Flag(cookie.Secure)).Append(Separator);

            builder.Append(cookie.ExpiresUnix > 0
                ? cookie.ExpiresUnix.ToString(CultureInfo.InvariantCulture)
                : "").Append(Separator);

            builder.Append(cookie.Name).Append(Separator);
            builder.Append(cookie.Value).Append('\n');

            written++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Write the jar, replacing whatever was there, and say how many cookies it holds.
    /// </summary>
    /// <remarks>
    /// One call, from a string already built in full, so there is no moment at which the
    /// file on disk is half a cookie jar. A torn write would not fail loudly - it would
    /// produce a file yt-dlp loads part of, which is the same 403 with a longer path to
    /// the explanation.
    /// </remarks>
    public static int Write(string path, IEnumerable<NetscapeCookie> cookies)
    {
        string text = Build(cookies, out int written);

        string? folder = System.IO.Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // encoderShouldEmitUTF8Identifier is false. With it the magic line no longer starts
        // at byte zero, and Python refuses the entire file.
        File.WriteAllText(path, text, new UTF8Encoding(false));

        return written;
    }

    /// <summary>
    /// Whether this cookie can be represented at all.
    /// </summary>
    /// <remarks>
    /// The format has no escape character, so a tab or a newline inside a value would not
    /// produce a mangled cookie - it would produce a line with the wrong number of columns,
    /// which yt-dlp drops, or two lines, the second of which is nonsense. Neither can come
    /// out of a browser that really stored the cookie; dropping the impossible one keeps
    /// the rest of the jar loadable on the day one does.
    /// </remarks>
    private static bool IsUsable(NetscapeCookie cookie)
    {
        if (cookie.Domain.Length == 0 || cookie.Name.Length == 0)
        {
            return false;
        }

        return Clean(cookie.Domain) && Clean(cookie.Path) && Clean(cookie.Name) && Clean(cookie.Value);
    }

    private static bool Clean(string value) =>
        value.IndexOf('\t') < 0 && value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0;

    private static string Flag(bool value) => value ? "TRUE" : "FALSE";

    /// <summary>
    /// The cookie NAMES in a jar on disk. Never the values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so that "is there a session" is answered by the file rather than by what
    /// happened the last time somebody pressed a button. "The jar exists and is not empty"
    /// is a different and weaker fact: the two header lines alone are a non-empty file, and
    /// yt-dlp REWRITES the jar at the end of every run - so a session that stopped being
    /// accepted leaves behind a valid file with nothing in it that authenticates anybody.
    /// </para>
    /// <para>
    /// A line with eight columns is dropped rather than read, because that is what yt-dlp's
    /// loader does with it. A name taken from a line yt-dlp will not load would be a green
    /// light for a session that is never going to be sent.
    /// </para>
    /// <para>
    /// A cookie's value is never copied out of the line it arrived on, never returned and
    /// never logged. The names are the whole answer to the only question being asked, and
    /// this file is a live login: what is never extracted cannot end up where it should not.
    /// </para>
    /// </remarks>
    /// <exception cref="IOException">The jar could not be read. Callers treat that as signed out.</exception>
    public static List<string> ReadNames(string path)
    {
        List<string> names = new();
        int lines = 0;

        foreach (string raw in File.ReadLines(path))
        {
            if (++lines > MaxLines)
            {
                break;
            }

            string line = raw;

            if (line.StartsWith(HttpOnlyPrefix, StringComparison.Ordinal))
            {
                line = line.Substring(HttpOnlyPrefix.Length);
            }
            else if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string? name = NameOf(line);

            if (name != null)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Column six of seven - the name - or null when the line is not a cookie.
    /// </summary>
    /// <remarks>
    /// By index rather than by <c>Split</c>: splitting would build the value as a string of
    /// its own, and there is no reason for a credential to exist in memory as anything but
    /// the line it was read from.
    /// </remarks>
    private static string? NameOf(string line)
    {
        int start = 0;

        // domain, include-subdomains, path, https-only, expires: five separators to cross.
        for (int column = 0; column < 5; column++)
        {
            int next = line.IndexOf(Separator, start);

            if (next < 0)
            {
                return null;
            }

            start = next + 1;
        }

        int end = line.IndexOf(Separator, start);

        if (end < 0 || end == start)
        {
            // No value column, or a nameless cookie. Neither is a line yt-dlp would load.
            return null;
        }

        if (line.IndexOf(Separator, end + 1) >= 0)
        {
            return null;
        }

        return line.Substring(start, end - start);
    }
}
