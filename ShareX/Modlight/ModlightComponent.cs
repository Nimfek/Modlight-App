#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ShareX;

/// <summary>What has to happen to the downloaded file before it is a working component.</summary>
public enum ModlightComponentPackaging
{
    /// <summary>The download IS the component. Move it into place.</summary>
    File,

    /// <summary>A zip holding one file we want, wherever it sits inside.</summary>
    ZipSingleFile,

    /// <summary>A zip whose whole contents belong in the folder, directories and all.</summary>
    ZipTree
}

/// <summary>Which of the two folders a component belongs in.</summary>
public enum ModlightComponentFolder
{
    Tools,
    Models
}

/// <summary>
/// One external component: what it is for, where it comes from, and what its bytes must hash to.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a constant compiled into Modlight, and that is the point of the type.
/// The digest is not fetched at run time next to the file it is meant to vouch for - a
/// checksum served by whoever served the download only proves the two agree - it ships with
/// the application, so a swapped or truncated artefact fails against a value that was pinned
/// when a human looked at it. The same rule the updater follows for the installer.
/// </para>
/// <para>
/// The cost of pinning is that a version bump is a code change. That is deliberate for
/// ffmpeg, ExifTool and the model, which move rarely. It is not workable for yt-dlp, which
/// needs a fresh build every few weeks to keep working at all - so that one component also
/// carries <see cref="ModlightComponent.UpdateRepository"/> and follows its publisher
/// between Modlight releases, under a digest rule of its own written up in
/// ModlightToolRelease.
/// </para>
/// </remarks>
public sealed class ModlightComponent
{
    public required string Id { get; init; }

    /// <summary>Read at call time, not stored: the language can change while the app is up.</summary>
    public required Func<string> Name { get; init; }

    /// <summary>One line saying which feature dies without it.</summary>
    public required Func<string> Purpose { get; init; }

    /// <summary>HTTPS only. Enforced again before the request is made.</summary>
    public required string Url { get; init; }

    /// <summary>Lowercase hex SHA-256 of the file at <see cref="Url"/>.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Bytes of the download, so "this costs 65 MB" can be said before anybody commits.</summary>
    public required long DownloadSize { get; init; }

    /// <summary>The file whose presence means "installed".</summary>
    public required string FileName { get; init; }

    public required ModlightComponentFolder Folder { get; init; }

    public required ModlightComponentPackaging Packaging { get; init; }

    /// <summary>Ceiling for a zip's uncompressed size. Zero for a plain file.</summary>
    public long MaxUncompressedSize { get; init; }

    /// <summary>Where the artefact and its digest were published. Shown in the UI.</summary>
    public required string Source { get; init; }

