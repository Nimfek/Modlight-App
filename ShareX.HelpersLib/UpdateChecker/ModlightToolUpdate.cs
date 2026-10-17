#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.HelpersLib;

/// <summary>One published build of an external tool, ready to be fetched.</summary>
/// <remarks>
/// Only ever produced by <see cref="ModlightToolRelease"/>, which refuses anything it
/// cannot use - so <see cref="Sha256"/> is never empty and <see cref="Url"/> is never a
/// host or a scheme we would not fetch. The same rule the release manifest follows: the
/// question "is a missing digest worth stopping for" has exactly one safe answer, so it is
/// answered once, here, rather than by whoever happens to be holding the object.
/// </remarks>
public sealed class ModlightToolBuild
{
    /// <summary>The publisher's version string, kept verbatim.</summary>
    public string VersionText { get; init; } = "";

    /// <summary>Where the artefact is. https, on the publisher's host.</summary>
    public string Url { get; init; } = "";

    /// <summary>Bytes, or 0 when the publisher did not say.</summary>
    public long Size { get; init; }

    /// <summary>Lowercase hex, 64 characters. Never empty.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Which publications the digest came from, so a weakened check is visible in the log.</summary>
    public string DigestSource { get; init; } = "";
}

/// <summary>Why a release was rejected. Only ever written to the debug log.</summary>
public enum ModlightToolProblem
{
    None,
    Empty,
    NotJson,
    Unpublished,
    MissingVersion,
    BadVersion,
    MissingAsset,
    BadUrl,
    MissingDigest,
    DigestConflict
}

public enum ModlightToolFetchStatus
{
    /// <summary>A build we are willing to install.</summary>
    Ok,

    /// <summary>Nobody answered, or the request never completed.</summary>
    Unreachable,

    /// <summary>The publisher answered, but not with something this build can act on.</summary>
    Malformed
}

/// <summary>The outcome of one "what is the newest build" request.</summary>
public sealed class ModlightToolFetchResult
{
    public ModlightToolFetchStatus Status { get; }

    /// <summary>Non-null exactly when <see cref="Status"/> is <see cref="ModlightToolFetchStatus.Ok"/>.</summary>
    public ModlightToolBuild? Build { get; }

    /// <summary>One line for the debug log. Never shown to a user.</summary>
    public string Detail { get; }

    private ModlightToolFetchResult(ModlightToolFetchStatus status, ModlightToolBuild? build, string detail)
    {
        Status = status;
        Build = build;
        Detail = detail;
    }

    public static ModlightToolFetchResult Ok(ModlightToolBuild build) =>
        new ModlightToolFetchResult(ModlightToolFetchStatus.Ok, build, "");

    public static ModlightToolFetchResult Unreachable(string detail) =>
        new ModlightToolFetchResult(ModlightToolFetchStatus.Unreachable, null, detail);

    public static ModlightToolFetchResult Malformed(string detail) =>
        new ModlightToolFetchResult(ModlightToolFetchStatus.Malformed, null, detail);
}

/// <summary>
/// Reading a GitHub release and judging what it publishes. Pure: no network, no files, no UI.
/// </summary>
/// <remarks>
/// <para>
/// This exists because one of the external components is not like the others. ffmpeg and
/// ExifTool can sit still for a year; yt-dlp reads a hundred sites that keep changing
/// underneath it and goes stale in weeks - a build 46 days old already fails on TikTok
/// outright and silently drops a signed-in YouTube download to 360p. A component system
/// that installs it once and never looks again guarantees exactly that.
/// </para>
/// <para>
/// <b>The digest is the whole point.</b> A digest pinned in the component catalogue
/// is stronger than anything reachable from here:
/// it ships inside the application, so a human looked at it. An automatic update cannot
/// have that, because the version does not exist yet when the human looks. What it can
/// have is TWO publications that must agree - GitHub's own asset digest, computed
/// server-side and served from api.github.com, and the project's SHA2-256SUMS file,
/// produced by their release workflow and served as a release asset. Requiring both to be
/// present and identical is what is available; it is weaker than a pin and enormously
/// stronger than running whatever arrives, and which of the two was actually used is
/// recorded in <see cref="ModlightToolBuild.DigestSource"/> so it ends up in the log
/// rather than in an assumption.
/// </para>
/// <para>
/// It lives in this assembly but uses nothing from it beyond the two update helpers that
/// keep the same discipline: System and System.Text.Json, no WinForms, no Avalonia, no
/// DebugHelper. That is what lets the whole file compile into ShareX.Tests and be run
/// against releases written by hand - including the ones that must be refused.
/// </para>
/// </remarks>
public static class ModlightToolRelease
{
    /// <summary>The only host an artefact may come from. The URL arrives inside JSON.</summary>
    /// <remarks>
    /// Belt to the braces of the digest check. A digest makes a swapped artefact harmless,
    /// but the same document carries both - so an answer that could send the downloader
    /// anywhere at all is an answer worth refusing before it is read any further.
    /// </remarks>
    public const string ArtefactHost = "github.com";

