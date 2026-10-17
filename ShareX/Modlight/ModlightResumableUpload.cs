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

using ShareX.HelpersLib;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

internal enum ModlightTransferOutcome
{
    Success,

    /// <summary>Did not work this time. Trying again later is reasonable.</summary>
    Retry,

    /// <summary>Will not work by repetition. Somebody has to do something.</summary>
    Blocked,

    /// <summary>The queue asked for it to stop.</summary>
    Canceled
}

internal sealed class ModlightTransferResult
{
    public ModlightTransferOutcome Outcome { get; init; }
    public ModlightQueueReason Reason { get; init; } = ModlightQueueReason.None;

    /// <summary>Whatever the server said, or a number for the sentence. Never a finished sentence.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The link to put in front of the user, chosen by the server from the account's preference.</summary>
    public string CopyUrl { get; init; } = "";

    public string ViewUrl { get; init; } = "";
    public string RawUrl { get; init; } = "";

    /// <summary>What the server asked us to wait, when it asked.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public static ModlightTransferResult Failure(ModlightTransferOutcome outcome, ModlightQueueReason reason,
        string detail = "", TimeSpan? retryAfter = null) =>
        new() { Outcome = outcome, Reason = reason, Detail = detail, RetryAfter = retryAfter };
}

/// <summary>
/// Sending one queued file to a Modlight server over tus, resuming where it left off.
/// </summary>
/// <remarks>
/// <b>Why this exists next to a perfectly good uploader.</b> Every live upload in this
/// application goes out as a single streamed POST to /upload, and that stays exactly as
/// it is: it is the ShareX contract, the server keeps that endpoint uncapped for clients
/// that cannot chunk, and one round trip is the right shape for an upload the user is
/// watching. What it cannot do is survive the connection dying, because there is nothing
/// on the server afterwards to point at - the next attempt starts from byte zero.
///
/// A queued item is, by definition, one whose single POST did not survive the link. On
/// the connection this whole application is built for, restarting four gigabytes from
/// zero every time the link blinks is not a slower success, it is a permanent failure -
/// the transfer never gets a clear run long enough to finish. So the queue takes the
/// other route the same server already offers: tus on /api/files, which has accepted an
/// API token since the desktop clients arrived, and which answers HEAD with the exact
/// byte it got to. That number is what turns "start again" into "carry on".
///
/// Uniformly, for every queued item, small ones included. A threshold would buy three
/// saved round trips on a screenshot and cost a second code path, a second set of error
/// mappings, and a number nobody can tune from the outside; the queue is a background
/// drain, not the latency-sensitive path, and its one job is to be the same every time.
///
/// One PATCH carries the whole remainder rather than a march of fixed chunks. Chunking
/// would only add value if a half-written chunk were lost, and it is not: the store
/// keeps whatever arrived, so the resume point is wherever the bytes actually stopped.
/// What chunking would add is a round trip per chunk on the worst link in the story.
/// </remarks>
internal sealed class ModlightResumableUpload
{
    private const string TusVersion = "1.0.0";
    private const string TusPath = "/api/files";
    private const string OffsetContentType = "application/offset+octet-stream";

    /// <summary>The metadata key the server reads a drop target from.</summary>
    private const string FolderMetadataKey = "folderId";

    /// <summary>Handshake requests are small; if one of these hangs, the link is not going to carry a file.</summary>
    private static readonly TimeSpan handshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a transfer may accept no bytes before it is treated as dead.
    /// </summary>
    /// <remarks>
    /// The reason the body has no overall timeout but does have this one. A four-gigabyte
    /// upload legitimately takes hours, so any deadline long enough for it is far too long
    /// to notice a dropped link - and a dropped link very often does not produce an error
    /// at all. A laptop that sleeps mid-PATCH leaves a socket that neither delivers
    /// anything nor closes, and without this the queue would sit on it for ever, showing
    /// a progress bar that has not moved since Tuesday.
    /// </remarks>
    private static readonly TimeSpan stallTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How often the stall watch looks.</summary>
    private static readonly TimeSpan stallPoll = TimeSpan.FromSeconds(5);

    private static readonly object clientLock = new();
    private static HttpClient? client;

    private readonly ModlightAccount account;

    public ModlightResumableUpload(ModlightAccount account)
    {
        this.account = account;
    }

    /// <summary>Bytes confirmed so far, and the total. Fired from the transfer thread.</summary>
    public event Action<long, long>? Progress;

