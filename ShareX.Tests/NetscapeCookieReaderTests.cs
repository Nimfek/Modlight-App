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
using System.Text;

namespace ShareX.Tests;

/// <summary>
/// Reading a jar back: which names are in it, and which lines are not cookies at all.
/// </summary>
/// <remarks>
/// <para>
/// The reader answers one question - is there a session on this PC - and the Download tab
/// paints a dot red or green from the answer. Both ways of getting it wrong are silent and
/// both are worse than having no dot: green over a jar that authenticates nobody sends
/// somebody hunting through yt-dlp's output for a refusal the app already knew about, and
/// red over a working session invites them to sign in again to fix nothing.
/// </para>
/// <para>
/// The trap this file exists for is <c>#HttpOnly_</c>. Python's own cookie reader skips
/// every line beginning with a hash; yt-dlp's subclass carves out that one prefix, writes
/// it when it saves and strips it when it loads. Google's <c>__Secure-1PSID</c> is
/// httpOnly, and yt-dlp rewrites the jar at the end of every run - so a reader that took
/// the prefix for a comment would report "signed out" for every jar that has been through
/// a download, which is every jar after the first one. Nothing about that failure looks
/// like a parser bug from the outside.
/// </para>
/// <para>
/// Run it with <c>dotnet run --project ShareX.Tests</c>. Exit code 0 is a pass.
/// </para>
/// </remarks>
internal static class NetscapeCookieReaderTests
{
    private static int failures;

    internal static int Run()
    {
        failures = 0;

        RoundTripsTheNamesItWrote();
        HttpOnlyPrefixIsACookieNotAComment();
        CommentsAndBlankLinesAreSkipped();
        EightColumnLineIsDropped();
        ShortLineIsDropped();
        NamelessCookieIsDropped();
        HeaderOnlyJarHasNoNames();

        Console.WriteLine(failures == 0
            ? "NetscapeCookieFile reader: all checks passed."
            : "NetscapeCookieFile reader: " + failures + " check(s) FAILED.");

        return failures;
    }

    /// <summary>
    /// What the writer wrote is what the reader finds, in order.
    /// </summary>
    /// <remarks>
    /// The two halves live in one file and could drift together without either noticing.
    /// This is the check that they still agree about where a name is.
    /// </remarks>
    private static void RoundTripsTheNamesItWrote()
    {
        List<NetscapeCookie> jar = new()
        {
            new NetscapeCookie(".youtube.com", "/", "__Secure-3PSID", "abc123", true, 1798761600),
            new NetscapeCookie("www.youtube.com", "/watch", "PREF", "f6=40000000", true, 1767225600),
            new NetscapeCookie(".google.com", "/", "SID", "zz", true, 0)
        };

        Same("round trip", "__Secure-3PSID,PREF,SID", Names(NetscapeCookieFile.Build(jar)));
    }

    /// <summary>
    /// A jar in the form yt-dlp saves, which is the form nearly every real jar is in.
    /// </summary>
    /// <remarks>
    /// Written out by hand rather than produced by the writer above, because the writer
    /// never emits this prefix - only yt-dlp does, on its way out of a finished download.
    /// A round trip through our own code cannot reach this case at all.
    /// </remarks>
    private static void HttpOnlyPrefixIsACookieNotAComment()
    {
        string jar =
            "# Netscape HTTP Cookie File\n" +
            "\n" +
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t2000000000\t__Secure-1PSID\tvalue-here\n" +
            ".youtube.com\tTRUE\t/\tFALSE\t2000000000\tPREF\ttz=Europe.Warsaw\n";

        Same("httpOnly cookie is read", "__Secure-1PSID,PREF", Names(jar));
    }

    private static void CommentsAndBlankLinesAreSkipped()
    {
        string jar =
            "# Netscape HTTP Cookie File\n" +
            "# Written by Modlight. Do not edit and do not share: these lines are a live login.\n" +
            "\n" +
            "#.youtube.com\tTRUE\t/\tTRUE\t2000000000\tCOMMENTED_OUT\tv\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t2000000000\tSID\tv\n";

        Same("comments skipped", "SID", Names(jar));
    }

    /// <summary>
    /// Eight columns is not a cookie, because yt-dlp will not load it as one.
    /// </summary>
    /// <remarks>
    /// Its loader takes lines of exactly seven fields and drops the rest with a warning
    /// nobody reads. A name lifted out of a line that is never going to be sent would be a
    /// green dot over a request that goes out anonymous.
    /// </remarks>
    private static void EightColumnLineIsDropped()
    {
        string jar =
            "# Netscape HTTP Cookie File\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t2000000000\t__Secure-1PSID\tvalue\textra\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t2000000000\tSID\tv\n";

        Same("eight columns dropped", "SID", Names(jar));
    }

    private static void ShortLineIsDropped()
    {
        string jar =
            "# Netscape HTTP Cookie File\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t2000000000\t__Secure-1PSID\n" +
            "just some text\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t2000000000\tSID\tv\n";

        Same("short lines dropped", "SID", Names(jar));
    }

    private static void NamelessCookieIsDropped()
    {
        string jar =
            "# Netscape HTTP Cookie File\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t2000000000\t\tvalue\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t2000000000\tSID\tv\n";

        Same("nameless cookie dropped", "SID", Names(jar));
    }

    /// <summary>
    /// The case that made the reader necessary in the first place.
    /// </summary>
    /// <remarks>
    /// This file is not empty and it is a valid jar. It authenticates nobody. The check it
    /// replaced - the file exists and its length is above zero - called this signed in.
    /// </remarks>
    private static void HeaderOnlyJarHasNoNames()
    {
        string jar =
            "# Netscape HTTP Cookie File\n" +
            "# Written by Modlight. Do not edit and do not share: these lines are a live login.\n" +
            "\n";

        Same("header-only jar", "", Names(jar));
    }

    /// <summary>The names in a jar with these contents, comma separated.</summary>
    /// <remarks>
    /// Through a real file on disk, because that is the only interface the reader has and
    /// because line splitting is part of what is being tested.
    /// </remarks>
    private static string Names(string jar)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "modlight-jar-" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            File.WriteAllText(path, jar, new UTF8Encoding(false));

            return string.Join(",", NetscapeCookieFile.ReadNames(path));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // A temp file that would not go away is not a failed assertion.
            }
        }
    }

    private static void Same(string what, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Fail(what + "\n  expected: " + expected + "\n  actual:   " + actual);
        }
    }

    private static void Fail(string message)
    {
        failures++;
        Console.WriteLine("FAIL: " + message);
    }
}