    /// <summary>Where GitHub says what the newest release is.</summary>
    private const string ApiHost = "https://api.github.com";

    /// <summary>Version segments. yt-dlp publishes YYYY.MM.DD, and YYYY.MM.DD.HHMMSS nightly.</summary>
    private const int MinSegments = 3;

    private const int MaxSegments = 4;

    /// <summary>Where the newest release of a repository is described.</summary>
    /// <param name="apiOrigin">
    /// Somewhere other than api.github.com to ask. Null everywhere in the application; the
    /// tests pass a stub on 127.0.0.1 so the transport - a 404, a body that is not JSON, a
    /// body too large to be a release - is exercised against the shipping code rather than
    /// described in a comment. It does NOT loosen where an artefact may come from: that is
    /// <see cref="ArtefactHost"/>, it is checked separately, and a release document from
    /// anywhere at all still cannot point the downloader off github.com.
    /// </param>
    public static string LatestReleaseUrl(string repository, string? apiOrigin = null) =>
        (string.IsNullOrEmpty(apiOrigin) ? ApiHost : apiOrigin!.TrimEnd('/')) +
        "/repos/" + (repository ?? "").Trim('/') + "/releases/latest";

    /// <summary>
    /// A date-shaped tool version, as a comparable list of numbers.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Version"/>, which stops at four components and would read
    /// "2026.08.19.232658" as a build number it cannot hold. Deliberately strict about the
    /// shape: three or four numeric segments behind a four-digit year, which is exactly
    /// what yt-dlp publishes - a date, or a date and a time for a nightly. If it ever
    /// changes its scheme this stops recognising it and the updater stops updating - which
    /// is the safe direction to fail, because the alternative is ordering two strings whose
    /// order we no longer understand and handing somebody the loser as an upgrade.
    /// </remarks>
    public static bool TryParseVersion(string? text, out int[] parts)
    {
        parts = Array.Empty<int>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] pieces = text.Trim().Split('.');

        if (pieces.Length < MinSegments || pieces.Length > MaxSegments)
        {
            return false;
        }

        int[] numbers = new int[pieces.Length];