    /// <summary>
    /// The client used for transfers.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="HttpClientFactory"/> for one reason, and it is not style:
    /// that client has the default hundred-second timeout, which is a ceiling on the whole
    /// request including the body. Every upload over about a hundred seconds - which is
    /// every upload this queue exists for - would be cancelled by the clock rather than by
    /// anything that went wrong. Infinite here, with the stall watch above doing the job
    /// the timeout was doing badly. The proxy is read from the same place, so a machine
    /// behind a corporate proxy is no worse served by the queue than by the app.
    /// </remarks>
    private static HttpClient Client
    {
        get
        {
            if (client == null)
            {
                lock (clientLock)
                {
                    if (client == null)
                    {
                        HttpClientHandler handler = new()
                        {
                            Proxy = HelpersOptions.CurrentProxy.GetWebProxy(),

                            // The tus flow reads Location and Upload-Offset off the
                            // responses; a redirect followed silently would answer with
                            // headers belonging to a request we did not make.
                            AllowAutoRedirect = false
                        };

                        HttpClient created = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
                        created.DefaultRequestHeaders.UserAgent.ParseAdd(ShareXResources.UserAgent);
                        client = created;
                    }
                }
            }

            return client;
        }
    }

    /// <summary>
    /// Send one item as far as it will go this time.
    /// </summary>
    /// <remarks>
    /// Everything about the transfer that has to survive - the session URL and the
    /// confirmed offset - is written back onto <paramref name="item"/> as it is learned,
    /// so a caller that persists the item after this returns has persisted the progress,
    /// however this returned.
    /// </remarks>
    public async Task<ModlightTransferResult> SendAsync(ModlightQueueItem item, CancellationToken cancellationToken)
    {
        try
        {
            ModlightTransferResult? session = await EnsureSessionAsync(item, cancellationToken).ConfigureAwait(false);

            if (session != null)
            {
                return session;
            }

            return await SendBodyAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ModlightTransferResult.Failure(ModlightTransferOutcome.Canceled, ModlightQueueReason.None);
        }
        catch (Exception exception)
        {
            // Everything that stops a request from completing lands here: DNS, TLS, a
            // reset socket, a stall we cancelled ourselves. None of them says anything
            // about whether the upload would work in ten minutes, so all of them are
            // "unreachable" and none of them counts against the attempt budget.
            DebugHelper.WriteException(exception, "Modlight queue transfer");
            return ModlightTransferResult.Failure(ModlightTransferOutcome.Retry, ModlightQueueReason.Unreachable,
                exception.Message);
        }
    }

    /// <summary>
    /// Tell the server to forget a half-finished upload.
    /// </summary>
    /// <remarks>
    /// Called when a queued item is cancelled or given up on. Not required for
    /// correctness - the server reclaims sessions nobody touches within a day - but a
    /// cancelled four-gigabyte upload leaving four gigabytes of somebody else's scratch
    /// space occupied until tomorrow is rude when one request settles it.
    /// </remarks>
    public async Task AbandonAsync(string url)
    {
        using CancellationTokenSource deadline = new(handshakeTimeout);
        using HttpRequestMessage request = new(HttpMethod.Delete, url);
        Decorate(request);

        using HttpResponseMessage response = await Client.SendAsync(request, deadline.Token).ConfigureAwait(false);

        DebugHelper.WriteLine("Modlight queue: abandoned a resumable session, " + (int)response.StatusCode + ".");
    }

    /// <summary>
    /// Get a session with a known offset, creating one if there is none.
    /// </summary>
    /// <returns>Null when there is a usable session; a failure to report otherwise.</returns>
    private async Task<ModlightTransferResult?> EnsureSessionAsync(ModlightQueueItem item, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(item.TusUrl))
        {
            long? offset = await HeadOffsetAsync(item.TusUrl!, cancellationToken).ConfigureAwait(false);

            if (offset != null && offset.Value <= item.Length)
            {
                item.TusOffset = offset.Value;
                return null;
            }

            // 404 or 410: the server has forgotten this upload, which it does after a
            // day. Not a failure and not worth reporting - the file is still here and
            // the only cost is the bytes already sent. A new session, from zero.
            DebugHelper.WriteLine("Modlight queue: the resumable session is gone; starting the transfer again.");
            item.TusUrl = null;
            item.TusOffset = 0;
        }

