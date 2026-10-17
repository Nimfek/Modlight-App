#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Collections.Generic;

namespace ShareX.Tests;

/// <summary>
/// Every suite in this project, run in one go.
/// </summary>
/// <remarks>
/// Still no test framework, for the reason the project file gives: these are assertions over
/// code with no dependencies, and a runner would be a package to install, restore and update
/// in exchange for something <c>dotnet run</c> and an exit code already do. What changed is
/// that there is more than one suite, so the entry point stopped belonging to any of them.
///
/// The exit code is the total number of failed checks, so a broken build says how broken.
/// </remarks>
internal static class TestRunner
{
    private static int Main(string[] args)
    {
        int failures = 0;

        // Positional arguments only. The cookie suite reads args[0] as "also write the
        // sample jar here", so handing it the raw command line made "--live" a file name and
        // dropped a cookie jar called --live in the repository root. Flags belong to the
        // runner; a suite that wants a path gets a path.
        failures += NetscapeCookieFileTests.Run(Positional(args));
        failures += NetscapeCookieReaderTests.Run();
        failures += ModlightUpdateTests.Run(LiveHost(args));
        failures += ModlightToolUpdateTests.Run(Live(args, "--live-tools"));
        failures += ModlightSettingsSyncTests.Run();
        failures += ModlightLibraryTests.Run();
        failures += DownloadFormatTests.Run();
        failures += YtDlpErrorTests.Run();
        failures += TranslationCoverageTests.Run();

        Console.WriteLine(failures == 0
            ? "All suites passed."
            : failures + " check(s) FAILED across all suites.");

        return failures;
    }

    /// <summary>
    /// The origin passed as <c>--live https://host.example.com</c>, or null.
    /// </summary>
    /// <remarks>
    /// Opt-in, never a default. The update suite has to pass on a machine with no network,
    /// because that is the exact condition the code it tests has to survive.
    /// </remarks>
    private static string? LiveHost(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--live", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a bare flag was passed.
    /// </summary>
    /// <remarks>
    /// <c>--live-tools</c> makes the tool-update suite also ask github.com what yt-dlp has
    /// published and print it. Off by default, like every other live check here: a suite
    /// that needs the internet is a suite that fails on a train, and yt-dlp's release
    /// schedule is not ours to hold still.
    /// </remarks>
    private static bool Live(string[] args, string flag)
    {
        foreach (string argument in args)
        {
            if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Everything that is not a flag or a flag's value.</summary>
    private static string[] Positional(string[] args)
    {
        List<string> kept = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            kept.Add(args[i]);
        }

        return kept.ToArray();
    }
}
