#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Newtonsoft.Json;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>One folder from GET /api/folders.</summary>
public sealed class ModlightFolder
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("slug")]
    public string? Slug { get; set; }

    /// <summary>The folder this one sits in, or null when it sits at the top.</summary>
    [JsonProperty("parentId")]
    public string? ParentId { get; set; }

    [JsonProperty("isPublic")]
    public bool IsPublic { get; set; }

    /// <summary>
    /// Files sitting DIRECTLY in this folder. Nothing nested is counted.
    /// </summary>
    /// <remarks>
    /// The server says so explicitly, and the reason is worth keeping next to the field:
    /// rolling a subtree up server-side would be a recursive query on every request for a
    /// number the caller can work out in one loop, because the caller already holds every
    /// folder and its parent. <see cref="CloudFolderViewModel"/> does that rollup once per
    /// folder list rather than once per tile.
    /// </remarks>
    [JsonProperty("fileCount")]
    public int FileCount { get; set; }

    /// <summary>Bytes of the files directly in this folder. Direct contents only, like <see cref="FileCount"/>.</summary>
    [JsonProperty("size")]
    public long Size { get; set; }

    /// <summary>The order the panel shows them in, so both surfaces list them the same way.</summary>
    [JsonProperty("sortOrder")]
    public int SortOrder { get; set; }
}

/// <summary>What is loose in the account: files that are in no folder at all.</summary>
public sealed class ModlightUnfiled
{
    [JsonProperty("fileCount")]
    public int FileCount { get; set; }

    [JsonProperty("size")]
    public long Size { get; set; }
}

/// <summary>The answer to GET /api/folders.</summary>
public sealed class ModlightFolderList
{
    /// <summary>
    /// The literal to send back as <c>folderId</c> to mean "files in no folder".
    /// </summary>
    /// <remarks>
    /// Read from the server rather than hardcoded as "root" because that is what the
    /// endpoint exists to say: it hands out its own sentinel so a client cannot drift
    /// from it. Falls back to "root" only when an old deployment omits the field.
    /// </remarks>
    [JsonProperty("rootValue")]
    public string? RootValue { get; set; }

    /// <summary>
    /// The account's loose files, so a client can offer that shelf without counting itself.
    /// </summary>
    /// <remarks>
    /// Null on a deployment that predates the field. Callers must treat that as "unknown"
    /// rather than as zero: an account whose loose files are simply not reported would
    /// otherwise be shown a shelf saying it has none, which is a worse answer than no shelf.
    /// </remarks>
    [JsonProperty("unfiled")]
    public ModlightUnfiled? Unfiled { get; set; }

    [JsonProperty("folders")]
    public List<ModlightFolder>? Folders { get; set; }
}