        for (int i = 0; i < pieces.Length; i++)
        {
            string piece = pieces[i];

            if (piece.Length == 0 || piece.Length > 9)
            {
                return false;
            }

            foreach (char c in piece)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            if (!int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        // A year in front, so a tag like "1.2.3" from some other project cannot be ordered
        // against a yt-dlp date at all, let alone win.
        if (pieces[0].Length != 4 || numbers[0] < 2000 || numbers[0] > 2999)
        {
            return false;
        }

        parts = numbers;
        return true;
    }

    /// <summary>Compare two parsed versions, treating a missing segment as zero.</summary>
    public static int CompareVersions(int[] a, int[] b)
    {
        int length = Math.Max(a.Length, b.Length);

        for (int i = 0; i < length; i++)
        {
            int left = i < a.Length ? a[i] : 0;
            int right = i < b.Length ? b[i] : 0;

            if (left != right)
            {
                return left < right ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a version worth moving to from <paramref name="installed"/>.
    /// </summary>
    /// <remarks>
    /// False when either side does not parse, so an unreadable installed version can never
    /// be talked into "downgrade to the latest". Strictly newer, never merely different: a
    /// pulled nightly, a rolled-back release or a developer build must not be handed
    /// backwards to somebody as an upgrade.
    /// </remarks>
    public static bool IsNewer(string? candidate, string? installed)
    {
        return TryParseVersion(candidate, out int[] left) &&
            TryParseVersion(installed, out int[] right) &&
            CompareVersions(left, right) > 0;
    }

    /// <summary>
    /// Turn a GitHub release document into the one build we want out of it.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="assetName">The artefact for this architecture, matched exactly.</param>
    /// <param name="checksumAssetName">
    /// The publisher's own checksum manifest in the same release, or empty when there is
    /// none. It is fetched separately; this only finds its address.
    /// </param>
    /// <param name="checksumsUrl">Where that manifest is, or empty.</param>
    /// <param name="problem">Why nothing came back.</param>
    /// <returns>
    /// A build carrying the API's digest, or null. That digest may still be empty here -
    /// <see cref="WithPublishedDigest"/> is what settles the question, because the second
    /// publication has not been fetched yet at this point.
    /// </returns>
    public static ModlightToolBuild? TryParseRelease(string? json, string assetName, string checksumAssetName,
        out string checksumsUrl, out ModlightToolProblem problem)
    {
        checksumsUrl = "";
        problem = ModlightToolProblem.None;

        if (string.IsNullOrWhiteSpace(json))
        {
            problem = ModlightToolProblem.Empty;
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (Exception)
        {
            problem = ModlightToolProblem.NotJson;
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = ModlightToolProblem.NotJson;
                return null;
            }

            // /releases/latest answers with neither, but this is also the shape of
            // /releases/tags/x and of whatever a proxy decides to hand back - and a draft
            // is a release its own publisher has not stood behind yet.
            if (ReadBool(root, "draft") || ReadBool(root, "prerelease"))
            {
                problem = ModlightToolProblem.Unpublished;
                return null;
            }

            string versionText = ReadString(root, "tag_name").Trim();

            if (versionText.Length == 0)
            {
                problem = ModlightToolProblem.MissingVersion;
                return null;
            }

            if (!TryParseVersion(versionText, out _))
            {
                problem = ModlightToolProblem.BadVersion;
                return null;
            }

            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            {
                problem = ModlightToolProblem.MissingAsset;
                return null;
            }

            string url = "";
            string digest = "";
            long size = 0;
            bool found = false;

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string name = ReadString(asset, "name");

                if (name.Length == 0)
                {
                    continue;
                }

                if (!found && string.Equals(name, assetName, StringComparison.Ordinal))
                {
                    found = true;
                    url = ReadString(asset, "browser_download_url");
                    size = ReadSize(asset, "size");
                    digest = ReadDigest(asset);
                    continue;
                }

                if (checksumsUrl.Length == 0 && checksumAssetName.Length > 0 &&
                    string.Equals(name, checksumAssetName, StringComparison.Ordinal))
                {
                    string candidate = ReadString(asset, "browser_download_url");

                    if (IsArtefactUrl(candidate))
                    {
                        checksumsUrl = candidate;
                    }
                }
            }

            if (!found)
            {
                problem = ModlightToolProblem.MissingAsset;
                return null;
            }

            if (!IsArtefactUrl(url))
            {
                problem = ModlightToolProblem.BadUrl;
                return null;
            }

            return new ModlightToolBuild
            {
                VersionText = versionText,
                Url = url,
                Size = size,
                Sha256 = digest,
                DigestSource = digest.Length > 0 ? "release API" : ""
            };
        }
    }

    /// <summary>
    /// Fold the publisher's own checksum manifest into a build, and refuse a disagreement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both publications present and identical is the case this is written for, and it is
    /// what yt-dlp actually does today. One present is accepted with the weakening written
    /// onto the build; neither is refused outright, because an executable fetched over the
    /// network and run with no digest check at all is the hole this file exists to close.
    /// </para>
    /// <para>
    /// A disagreement is refused rather than resolved. There is no reading of "the two
    /// people who published this file do not agree about what is in it" that ends with
    /// installing it.
    /// </para>
    /// </remarks>
    public static ModlightToolBuild? WithPublishedDigest(ModlightToolBuild build, string? checksums,
        string assetName, out ModlightToolProblem problem)
    {
        problem = ModlightToolProblem.None;

        string published = DigestFromChecksums(checksums, assetName);
        string api = build.Sha256;

        if (api.Length > 0 && published.Length > 0)
        {
            if (!ModlightUpdateFile.DigestsMatch(api, published))
            {
                problem = ModlightToolProblem.DigestConflict;
                return null;
            }

            return Adopt(build, published, "release API and published checksums");
        }

        if (published.Length > 0)
        {
            return Adopt(build, published, "published checksums only");
        }

        if (api.Length > 0)
        {
            return Adopt(build, api, "release API only");
        }

        problem = ModlightToolProblem.MissingDigest;
        return null;
    }

    /// <summary>
    /// The digest for one file out of a sha256sum-style manifest.
    /// </summary>
    /// <remarks>
    /// The name is matched exactly, and the leading <c>*</c> that sha256sum writes for a
    /// file it read in binary mode is stripped first. The manifest lists every artefact of
    /// the release - Linux, macOS, the zips, the x86 build - so a loose match is not a
    /// missing digest, it is a perfectly valid digest for a completely different file,
    /// which then fails verification and looks like a corrupt download forever.
    /// </remarks>
    public static string DigestFromChecksums(string? text, string fileName)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(fileName))
        {
            return "";
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            int space = line.IndexOf(' ');

            if (space <= 0)
            {
                continue;
            }

            string digest = line.Substring(0, space);

            if (!ModlightRelease.IsDigest(digest))
            {
                continue;
            }

            string name = line.Substring(space + 1).TrimStart(' ', '*');

            if (string.Equals(name, fileName, StringComparison.Ordinal))
            {
                return digest.ToLowerInvariant();
            }
        }

        return "";
    }

    /// <summary>Whether we are willing to fetch this address at all.</summary>
    public static bool IsArtefactUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, ArtefactHost, StringComparison.OrdinalIgnoreCase);
    }

    private static ModlightToolBuild Adopt(ModlightToolBuild build, string digest, string source) =>
        new ModlightToolBuild
        {
            VersionText = build.VersionText,
            Url = build.Url,
            Size = build.Size,
            Sha256 = digest.ToLowerInvariant(),
            DigestSource = source
        };

    /// <summary>The asset digest field, which GitHub publishes as "sha256:&lt;hex&gt;".</summary>
    private static string ReadDigest(JsonElement asset)
    {
        string value = ReadString(asset, "digest");

        const string prefix = "sha256:";

        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        string digest = value.Substring(prefix.Length).Trim();

        return ModlightRelease.IsDigest(digest) ? digest.ToLowerInvariant() : "";
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool ReadBool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static long ReadSize(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long size) && size > 0
            ? size
            : 0;
}

