#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>How much has arrived, and how fast it is arriving.</summary>
public readonly record struct ModlightDownloadProgress(long Received, long Total, double BytesPerSecond);

public enum ModlightDownloadOutcome
{
    Completed,

    /// <summary>The user pressed cancel. The part file is kept so it can be resumed.</summary>
    Cancelled,

    /// <summary>Nothing answered, or the line went away. Also the answer when the app is offline.</summary>
    Offline,

    /// <summary>Something answered and said no, or sent a body that stopped short.</summary>
    HttpFailed,

    /// <summary>The bytes arrived but could not be written.</summary>
    WriteFailed,

    /// <summary>The URL was not https. Nothing was requested.</summary>
    Refused
}

public readonly record struct ModlightDownloadResult(ModlightDownloadOutcome Outcome, string Message)
{
    public bool Completed => Outcome == ModlightDownloadOutcome.Completed;
}

/// <summary>
/// Fetching one component file over HTTPS: resumable, cancellable, and unable to hang.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately NOT <see cref="FileDownloader"/>, which the updater uses, and the
/// three reasons are worth writing down because "why is there a second downloader" is the
/// first question anybody will ask.
/// </para>
/// <para>
/// 1. It cannot resume. Half of a 977 MB model thrown away because a laptop lid closed is
/// the exact failure this whole feature exists to stop happening.
/// </para>
/// <para>
/// 2. It downloads through the shared <see cref="HttpClientFactory"/> client, whose Timeout
/// is the 100-second default. With HttpCompletionOption.ResponseHeadersRead that timer keeps
/// running while the body is read, so a download slower than 100 seconds in total is
/// cancelled mid-stream - which an 80 MB installer on a fast line never notices and a
/// gigabyte on a home connection never survives.
/// </para>
/// <para>
/// 3. It cancels through a bool field rather than a CancellationToken, so a stalled read
/// blocks until TCP gives up, which on Windows can be minutes of a window that says
/// "Downloading" and means nothing of the kind.
/// </para>
/// <para>
/// What IS reused is the part that matters most: verification. The digest check is
/// <see cref="ModlightUpdateFile"/>, the same gate the updater puts in front of an installer
/// it is about to run elevated.
/// </para>
/// </remarks>
internal static class ModlightComponentDownload
{
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// How long a silent connection is given before it is called dead.
    /// </summary>
    /// <remarks>
    /// Not a total timeout - a big download over a slow line is not a fault - but a
    /// no-progress timeout. Sixty seconds without a single byte is a connection that has
    /// gone away without saying so, and waiting for the operating system to work that out
    /// can take several minutes of a progress bar that has stopped meaning anything.
    /// </remarks>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Download to <paramref name="path"/>, continuing an earlier attempt if one is there.
    /// </summary>
    public static async Task<ModlightDownloadResult> ToFileAsync(string url, string path, long expectedSize,
        IProgress<ModlightDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        // Belt and braces. Every URL in the catalogue is https, and this is here so that
        // stays true after somebody pastes a new one in a hurry: a component is an
        // executable, and plain http means anyone on the path chooses which one runs.
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new ModlightDownloadResult(ModlightDownloadOutcome.Refused, url);
        }

        long resumeFrom = 0;

        try
        {
            FileInfo existing = new(path);

            if (existing.Exists)
            {
                if (expectedSize > 0 && existing.Length >= expectedSize)
                {
                    // Either it is already the whole file - the caller hashes it next and
                    // finds out - or it is longer than the whole file, which means it is not
                    // this file at all and starting over is the only honest option.
                    if (existing.Length == expectedSize)
                    {
                        return new ModlightDownloadResult(ModlightDownloadOutcome.Completed, string.Empty);
                    }

                    existing.Delete();
                }
                else
                {
                    resumeFrom = existing.Length;
                }
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: could not look at the partial download.");
            resumeFrom = 0;
        }

        using HttpClientHandler handler = new()
        {
            Proxy = HelpersOptions.CurrentProxy?.GetWebProxy(),
            AutomaticDecompression = DecompressionMethods.None
        };

        using HttpClient client = new(handler)
        {
            // Infinite, because the point of Timeout here would be to cut off a download for
            // being big. The stall watchdog below is the thing that stops a hang, and it can
            // tell "slow" from "dead", which a total timeout cannot.
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(ShareXResources.UserAgent);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);

            if (resumeFrom > 0)
            {
                request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            }

            using HttpResponseMessage response =
                await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The file on the server is no longer as long as what we already have.
                // Start again from nothing rather than argue with it.
                TryDeleteQuietly(path);
                return await ToFileAsync(url, path, expectedSize, progress, cancellationToken);
            }

