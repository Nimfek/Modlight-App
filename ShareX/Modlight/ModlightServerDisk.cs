#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

#nullable enable

using Newtonsoft.Json;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// What the server's storage is holding, as the administration screen draws it.
/// </summary>
/// <remarks>
/// <para>
/// A mirror of the web panel's disk view, and deliberately not a second opinion about
/// it. Every number here is one the panel already computes in
/// <c>lib/data/admin.ts</c> (<c>serverStorageUsage</c>) and <c>lib/data/admin-disk.ts</c>
/// (<c>diskInsights</c>); this type is the wire shape those two read models take on the
/// way out, field for field, so the two screens cannot drift into disagreeing about how
/// full the disk is. The property names are the panel's own - renaming them here to read
/// more like C# would mean the next person comparing the two has to hold a translation
/// table in their head.
/// </para>
/// <para>
/// WHAT IS MEASURED AND WHAT IS ONLY COUNTED. <see cref="Disk"/> is a real
/// <c>statfs</c> of the filesystem the uploads sit on: total, free and used are the
/// machine's own answer, and they include everything else on that disk. Everything
/// else on this type is the DATABASE's answer - the sum of the sizes recorded when each
/// file arrived. On a local install the two agree closely; on an object store there is
/// no filesystem to measure at all, <see cref="Disk"/> is null, and the totals can only
/// be a floor: thumbnails, cached conversions and the leftovers of an interrupted
/// upload are objects that no row accounts for. <see cref="IsLocal"/> is what the screen
/// reads to decide which of those two sentences to print, because presenting a count as
/// a measurement is the one mistake this screen must not make.
/// </para>
/// </remarks>
public sealed class ModlightDiskReport
{
    /// <summary>Where the bytes actually live: "local", "s3", and so on.</summary>
    [JsonProperty("driver")]
    public string? Driver { get; set; }

    /// <summary>True when the numbers can be compared against a real filesystem.</summary>
    [JsonProperty("local")]
    public bool IsLocal { get; set; }

    /// <summary>Uploads that are not in the trash.</summary>
    [JsonProperty("liveFiles")]
    public long LiveFiles { get; set; }

    [JsonProperty("liveBytes")]
    public long LiveBytes { get; set; }

    /// <summary>Soft-deleted uploads. Still occupying storage, which is the point.</summary>
    [JsonProperty("trashedFiles")]
    public long TrashedFiles { get; set; }

    [JsonProperty("trashedBytes")]
    public long TrashedBytes { get; set; }

    /// <summary>The filesystem holding storage, or null for an object store.</summary>
    [JsonProperty("disk")]
    public ModlightDiskSpace? Disk { get; set; }

    /// <summary>The operator's floor: uploads are refused once free space is under it.</summary>
    [JsonProperty("minFreeBytes")]
    public long MinFreeBytes { get; set; }

    /// <summary>Live bytes per kind of file, heaviest first.</summary>
    [JsonProperty("kinds")]
    public List<ModlightDiskKind>? Kinds { get; set; }

    /// <summary>What the kind breakdown adds up to, so shares can be honest.</summary>
    [JsonProperty("kindBytes")]
    public long KindBytes { get; set; }

    /// <summary>Per-account totals, biggest first, with the tail folded into one row.</summary>
    [JsonProperty("accounts")]
    public List<ModlightDiskAccount>? Accounts { get; set; }

    /// <summary>The heaviest live files.</summary>
    [JsonProperty("biggest")]
    public List<ModlightDiskFile>? Biggest { get; set; }

    /// <summary>The longest-untouched live files.</summary>
    [JsonProperty("cold")]
    public List<ModlightDiskFile>? Cold { get; set; }

    [JsonProperty("coldTotals")]
    public ModlightDiskCold? ColdTotals { get; set; }

    [JsonProperty("trash")]
    public ModlightDiskTrash? Trash { get; set; }

    /// <summary>How long the trash holds a file before the sweeper purges it.</summary>
    [JsonProperty("trashRetentionDays")]
    public int TrashRetentionDays { get; set; }

    /// <summary>The window "about to be purged" means.</summary>
    [JsonProperty("trashDueSoonDays")]
    public int TrashDueSoonDays { get; set; }
}

/// <summary>A real filesystem, as the machine reports it.</summary>
public sealed class ModlightDiskSpace
{
    [JsonProperty("totalBytes")]
    public long TotalBytes { get; set; }

    /// <summary>Bytes an unprivileged process may still write - df's "Avail".</summary>
    [JsonProperty("freeBytes")]
    public long FreeBytes { get; set; }

    /// <summary>Occupied by EVERYTHING on this filesystem, not only by us.</summary>
    [JsonProperty("usedBytes")]
    public long UsedBytes { get; set; }
}