        return await CreateSessionAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ask the server where it got to. Null means the session no longer exists.</summary>
    private async Task<long?> HeadOffsetAsync(string url, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = Deadline(cancellationToken, handshakeTimeout);
        using HttpRequestMessage request = new(HttpMethod.Head, url);
        Decorate(request);

        using HttpResponseMessage response = await Client.SendAsync(request, deadline.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string? value = First(response, "Upload-Offset");

        return long.TryParse(value, out long offset) ? offset : null;
    }

    private async Task<ModlightTransferResult?> CreateSessionAsync(ModlightQueueItem item, CancellationToken cancellationToken)
    {
        ModlightTransferResult? failure = await CreateOnceAsync(item, item.FolderId, cancellationToken).ConfigureAwait(false);

        if (failure == null || failure.Reason != ModlightQueueReason.Refused || string.IsNullOrEmpty(item.FolderId))
        {
            return failure;
        }

        // 403 with a folder named. The folder is the only thing in the request that can
        // have stopped belonging to this account since the item was queued, and the live
        // upload path treats exactly this case as a warning rather than a failure - it
        // stores the file in the default folder and says so. Refusing here would be the
        // two paths disagreeing about the same event, and it would strand a queued
        // upload for ever over a folder the user deleted last week.
        DebugHelper.WriteLine("Modlight queue: the target folder was refused; sending to the account's default folder instead.");

        item.FolderId = null;

        return await CreateOnceAsync(item, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModlightTransferResult?> CreateOnceAsync(ModlightQueueItem item, string? folderId,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = Deadline(cancellationToken, handshakeTimeout);
        using HttpRequestMessage request = new(HttpMethod.Post, Origin() + TusPath);
        Decorate(request);

        request.Headers.TryAddWithoutValidation("Upload-Length", item.Length.ToString());
        request.Headers.TryAddWithoutValidation("Upload-Metadata", Metadata(item, folderId));

        // An empty body with an explicit length. Without it some proxies answer 411
        // before the request reaches the application at all.
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentLength = 0;

        using HttpResponseMessage response = await Client.SendAsync(request, deadline.Token).ConfigureAwait(false);

        // An empty file is finished the moment it is created: the server runs its finish
        // hook there and answers 200 with the links, and - because the status is no longer
        // 201 - deliberately WITHOUT a Location. Reading that as a refusal would leave a
        // zero-byte upload creating a new session on every attempt for two weeks.
        if (response.StatusCode == HttpStatusCode.OK && item.Length == 0)
        {
            item.TusUrl = null;
            item.TusOffset = 0;

            return Landed(response);
        }

        if (response.StatusCode != HttpStatusCode.Created)
        {
            return await ClassifyAsync(response).ConfigureAwait(false);
        }

        string? location = First(response, "Location");

        if (string.IsNullOrEmpty(location))
        {
            return ModlightTransferResult.Failure(ModlightTransferOutcome.Retry, ModlightQueueReason.ServerError,
                "201 without Location");
        }

        item.TusUrl = ResolveLocation(location!);
        item.TusOffset = 0;

        return null;
    }

    /// <summary>
    /// Turn the server's Location into a URL this client is willing to send a token to.
    /// </summary>
    /// <remarks>
    /// Only the path and query are taken; the scheme and host are always this account's.
    /// The server builds that header from the Host and X-Forwarded-Proto it was given, so
    /// behind a proxy that forwards neither it will honestly answer <c>http://</c> - and
    /// following that would put the API token on the wire in clear text, for no better
    /// reason than that something upstream is misconfigured. The path is the only part of
    /// the header we actually needed.
    /// </remarks>
    private string ResolveLocation(string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out Uri? absolute))
        {
            return Origin() + absolute.PathAndQuery;
        }

        return Origin() + (location.StartsWith('/') ? location : "/" + location);
    }

    private async Task<ModlightTransferResult> SendBodyAsync(ModlightQueueItem item, CancellationToken cancellationToken)
    {
        long offset = item.TusOffset;

        if (offset > item.Length)
        {
            // The server holds more bytes than the file has. The file changed under a
            // live session; the caller re-measures, so the honest move is to abandon
            // this session and let the next attempt build a new one.
            item.TusUrl = null;
            item.TusOffset = 0;
            return ModlightTransferResult.Failure(ModlightTransferOutcome.Retry, ModlightQueueReason.ServerError,
                "offset past the end of the file");
        }

        Progress?.Invoke(offset, item.Length);

        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using FileStream source = new(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        source.Position = offset;

        long sent = offset;
        long lastActivity = Environment.TickCount64;

        using ProgressReportingStream body = new(source, item.Length - offset, moved =>
        {
            sent = offset + moved;
            lastActivity = Environment.TickCount64;
            Progress?.Invoke(sent, item.Length);
        });

        using Timer watchdog = new(_ =>
        {
            if (Environment.TickCount64 - Interlocked.Read(ref lastActivity) > (long)stallTimeout.TotalMilliseconds)
            {
                stall.Cancel();
            }
        }, null, stallPoll, stallPoll);

        using HttpRequestMessage request = new(new HttpMethod("PATCH"), item.TusUrl);
        Decorate(request);
        request.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString());

        request.Content = new StreamContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(OffsetContentType);
        request.Content.Headers.ContentLength = item.Length - offset;

        HttpResponseMessage response;

        try
        {
            response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Ours, not the caller's: the watchdog fired. Whatever the server did take is
            // still on its disk, and the next HEAD will say how much.
            DebugHelper.WriteLine("Modlight queue: the transfer stopped moving, so it was dropped and will be resumed.");
            return ModlightTransferResult.Failure(ModlightTransferOutcome.Retry, ModlightQueueReason.Unreachable);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.NotFound ||
                response.StatusCode == HttpStatusCode.Gone)
            {
                // 409 is tus for "that is not where I am". Never a reason to give up -
                // the next attempt asks again and starts from wherever the server says.
                item.TusOffset = 0;
                return ModlightTransferResult.Failure(ModlightTransferOutcome.Retry, ModlightQueueReason.ServerError,
                    ((int)response.StatusCode).ToString());
            }

            if (!response.IsSuccessStatusCode)
            {
                return await ClassifyAsync(response).ConfigureAwait(false);
            }

            if (long.TryParse(First(response, "Upload-Offset"), out long confirmed))
            {
                item.TusOffset = confirmed;
            }

            // 200 rather than 204 is how the server signals that its finish hook ran; the
            // links come back on that response and nowhere else.
            if (response.StatusCode != HttpStatusCode.OK && item.TusOffset < item.Length)
            {
                return ModlightTransferResult.Failure(ModlightTransferOutcome.Retry, ModlightQueueReason.Unreachable);
            }

            item.TusUrl = null;
            item.TusOffset = item.Length;

            return Landed(response);
        }
    }

