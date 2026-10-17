#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using System;
using System.IO;

namespace ShareX;

/// <summary>
/// Finding the two executables the Download tab drives, and saying so when they are not there.
/// </summary>
/// <remarks>
/// yt-dlp is NOT bundled and NOT fetched automatically. It is a tool that reaches out to
/// a hundred sites and needs updating every few weeks to keep working; shipping a copy
/// inside Modlight would mean shipping a stale one, and downloading an executable on the
/// user's behalf is a thing an app should not do quietly. So the tab looks for it, and
/// when it is absent it says so in place of the queue with a button to the install page -
/// which is a state the user can act on, unlike a download that fails for no stated reason.
///
/// The search order copies <c>FFmpegOptions.FFmpegPath</c> for its first step - beside
/// the executable, which is where a portable install keeps its tools - and then widens to
/// the personal folder and PATH, because a pip, winget or scoop install lands on PATH and
/// that is how nearly everyone will have it.
///
/// Only <c>.exe</c> is accepted. A <c>.cmd</c> or <c>.bat</c> shim cannot be started
/// through <see cref="System.Diagnostics.ProcessStartInfo"/> with UseShellExecute off,
/// and going through a shell to run one would mean handing a command line to cmd.exe
/// with a URL in it.
/// </remarks>
internal static class YtDlpTool
{
    /// <summary>Where somebody sent to install yt-dlp should land.</summary>
    public const string InstallPage = "https://github.com/yt-dlp/yt-dlp/wiki/Installation";

    /// <summary>
    /// The QuickJS binary, under the name yt-dlp looks for.
    /// </summary>
    /// <remarks>
    /// yt-dlp resolves the "quickjs" runtime by looking for an executable called
    /// <c>qjs</c> (yt_dlp/utils/_jsruntime.py), so the name is not ours to choose.
    /// </remarks>
    private const string QuickJsFileName = "qjs.exe";

    /// <summary>Where the installer puts the tools we ship, relative to the app folder.</summary>
    private const string BundledToolsFolder = "Tools";

    private static readonly object gate = new();

    private static string? ytDlpPath;
    private static string? ffmpegPath;
    private static string? quickJsPath;
    private static bool searched;

    /// <summary>The yt-dlp executable, or null when this PC has none.</summary>
    public static string? YtDlpPath
    {
        get
        {
            Ensure();
            return ytDlpPath;
        }
    }

    /// <summary>
    /// The ffmpeg executable, or null.
    /// </summary>
    /// <remarks>
    /// Not fatal when missing, unlike yt-dlp: without it the tab asks for a format that
    /// needs no merging, which costs quality on YouTube and nothing anywhere else. The
    /// app already ships ffmpeg for screen recording, so on a normal install this is the
    /// copy beside the executable.
    /// </remarks>
    public static string? FFmpegPath
    {
        get
        {
            Ensure();
            return ffmpegPath;
        }
    }

    /// <summary>The QuickJS executable, or null when neither ours nor one of theirs is there.</summary>
    /// <remarks>
    /// <para>
    /// This one IS bundled, and the reasoning that keeps yt-dlp out of the installer does
    /// not apply to it. yt-dlp reaches a hundred sites and goes stale in weeks; QuickJS is
    /// a JavaScript engine being handed a script yt-dlp generates, so a copy that works
    /// today keeps working, and it is what makes "all-in-one" true rather than a claim
    /// with an install page attached.
    /// </para>
    /// <para>
    /// What it buys is not cosmetic. Without any JS runtime yt-dlp 2026.03 falls back to
    /// <c>_DEFAULT_JSLESS_CLIENTS</c>, which is the android_vr client alone, and prints a
    /// deprecation warning saying formats may be missing. Measured against a real
    /// made-for-kids video, that fallback does not merely lose formats: android_vr cannot
    /// play it, there is no web_embedded fallback without a runtime, and yt-dlp exits 1
    /// with "This video is not available". With QuickJS the same URL lists 27 formats.
    /// </para>
    /// <para>
    /// Ours is preferred over a copy on PATH deliberately - it is the build this was
    /// tested against, and yt-dlp warns that the QuickJS-ng fork somebody may have
    /// installed is "very slow". Preferring it costs a user nothing, because a better
    /// runtime still wins: see <see cref="YtDlpRun"/> on why deno is left alone.
    /// </para>
    /// </remarks>
    public static string? QuickJsPath
    {
        get
        {
            Ensure();
            return quickJsPath;
        }
    }

    public static bool IsAvailable => YtDlpPath != null;

    /// <summary>
    /// Look again.
    /// </summary>
    /// <remarks>
    /// Behind the "check again" button, and that button is the whole reason the answer is
    /// cached rather than recomputed: somebody who has just installed yt-dlp in a terminal
    /// should not have to restart Modlight, and everybody else should not pay a PATH walk
    /// on every keystroke.
    /// </remarks>
    public static void Rescan()
    {
        lock (gate)
        {
            searched = false;
        }
    }

    private static void Ensure()
    {
        lock (gate)
        {
            if (searched)
            {
                return;
            }

            ytDlpPath = Find("yt-dlp.exe");
            ffmpegPath = Find("ffmpeg.exe");

            // Our own copy first, then the ordinary search, so a portable unzip that
            // dropped one beside the executable or a PATH install still counts.
            quickJsPath = Exists(SafeAbsolute(SafeCombine(BundledToolsFolder, QuickJsFileName))) ??
                Find(QuickJsFileName);

            searched = true;

            DebugHelper.WriteLine("Download: yt-dlp is " + (ytDlpPath ?? "not installed") +
                ", ffmpeg is " + (ffmpegPath ?? "not installed") +
                ", QuickJS is " + (quickJsPath ?? "not installed") + ".");
        }
    }

    private static string? Find(string fileName)
    {
        // Beside the executable first - a portable install carries its own tools, and one
        // that has been put there deliberately should win over whatever is on PATH.
        string? beside = Exists(SafeAbsolute(fileName));

        if (beside != null)
        {
            return beside;
        }

        string? personal = Exists(SafeCombine(PersonalFolder(), "Tools", fileName));

        if (personal != null)
        {
            return personal;
        }

        return FindOnPath(fileName);
    }

    private static string? FindOnPath(string fileName)
    {
        string path;

        try
        {
            path = Environment.GetEnvironmentVariable("PATH") ?? "";
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return null;
        }

        foreach (string entry in path.Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');

            if (directory.Length == 0)
            {
                continue;
            }

            string? found = Exists(SafeCombine(directory, fileName));

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The personal folder, or empty when it cannot be read.
    /// </summary>
    /// <remarks>
    /// It reads a config file the first time it is asked, and this runs while the tab is
    /// being drawn. A tool search is not a reason to take the window down with it.
    /// </remarks>
    private static string PersonalFolder()
    {
        try
        {
            return Program.PersonalFolder ?? "";
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
            return "";
        }
    }

    private static string SafeAbsolute(string fileName)
    {
        try
        {
            return FileHelpers.GetAbsolutePath(fileName);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Join path pieces without letting a malformed PATH entry throw.
    /// </summary>
    /// <remarks>
    /// PATH is edited by hand on most machines and routinely holds entries with quotes,
    /// stray characters or variables that never expanded. One of those must skip its own
    /// directory, not end the search.
    /// </remarks>
    private static string SafeCombine(params string[] parts)
    {
        try
        {
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part))
                {
                    return "";
                }
            }

            return Path.Combine(parts);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string? Exists(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
