#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX;

/// <summary>
/// The update checker for this fork: the Modlight release channel, not GitHub.
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose. Everything that can be got wrong - reading the manifest, comparing the
/// versions, deciding what to do - is in <see cref="ModlightRelease"/> and
/// <see cref="ModlightReleaseClient"/>, which touch nothing but System and therefore run
/// under ShareX.Tests against a stub server. What is left here is copying fields onto the
/// base class and never throwing.
/// </para>
/// <para>
/// The digest travels with those fields. <see cref="UpdateChecker.ExpectedSha256"/> is what
/// <see cref="DownloaderWindow"/> checks the finished download against, and a manifest that
/// did not carry one never parses in the first place - so there is no path from here to a
/// running installer that skipped verification.
/// </para>
/// </remarks>
internal sealed class ModlightUpdateChecker : UpdateChecker
{
    /// <summary>
    /// Where this build looks for its own updates.
    /// </summary>
    /// <remarks>
    /// A constant, and deliberately NOT the server the user signed in to. The two look alike
    /// - almost everyone signs in to the same host the binary came from - but they answer
    /// different questions: the sign-in host is "where do my files live", and this one is
    /// "who is allowed to hand me an executable and have it run with elevation". Taking the
    /// second from a text box would mean that pointing the app at somebody else's Modlight
    /// instance, for perfectly ordinary reasons, also hands that instance the ability to
    /// install software here on the next launch. The SHA-256 does not help against that: the
    /// same server publishes the digest and the bytes, so it can make them agree.
    /// </remarks>
    public const string ReleaseHost = ModlightAccount.DefaultHost;

    /// <summary>The page a person is sent to when they would rather fetch it themselves.</summary>
    public static string DownloadPageUrl => ReleaseHost + "/download";

    private readonly string host;

    public ModlightUpdateChecker(string releaseHost = ReleaseHost)
    {
        host = string.IsNullOrWhiteSpace(releaseHost) ? ReleaseHost : releaseHost;
        CurrentVersion = InstalledVersion();
        ChangelogURL = DownloadPageUrl;
        PortableDownloadURL = DownloadPageUrl;
    }

    /// <summary>
    /// Ask the channel, and set <see cref="UpdateChecker.Status"/> accordingly.
    /// </summary>
    /// <remarks>
    /// Called from a timer that fires at <see cref="TimeSpan.Zero"/> on every launch, so the
    /// contract is that it returns quietly whatever happened. No release published, no
    /// network, a proxy answering with a login page, a truncated manifest: all of them end
    /// as a status and a line in the debug log, and none of them as a dialog.
    /// </remarks>
    public override async Task CheckUpdateAsync()
    {
        try
        {
            ModlightManifestResult fetch = await ModlightReleaseClient.FetchAsync(
                HttpClientFactory.Create(), host, CancellationToken.None);

            ModlightUpdateOutcome outcome = ModlightReleaseClient.Evaluate(CurrentVersion, fetch, out string reason);

            DebugHelper.WriteLine("Modlight update check: {0} ({1})", outcome, reason);

            switch (outcome)
            {
                case ModlightUpdateOutcome.UpdateAvailable when fetch.Manifest != null:
                    Adopt(fetch.Manifest);
                    Status = UpdateStatus.UpdateAvailable;
                    break;

                case ModlightUpdateOutcome.UpToDate:
                    Status = UpdateStatus.UpToDate;
                    break;

                default:
                    Status = UpdateStatus.UpdateCheckFailed;
                    break;
            }
        }
        catch (Exception exception)
        {
            // FetchAsync already swallows everything it can reach; this is the belt to that
            // pair of braces, because the one thing this method must not do is throw out of
            // a background timer callback on somebody's laptop.
            DebugHelper.WriteException(exception, "Modlight update check failed.");
            Status = UpdateStatus.UpdateCheckFailed;
        }
    }

    private void Adopt(ModlightReleaseManifest manifest)
    {
        LatestVersion = manifest.Version;
        LatestVersionText = manifest.VersionText;
        DownloadURL = manifest.Url;
        FileName = manifest.FileName;
        ExpectedSha256 = manifest.Sha256;
        ReleaseNotes = manifest.Notes;
        ReleaseSize = manifest.Size;
    }

    /// <summary>
    /// What version this build is.
    /// </summary>
    /// <remarks>
    /// Null when it cannot be worked out, which <see cref="ModlightReleaseClient.Evaluate"/>
    /// turns into a failed check rather than an update. The alternative - defaulting to
    /// something like 0.0 - would make every release on the server look newer, which is the
    /// exact shape of the bug this whole change exists to remove.
    /// </remarks>
    private static Version? InstalledVersion() =>
        ModlightRelease.TryParseVersion(Application.ProductVersion, out Version version) ? version : null;
}