    /// <summary>
    /// The upload is stored. Take the links the finish hook put on the response.
    /// </summary>
    /// <remarks>
    /// <c>copy_url</c> is the one that goes in front of a person: the server picks between
    /// the viewer page and the raw file from the account's own preference, so following it
    /// means the queue, the live uploader, ShareX and the website all hand over the same
    /// kind of link without a fourth switch of ours to fall out of step.
    ///
    /// A stored upload with no link in the answer is stopped rather than retried. Trying
    /// again would send the whole file a second time and leave the account holding it
    /// twice, which is a worse outcome than a row saying it does not understand the reply.
    /// </remarks>
    private static ModlightTransferResult Landed(HttpResponseMessage response)
    {
        string copy = First(response, "X-Media-Copy-Url") ?? "";
        string view = First(response, "X-Media-Url") ?? "";
        string raw = First(response, "X-Media-Raw-Url") ?? "";

        if (string.IsNullOrEmpty(copy) && string.IsNullOrEmpty(view))
        {
            return ModlightTransferResult.Failure(ModlightTransferOutcome.Blocked, ModlightQueueReason.ServerError,
                "no link in the response");
        }

        return new ModlightTransferResult
        {
            Outcome = ModlightTransferOutcome.Success,
            CopyUrl = string.IsNullOrEmpty(copy) ? view : copy,
            ViewUrl = view,
            RawUrl = raw
        };
    }

    /// <summary>
    /// Decide what a refusal means for the queue.
    /// </summary>
    /// <remarks>
    /// The split that matters is not by number but by whether repeating the request could
    /// ever produce a different answer. A 500 could; a 507 could not, because the account
    /// is full and nothing the client does changes that. Retrying a permanent refusal for
    /// a week is the bug this distinction exists to avoid - and the user never learning
    /// why their upload is not happening is the worse half of it.
    /// </remarks>
    private static async Task<ModlightTransferResult> ClassifyAsync(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        string detail = await ServerTextAsync(response).ConfigureAwait(false);

        switch (status)
        {
            case 401:
            case 403 when IsAuthRefusal(detail):
                return ModlightTransferResult.Failure(ModlightTransferOutcome.Blocked,
                    ModlightQueueReason.TokenRejected, detail);

            case 403:
                // Left for the caller to read as "the folder": every other 403 this
                // endpoint produces says so in words, which is what the check above uses.
                return ModlightTransferResult.Failure(ModlightTransferOutcome.Blocked,
                    ModlightQueueReason.Refused, detail);

            case 507:
                return ModlightTransferResult.Failure(ModlightTransferOutcome.Blocked,
                    ModlightQueueReason.OutOfSpace, detail);

            case 413:
                // Creation compares the declared length against the smaller of the
                // operator's ceiling and what is left of the quota, so this arrives for
                // both. The quota is checked before we get here, which is what lets this
                // be reported as a size the server will not take.
                return ModlightTransferResult.Failure(ModlightTransferOutcome.Blocked,
                    ModlightQueueReason.TooLarge, detail);

            case 400:
            case 404:
            case 405:
            case 409:
            case 411:
            case 412:
            case 415:
                return ModlightTransferResult.Failure(ModlightTransferOutcome.Blocked,
                    ModlightQueueReason.Refused, detail);

            case 429:
                return ModlightTransferResult.Failure(ModlightTransferOutcome.Retry,
                    ModlightQueueReason.ServerError, status.ToString(), RetryAfter(response));

            default:
                return ModlightTransferResult.Failure(ModlightTransferOutcome.Retry,
                    ModlightQueueReason.ServerError, status.ToString(), RetryAfter(response));
        }
    }

