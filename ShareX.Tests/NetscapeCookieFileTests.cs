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
/// The cookie jar writer, checked against output written out by hand.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because every way of getting the Netscape format wrong is SILENT.
/// yt-dlp does not reject a malformed jar - it drops the lines it cannot parse, warns on a
/// stream nobody is reading, and carries on as an anonymous client. The symptom of a
/// mis-written column is therefore not an error about cookies: it is the same 403 the
/// sign-in was supposed to fix, which sends the next person looking at the browser, at
/// Google, and at everything except the seven columns below.
/// </para>
/// <para>
/// So the expectations here are literal text, written by hand from the format definition
/// rather than captured from the code. A test whose expected value came out of the thing
/// it is testing only proves the code has not changed.
/// </para>
/// <para>
/// Run it with <c>dotnet run --project ShareX.Tests</c>. Exit code 0 is a pass.
/// </para>
/// </remarks>
internal static class NetscapeCookieFileTests
{
    private static int failures;

    /// <param name="args">
    /// Optionally one path. Given it, the sample jar is also written there - so that the
    /// bytes this code really produces can be handed to yt-dlp's own loader, rather than a
    /// hand-typed copy of them that agrees with nothing but itself.
    /// </param>
    internal static int Run(string[] args)
    {
        failures = 0;

        WritesTheExpectedFileExactly();
        SessionCookieHasAnEmptyExpiryNotAZero();
        DomainDotDecidesTheSecondColumn();
        EmptyPathBecomesRoot();
        EveryLineHasSevenColumns();
        ExpiryIsEmptyOrAllDigits();
        UnrepresentableCookiesAreDropped();
        CountMatchesTheLinesWritten();
        FileStartsWithTheMagicAndHasNoByteOrderMark();

        if (args.Length > 0)
        {
            NetscapeCookieFile.Write(args[0], Jar());
            Console.WriteLine("Sample jar written to " + args[0]);
        }

        Console.WriteLine(failures == 0
            ? "NetscapeCookieFile: all checks passed."
            : "NetscapeCookieFile: " + failures + " check(s) FAILED.");

        return failures;
    }

    /// <summary>
    /// A realistic jar, against text written out column by column from the format.
    /// </summary>
    /// <remarks>
    /// One assertion over the whole file rather than nine over its parts, because the
    /// failure this is guarding against is structural - a column in the wrong place, a
    /// separator that became a space, a header that grew a line. Any of those changes the
    /// whole string, and none of them changes a per-field check.
    /// </remarks>
    private static void WritesTheExpectedFileExactly()
    {
        string actual = NetscapeCookieFile.Build(Jar(), out int written);

        string expected =
            "# Netscape HTTP Cookie File\n" +
            "# Written by Modlight. Do not edit and do not share: these lines are a live login.\n" +
            "\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t1798761600\t__Secure-3PSID\tabc123\n" +
            "www.youtube.com\tFALSE\t/watch\tTRUE\t1767225600\tPREF\tf6=40000000\n" +
            ".youtube.com\tTRUE\t/\tFALSE\t1780000000\tVISITOR_INFO1_LIVE\tAb-cD_1\n" +
            ".google.com\tTRUE\t/\tTRUE\t\tSID\tzz\n";

        Same("whole file", expected, actual);
        Same("count", "4", written.ToString());
    }

    /// <summary>
    /// A session cookie's expiry column is EMPTY.
    /// </summary>
    /// <remarks>
    /// Not a zero. Zero is 1 January 1970, which is a cookie that expired fifty-six years
    /// ago; empty is what Python's own writer emits and what its loader reads back as
    /// "session". They survive either way because yt-dlp loads with ignore_expires, but a
    /// jar that says something false about its own cookies is a jar somebody will one day
    /// filter on.
    /// </remarks>
    private static void SessionCookieHasAnEmptyExpiryNotAZero()
    {
        string line = OnlyLine(new NetscapeCookie(".google.com", "/", "SID", "zz", true, 0));

        Same("session expiry column", "", Column(line, 4));

        // Negative and MinValue arrive here too - the browser's date for a session cookie
        // is a sentinel, and which sentinel is not ours to depend on.
        Same("negative expiry column", "",
            Column(OnlyLine(new NetscapeCookie(".google.com", "/", "SID", "zz", true, -1)), 4));
    }

    /// <summary>
    /// Column two is the leading dot, and nothing else.
    /// </summary>
    /// <remarks>
    /// It is not an independent flag: Python writes TRUE exactly when the domain starts
    /// with a dot and reads it straight back into domain_specified. Setting it any other
    /// way produces a jar that round-trips wrong through the loader.
    /// </remarks>
    private static void DomainDotDecidesTheSecondColumn()
    {
        Same("dotted domain", "TRUE",
            Column(OnlyLine(new NetscapeCookie(".youtube.com", "/", "A", "1", true, 1)), 1));

        Same("host-only domain", "FALSE",
            Column(OnlyLine(new NetscapeCookie("www.youtube.com", "/", "A", "1", true, 1)), 1));
    }

    private static void EmptyPathBecomesRoot()
    {
        Same("empty path", "/",
            Column(OnlyLine(new NetscapeCookie(".youtube.com", "", "A", "1", true, 1)), 2));
    }

