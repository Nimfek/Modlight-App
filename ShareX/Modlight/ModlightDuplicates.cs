#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>One copy inside a duplicate set.</summary>
public sealed class ModlightDuplicateFile
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("code")]
    public string? Code { get; set; }

    [JsonProperty("filename")]
    public string? Filename { get; set; }

    [JsonProperty("ext")]
    public string? Ext { get; set; }

    [JsonProperty("mimeType")]
    public string? MimeType { get; set; }

    [JsonProperty("size")]
    public long Size { get; set; }

    /// <summary>ISO 8601, as the server sends it. Parsed for display, never for identity.</summary>
    [JsonProperty("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonProperty("folderId")]
    public string? FolderId { get; set; }

    /// <summary>The folder's name, already resolved - this listing does hydrate it.</summary>
    [JsonProperty("folderName")]
    public string? FolderName { get; set; }

    [JsonProperty("views")]
    public int Views { get; set; }

    [JsonProperty("downloads")]
    public int Downloads { get; set; }

    /// <summary>The public preview link, in the shape and on the hostname this account uses.</summary>
    [JsonProperty("url")]
    public string? Url { get; set; }
}

/// <summary>
/// Files that are byte-for-byte the same, grouped by their sha256.
/// </summary>
/// <remarks>
/// By the CHECKSUM and by nothing else. Names are deliberately not consulted at the far
/// end: a capture tool hands the same <c>image.png</c> to twenty unrelated files, and two
/// copies of one video routinely arrive as <c>clip.mp4</c> and <c>clip (1).mp4</c>.
/// Matching on the bytes is the only version of this that may be allowed to propose a
/// delete.
/// </remarks>
public sealed class ModlightDuplicateGroup
{
    [JsonProperty("checksum")]
    public string? Checksum { get; set; }

    /// <summary>
    /// Live copies that exist, which can exceed <see cref="Files"/>.
    /// </summary>
    /// <remarks>
    /// The server lists at most fifty per set. The count comes from the aggregate rather
    /// than from the rows, so a trimmed list still says how many copies are really there.
    /// </remarks>
    [JsonProperty("copies")]
    public int Copies { get; set; }

    /// <summary>Bytes one copy occupies.</summary>
    [JsonProperty("size")]
    public long Size { get; set; }

    /// <summary>Bytes the whole set occupies.</summary>
    [JsonProperty("bytes")]
    public long Bytes { get; set; }

    /// <summary>Bytes freed by keeping exactly one copy.</summary>
    [JsonProperty("reclaimable")]
    public long Reclaimable { get; set; }

    /// <summary>The owner has said these copies are meant to exist.</summary>
    [JsonProperty("dismissed")]
    public bool Dismissed { get; set; }

    /// <summary>Oldest first: the original is almost always the one worth keeping.</summary>
    [JsonProperty("files")]
    public List<ModlightDuplicateFile>? Files { get; set; }
}

/// <summary>The three numbers "how much duplication is there" can mean.</summary>
public sealed class ModlightDuplicateTotals
{
    [JsonProperty("groups")]
    public int Groups { get; set; }

    /// <summary>Copies beyond the one kept in each set: what cleaning up would remove.</summary>
    [JsonProperty("redundant")]
    public int Redundant { get; set; }

    [JsonProperty("reclaimable")]
    public long Reclaimable { get; set; }
}

/// <summary>
/// When the server considers a library worth cleaning up.
/// </summary>
/// <remarks>
/// Sent by the server rather than held here. A client carrying its own copy of "ten
/// files or one gigabyte" would go on nagging at the old figures the day either is
/// changed, and the app and the website would then disagree about whether the same
/// library needs attention.
/// </remarks>
public sealed class ModlightDuplicateThresholds
{
    [JsonProperty("redundant")]
    public int Redundant { get; set; }

    [JsonProperty("bytes")]
    public long Bytes { get; set; }
}

/// <summary>The answer to GET /api/duplicates.</summary>
public sealed class ModlightDuplicateScan
{
    /// <summary>Ordered by reclaimable bytes, descending, with dismissed sets last.</summary>
    [JsonProperty("groups")]
    public List<ModlightDuplicateGroup>? Groups { get; set; }

    /// <summary>Sets found in total, which exceeds <see cref="Groups"/> when capped.</summary>
    [JsonProperty("totalGroups")]
    public int TotalGroups { get; set; }

    [JsonProperty("totalFiles")]
    public int TotalFiles { get; set; }

    [JsonProperty("reclaimable")]
    public long Reclaimable { get; set; }

    [JsonProperty("dismissedGroups")]
    public int DismissedGroups { get; set; }

    /// <summary>The same library with the dismissed sets subtracted.</summary>
    [JsonProperty("active")]
    public ModlightDuplicateTotals? Active { get; set; }

    [JsonProperty("truncated")]
    public bool Truncated { get; set; }

    /// <summary>
    /// Live files the scan could not consider at all, because they carry no checksum.
    /// </summary>
    /// <remarks>
    /// Reported rather than hidden, and it has to be shown. On a deployment whose library
    /// was imported this is most of it, so "no duplicates found"
    /// would otherwise read as a statement about files the scan never looked at.
    /// </remarks>
    [JsonProperty("unchecksummed")]
    public int Unchecksummed { get; set; }

    /// <summary>Is the account over one of the two thresholds right now?</summary>
    [JsonProperty("due")]
    public bool Due { get; set; }

    [JsonProperty("thresholds")]
    public ModlightDuplicateThresholds? Thresholds { get; set; }
}

/// <summary>The answer to POST /api/duplicates, whichever verb was sent.</summary>
public sealed class ModlightDuplicateDecision
{
    /// <summary>Sets now marked as meant-to-exist.</summary>
    [JsonProperty("kept")]
    public int Kept { get; set; }

    /// <summary>Sets taken back out of that pile.</summary>
    [JsonProperty("restored")]
    public int Restored { get; set; }
}

public static partial class ModlightLibrary
{
    /// <summary>
    /// Ids the delete endpoint accepts in one request - the server's own MAX_IDS.
    /// </summary>
    /// <remarks>
    /// Public because a duplicate clean-up is the one screen that can legitimately name
    /// more files than this in a single gesture: a scan lists up to two thousand rows and
    /// the default selection is nearly all of them. The caller batches; the server does
    /// not raise its ceiling for us.
    /// </remarks>
    public const int MaxDeleteIds = 500;

    /// <summary>
    /// A scan is allowed to take longer than a listing.
    /// </summary>
    /// <remarks>
    /// A listing reads one page; this one groups the whole account in SQL and then fetches
    /// the rows of the sets it found. It is measured in single seconds on the live server,
    /// but it is the only call here whose cost grows with the size of the library, and a
    /// scan that gives up at twenty seconds would fail exactly on the accounts that need
    /// it most.
    /// </remarks>
    private static readonly TimeSpan duplicateScanTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Find every set of files on this account that are byte-for-byte identical.
    /// </summary>
    /// <remarks>
    /// Read-only, and the grouping happens on the server. Doing it here would mean paging
    /// the entire library down to compare it - and, worse, deciding what a MISSING
    /// checksum means. The server has already decided: rows with none are excluded from
    /// the grouping and counted separately, because rows written before checksums were
    /// recorded have none, and treating NULL as equal to NULL would
    /// report that whole library as one enormous set of identical files and offer
    /// to delete all but the first.
    /// </remarks>
    /// <exception cref="ModlightUnauthorizedException">The token was refused.</exception>
    public static async Task<ModlightDuplicateScan> GetDuplicatesAsync(string host, string token,
        CancellationToken cancellationToken)
    {
        string json = await GetStringAsync(host + "/api/duplicates", host, token, duplicateScanTimeout,
            cancellationToken);

        return JsonConvert.DeserializeObject<ModlightDuplicateScan>(json) ?? new ModlightDuplicateScan();
    }

    /// <summary>
    /// Say that a set's copies are meant to exist, so it stops being counted.
    /// </summary>
    /// <remarks>
    /// Keyed by checksum, never by upload id, and the difference matters: trashing one
    /// copy of a set must not resurrect the reminder for the survivors, and re-uploading
    /// the same bytes must not either.
    ///
    /// The copies/bytes go with it so the server can record what the set looked like when
    /// the decision was taken. They are a note, not a key - the decision holds however the
    /// set grows afterwards.
    /// </remarks>
    /// <exception cref="ModlightUnauthorizedException">The token was refused.</exception>
    public static async Task<int> KeepDuplicateSetsAsync(string host, string token,
        IReadOnlyList<ModlightDuplicateGroup> sets, CancellationToken cancellationToken)
    {
        List<object> keep = new();

        foreach (ModlightDuplicateGroup set in sets)
        {
            if (string.IsNullOrEmpty(set.Checksum))
            {
                continue;
            }

            keep.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["checksum"] = set.Checksum,
                ["copies"] = set.Copies,
                ["bytes"] = set.Bytes
            });
        }

        if (keep.Count == 0)
        {
            return 0;
        }

        string json = await SendJsonAsync(HttpMethod.Post, host + "/api/duplicates", host, token,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["keep"] = keep }, cancellationToken);

        return (JsonConvert.DeserializeObject<ModlightDuplicateDecision>(json)
            ?? new ModlightDuplicateDecision()).Kept;
    }

    /// <summary>Undo the above: start counting these sets again.</summary>
    /// <exception cref="ModlightUnauthorizedException">The token was refused.</exception>
    public static async Task<int> CountDuplicateSetsAgainAsync(string host, string token,
        IReadOnlyList<string> checksums, CancellationToken cancellationToken)
    {
        if (checksums.Count == 0)
        {
            return 0;
        }

        string json = await SendJsonAsync(HttpMethod.Post, host + "/api/duplicates", host, token,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["countAgain"] = checksums },
            cancellationToken);

        return (JsonConvert.DeserializeObject<ModlightDuplicateDecision>(json)
            ?? new ModlightDuplicateDecision()).Restored;
    }
}