    /// <summary>Version string, for the log and the settings page.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Everything this component owns in its folder, for removal.
    /// </summary>
    /// <remarks>
    /// ExifTool is why this is a list rather than just <see cref="FileName"/>: the executable
    /// is 58 KB of stub and the 33 MB of Perl beside it in exiftool_files is what makes it
    /// run. Removing the component and leaving that behind would free nothing and leave a
    /// directory nobody can explain.
    /// </remarks>
    public IReadOnlyList<string> OwnedEntries { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The GitHub repository whose latest release this component follows, or null when the
    /// pin above is the whole story.
    /// </summary>
    /// <remarks>
    /// Set for yt-dlp and for nothing else, on purpose. A component that tracks a moving
    /// target is a component whose bytes no human looked at before they ran here, and that
    /// is a trade worth making exactly where the alternative is worse: yt-dlp goes stale in
    /// weeks and fails silently when it does. ffmpeg, ExifTool and a model file do not, so
    /// they keep the stronger guarantee.
    /// </remarks>
    public string? UpdateRepository { get; init; }

    /// <summary>The release asset for this architecture, matched by exact name.</summary>
    public string? UpdateAsset { get; init; }

    /// <summary>The publisher's checksum manifest in the same release, or null when there is none.</summary>
    public string? UpdateChecksums { get; init; }

    /// <summary>
    /// What to run the executable with to make it print its own version. Empty when there
    /// is nothing to ask.
    /// </summary>
    /// <remarks>
    /// Asking the binary is the only honest answer once updates exist: after the first one
    /// the constant above is no longer what is on the disk, and a version remembered in the
    /// settings file is a version that lies the moment somebody drops their own build into
    /// the folder.
    /// </remarks>
    public IReadOnlyList<string> VersionArguments { get; init; } = Array.Empty<string>();

    /// <summary>True when this component has somewhere to look for a newer build.</summary>
    public bool TracksUpdates =>
        !string.IsNullOrEmpty(UpdateRepository) && !string.IsNullOrEmpty(UpdateAsset);
}

/// <summary>
/// Every external component, and the URLs and digests behind them.
/// </summary>
/// <remarks>
/// <para>
/// PROVENANCE - each digest below was taken from the publisher AND recomputed from the real
/// download before being pinned here:
/// </para>
/// <list type="bullet">
/// <item>ffmpeg: GitHub's release-asset digest field on api.github.com/repos/ShareX/FFmpeg,
/// which is served by a different host from the bytes themselves.</item>
/// <item>yt-dlp: the project's own SHA2-256SUMS asset, published in the same release, and
/// GitHub's asset digest field. Two publications that agree - and the pair the automatic
/// update path demands as well, since it has no human-checked pin to fall back on.</item>
/// <item>background removal model: rembg pins it in rembg/sessions/bria_rmbg.py on its main
/// branch - a file in the source tree, not an artefact served with the download.</item>
/// <item>ExifTool: NOTHING is published. The ShareX/ExifTool release predates GitHub's digest
/// field and ships no checksum file, so the value below is our own hash of the artefact. It
/// still does what a digest does for every download after this one - pins the bytes to a
/// build somebody looked at - but nobody except us has published it, and this note exists so
/// that is not mistaken for third-party corroboration.</item>
/// </list>
/// </remarks>
public static class ModlightComponentCatalog
{
    public const string FFmpeg = "ffmpeg";
    public const string YtDlp = "yt-dlp";
    public const string BackgroundModel = "background-model";
    public const string ExifTool = "exiftool";

    private static readonly bool IsArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    private static ModlightComponent[]? all;

    /// <summary>Every component, in the order they are offered.</summary>
    public static IReadOnlyList<ModlightComponent> All => all ??= Build();