/// <summary>Live files of one coarse type.</summary>
public sealed class ModlightDiskKind
{
    /// <summary>image, video, audio, text, pdf, archive, other.</summary>
    [JsonProperty("kind")]
    public string? Kind { get; set; }

    [JsonProperty("files")]
    public long Files { get; set; }

    [JsonProperty("bytes")]
    public long Bytes { get; set; }
}

/// <summary>What one account occupies, or the folded tail of the list.</summary>
public sealed class ModlightDiskAccount
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("email")]
    public string? Email { get; set; }

    /// <summary>The first segment of this account's public links.</summary>
    [JsonProperty("tag")]
    public string? Tag { get; set; }

    /// <summary>True for the pseudo-row standing for files whose owner is gone.</summary>
    [JsonProperty("orphaned")]
    public bool Orphaned { get; set; }

    /// <summary>True for the folded tail; <see cref="Accounts"/> says how many.</summary>
    [JsonProperty("rest")]
    public bool Rest { get; set; }

    [JsonProperty("accounts")]
    public int Accounts { get; set; }

    [JsonProperty("liveFiles")]
    public long LiveFiles { get; set; }

    [JsonProperty("liveBytes")]
    public long LiveBytes { get; set; }

    [JsonProperty("trashedFiles")]
    public long TrashedFiles { get; set; }

    [JsonProperty("trashedBytes")]
    public long TrashedBytes { get; set; }
}