/// <summary>One uploaded file from GET /api/uploads.</summary>
public sealed class ModlightUpload
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    /// <summary>The short code the links are built from. Identifies the file to the delete route.</summary>
    [JsonProperty("code")]
    public string? Code { get; set; }

    [JsonProperty("filename")]
    public string? Filename { get; set; }

    [JsonProperty("ext")]
    public string? Ext { get; set; }

    [JsonProperty("mimeType")]
    public string? MimeType { get; set; }

    /// <summary>
    /// The server's own classification: image, video, audio, document, archive, paste, other.
    /// </summary>
    /// <remarks>
    /// Taken as given rather than re-derived from <see cref="MimeType"/>. The server
    /// consults its <c>effectiveType</c>, so a file ShareX stored as
    /// application/octet-stream - which is what Windows reports for .avif and every
    /// other format missing from the registry - is still filed as the image it is. A
    /// client sniffing the MIME string alone would put a generic icon on exactly the
    /// files the panel shows a picture for.
    /// </remarks>
    [JsonProperty("kind")]
    public string? Kind { get; set; }

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("width")]
    public int? Width { get; set; }

    [JsonProperty("height")]
    public int? Height { get; set; }

    [JsonProperty("published")]
    public bool Published { get; set; }

    [JsonProperty("views")]
    public int Views { get; set; }

    [JsonProperty("downloads")]
    public int Downloads { get; set; }

    [JsonProperty("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonProperty("folderId")]
    public string? FolderId { get; set; }

    /// <summary>When the link stops working. Null when it does not expire.</summary>
    [JsonProperty("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>How many views the link is allowed in total. Null when unlimited.</summary>
    [JsonProperty("maxViews")]
    public int? MaxViews { get; set; }

    [JsonProperty("hasPassword")]
    public bool HasPassword { get; set; }

    /// <summary>The preview page. Null for a file in the server's trash.</summary>
    [JsonProperty("url")]
    public string? Url { get; set; }

    /// <summary>The file itself.</summary>
    [JsonProperty("rawUrl")]
    public string? RawUrl { get; set; }

    /// <summary>A resized preview, or null when this file cannot have one.</summary>
    [JsonProperty("thumbUrl")]
    public string? ThumbUrl { get; set; }

    /// <summary>
    /// A preview that MOVES - an animated WebP of frames sampled across the file -
    /// or null when this row cannot produce one.
    /// </summary>
    /// <remarks>
    /// Null is the field's whole point, and it is taken as final. The server answers it
    /// from <c>canMotionPreview</c>, which applies exactly the conditions the serving
    /// route applies, so a URL that is here always yields a tile. Deriving one locally
    /// from <see cref="Kind"/> would send a grid off to ask for a preview of every still
    /// image in the library and collect a 415 for each.
    ///
    /// It carries the same w/h as <see cref="ThumbUrl"/>, which is what lets one be laid
    /// over the other without the picture shifting: same size, same fit, same framing.
    ///
    /// A row can GAIN one over time. Most of the imported library has no recorded frame
    /// count and the server refuses to advertise what it is not sure of - until drawing
    /// the still thumbnail once teaches it. So this is re-read from every listing rather
    /// than remembered per file.
    /// </remarks>
    [JsonProperty("motionThumbUrl")]
    public string? MotionThumbUrl { get; set; }

    /// <summary>
    /// Which of the two links belongs on the clipboard, decided by the server.
    /// </summary>
    /// <remarks>
    /// The account's <c>copy_raw</c> switch governs this, and the server applies it
    /// here exactly as it applies it to a ShareX upload. Choosing locally would mean
    /// one account copying two different shapes of link depending on which app it came
    /// from, with the switch in the web panel governing only one of them.
    /// </remarks>
    [JsonProperty("copyUrl")]
    public string? CopyUrl { get; set; }
}

/// <summary>One page of the answer to GET /api/uploads.</summary>
public sealed class ModlightUploadPage
{
    /// <summary>The page actually returned, which need not be the one asked for.</summary>
    [JsonProperty("page")]
    public int Page { get; set; }

    /// <summary>
    /// The page size actually used.
    /// </summary>
    /// <remarks>
    /// The server clamps this, and the clamp has to be believed: a client that trusted
    /// its own request of 500 while receiving 120 would compute a page count four times
    /// too small and never reach most of the library.
    /// </remarks>
    [JsonProperty("perPage")]
    public int PerPage { get; set; }

    [JsonProperty("total")]
    public int Total { get; set; }

    [JsonProperty("rootValue")]
    public string? RootValue { get; set; }

    [JsonProperty("items")]
    public List<ModlightUpload>? Items { get; set; }
}

/// <summary>The answer to DELETE /api/uploads.</summary>
public sealed class ModlightDeleteResult
{
    /// <summary>What the server actually did: trash, permanent or restore.</summary>
    [JsonProperty("mode")]
    public string? Mode { get; set; }

    [JsonProperty("requested")]
    public int Requested { get; set; }

    /// <summary>
    /// How many rows it touched.
    /// </summary>
    /// <remarks>
    /// Lower than <see cref="Requested"/> is not an error and the server says so: an id
    /// that is already in the trash, already gone or not this account's is reported by
    /// this number rather than by a status code.
    /// </remarks>
    [JsonProperty("affected")]
    public int Affected { get; set; }
}

/// <summary>The answer to POST or PATCH /api/folders.</summary>
public sealed class ModlightFolderResult
{
    [JsonProperty("folder")]
    public ModlightFolder? Folder { get; set; }
}

/// <summary>The answer to DELETE /api/folders.</summary>
public sealed class ModlightFolderDeleteResult
{
    /// <summary>Which mode the server actually ran, echoed back rather than assumed.</summary>
    [JsonProperty("withContents")]
    public bool WithContents { get; set; }

    /// <summary>How many folders went, this one included.</summary>
    [JsonProperty("folders")]
    public int Folders { get; set; }

    /// <summary>How many files were moved to the trash. Zero in the safe mode.</summary>
    [JsonProperty("trashed")]
    public int Trashed { get; set; }
}

/// <summary>The answer to PATCH /api/uploads.</summary>
public sealed class ModlightUploadPatchResult
{
    [JsonProperty("requested")]
    public int Requested { get; set; }

    /// <summary>
    /// How many rows it touched.
    /// </summary>
    /// <remarks>
    /// Lower than <see cref="Requested"/> is not an error: an id that is gone, not a
    /// uuid, or not this account's is reported by this number rather than by a status.
    /// </remarks>
    [JsonProperty("affected")]
    public int Affected { get; set; }

    /// <summary>
    /// The rows as they are NOW, in the same shape the listing uses.
    /// </summary>
    /// <remarks>
    /// The server reads them back deliberately so a client can redraw a card without
    /// re-fetching the page it is on - which is the difference between an edit that
    /// feels instant and one that empties the grid, throws away every decoded
    /// thumbnail and scrolls back to the top.
    /// </remarks>
    [JsonProperty("items")]
    public List<ModlightUpload>? Items { get; set; }
}

/// <summary>
/// What one PATCH /api/uploads is asking for.
/// </summary>
/// <remarks>
/// A field nobody set is left OUT of the body; a field set to null is sent as JSON
/// null. The server draws exactly that distinction - absent means "leave alone", null
/// means "clear" - so a builder that serialised every property of a plain DTO would
/// wipe an expiry every time somebody renamed a file. Hence a dictionary rather than
/// six nullable properties: the difference between "not mentioned" and "explicitly
/// nothing" is the whole point, and C# spells both of them <c>null</c>.
/// </remarks>
public sealed class ModlightUploadEdit
{
    private readonly Dictionary<string, object?> fields = new(StringComparer.Ordinal);

    /// <summary>A folder id, or null for "no folder".</summary>
    public ModlightUploadEdit MoveTo(string? folderId) => Set("folderId", folderId);

    public ModlightUploadEdit SetPublished(bool published) => Set("published", published);

    /// <summary>The vanity slug. One file only - a URL cannot belong to two rows.</summary>
    public ModlightUploadEdit SetCode(string code) => Set("code", code);

    /// <summary>When the link stops working, or null to clear the expiry.</summary>
    public ModlightUploadEdit SetExpiry(DateTimeOffset? expires) =>
        Set("expiresAt", expires?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

    /// <summary>The view budget, or null to remove it.</summary>
    public ModlightUploadEdit SetMaxViews(int? maxViews) => Set("maxViews", maxViews);

    /// <summary>A per-file password, or null to remove the one that is set.</summary>
    public ModlightUploadEdit SetPassword(string? password) => Set("password", password);

    public bool IsEmpty => fields.Count == 0;

    internal IReadOnlyDictionary<string, object?> Fields => fields;

    private ModlightUploadEdit Set(string key, object? value)
    {
        fields[key] = value;
        return this;
    }
}

/// <summary>Whether a conversion would work, asked without doing the work.</summary>
/// <remarks>
/// See <see cref="ModlightLibrary.CheckConversionAsync"/> for why this exists at all.
/// </remarks>
public sealed class ModlightConvertVerdict
{
    private ModlightConvertVerdict(bool ok, string reason)
    {
        Ok = ok;
        Reason = reason;
    }

    /// <summary>The server would produce the file.</summary>
    public static readonly ModlightConvertVerdict Yes = new(true, "");

    /// <summary>A refusal carrying the server's own token, e.g. <c>no-audio</c>.</summary>
    public static ModlightConvertVerdict No(string reason) =>
        new(false, string.IsNullOrEmpty(reason) ? "unknown" : reason);

    public bool Ok { get; }

    /// <summary>The <c>X-Convert-Refused</c> token, or empty when the answer was yes.</summary>
    public string Reason { get; }
}

/// <summary>
/// Reading and deleting the account's own files on a Modlight server.
/// </summary>
/// <remarks>
/// Split from <see cref="ModlightApi"/>, which answers the one question sign-in needs
/// ("whose token is this"). This is the library surface: what is in it, and what a
/// picture of one file looks like.
/// </remarks>
public static partial class ModlightLibrary
{
    /// <summary>What the server means by "not in any folder" when it has not said otherwise.</summary>
    public const string RootFolder = "root";

    /// <summary>
    /// Long enough for a cold server, short enough that nobody wonders whether it hung.
    /// </summary>
    private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A picture is allowed to take longer than a listing.
    /// </summary>
    /// <remarks>
    /// The first request for a given size makes the server render it with sharp; only
    /// afterwards is it served from cache. Twenty seconds is a listing's budget, not a
    /// render's, and a tile that gives up early leaves a permanent placeholder on a file
    /// that was merely slow the first time.
    /// </remarks>
    private static readonly TimeSpan thumbnailTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The most bytes we will read for one tile.
    /// </summary>
    /// <remarks>
    /// The server only offers <c>thumbUrl</c> for files it will actually resize, so this
    /// should never be reached. It is here because the URL is a string from the network:
    /// if a future server ever pointed it at an original, this decides between a slow
    /// tile and a 700 MB video pulled down to draw a 150-pixel square.
    /// </remarks>
    private const int MaxThumbnailBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The most bytes we will read for one MOVING tile.
    /// </summary>
    /// <remarks>
    /// Much tighter than the still ceiling because the server's own bound is tighter: a
    /// motion preview is capped at a 480 px edge, and holds up to thirty frames of one
    /// continuous clip. Re-measured on production after that change - it used to be eight
    /// sampled frames - twenty-one previews came in between 62.6 kB and 815.9 kB, mean
    /// 220 kB, against 1 kB to 138 kB before.
    ///
    /// FOUR megabytes rather than the two this was set to when the largest artefact seen
    /// was 704 kB. Nothing has grown past two; what changed is the confidence in the
    /// number, because the artefact QUADRUPLED the day the server swapped eight sampled
    /// frames for a thirty-frame clip, and the ceiling that survived that was two and a
    /// half times the new worst case rather than the ten it had been. This is a fence
    /// around a bug, not a working limit - what it really guards against is a future
    /// server answering this URL with the source video, which on this path is gigabytes -
    /// and a fence set close to the traffic is the kind that starts refusing real
    /// previews. A refusal here is also the quiet sort: the tile simply never moves, and
    /// nothing is written down, so every hover asks again.
    /// </remarks>
    private const int MaxMotionBytes = 4 * 1024 * 1024;

    /// <summary>Every folder the account has.</summary>
    public static async Task<ModlightFolderList> GetFoldersAsync(string host, string token, CancellationToken cancellationToken)
    {
        string json = await GetStringAsync(host + "/api/folders", host, token, requestTimeout, cancellationToken);
        return JsonConvert.DeserializeObject<ModlightFolderList>(json) ?? new ModlightFolderList();
    }

    /// <summary>
    /// One page of the account's files.
    /// </summary>
    /// <param name="folderId">
    /// A folder id, the root sentinel for "no folder", or null for "everywhere".
    /// </param>
    /// <param name="kind">
    /// One of the server's own coarse classes - image, video, audio, document, archive,
    /// paste - or null for every kind.
    /// </param>
    /// <remarks>
    /// The kind is a FILTER OVER THE WHOLE LIBRARY, not a place, which is exactly what it
    /// is in the web panel: its sidebar entries are query strings on the dashboard rather
    /// than routes, and they deliberately drop the folder on the way. This client sends
    /// the two together only because the server accepts both; the caller is what decides
    /// that picking a kind means leaving the folder.
    /// </remarks>
    public static async Task<ModlightUploadPage> GetUploadsAsync(string host, string token,
        int page, int perPage, string? folderId, string? search, string? kind, bool trashed,
        CancellationToken cancellationToken)
    {
        StringBuilder url = new StringBuilder(host)
            .Append("/api/uploads?page=").Append(Math.Max(1, page))
            .Append("&perPage=").Append(Math.Max(1, perPage));

        if (!string.IsNullOrEmpty(folderId))
        {
            url.Append("&folderId=").Append(Uri.EscapeDataString(folderId!));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            url.Append("&search=").Append(Uri.EscapeDataString(search!.Trim()));
        }

        if (!string.IsNullOrEmpty(kind))
        {
            // An unknown kind is not an error at the far end: the server ignores a value
            // it does not recognise and lists everything, which is the right answer for a
            // client that is newer than the deployment.
            url.Append("&kind=").Append(Uri.EscapeDataString(kind!));
        }

        if (trashed)
        {
            // The literal "1", which is what the server tests for.
            url.Append("&trashed=1");
        }

        string json = await GetStringAsync(url.ToString(), host, token, requestTimeout, cancellationToken);
        return JsonConvert.DeserializeObject<ModlightUploadPage>(json) ?? new ModlightUploadPage();
    }

    /// <summary>
    /// Fetch one preview image, or null when the server has none to give.
    /// </summary>
    /// <remarks>
    /// A missing picture is not an error worth stopping a grid over - the file may have
    /// expired, spent its view budget or simply not be renderable - so anything the
    /// server answers with comes back as null rather than an exception. A connection
    /// that never completed still throws, because that is a fact about the whole grid
    /// rather than about one tile.
    /// </remarks>
    public static async Task<byte[]?> GetThumbnailAsync(string host, string token, string thumbnailUrl,
        CancellationToken cancellationToken)
    {
        if (!TryRewriteToHost(thumbnailUrl, host, out string url))
        {
            return null;
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(thumbnailTimeout);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", token);

        using HttpResponseMessage response = await HttpClientFactory.Create()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength > MaxThumbnailBytes)
        {
            return null;
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        return bytes.Length > MaxThumbnailBytes ? null : bytes;
    }

    /// <summary>
    /// Fetch one MOVING preview - an animated WebP - and say which kind of no it got.
    /// </summary>
    /// <remarks>
    /// The distinction is the reason this is not <see cref="GetThumbnailAsync"/> with a
    /// different URL. That one answers null for every failure, which is right for a still:
    /// the card keeps its icon and the next scroll past will try again. Here the answers
    /// mean opposite things. 415 and 413 are the server saying this FILE will never
    /// produce a preview - a still image, a video whose duration the container will not
    /// state - and a client that treats that as "try later" asks the same question every
    /// time the pointer crosses the tile, for ever. 503 from the transform limiter and a
    /// dropped connection are about this MOMENT and must not be written down as facts
    /// about the file.
    ///
    /// Anything unexpected is filed as the temporary kind. A refusal is remembered for
    /// the rest of the session, so guessing wrong in that direction costs a preview the
    /// file could have had, while guessing wrong the other way costs one request the
    /// next time somebody hovers.
    /// </remarks>
    public static async Task<ModlightMotionPreview> GetMotionThumbnailAsync(string host, string token,
        string motionUrl, CancellationToken cancellationToken)
    {
        if (!TryRewriteToHost(motionUrl, host, out string url))
        {
            return ModlightMotionPreview.Refused;
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(thumbnailTimeout);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", token);

        using HttpResponseMessage response = await HttpClientFactory.Create()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

        if (!response.IsSuccessStatusCode)
        {
            // 415 unsupported media type and 413 too large are the two the serving route
            // spells its refusals as, and it sends X-Motion-Refused with the reason. The
            // reason itself is not read: every value of it means the same thing to a grid,
            // which is "draw the still and stop asking".
            return response.StatusCode == HttpStatusCode.UnsupportedMediaType ||
                   response.StatusCode == HttpStatusCode.RequestEntityTooLarge
                ? ModlightMotionPreview.Refused
                : ModlightMotionPreview.Unavailable;
        }

        if (response.Content.Headers.ContentLength > MaxMotionBytes)
        {
            return ModlightMotionPreview.Unavailable;
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);

        return bytes.Length is 0 or > MaxMotionBytes
            ? ModlightMotionPreview.Unavailable
            : ModlightMotionPreview.Ok(bytes);
    }

    /// <summary>
    /// Move one file to the account's trash on the server.
    /// </summary>
    /// <remarks>
    /// This used to be a permanent delete over <c>POST /{tag}/{code}/delete/{token}</c>,
    /// and the reason it is not any more is the token in that URL. That route was built
    /// for a link a person hands to somebody else, so it identifies the caller by a path
    /// segment - which meant the ACCOUNT'S API TOKEN, the credential that can read and
    /// delete everything it owns, was written into the request line of every delete. The
    /// request line is the one part of an HTTP request that everything in the chain writes
    /// down as a matter of course: the reverse proxy's access log first of all. The token
    /// sat in plain text in nginx's log on the server for as long as the log was kept, and
    /// nothing about that was visible from here.
    ///
    /// <c>DELETE /api/uploads</c> takes the same credential in the header the rest of this
    /// client already uses, so it is never written down, and it names the file by its id
    /// in a JSON body rather than by a path segment. It also defaults to the thirty-day
    /// trash rather than the hard delete, which is why the confirmation no longer has to
    /// say "permanent": the web panel can put the file back.
    /// </remarks>
    /// <param name="id">The upload's id from the listing. Not the short code the links use.</param>
    /// <returns>
    /// True when the server moved it. False when nothing was affected - which it answers
    /// identically for "already in the trash", "already gone" and "not yours",
    /// deliberately, so that this route cannot be used to find out which.
    /// </returns>
    /// <exception cref="ModlightUnauthorizedException">The token was refused.</exception>
    public static async Task<bool> TrashAsync(string host, string id, string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        ModlightDeleteResult result = await DeleteUploadsAsync(host, token, new[] { id }, "trash", cancellationToken);

        // The count, not the status. A request naming an id the account does not own is a
        // perfectly good request and is answered 200 with nothing affected, so reading
        // success off the status alone would report a file as trashed that is still there.
        return result.Affected > 0;
    }

    /// <summary>
    /// Change files: move, publish, rename the URL, or set the link's gates.
    /// </summary>
    /// <remarks>
    /// One call per menu action rather than one per file. The server takes a list and
    /// answers with the rows as they now are, which is what lets a card be redrawn
    /// without re-fetching the page it sits on.
    /// </remarks>
    /// <exception cref="ModlightUnauthorizedException">The token was refused.</exception>
    /// <exception cref="ModlightApiException">The server refused, and said why.</exception>
    public static async Task<ModlightUploadPatchResult> EditUploadsAsync(string host, string token,
        IReadOnlyList<string> ids, ModlightUploadEdit edit, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["ids"] = ids };

        foreach (KeyValuePair<string, object?> field in edit.Fields)
        {
            body[field.Key] = field.Value;
        }

        string json = await SendJsonAsync(HttpMethod.Patch, host + "/api/uploads", host, token, body, cancellationToken);
        return JsonConvert.DeserializeObject<ModlightUploadPatchResult>(json) ?? new ModlightUploadPatchResult();
    }

    /// <summary>
    /// Trash, restore, or delete for good.
    /// </summary>
    /// <param name="mode">
    /// <c>trash</c>, <c>restore</c> or <c>permanent</c>. The server defaults an absent
    /// or unknown value to <c>trash</c>, so the destructive reading is never the one a
    /// caller reaches by forgetting a field - and this client never sends it by accident
    /// either, because every call site names the mode.
    /// </param>
    /// <exception cref="ModlightUnauthorizedException">The token was refused.</exception>
    public static async Task<ModlightDeleteResult> DeleteUploadsAsync(string host, string token,
        IReadOnlyList<string> ids, string mode, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new ModlightDeleteResult();
        }

        string json = await SendJsonAsync(HttpMethod.Delete, host + "/api/uploads", host, token,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["ids"] = ids, ["mode"] = mode },
            cancellationToken);

        return JsonConvert.DeserializeObject<ModlightDeleteResult>(json) ?? new ModlightDeleteResult();
    }

    /// <summary>Make a folder, at the top of the library or inside another one.</summary>
    /// <exception cref="ModlightApiException">The name was refused, or the tree is too deep.</exception>
    public static async Task<ModlightFolder?> CreateFolderAsync(string host, string token, string name,
        string? parentId, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["name"] = name };

        // Sent only when there is one. An explicit null is a perfectly good "at the top",
        // but leaving the key out says the same thing and keeps the body honest about
        // what the caller actually asked for.
        if (!string.IsNullOrEmpty(parentId))
        {
            body["parentId"] = parentId;
        }

        string json = await SendJsonAsync(HttpMethod.Post, host + "/api/folders", host, token, body, cancellationToken);
        return JsonConvert.DeserializeObject<ModlightFolderResult>(json)?.Folder;
    }

    /// <summary>Give a folder a different name. Its public gallery address follows.</summary>
    public static Task<ModlightFolder?> RenameFolderAsync(string host, string token, string id, string name,
        CancellationToken cancellationToken) =>
        PatchFolderAsync(host, token, id, "name", name, cancellationToken);

    /// <summary>Turn the folder's public gallery on or off.</summary>
    public static Task<ModlightFolder?> SetFolderVisibilityAsync(string host, string token, string id, bool isPublic,
        CancellationToken cancellationToken) =>
        PatchFolderAsync(host, token, id, "isPublic", isPublic, cancellationToken);

    /// <summary>
    /// Remove a folder, keeping or trashing what is inside it.
    /// </summary>
    /// <param name="withContents">
    /// False - the default everywhere - takes the folder alone: its files fall back to
    /// "no folder" and its children move up a level. True puts the whole subtree in the
    /// trash. The server insists on a literal <c>true</c> for the second reading, and
    /// this client only ever sends one from a dialog that had to be armed first.
    /// </param>
    public static async Task<ModlightFolderDeleteResult> DeleteFolderAsync(string host, string token, string id,
        bool withContents, CancellationToken cancellationToken)
    {
        string json = await SendJsonAsync(HttpMethod.Delete, host + "/api/folders", host, token,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, ["withContents"] = withContents },
            cancellationToken);

        return JsonConvert.DeserializeObject<ModlightFolderDeleteResult>(json) ?? new ModlightFolderDeleteResult();
    }

    private static async Task<ModlightFolder?> PatchFolderAsync(string host, string token, string id, string field,
        object value, CancellationToken cancellationToken)
    {
        string json = await SendJsonAsync(HttpMethod.Patch, host + "/api/folders", host, token,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, [field] = value },
            cancellationToken);

        return JsonConvert.DeserializeObject<ModlightFolderResult>(json)?.Folder;
    }

    /// <summary>
    /// Ask whether a conversion would work, without making it happen.
    /// </summary>
    /// <remarks>
    /// The reason this exists is the one that made the web panel grow it: a refusal
    /// arrives as a header on a response the client has ALREADY committed to treating as
    /// a download, so a video with no sound track answers "Failed" with no reason
    /// attached. <c>?check=1</c> runs every gate and none of the work - measured on
    /// production at 0.12 s and zero bytes for a picture, because the row already knows
    /// its own width, height and frame count.
    ///
    /// Two things the dry run deliberately does NOT do: it does not spend a view (so
    /// opening a menu cannot burn a one-time link) and it does not write to the derived
    /// cache. Both are the server's doing; this note is here so that nobody later
    /// "saves a round trip" by treating the check as the download.
    /// </remarks>
    /// <returns>Yes, or a refusal carrying the server's own reason token.</returns>
    public static async Task<ModlightConvertVerdict> CheckConversionAsync(string host, string token,
        string convertUrl, CancellationToken cancellationToken)
    {
        if (!TryRewriteToHost(convertUrl, host, out string url))
        {
            return ModlightConvertVerdict.No("missing");
        }

        url += url.Contains('?') ? "&check=1" : "?check=1";

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", token);

        HttpResponseMessage response;

        try
        {
            response = await HttpClientFactory.Create()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline, not the caller's. "The server did not answer in time" is a
            // refusal like any other as far as the person clicking is concerned.
            return ModlightConvertVerdict.No("timeout");
        }
        catch (HttpRequestException)
        {
            return ModlightConvertVerdict.No("offline");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return ModlightConvertVerdict.Yes;
            }

            return ModlightConvertVerdict.No(RefusalOf(response) ?? "");
        }
    }

    /// <summary>
    /// Pull one file down to disk, streaming rather than buffering it.
    /// </summary>
    /// <remarks>
    /// Not <c>URLHelpers.OpenURL</c>, which is how the web panel does it: the browser
    /// arrives carrying the session cookie, and this app has a token instead. Without the
    /// token the server treats the request as a stranger's, which for this account means
    /// two refusals rather than none - Round 6 stamps a default password on every new
    /// upload, and the encoder-backed conversions are owner-only.
    ///
    /// <c>ResponseHeadersRead</c> plus a copy, so a two-gigabyte video never exists in
    /// memory. The caller owns the part-written file if this throws: a truncated download
    /// left lying about with a plausible name is worse than no download.
    /// </remarks>
    /// <exception cref="ModlightApiException">The server refused, with its own reason where it gave one.</exception>
    public static async Task DownloadToFileAsync(string host, string token, string link, string path,
        CancellationToken cancellationToken)
    {
        if (!TryRewriteToHost(link, host, out string url))
        {
            throw new ModlightApiException(HttpStatusCode.BadRequest, "", "missing");
        }

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", token);

        using HttpResponseMessage response = await HttpClientFactory.Create()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ModlightUnauthorizedException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ModlightApiException(response.StatusCode, "", RefusalOf(response));
        }

        FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        await using (file.ConfigureAwait(false))
        {
            Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);

            await using (body.ConfigureAwait(false))
            {
                await body.CopyToAsync(file, cancellationToken);
            }
        }
    }

    /// <summary>
    /// The reason a conversion was refused, or null when the response did not name one.
    /// </summary>
    /// <remarks>
    /// Null rather than a stand-in, because "the server refused without saying why" has
    /// its own sentence. Guessing one of the named reasons here would put a confident,
    /// specific and possibly false statement in front of somebody.
    /// </remarks>
    private static string? RefusalOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Convert-Refused", out IEnumerable<string>? values)
            ? Enumerable.FirstOrDefault(values)
            : null;

    /// <summary>
    /// One JSON request, one JSON answer, with the refusals turned into exceptions.
    /// </summary>
    /// <remarks>
    /// The token travels in the <c>x-api-key</c> HEADER and never in the request line.
    /// That is not style: the request line is the one part of an HTTP request that every
    /// proxy in the chain writes to a log as a matter of course, and the credential this
    /// carries can read and delete everything the account owns.
    /// </remarks>
    private static async Task<string> SendJsonAsync(HttpMethod method, string url, string host, string token,
        object body, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);

        using HttpRequestMessage request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
        };

        request.Headers.TryAddWithoutValidation("x-api-key", token);

        using HttpResponseMessage response = await HttpClientFactory.Create().SendAsync(request, deadline.Token);
        string json = await response.Content.ReadAsStringAsync(deadline.Token);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ModlightUnauthorizedException();
        }

        if (!response.IsSuccessStatusCode)
        {
            // The server writes a sentence for every refusal it makes, and a `reason`
            // token beside it for the ones a client might want to act on. Showing its
            // words rather than a status code is the difference between "that address is
            // already taken" and "409".
            ModlightApiError? error = null;

            try
            {
                error = JsonConvert.DeserializeObject<ModlightApiError>(json);
            }
            catch (JsonException)
            {
                // A refusal that is not JSON at all - a proxy's own error page, say. The
                // status code is then the whole of what we know.
            }

            throw new ModlightApiException(response.StatusCode, error?.Message ?? "", error?.Reason);
        }

        return json;
    }

    private static async Task<string> GetStringAsync(string url, string host, string token, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", token);

        using HttpResponseMessage response = await HttpClientFactory.Create().SendAsync(request, deadline.Token);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ModlightUnauthorizedException();
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(deadline.Token);
    }

    /// <summary>
    /// Point a link the server built at the deployment we hold a token for.
    /// </summary>
    /// <remarks>
    /// Links come back on the account's PREFERRED hostname, which can be a different
    /// name from the one signed in to - the deployment answers on several, and the
    /// account chooses which its links read as. Every one of them serves the same files
    /// from the same paths, so swapping the origin costs nothing and buys the rule that
    /// matters: the token only ever goes to the host that issued it. Copying and
    /// opening links keep the server's own spelling, because that is the address the
    /// owner means to share.
    ///
    /// A link that is not absolute, or not http(s), is refused rather than patched up.
    /// </remarks>
    private static bool TryRewriteToHost(string link, string host, out string result)
    {
        result = "";

        if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !Uri.TryCreate(host, UriKind.Absolute, out Uri? origin))
        {
            return false;
        }

        result = origin.Scheme + "://" + origin.Authority + uri.PathAndQuery;
        return true;
    }
}

