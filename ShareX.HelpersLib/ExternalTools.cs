#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.IO;

namespace ShareX.HelpersLib;

/// <summary>
/// Where an external executable is, when it is not sitting beside the application.
/// </summary>
/// <remarks>
/// <para>
/// Two places resolve an external tool by bare file name - <c>FFmpegOptions.FFmpegPath</c>
/// and <c>MetadataService.ExifToolPath</c> - and both did it with
/// <see cref="FileHelpers.GetAbsolutePath(string)"/>, which resolves a relative path against
/// the folder the executable is in. That is the right first answer and the wrong only one:
/// the default install directory is under Program Files, a standard user cannot write there,
/// and the whole point of fetching a component after installation is that it has to land
/// somewhere the running application can actually write.
/// </para>
/// <para>
/// So the answer is now "beside the executable, and failing that the managed folder", and
/// this type exists because neither ShareX.ScreenCaptureLib nor ShareX.Tools can see the
/// application project that knows where that folder is. The application pushes the path in
/// at startup (Program.UpdateHelpersSpecialFolders), the same way it already pushes the
/// proxy and the special folders.
/// </para>
/// <para>
/// When nothing is found the answer is still the path beside the executable, deliberately:
/// callers put it in "not found at ..." messages, and the place a person expects to look is
/// the application folder, not a staging directory they have never heard of.
/// </para>
/// </remarks>
public static class ExternalTools
{
    /// <summary>
    /// The folder fetched components are installed into, or empty before startup sets it.
    /// </summary>
    public static string ManagedFolder { get; set; } = string.Empty;

    /// <summary>The full path of a managed component, or empty when there is no folder yet.</summary>
    public static string ManagedPath(string fileName)
    {
        string folder = ManagedFolder;

        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        try
        {
            return Path.Combine(folder, fileName);
        }
        catch (Exception)
        {
            // A malformed personal path is not a reason to take a capture down with it.
            return string.Empty;
        }
    }

    /// <summary>
    /// The path to use for a tool: beside the executable if it is there, else the managed
    /// copy, else the path beside the executable so the message names somewhere sensible.
    /// </summary>
    public static string Locate(string fileName)
    {
        string beside;

        try
        {
            beside = FileHelpers.GetAbsolutePath(fileName);
        }
        catch (Exception)
        {
            beside = fileName;
        }

        if (Exists(beside))
        {
            return beside;
        }

        string managed = ManagedPath(fileName);

        return Exists(managed) ? managed : beside;
    }

    private static bool Exists(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