/// <summary>One file worth naming on the administration screen.</summary>
public sealed class ModlightDiskFile
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

    [JsonProperty("ownerTag")]
    public string? OwnerTag { get; set; }

    [JsonProperty("ownerName")]
    public string? OwnerName { get; set; }

    [JsonProperty("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonProperty("views")]
    public long Views { get; set; }

    /// <summary>
    /// The last recorded fetch, or null when nothing has fetched it since it arrived.
    /// </summary>
    /// <remarks>
    /// NULL IS NOT PROOF THE FILE WAS NEVER READ, and the screen has to say so. Access
    /// has only been counted since the row was written on this server; files that
    /// predate the counter, or were imported, carry no history at all. Drawing "never opened" here would invite somebody to delete a file
    /// that is downloaded every week.
    /// </remarks>
    [JsonProperty("lastAccessedAt")]
    public DateTimeOffset? LastAccessedAt { get; set; }
}

/// <summary>How much has gone untouched, over the window it was measured on.</summary>
public sealed class ModlightDiskCold
{
    [JsonProperty("files")]
    public long Files { get; set; }

    [JsonProperty("bytes")]
    public long Bytes { get; set; }

    /// <summary>The window the two numbers above cover.</summary>
    [JsonProperty("days")]
    public int Days { get; set; }
}

/// <summary>What the trash is holding and what it is about to give back.</summary>
public sealed class ModlightDiskTrash
{
    [JsonProperty("files")]
    public long Files { get; set; }

    [JsonProperty("bytes")]
    public long Bytes { get; set; }

    /// <summary>When the longest-standing item was trashed, or null for an empty trash.</summary>
    [JsonProperty("oldest")]
    public DateTimeOffset? Oldest { get; set; }

    [JsonProperty("dueSoonFiles")]
    public long DueSoonFiles { get; set; }

    [JsonProperty("dueSoonBytes")]
    public long DueSoonBytes { get; set; }

    /// <summary>
    /// Past the retention window and STILL stored.
    /// </summary>
    /// <remarks>
    /// Should be zero: the sweeper runs every six hours. Anything else is the server
    /// reporting that the sweeper is not running, which is the only way an operator
    /// would ever find out - so the screen shows it as a warning rather than a number.
    /// </remarks>
    [JsonProperty("overdueFiles")]
    public long OverdueFiles { get; set; }

    [JsonProperty("overdueBytes")]
    public long OverdueBytes { get; set; }
}

/// <summary>
/// The server said it has no such endpoint.
/// </summary>
/// <remarks>
/// Its own type because it is the one failure whose remedy is neither "try again" nor
/// "sign in": the deployment is older than this app. A client that showed it as a plain
/// error would send an administrator hunting for a fault on a server that is working
/// perfectly.
/// </remarks>
public sealed class ModlightNotSupportedException : Exception
{
}

/// <summary>
/// The credential is real, and too narrow for this screen.
/// </summary>
/// <remarks>
/// Its own type rather than a field on <see cref="ModlightUnauthorizedException"/>,
/// which is thrown from every call in <see cref="ModlightLibrary"/> and caught in a
/// dozen places: widening it would make every one of those sites decide something they
/// have no opinion about. Nothing outside this screen needs the distinction, so nothing
/// outside this screen is asked to carry it.
/// </remarks>
public sealed class ModlightAdminForbiddenException : Exception
{
}

/// <summary>
/// Asking the server what its storage is holding.
/// </summary>
/// <remarks>
/// <para>
/// SEPARATE FROM <see cref="ModlightLibrary"/> on purpose. That class is the account's
/// own files, which every signed-in client may read; this is the whole install, which
/// only an administrator may. Keeping them apart means no ordinary call can drift into
/// this one's authority by sharing a helper that quietly stopped checking.
/// </para>
/// <para>
/// ADVISORY ROLE, ENFORCED AUTHORITY. <see cref="ModlightAdmin"/> decides whether the
/// app OFFERS this screen; the server decides whether it answers. Those are different
/// decisions and this class never conflates them - every refusal comes back as a type
/// the screen can name, rather than being papered over with an empty screen.
/// </para>
/// <para>
/// TWO GATES AT THE FAR END, and the four refusals are four because they have four
/// different remedies: 401 sign in again, 403 mint a wider token, 404 update the server
/// (or accept that this account is not an administrator), anything else a real fault.
/// The panel demands the <c>admin</c> SCOPE on the credential and the administrator ROLE
/// on the account, and one being satisfied does not excuse the other.
/// </para>
/// </remarks>
public static class ModlightServerDisk
{
    /// <summary>
    /// Where the report lives.
    /// </summary>
    /// <remarks>
    /// Named once so that the screen, the error message and the request cannot drift
    /// apart. Implemented in the panel as <c>app/api/admin/disk/route.ts</c>, which
    /// answers from the same two read models the web administration page renders.
    /// </remarks>
    public const string Path = "/api/admin/disk";

    /// <summary>
    /// Longer than a listing's budget.
    /// </summary>
    /// <remarks>
    /// The server answers this from six aggregate queries over the uploads table inside
    /// one repeatable-read transaction. On this install that is tens of milliseconds
    /// warm, but the first read after a restart pays for the pages coming off disk, and
    /// a screen that gives up on a cold database looks broken when it was merely first.
    ///
    /// Public because the screen quotes it: "did not answer within 30 seconds" has to be
    /// the number actually waited, and a message with its own copy of it starts lying
    /// the first time this line is edited.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fetch the report for the signed-in server.
    /// </summary>
    /// <exception cref="ModlightUnauthorizedException">
    /// The server will not act on this credential at all: unknown, revoked, or belonging
    /// to a disabled account. Signing in again is the remedy.
    /// </exception>
    /// <exception cref="ModlightAdminForbiddenException">
    /// The credential is real and too narrow. This needs a token whose scope grants
    /// <c>admin</c>, i.e. a full one; a new token is the remedy, not a new sign-in.
    /// </exception>
    /// <exception cref="ModlightNotSupportedException">
    /// The server answered 404, which it uses for BOTH "no such endpoint on this build"
    /// and "this account does not administer me" - on purpose, so that an ordinary
    /// account cannot use the status to discover who the administrators are. The caller
    /// settles which by asking <c>/api/me</c> again.
    /// </exception>
    /// <remarks>
    /// The token travels in the <c>x-api-key</c> HEADER, never in the request line: the
    /// panel sits behind nginx, and a query string lands in the access log.
    /// </remarks>
    public static async Task<ModlightDiskReport> GetAsync(string host, string token,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, host + Path);
        request.Headers.TryAddWithoutValidation("x-api-key", token);

        using HttpResponseMessage response = await HttpClientFactory.Create().SendAsync(request, deadline.Token);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ModlightUnauthorizedException();
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Told apart from the 401 above, which the rest of this app fuses with it.
            // Here the difference IS the remedy: a 401 means the credential is no longer
            // one this server will act on, and the answer is to sign in again; a 403
            // means the credential is perfectly good and too NARROW, and signing in
            // again with the same token reproduces it exactly. No single sentence is
            // true of both, and the sentence that tries sends half the people who read
            // it to do the wrong thing.
            //
            // The server also names the scope it wanted, as `required_scope` on the
            // body. Deliberately not read: this screen needs exactly one scope, and
            // assembling the message from the wire would buy a translation problem in
            // exchange for a fact that is already fixed.
            throw new ModlightAdminForbiddenException();
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // 404 is TWO answers wearing one status, and that is the server being
            // careful rather than the server being vague: it will not tell a
            // non-administrator that an administrators' endpoint exists, so "you are
            // not one" and "this build has no such route" are made to look identical.
            //
            // Which one it is cannot be settled here - it takes a second question, and
            // the caller asks it (AdminViewModel.LoadAsync re-reads /api/me). This
            // throws the same type either way so that the decision lives in one place
            // rather than half here and half there.
            throw new ModlightNotSupportedException();
        }

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(deadline.Token);

        // An empty object rather than null: every list on the report is already nullable
        // and every screen already reads them defensively, so a body that parsed to
        // nothing renders as "nothing stored" instead of throwing on the UI thread.
        return JsonConvert.DeserializeObject<ModlightDiskReport>(json) ?? new ModlightDiskReport();
    }
}