/// <summary>
/// What came back when a moving preview was asked for.
/// </summary>
/// <remarks>
/// Three outcomes rather than a nullable array, because the two failures have different
/// consequences and a caller holding only null cannot tell them apart. See
/// <see cref="ModlightLibrary.GetMotionThumbnailAsync"/>.
/// </remarks>
public readonly struct ModlightMotionPreview
{
    private ModlightMotionPreview(byte[]? bytes, bool refused)
    {
        Bytes = bytes;
        IsRefused = refused;
    }

    /// <summary>The animated WebP, or null when there is none.</summary>
    public byte[]? Bytes { get; }

    /// <summary>This FILE will never have a moving preview. Permanent; stop asking.</summary>
    public bool IsRefused { get; }

    public bool IsOk => Bytes != null;

    public static ModlightMotionPreview Ok(byte[] bytes) => new(bytes, refused: false);

    public static ModlightMotionPreview Refused => new(null, refused: true);

    /// <summary>Not this time. Says nothing about the file; asking again later is fine.</summary>
    public static ModlightMotionPreview Unavailable => new(null, refused: false);
}

/// <summary>
/// The server answered, and said this token is not one it will act on.
/// </summary>
/// <remarks>
/// Its own type because it is the one failure with a different remedy: every other
/// error asks the person to try again, and this one asks them to sign in. A caller
/// that could not tell the two apart would offer a Retry button that can only fail.
/// </remarks>
public sealed class ModlightUnauthorizedException : Exception
{
}

