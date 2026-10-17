#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ShareX.HelpersLib;

/// <summary>One published release, exactly as GET /api/releases/app describes it.</summary>
/// <remarks>
/// An instance of this only ever comes out of <see cref="ModlightRelease.TryParse"/>, and
/// that method refuses anything it cannot use - so every field here is already known good
/// by the time anybody reads it. In particular <see cref="Sha256"/> is never empty and
/// <see cref="Url"/> is never a scheme we would not fetch: the alternative is a caller
/// somewhere down the line deciding for itself whether a missing digest is worth stopping
/// for, and that decision has exactly one safe answer, so it is made once, here.
/// </remarks>
public sealed class ModlightReleaseManifest
{
    /// <summary>The product name the server published under, for display only.</summary>
    public string Name { get; init; } = "";

    /// <summary>The version string as published, kept verbatim so the user sees what the server said.</summary>
    public string VersionText { get; init; } = "";

    /// <summary>The same version parsed. This is the only form that is ever compared.</summary>
    public Version Version { get; init; } = new Version(0, 0);

    /// <summary>Where the installer is. https, or http on the loopback interface.</summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// A file name that is safe to join onto a temp directory - built from the manifest,
    /// never taken from it verbatim. See <see cref="ModlightRelease.SafeInstallerFileName"/>.
    /// </summary>
    public string FileName { get; init; } = "";

    /// <summary>Size in bytes, or 0 when the server did not say. Informational: the digest is the check.</summary>
    public long Size { get; init; }

    /// <summary>Lowercase hex, 64 characters. Never empty - a manifest without one does not parse.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Release notes, trimmed to something a dialog can hold.</summary>
    public string Notes { get; init; } = "";

    /// <summary>When it was published, or null when the server did not say or said something unreadable.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>Why a manifest was rejected. Only ever written to the debug log.</summary>
public enum ModlightManifestProblem
{
    None,
    Empty,
    NotJson,
    MissingVersion,
    BadVersion,
    MissingUrl,
    BadUrl,
    MissingDigest,
    BadDigest
}

/// <summary>How the published version relates to the installed one.</summary>
public enum ModlightUpdateVerdict
{
    /// <summary>The server has something newer.</summary>
    UpdateAvailable,

    /// <summary>Same version. Nothing to do.</summary>
    UpToDate,

    /// <summary>
    /// The server is BEHIND this install. Nothing to do - and specifically not a downgrade.
    /// </summary>
    /// <remarks>
    /// This is not hypothetical: it is what a developer build, a rolled-back release or a
    /// half-finished publish looks like from here, and treating "the two differ" as "there
    /// is an update" would hand the user an older installer and call it an upgrade.
    /// </remarks>
    ServerIsOlder,

    /// <summary>There was no usable manifest, or this build cannot tell what version it is.</summary>
    Unreadable
}

/// <summary>
/// Reading and judging the release manifest. Pure: no network, no files, no UI.
/// </summary>
/// <remarks>
/// It lives in this assembly but uses nothing from it: System and System.Text.Json only,
/// no WinForms, no Avalonia, no other HelpersLib type. That is what lets the whole file
/// compile into ShareX.Tests - which targets plain net10.0 - and be run against manifests
/// written by hand. <see cref="ModlightReleaseClient"/> and <see cref="ModlightUpdateFile"/>
/// keep the same discipline for the same reason.
/// </remarks>
public static class ModlightRelease
{
    /// <summary>Characters in a hex SHA-256.</summary>
    public const int DigestLength = 64;

    /// <summary>Longer than any release note we would sensibly show in a small dialog.</summary>
    private const int MaxNotesLength = 1200;

    /// <summary>Long enough for any real installer name, short enough to keep the temp path sane.</summary>
    private const int MaxFileNameLength = 100;

    private const string FallbackFileName = "Modlight-setup.exe";

    /// <summary>
    /// Turn the manifest body into a release, or into nothing.
    /// </summary>
    /// <returns>
    /// The release, or null with <paramref name="problem"/> saying what was wrong. Never
    /// throws: a server that answers with a stack trace, an HTML error page or half a JSON
    /// document is a thing that happens, and none of it is the user's business.
    /// </returns>
    public static ModlightReleaseManifest? TryParse(string? json, out ModlightManifestProblem problem)
    {
        problem = ModlightManifestProblem.None;

        if (string.IsNullOrWhiteSpace(json))
        {
            problem = ModlightManifestProblem.Empty;
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json!);
        }
        catch (JsonException)
        {
            problem = ModlightManifestProblem.NotJson;
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = ModlightManifestProblem.NotJson;
                return null;
            }

            string versionText = ReadString(root, "version");

            if (versionText.Length == 0)
            {
                problem = ModlightManifestProblem.MissingVersion;
                return null;
            }

            if (!TryParseVersion(versionText, out Version version))
            {
                problem = ModlightManifestProblem.BadVersion;
                return null;
            }

            string url = ReadString(root, "url");

            if (url.Length == 0)
            {
                problem = ModlightManifestProblem.MissingUrl;
                return null;
            }

            if (!IsAcceptableUrl(url))
            {
                problem = ModlightManifestProblem.BadUrl;
                return null;
            }

            string digest = ReadString(root, "sha256");