    public static ModlightComponent? Find(string id)
    {
        foreach (ModlightComponent component in All)
        {
            if (string.Equals(component.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return component;
            }
        }

        return null;
    }

    private static ModlightComponent[] Build()
    {
        return
        [
            // ShareX's own ffmpeg build, which is what the recorder was written against. The
            // zip holds exactly one entry, ffmpeg.exe, 193 MB unpacked - worth knowing,
            // because the inherited FFmpegGitHubDownloader capped extraction at 200 000 000
            // bytes, which this passed under until the 8.x builds grew past it.
            new ModlightComponent
            {
                Id = FFmpeg,
                Name = () => Strings.Components_FFmpeg,
                Purpose = () => Strings.Components_FFmpegPurpose,
                Version = "8.1",
                Url = IsArm64
                    ? "https://github.com/ShareX/FFmpeg/releases/download/v8.1/ffmpeg-8.1-win-arm64.zip"
                    : "https://github.com/ShareX/FFmpeg/releases/download/v8.1/ffmpeg-8.1-win-x64.zip",
                Sha256 = IsArm64
                    ? "7ac6c5dbbe61037bcce97482549294238d87400d8b7b1cb559949293c3b06dd5"
                    : "1838932e32f01e0e7b65fde5ca0ebb2ab131cb6a9ff1a84025ee5b784075576c",
                DownloadSize = IsArm64 ? 50657497L : 68599421L,
                FileName = "ffmpeg.exe",
                Folder = ModlightComponentFolder.Tools,
                Packaging = ModlightComponentPackaging.ZipSingleFile,
                MaxUncompressedSize = 512L * 1024 * 1024,
                Source = "github.com/ShareX/FFmpeg",
                OwnedEntries = ["ffmpeg.exe"]
            },

            // The pin here is the FLOOR, not the version anybody ends up running. yt-dlp
            // ships every few weeks because the sites it reads keep changing under it, and
            // a build two months old already fails on TikTok outright and quietly drops a
            // signed-in YouTube download to 360p - both measured on the owner's machine
            // against the build that used to be pinned here. So this component, alone among
            // the four, follows its publisher: see ModlightComponentUpdates for the schedule
            // and for what has to be true about a build before it replaces this one.
            //
            // The constants still matter and are still worth bumping with each Modlight
            // release: they are what a first install fetches, what a machine with no
            // internet keeps, and the only digest here a human has actually looked at.
            new ModlightComponent
            {
                Id = YtDlp,
                Name = () => Strings.Components_YtDlp,
                Purpose = () => Strings.Components_YtDlpPurpose,
                Version = "2026.08.19",
                Url = IsArm64
                    ? "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp_arm64.exe"
                    : "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe",
                Sha256 = IsArm64
                    ? "05b438997bafc3affdfda9d041353c9d73e04dc842207254b655b0887c4445b0"
                    : "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a",
                DownloadSize = IsArm64 ? 21159286L : 17840399L,
                FileName = "yt-dlp.exe",
                Folder = ModlightComponentFolder.Tools,
                Packaging = ModlightComponentPackaging.File,
                Source = "github.com/yt-dlp/yt-dlp",
                OwnedEntries = ["yt-dlp.exe"],
                UpdateRepository = "yt-dlp/yt-dlp",
                UpdateAsset = IsArm64 ? "yt-dlp_arm64.exe" : "yt-dlp.exe",
                UpdateChecksums = "SHA2-256SUMS",

                // --ignore-config first, because a yt-dlp.conf on this machine is read
                // before anything else and one line of it - a --print, a --simulate, a
                // --quiet - would turn the answer into something else or into nothing.
                VersionArguments = ["--ignore-config", "--version"]
            },

            // The model ShareX's own documentation recommends, and the one the owner named.
            // Also, by a distance, the largest thing here: it is offered with its size in
            // front of it, because a gigabyte is a decision rather than a detail.
            new ModlightComponent
            {
                Id = BackgroundModel,
                Name = () => Strings.Components_BackgroundModel,
                Purpose = () => Strings.Components_BackgroundModelPurpose,
                Version = "bria-rmbg-2.0",
                Url = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/bria-rmbg-2.0.onnx",
                Sha256 = "5b486f08200f513f460da46dd701db5fbb47d79b4be4b708a19444bcd4e79958",
                DownloadSize = 1024331469L,
                FileName = "bria-rmbg-2.0.onnx",
                Folder = ModlightComponentFolder.Models,
                Packaging = ModlightComponentPackaging.File,
                Source = "github.com/danielgatis/rembg",
                OwnedEntries = ["bria-rmbg-2.0.onnx"]
            },

            // 621 entries, of which one is an executable and the rest are the Perl it runs.
            // ZipTree rather than ZipSingleFile for exactly that reason.
            new ModlightComponent
            {
                Id = ExifTool,
                Name = () => Strings.Components_ExifTool,
                Purpose = () => Strings.Components_ExifToolPurpose,
                Version = "13.29",
                Url = "https://github.com/ShareX/ExifTool/releases/download/v13.29/exiftool-13.29-win64.zip",
                Sha256 = "ae8e1fc95266aa5f2769e16cd940f88bf80fa408484ab10a8808895992e1cfb6",
                DownloadSize = 10146032L,
                FileName = "exiftool.exe",
                Folder = ModlightComponentFolder.Tools,
                Packaging = ModlightComponentPackaging.ZipTree,
                MaxUncompressedSize = 256L * 1024 * 1024,
                Source = "github.com/ShareX/ExifTool",
                OwnedEntries = ["exiftool.exe", "exiftool_files"]
            }
        ];
    }
}