/// <summary>How every refusal from the Modlight API is shaped.</summary>
internal sealed class ModlightApiError
{
    [JsonProperty("message")]
    public string? Message { get; set; }

    /// <summary>
    /// A stable token for the refusals a client might want to act on, e.g.
    /// <c>code-taken</c>. Absent on the ones that are only worth reading.
    /// </summary>
    [JsonProperty("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// The server understood the request and refused it, in its own words.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ModlightUnauthorizedException"/>, which asks the person to
/// sign in, and from a transport failure, which asks them to try again. This one is
/// neither: the connection worked, the credential was accepted, and the answer is no.
/// Showing <see cref="Detail"/> is the difference between telling somebody that the
/// address they picked is taken and telling them "409".
/// </remarks>
public sealed class ModlightApiException : Exception
{
    public ModlightApiException(HttpStatusCode status, string detail, string? reason)
    {
        Status = status;
        Detail = detail;
        Reason = reason;
    }

    public HttpStatusCode Status { get; }

    /// <summary>The server's own sentence, or empty when it did not write one.</summary>
    public string Detail { get; }

    /// <summary>The machine-readable token, when the refusal carried one.</summary>
    public string? Reason { get; }

    /// <summary>Whether the refusal is the one a different name would fix.</summary>
    public bool IsNameTaken => Reason is "code-taken" or "code-reserved";
}
