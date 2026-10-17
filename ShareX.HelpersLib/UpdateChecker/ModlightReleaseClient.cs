#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.HelpersLib;

/// <summary>What came back when we asked the server what it has published.</summary>
public enum ModlightFetchStatus
{
    /// <summary>A release manifest we can use.</summary>
    Ok,

    /// <summary>The server answered 404: there is no release. Not an error.</summary>
    NotPublished,

    /// <summary>Nobody answered, or answered with something other than a manifest.</summary>
    Unreachable,

    /// <summary>The server answered, but not with a manifest this build can act on.</summary>
    Malformed
}

/// <summary>The outcome of one manifest request.</summary>
public sealed class ModlightManifestResult
{
    public ModlightFetchStatus Status { get; }

    /// <summary>The release. Non-null exactly when <see cref="Status"/> is <see cref="ModlightFetchStatus.Ok"/>.</summary>
    public ModlightReleaseManifest? Manifest { get; }

    /// <summary>One line for the debug log. Never shown to a user.</summary>
    public string Detail { get; }

    private ModlightManifestResult(ModlightFetchStatus status, ModlightReleaseManifest? manifest, string detail)
    {
        Status = status;
        Manifest = manifest;
        Detail = detail;
    }

    public static ModlightManifestResult Ok(ModlightReleaseManifest manifest) =>
        new ModlightManifestResult(ModlightFetchStatus.Ok, manifest, "");

    public static ModlightManifestResult NotPublished(string detail = "") =>
        new ModlightManifestResult(ModlightFetchStatus.NotPublished, null, detail);

    public static ModlightManifestResult Unreachable(string detail) =>
        new ModlightManifestResult(ModlightFetchStatus.Unreachable, null, detail);

    public static ModlightManifestResult Malformed(string detail) =>
        new ModlightManifestResult(ModlightFetchStatus.Malformed, null, detail);
}

/// <summary>What the app should do about a completed check.</summary>
public enum ModlightUpdateOutcome
{
    /// <summary>A newer release exists. Offer it - never install it without being told to.</summary>
    UpdateAvailable,

    /// <summary>Nothing to do. Same version, older on the server, or nothing published at all.</summary>
    UpToDate,

    /// <summary>The check did not complete. Silent at startup; the About window may say so.</summary>
    CheckFailed
}

/// <summary>
/// Asking a Modlight server what it has published. No UI, no files, no HelpersLib.
/// </summary>
/// <remarks>
/// The <see cref="HttpClient"/> is a parameter rather than something this reaches for,
/// which is what lets the tests point the real fetch-and-judge path at a stub on
/// 127.0.0.1 instead of at production.
/// </remarks>
public static class ModlightReleaseClient
{
    /// <summary>The manifest endpoint. Public by design on the server side: an updater that
    /// needs a credential stops working exactly when the credential expires.</summary>
    public const string ManifestPath = "/api/releases/app";

    /// <summary>Long enough for a cold server, short enough that a launch never waits on it.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The real manifest is a few hundred bytes. Anything past this is not one.</summary>
    private const int MaxManifestBytes = 64 * 1024;

    public static string ManifestUrl(string host) => (host ?? "").TrimEnd('/') + ManifestPath;

    /// <summary>
    /// Ask the server for its current release.
    /// </summary>
    /// <remarks>
    /// Never throws. This runs on every launch, including launches with no network, a
    /// captive portal, a proxy that answers every request with a login page, or a server
    /// mid-deploy - and a person who is offline must not meet an error because a background
    /// timer fired. Everything that can go wrong comes back as a status.
    /// </remarks>
    public static async Task<ModlightManifestResult> FetchAsync(HttpClient client, string host,
        CancellationToken cancellationToken)
    {
        string url = ManifestUrl(host);

        if (!ModlightRelease.IsAcceptableUrl(url))
        {
            return ModlightManifestResult.Unreachable("release host is not usable: " + host);
        }

        try
        {
            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(RequestTimeout);

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using HttpResponseMessage response =
                await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The documented shape of "nothing is published yet": a 404 with a JSON body.
                return ModlightManifestResult.NotPublished("HTTP 404");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ModlightManifestResult.Unreachable("HTTP " + (int)response.StatusCode);
            }

            string body = await ReadBodyAsync(response, deadline.Token);

            if (body.Length == 0)
            {
                return ModlightManifestResult.Malformed("empty or oversized body");
            }

            ModlightReleaseManifest? manifest = ModlightRelease.TryParse(body, out ModlightManifestProblem problem);

            if (manifest == null)
            {
                return ModlightManifestResult.Malformed("manifest rejected: " + problem);
            }

            return ModlightManifestResult.Ok(manifest);
        }
        catch (Exception exception)
        {
            return ModlightManifestResult.Unreachable(exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Turn a completed check into the one of three things the app can do about it.
    /// </summary>
    /// <remarks>
    /// "Nothing published" is deliberately <see cref="ModlightUpdateOutcome.UpToDate"/> and
    /// not a failure. Nothing failed: the channel answered, and what it said was that the
    /// newest release in existence is the one already installed. Reporting a failure there
    /// would put a red line in the About window of a perfectly healthy install.
    /// </remarks>
    public static ModlightUpdateOutcome Evaluate(Version? installed, ModlightManifestResult? fetch, out string reason)
    {
        if (fetch == null)
        {
            reason = "no result";
            return ModlightUpdateOutcome.CheckFailed;
        }

        switch (fetch.Status)
        {
            case ModlightFetchStatus.NotPublished:
                reason = "no release published";
                return ModlightUpdateOutcome.UpToDate;

            case ModlightFetchStatus.Unreachable:
            case ModlightFetchStatus.Malformed:
                reason = fetch.Detail;
                return ModlightUpdateOutcome.CheckFailed;
        }

        if (installed == null)
        {
            reason = "installed version unknown";
            return ModlightUpdateOutcome.CheckFailed;
        }

        ModlightUpdateVerdict verdict = ModlightRelease.Decide(installed, fetch.Manifest);

        switch (verdict)
        {
            case ModlightUpdateVerdict.UpdateAvailable:
                reason = "newer release available";
                return ModlightUpdateOutcome.UpdateAvailable;

            case ModlightUpdateVerdict.ServerIsOlder:
                reason = "server release is older than this build";
                return ModlightUpdateOutcome.UpToDate;

            case ModlightUpdateVerdict.UpToDate:
                reason = "already the published version";
                return ModlightUpdateOutcome.UpToDate;

            default:
                reason = "manifest unreadable";
                return ModlightUpdateOutcome.CheckFailed;
        }
    }

    /// <summary>
    /// Read the body, refusing to hold more of it than a manifest could ever be.
    /// </summary>
    /// <returns>The text, or an empty string when there was too much of it.</returns>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new MemoryStream();

        byte[] chunk = new byte[8192];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes)
            {
                return "";
            }

            buffer.Write(chunk, 0, read);
        }

        // TrimStart on the BOM, not on whitespace: a byte order mark survives decoding as
        // U+FEFF and the JSON reader rejects it as a stray character, which would turn a
        // perfectly good manifest into "malformed".
        return new UTF8Encoding(false).GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }
}