/// <summary>
/// Asking a publisher what the newest build of a tool is. No UI, no files, no HelpersLib.
/// </summary>
/// <remarks>
/// The <see cref="HttpClient"/> is a parameter for the same reason it is one in
/// <see cref="ModlightReleaseClient"/>: it is what lets the real fetch-and-judge path be
/// pointed at a stub on 127.0.0.1 in the tests instead of at GitHub.
/// </remarks>
public static class ModlightToolClient
{
    /// <summary>Long enough for a cold answer, short enough that nothing ever waits on it.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A release document with two dozen assets and release notes. Anything past this is not one.</summary>
    private const int MaxReleaseBytes = 2 * 1024 * 1024;

    /// <summary>The real checksum manifest is under two kilobytes.</summary>
    private const int MaxChecksumBytes = 256 * 1024;

    /// <summary>
    /// What the newest published build is, or why we do not know.
    /// </summary>
    /// <remarks>
    /// Never throws. This runs on machines that are offline, behind a captive portal, or on
    /// a corporate proxy that answers every request with a login page - and none of that
    /// may reach a person who was only trying to download a video. Everything that can go
    /// wrong comes back as a status and a line for the log.
    /// </remarks>
    public static async Task<ModlightToolFetchResult> FetchAsync(HttpClient client, string repository,
        string assetName, string checksumAssetName, CancellationToken cancellationToken,
        string? apiOrigin = null)
    {
        string url = ModlightToolRelease.LatestReleaseUrl(repository, apiOrigin);

        try
        {
            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(RequestTimeout);

            string body = await GetAsync(client, url, MaxReleaseBytes, deadline.Token);

            if (body.Length == 0)
            {
                return ModlightToolFetchResult.Unreachable("no release document from " + url);
            }

            ModlightToolBuild? build = ModlightToolRelease.TryParseRelease(body, assetName, checksumAssetName,
                out string checksumsUrl, out ModlightToolProblem problem);

            if (build == null)
            {
                return ModlightToolFetchResult.Malformed("release rejected: " + problem);
            }

            string checksums = "";

            if (checksumsUrl.Length > 0)
            {
                // A failure here is not fatal, deliberately: it costs the second
                // publication, which is then recorded on the build as the weaker source.
                // Losing it is a smaller harm than an updater that stops updating for the
                // week GitHub is having trouble serving one small file.
                checksums = await GetAsync(client, checksumsUrl, MaxChecksumBytes, deadline.Token);
            }

            ModlightToolBuild? verified =
                ModlightToolRelease.WithPublishedDigest(build, checksums, assetName, out problem);

            if (verified == null)
            {
                return ModlightToolFetchResult.Malformed("digest rejected: " + problem);
            }

            return ModlightToolFetchResult.Ok(verified);
        }
        catch (Exception exception)
        {
            return ModlightToolFetchResult.Unreachable(exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>Fetch a body, refusing to hold more of it than the thing could ever be.</summary>
    /// <returns>The text, or empty when the request failed or there was too much of it.</returns>
    private static async Task<string> GetAsync(HttpClient client, string url, int limit,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);

        // Without these GitHub answers in whatever its current default happens to be. The
        // version header pins the shape of the answer to an API that has the digest field
        // in it, which is half of what makes an unattended update defensible at all.
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using HttpResponseMessage response =
            await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return "";
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new MemoryStream();

        byte[] chunk = new byte[8192];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return "";
            }

            buffer.Write(chunk, 0, read);
        }

        // TrimStart on the byte order mark, not on whitespace: it survives decoding as
        // U+FEFF and the JSON reader rejects it as a stray character, which would turn a
        // perfectly good release into "malformed".
        return new UTF8Encoding(false).GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }
}