            if (digest.Length == 0)
            {
                problem = ModlightManifestProblem.MissingDigest;
                return null;
            }

            if (!IsDigest(digest))
            {
                problem = ModlightManifestProblem.BadDigest;
                return null;
            }

            return new ModlightReleaseManifest
            {
                Name = ReadString(root, "name"),
                VersionText = versionText,
                Version = version,
                Url = url,
                FileName = SafeInstallerFileName(ReadString(root, "filename"), url),
                Size = ReadSize(root, "size"),
                Sha256 = digest.ToLowerInvariant(),
                Notes = Clamp(ReadString(root, "notes"), MaxNotesLength),
                PublishedAt = ReadTimestamp(root, "publishedAt")
            };
        }
    }

    /// <summary>
    /// Whether the published release is newer than what is installed.
    /// </summary>
    /// <remarks>
    /// Numeric, component by component, which is the whole point: as strings "0.7.10" sorts
    /// BEFORE "0.7.9", so the tenth patch release of a line would never be offered to
    /// anybody. Equal is not an update, and older on the server is not an update either.
    /// </remarks>
    public static ModlightUpdateVerdict Decide(Version? installed, ModlightReleaseManifest? manifest)
    {
        if (installed == null || manifest == null)
        {
            return ModlightUpdateVerdict.Unreadable;
        }

        int order = Normalize(installed).CompareTo(Normalize(manifest.Version));

        if (order < 0)
        {
            return ModlightUpdateVerdict.UpdateAvailable;
        }

        return order > 0 ? ModlightUpdateVerdict.ServerIsOlder : ModlightUpdateVerdict.UpToDate;
    }

    /// <summary>
    /// Parse a published version string.
    /// </summary>
    /// <remarks>
    /// Tolerant at the edges and strict in the middle. A leading "v" is dropped because tags
    /// commonly carry one; anything from the first "-", "+" or space is dropped because that
    /// is where semver puts pre-release and build metadata and <see cref="Version"/> cannot
    /// read it. What is left has to be nothing but digits and dots - <see cref="Version.TryParse"/>
    /// on its own is happy to accept surprises, and a version number is not a place for them.
    /// </remarks>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text!.Trim();

        if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V'))
        {
            value = value.Substring(1);
        }

        int cut = value.IndexOfAny(['-', '+', ' ']);

        if (cut >= 0)
        {
            value = value.Substring(0, cut);
        }

        if (value.Length == 0)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c != '.' && (c < '0' || c > '9'))
            {
                return false;
            }
        }

        if (Version.TryParse(value, out Version? parsed) && parsed != null)
        {
            version = parsed;
            return true;
        }

        return false;
    }

    /// <summary>Missing components count as zero, so "0.7" and "0.7.0.0" are the same version.</summary>
    public static Version Normalize(Version version) =>
        new Version(Math.Max(version.Major, 0), Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>Whether a string is a hex SHA-256. Case is not part of the answer.</summary>
    public static bool IsDigest(string? value)
    {
        if (value == null || value.Length != DigestLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

            if (!hex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether we are willing to fetch this URL.
    /// </summary>
    /// <remarks>
    /// https anywhere; plain http ONLY on the loopback interface. The digest check makes a
    /// tampered download harmless, but it is the manifest that carries the digest - so a
    /// manifest fetched over plaintext would let whoever rewrote the bytes rewrite the
    /// expected hash to match, and the check would pass on somebody else's installer.
    /// Loopback is exempt because there is no wire to sit on, and because a stub server on
    /// 127.0.0.1 is how this code gets exercised without publishing anything.
    /// </remarks>
    public static bool IsAcceptableUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>
    /// A file name that can be joined onto the temp directory without thinking about it.
    /// </summary>
    /// <remarks>
    /// The name in the manifest is a string from a server, and it ends up as a path we
    /// create, write to and then EXECUTE. "..\..\Startup\x.exe" is a file name too. So the
    /// result keeps letters, digits, dot, dash and underscore and nothing else, cannot start
    /// with a dot, and always ends in .exe - which is the only thing the downloader knows
    /// how to run anyway.
    /// </remarks>
    public static string SafeInstallerFileName(string? fromManifest, string? url)
    {
        string candidate = (fromManifest ?? "").Trim();

        if (candidate.Length == 0 && !string.IsNullOrEmpty(url) &&
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Segments.Length > 0)
        {
            candidate = Uri.UnescapeDataString(uri.Segments[uri.Segments.Length - 1]);
        }

        StringBuilder cleaned = new StringBuilder(candidate.Length);

        foreach (char c in candidate)
        {
            bool keep = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                c == '.' || c == '-' || c == '_';

            if (keep)
            {
                cleaned.Append(c);
            }
        }

        string name = cleaned.ToString().TrimStart('.');

        if (name.Length == 0 || name.Length > MaxFileNameLength)
        {
            name = FallbackFileName;
        }

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        return name;
    }

    private static string ReadString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return (value.GetString() ?? "").Trim();
        }

        return "";
    }

    private static long ReadSize(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long size) && size > 0)
        {
            return size;
        }

        return 0;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root, string property)
    {
        string text = ReadString(root, property);

        if (text.Length > 0 && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset value))
        {
            return value;
        }

        return null;
    }

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max).TrimEnd() + "...";
}