    /// <summary>
    /// Whether a 403 is about the credential rather than about the folder.
    /// </summary>
    /// <remarks>
    /// Both come out of the same endpoint with the same code: the server answers 403 for
    /// a token whose scope does not cover uploading, and 403 for a folder that is not this
    /// account's. Only the second is worth retrying without the folder, and getting it
    /// wrong the other way would silently move somebody's upload out of the folder they
    /// chose. The words are the only thing that distinguishes them, so they are read - and
    /// when they say nothing recognisable, the folder retry is the safe guess, because it
    /// runs once and then reports whatever comes back.
    /// </remarks>
    private static bool IsAuthRefusal(string detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return false;
        }

        return detail.Contains("scope", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;

        if (header?.Delta != null)
        {
            return header.Delta;
        }

        if (header?.Date != null)
        {
            TimeSpan wait = header.Date.Value - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>The server's own words about a refusal, clipped to something a row can hold.</summary>
    private static async Task<string> ServerTextAsync(HttpResponseMessage response)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            text = text.Trim();

            if (text.Length == 0 || text.StartsWith('<'))
            {
                // A proxy's HTML error page. There is nothing in it a person wants.
                return "";
            }

            return text.Length > 200 ? text.Substring(0, 200) : text;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void Decorate(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Tus-Resumable", TusVersion);
        request.Headers.TryAddWithoutValidation("x-api-key", account.Token);
    }

    private string Origin()
    {
        string host = account.Host;

        return string.IsNullOrEmpty(host) ? ModlightAccount.DefaultHost : host.TrimEnd('/');
    }

    private static string Metadata(ModlightQueueItem item, string? folderId)
    {
        StringBuilder builder = new();

        Append(builder, "filename", item.FileName);
        Append(builder, "filetype", MimeTypes.GetMimeTypeFromFileName(item.FileName));

        if (!string.IsNullOrEmpty(folderId))
        {
            Append(builder, FolderMetadataKey, folderId!);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string key, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append(',');
        }

        builder.Append(key).Append(' ').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? "")));
    }

    private static string? First(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out System.Collections.Generic.IEnumerable<string>? values))
        {
            foreach (string value in values)
            {
                return value;
            }
        }

        return null;
    }

    private static CancellationTokenSource Deadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    /// <summary>
    /// The request body: a slice of a file that says how much of itself has gone.
    /// </summary>
    /// <remarks>
    /// Read-only and forward-only, which is what <see cref="StreamContent"/> needs and all
    /// it needs. The length is stated so the content can set Content-Length rather than
    /// falling back to chunked transfer encoding - the server's proxy is configured not to
    /// buffer request bodies, and a chunked body is the one shape that can still make it
    /// decide to.
    /// </remarks>
    private sealed class ProgressReportingStream : Stream
    {
        private readonly Stream inner;
        private readonly long length;
        private readonly Action<long> moved;
        private long read;

        public ProgressReportingStream(Stream inner, long length, Action<long> moved)
        {
            this.inner = inner;
            this.length = length;
            this.moved = moved;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int allowed = (int)Math.Min(count, length - read);

            if (allowed <= 0)
            {
                return 0;
            }

            int n = inner.Read(buffer, offset, allowed);

            if (n > 0)
            {
                read += n;
                moved(read);
            }

            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int allowed = (int)Math.Min(count, length - read);

            if (allowed <= 0)
            {
                return 0;
            }

            int n = await inner.ReadAsync(buffer.AsMemory(offset, allowed), cancellationToken).ConfigureAwait(false);

            if (n > 0)
            {
                read += n;
                moved(read);
            }

            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