    /// <summary>
    /// Seven fields on every line, because six or eight means the line is thrown away.
    /// </summary>
    /// <remarks>
    /// yt-dlp's loader raises on any count but seven and skips the entry. This is the
    /// check that would have caught a stray separator in the header or a value that
    /// smuggled a tab through.
    /// </remarks>
    private static void EveryLineHasSevenColumns()
    {
        foreach (string line in DataLines(NetscapeCookieFile.Build(Jar())))
        {
            Same("column count of " + line, "7", line.Split('\t').Length.ToString());
        }
    }

    /// <summary>The expiry column is empty or digits, which is the other rule that drops lines.</summary>
    private static void ExpiryIsEmptyOrAllDigits()
    {
        foreach (string line in DataLines(NetscapeCookieFile.Build(Jar())))
        {
            string expires = Column(line, 4);

            foreach (char c in expires)
            {
                if (c is < '0' or > '9')
                {
                    Fail("expiry column is not digits: " + expires);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// A cookie that cannot be written is left out, not written badly.
    /// </summary>
    /// <remarks>
    /// The format has no escape character, so a tab in a value would silently become an
    /// extra column and take that line - or, with a newline, the rest of the jar - with it.
    /// </remarks>
    private static void UnrepresentableCookiesAreDropped()
    {
        List<NetscapeCookie> bad = new()
        {
            new NetscapeCookie("", "/", "NAME", "v", true, 1),
            new NetscapeCookie(".youtube.com", "/", "", "v", true, 1),
            new NetscapeCookie(".youtube.com", "/", "TAB", "a\tb", true, 1),
            new NetscapeCookie(".youtube.com", "/", "NEWLINE", "a\nb", true, 1),
            new NetscapeCookie(".youtube.com", "/", "RETURN", "a\rb", true, 1),
            new NetscapeCookie("bad\tdomain.com", "/", "D", "v", true, 1)
        };

        string text = NetscapeCookieFile.Build(bad, out int written);

        Same("nothing written", "0", written.ToString());
        Same("no data lines", "0", DataLines(text).Count.ToString());
    }

    private static void CountMatchesTheLinesWritten()
    {
        string text = NetscapeCookieFile.Build(Jar(), out int written);

        Same("count matches lines", DataLines(text).Count.ToString(), written.ToString());
    }

    /// <summary>
    /// On disk: the magic at byte zero, and no byte order mark in front of it.
    /// </summary>
    /// <remarks>
    /// The one check that has to touch the file system, because a BOM is invisible in a
    /// string and fatal in the file: Python matches the magic at position zero, so three
    /// bytes ahead of the hash sign make the whole jar unreadable and the sign-in a
    /// no-op.
    /// </remarks>
    private static void FileStartsWithTheMagicAndHasNoByteOrderMark()
    {
        string path = Path.Combine(Path.GetTempPath(), "modlight-cookie-test-" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            NetscapeCookieFile.Write(path, Jar());

            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                Fail("file starts with a UTF-8 byte order mark");
            }

            string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(27, bytes.Length));

            Same("magic line", "# Netscape HTTP Cookie File", head);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // A leftover file in the temp folder is not a failing test.
            }
        }
    }

    /// <summary>
    /// The sample jar: a domain cookie, a host cookie, a non-secure one, a session one, and
    /// three that cannot be represented at all.
    /// </summary>
    /// <remarks>
    /// The leading dot and the secure flag DISAGREE on two of these rows, and that is not
    /// decoration. Written the obvious way - every cookie secure, every domain dotted -
    /// columns two and four hold the same value on every line, and a writer that swapped
    /// them produced byte-identical output. The first draft of this file did exactly that,
    /// and the whole-file check passed while the writer was wrong.
    /// </remarks>
    private static List<NetscapeCookie> Jar() => new()
    {
        new NetscapeCookie(".youtube.com", "/", "__Secure-3PSID", "abc123", true, 1798761600),
        new NetscapeCookie("www.youtube.com", "/watch", "PREF", "f6=40000000", true, 1767225600),
        new NetscapeCookie(".youtube.com", "", "VISITOR_INFO1_LIVE", "Ab-cD_1", false, 1780000000),
        new NetscapeCookie(".google.com", "/", "SID", "zz", true, 0),
        new NetscapeCookie("", "/", "NODOMAIN", "x", true, 5),
        new NetscapeCookie(".youtube.com", "/", "", "noname", true, 5),
        new NetscapeCookie(".youtube.com", "/", "TABBY", "a\tb", true, 5)
    };

    private static string OnlyLine(NetscapeCookie cookie)
    {
        List<string> lines = DataLines(NetscapeCookieFile.Build(new List<NetscapeCookie> { cookie }));

        return lines.Count == 1 ? lines[0] : "";
    }

    /// <summary>Everything that is not the header and not blank.</summary>
    private static List<string> DataLines(string text)
    {
        List<string> lines = new();

        foreach (string line in text.Split('\n'))
        {
            if (line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static string Column(string line, int index)
    {
        string[] parts = line.Split('\t');

        return index < parts.Length ? parts[index] : "<missing>";
    }

    private static void Same(string what, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Fail(what + "\n  expected: " + Show(expected) + "\n  actual:   " + Show(actual));
        }
    }

    private static void Fail(string message)
    {
        failures++;
        Console.WriteLine("FAIL: " + message);
    }

    /// <summary>Tabs and newlines named rather than printed - they are the thing being tested.</summary>
    private static string Show(string value) =>
        value.Replace("\t", "<TAB>").Replace("\n", "<LF>\n").Replace("\r", "<CR>");
}