            response.EnsureSuccessStatusCode();

            bool append = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;

            if (resumeFrom > 0 && !append)
            {
                // Asked for a range and got the whole thing: the server does not do ranges.
                // Writing that on top of what we have would splice two copies together, and
                // the digest would then fail for a reason nobody could work out.
                DebugHelper.WriteLine("Components: the server ignored the range request, starting over.");
                resumeFrom = 0;
            }

            long total = expectedSize > 0
                ? expectedSize
                : (response.Content.Headers.ContentLength ?? 0) + resumeFrom;

            FileHelpers.CreateDirectoryFromFilePath(path);

            await using FileStream file = new(path, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            await using Stream network = await response.Content.ReadAsStreamAsync(cancellationToken);

            byte[] buffer = new byte[BufferSize];
            long received = resumeFrom;
            long sinceSample = 0;
            double speed = 0;
            Stopwatch speedTimer = Stopwatch.StartNew();
            Stopwatch reportTimer = Stopwatch.StartNew();

            using CancellationTokenSource stall = new(StallTimeout);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall.Token);

            while (true)
            {
                int read;

                try
                {
                    read = await network.ReadAsync(buffer, linked.Token);
                }
                catch (OperationCanceledException) when (stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    await file.FlushAsync(CancellationToken.None);
                    return new ModlightDownloadResult(ModlightDownloadOutcome.Offline,
                        Localization.Strings.Components_ErrorStalled);
                }

                if (read <= 0)
                {
                    break;
                }

                // Reschedules the watchdog rather than adding to it: every byte that arrives
                // buys another full StallTimeout of patience.
                stall.CancelAfter(StallTimeout);

                await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);

                received += read;
                sinceSample += read;

                if (speedTimer.ElapsedMilliseconds > 500)
                {
                    speed = sinceSample / (speedTimer.Elapsed.TotalSeconds > 0 ? speedTimer.Elapsed.TotalSeconds : 1);
                    sinceSample = 0;
                    speedTimer.Restart();
                }

                if (reportTimer.ElapsedMilliseconds > 150)
                {
                    progress?.Report(new ModlightDownloadProgress(received, total, speed));
                    reportTimer.Restart();
                }
            }

            await file.FlushAsync(CancellationToken.None);
            progress?.Report(new ModlightDownloadProgress(received, total, speed));

            if (expectedSize > 0 && received != expectedSize)
            {
                // Short body. Kept on disk on purpose - the next attempt resumes from here -
                // and it can never be mistaken for a finished download, because nothing is
                // installed without hashing it first.
                return new ModlightDownloadResult(ModlightDownloadOutcome.HttpFailed,
                    Localization.Strings.Components_ErrorIncomplete);
            }

            return new ModlightDownloadResult(ModlightDownloadOutcome.Completed, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ModlightDownloadResult(ModlightDownloadOutcome.Cancelled, string.Empty);
        }
        catch (HttpRequestException exception)
        {
            // StatusCode is null when nothing ever answered - no DNS, no route, no server -
            // which is a different thing to say to a person than "the server said 404".
            ModlightDownloadOutcome outcome = exception.StatusCode == null
                ? ModlightDownloadOutcome.Offline
                : ModlightDownloadOutcome.HttpFailed;

            DebugHelper.WriteException(exception, "Components: download failed.");
            return new ModlightDownloadResult(outcome, exception.Message);
        }
        catch (IOException exception)
        {
            DebugHelper.WriteException(exception, "Components: could not write the download.");
            return new ModlightDownloadResult(ModlightDownloadOutcome.WriteFailed, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            DebugHelper.WriteException(exception, "Components: could not write the download.");
            return new ModlightDownloadResult(ModlightDownloadOutcome.WriteFailed, exception.Message);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: download failed.");
            return new ModlightDownloadResult(ModlightDownloadOutcome.HttpFailed, exception.Message);
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception);
        }
    }
}
