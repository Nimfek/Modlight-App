#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.Tests;

/// <summary>
/// The yt-dlp update channel: what it accepts, what it refuses, and why.
/// </summary>
/// <remarks>
/// <para>
/// The interesting half of this file is the refusals. An updater that installs a new build
/// of the right thing is easy to write and easy to believe in; what has to be proven is
/// that it will not install an artefact from somewhere else, will not install one whose two
/// published digests disagree, will not install one with no digest at all, and will not
/// hand somebody an older build because two version strings sorted the wrong way round.
/// </para>
/// <para>
/// The release document below is the real one, cut down to the assets that matter. Both
/// digests in it are the ones GitHub and the yt-dlp project actually published for
/// 2026.08.19, and they agree - which is the case the whole design turns on.
/// </para>
/// </remarks>
internal static class ModlightToolUpdateTests
{
    private const string Repository = "yt-dlp/yt-dlp";

    private const string Asset = "yt-dlp.exe";

    private const string Checksums = "SHA2-256SUMS";

    private const string ExeDigest = "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a";

    private const string Arm64Digest = "05b438997bafc3affdfda9d041353c9d73e04dc842207254b655b0887c4445b0";

    /// <summary>The real release, trimmed to five of its twenty-four assets.</summary>
    private const string RealRelease =
        "{\"tag_name\":\"2026.08.19\",\"name\":\"yt-dlp 2026.08.19\",\"draft\":false,\"prerelease\":false," +
        "\"published_at\":\"2026-08-19T23:48:43Z\",\"assets\":[" +
        "{\"name\":\"SHA2-256SUMS\",\"size\":1595,\"digest\":\"sha256:a63701f30755cb4d9317950d69703e9d751d490cb1b7059e1bb501a353fe7dcb\"," +
        "\"browser_download_url\":\"https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/SHA2-256SUMS\"}," +
        "{\"name\":\"yt-dlp\",\"size\":3172417,\"digest\":\"sha256:1fa6733c37ea6fb51c99ad8fe785e7b7e5f3246c9b980230329d4fb72ed8d4d6\"," +
        "\"browser_download_url\":\"https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp\"}," +
        "{\"name\":\"yt-dlp.exe\",\"size\":17840399,\"digest\":\"sha256:" + ExeDigest + "\"," +
        "\"browser_download_url\":\"https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe\"}," +
        "{\"name\":\"yt-dlp_arm64.exe\",\"size\":21159286,\"digest\":\"sha256:" + Arm64Digest + "\"," +
        "\"browser_download_url\":\"https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp_arm64.exe\"}," +
        "{\"name\":\"yt-dlp_x86.exe\",\"size\":16500000,\"digest\":\"sha256:a8f91bd41452506bc81ebd2f369b186fea0ee7075413ba00cef9fd346a0a5d0c\"," +
        "\"browser_download_url\":\"https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp_x86.exe\"}]}";

    /// <summary>The real manifest, in the order and spelling sha256sum writes it.</summary>
    private const string RealChecksums =
        "1fa6733c37ea6fb51c99ad8fe785e7b7e5f3246c9b980230329d4fb72ed8d4d6  yt-dlp\n" +
        ExeDigest + "  yt-dlp.exe\n" +
        "072aad4f2a7604e92155f61a275a4752dc64046c8f6d90df3710525d94cd37c1  yt-dlp.tar.gz\n" +
        Arm64Digest + "  yt-dlp_arm64.exe\n" +
        "a8f91bd41452506bc81ebd2f369b186fea0ee7075413ba00cef9fd346a0a5d0c  yt-dlp_x86.exe\n";

    private static int failures;

    internal static int Run(bool live = false)
    {
        failures = 0;

        RealReleaseParses();
        DatedVersionsCompareAsNumbers();
        VersionsThatAreNotDatesAreRefused();
        OnlyStrictlyNewerCounts();
        ChecksumLinesAreMatchedByExactName();
        TwoPublishedDigestsMustAgree();
        ArtefactsMustComeFromGitHub();
        BadReleasesAreRefusedOneReasonAtATime();

        Console.WriteLine("ModlightToolUpdate: the transport, against a stub on 127.0.0.1");
        StubServesEveryOutcome().GetAwaiter().GetResult();

        if (live)
        {
            AskTheRealChannel().GetAwaiter().GetResult();
        }

        Console.WriteLine(failures == 0
            ? "ModlightToolUpdate: all checks passed."
            : "ModlightToolUpdate: " + failures + " check(s) FAILED.");

        return failures;
    }

    /// <summary>The real thing, end to end through the two pure steps.</summary>
    private static void RealReleaseParses()
    {
        Console.WriteLine("ModlightToolUpdate: the real yt-dlp 2026.08.19 release");

        ModlightToolBuild? build = ModlightToolRelease.TryParseRelease(RealRelease, Asset, Checksums,
            out string checksumsUrl, out ModlightToolProblem problem);

        if (build == null)
        {
            Fail("the real release was refused: " + problem);
            return;
        }

        Same("version", "2026.08.19", build.VersionText);
        Same("url", "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe", build.Url);
        Same("size", "17840399", build.Size.ToString());
        Same("checksums asset", "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/SHA2-256SUMS",
            checksumsUrl);

        ModlightToolBuild? verified =
            ModlightToolRelease.WithPublishedDigest(build, RealChecksums, Asset, out problem);

        if (verified == null)
        {
            Fail("the real release lost its digest: " + problem);
            return;
        }

        Same("digest", ExeDigest, verified.Sha256);
        Same("digest source", "release API and published checksums", verified.DigestSource);
        Console.WriteLine("  ok   " + verified.VersionText + ", " + verified.Size + " bytes, digest from " +
            verified.DigestSource);

        // The arm64 build is the same document read for a different asset. Worth a check of
        // its own: picking the neighbouring line out of a manifest that lists nineteen files
        // is exactly the mistake that would produce a permanent "corrupt download".
        ModlightToolBuild? arm = ModlightToolRelease.TryParseRelease(RealRelease, "yt-dlp_arm64.exe", Checksums,
            out _, out problem);

        ModlightToolBuild? armVerified = arm == null
            ? null
            : ModlightToolRelease.WithPublishedDigest(arm, RealChecksums, "yt-dlp_arm64.exe", out problem);

        if (armVerified == null)
        {
            Fail("the arm64 asset was refused: " + problem);
            return;
        }

        Same("arm64 digest", Arm64Digest, armVerified.Sha256);
        Console.WriteLine("  ok   the arm64 asset takes its own line, not the one above it");
    }

    private static void DatedVersionsCompareAsNumbers()
    {
        Console.WriteLine("ModlightToolUpdate: ordering yt-dlp's dated versions");

        // As strings "2026.08.19" sorts before "2026.08.9", and "2026.7.4" before
        // "2026.07.04". Neither is how anybody means them.
        Newer("2026.08.19 is newer than 2026.07.04", "2026.08.19", "2026.07.04");
        Newer("2026.08.9 is newer than 2026.08.19 is NOT true", "2026.08.19", "2026.08.9");
        Newer("a nightly is newer than the stable it came from", "2026.08.19.232658", "2026.08.19");
        NotNewer("and the stable is not newer than the nightly", "2026.08.19", "2026.08.19.232658");
        NotNewer("the same version is not an update", "2026.08.19", "2026.08.19");
        NotNewer("nor is a padded spelling of it", "2026.8.19", "2026.08.19");
        NotNewer("an older release is never offered as one", "2026.07.04", "2026.08.19");
    }

    private static void VersionsThatAreNotDatesAreRefused()
    {
        Console.WriteLine("ModlightToolUpdate: what is not a version");

        foreach (string bad in new[]
        {
            "", "   ", "nightly", "v2026.08.19", "2026.08", "2026.08.19-rc1", "1.2.3", "202.08.19",
            "3026.08.19", "2026.08.19.232658.1.2", "2026.08.nineteen", "2026.08.19 ; rm -rf"
        })
        {
            if (ModlightToolRelease.TryParseVersion(bad, out _))
            {
                Fail("accepted as a version: " + bad);
            }
        }

        True("a plain date parses", ModlightToolRelease.TryParseVersion("2026.08.19", out _));
        True("and a nightly parses", ModlightToolRelease.TryParseVersion("2026.08.19.232658", out _));
        Console.WriteLine("  ok   twelve shapes refused, two accepted");
    }

    private static void OnlyStrictlyNewerCounts()
    {
        Console.WriteLine("ModlightToolUpdate: an unreadable version is never overruled");

        // The one that matters most: if this code cannot tell what is installed, it must not
        // conclude that the newest published build is an upgrade from it.
        NotNewer("published beats nothing? no", "2026.08.19", "");
        NotNewer("nor does published beat a version we cannot read", "2026.08.19", "not a version");
        NotNewer("and nothing published beats what is here", "", "2026.08.19");
    }

    private static void ChecksumLinesAreMatchedByExactName()
    {
        Console.WriteLine("ModlightToolUpdate: reading a sha256sum manifest");

        Same("exact name", ExeDigest, ModlightToolRelease.DigestFromChecksums(RealChecksums, "yt-dlp.exe"));
        Same("the neighbouring line is a different file", Arm64Digest,
            ModlightToolRelease.DigestFromChecksums(RealChecksums, "yt-dlp_arm64.exe"));
        Same("a name that is not listed has no digest", "",
            ModlightToolRelease.DigestFromChecksums(RealChecksums, "yt-dlp_win.zip"));
        Same("and a prefix of a listed name is not a match", "",
            ModlightToolRelease.DigestFromChecksums(RealChecksums, "yt-dlp.e"));

        // sha256sum writes an asterisk for a file it read in binary mode.
        Same("the binary-mode asterisk is not part of the name", ExeDigest,
            ModlightToolRelease.DigestFromChecksums(ExeDigest + " *yt-dlp.exe\n", "yt-dlp.exe"));

        Same("a line whose digest is not a digest is skipped", "",
            ModlightToolRelease.DigestFromChecksums("not-a-digest  yt-dlp.exe\n", "yt-dlp.exe"));

        Same("and CRLF does not become part of the name", ExeDigest,
            ModlightToolRelease.DigestFromChecksums(ExeDigest + "  yt-dlp.exe\r\n", "yt-dlp.exe"));
    }

    private static void TwoPublishedDigestsMustAgree()
    {
        Console.WriteLine("ModlightToolUpdate: the digest rule");

        ModlightToolBuild api = Build(ExeDigest);
        ModlightToolBuild none = Build("");

        ModlightToolBuild? both = ModlightToolRelease.WithPublishedDigest(api, RealChecksums, Asset, out _);
        Same("both publications agree", "release API and published checksums", both?.DigestSource ?? "refused");

        ModlightToolBuild? apiOnly = ModlightToolRelease.WithPublishedDigest(api, "", Asset, out _);
        Same("only the API published one", "release API only", apiOnly?.DigestSource ?? "refused");

        ModlightToolBuild? fileOnly = ModlightToolRelease.WithPublishedDigest(none, RealChecksums, Asset, out _);
        Same("only the project published one", "published checksums only", fileOnly?.DigestSource ?? "refused");

        // The one that must never install: two publishers, two answers.
        string other = "0000000000000000000000000000000000000000000000000000000000000000  yt-dlp.exe\n";
        ModlightToolBuild? conflict =
            ModlightToolRelease.WithPublishedDigest(api, other, Asset, out ModlightToolProblem problem);

        True("a disagreement is refused", conflict == null);
        Same("and says so", "DigestConflict", problem.ToString());

        ModlightToolBuild? nothing = ModlightToolRelease.WithPublishedDigest(none, "", Asset, out problem);
        True("no digest at all is refused", nothing == null);
        Same("and says so", "MissingDigest", problem.ToString());
    }

    private static void ArtefactsMustComeFromGitHub()
    {
        Console.WriteLine("ModlightToolUpdate: where an artefact may come from");

        True("the release host", ModlightToolRelease.IsArtefactUrl(
            "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe"));

        foreach (string bad in new[]
        {
            "http://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe",
            "https://github.com.example.invalid/yt-dlp.exe",
            "https://raw.githubusercontent.com/yt-dlp/yt-dlp.exe",
            "https://evil.invalid/yt-dlp.exe",
            "file:///C:/Windows/System32/cmd.exe",
            "yt-dlp.exe",
            ""
        })
        {
            False("refused: " + (bad.Length == 0 ? "(empty)" : bad), ModlightToolRelease.IsArtefactUrl(bad));
        }
    }

    private static void BadReleasesAreRefusedOneReasonAtATime()
    {
        Console.WriteLine("ModlightToolUpdate: releases we will not act on");

        Refused("empty body", "", ModlightToolProblem.Empty);
        Refused("an HTML error page", "<html>rate limited</html>", ModlightToolProblem.NotJson);
        Refused("a JSON array", "[]", ModlightToolProblem.NotJson);
        Refused("a draft", RealRelease.Replace("\"draft\":false", "\"draft\":true"),
            ModlightToolProblem.Unpublished);
        Refused("a prerelease", RealRelease.Replace("\"prerelease\":false", "\"prerelease\":true"),
            ModlightToolProblem.Unpublished);
        Refused("no tag", RealRelease.Replace("\"tag_name\":\"2026.08.19\"", "\"tag_name\":\"\""),
            ModlightToolProblem.MissingVersion);
        Refused("a tag that is not a version",
            RealRelease.Replace("\"tag_name\":\"2026.08.19\"", "\"tag_name\":\"nightly\""),
            ModlightToolProblem.BadVersion);
        Refused("no assets", "{\"tag_name\":\"2026.08.19\"}", ModlightToolProblem.MissingAsset);
        Refused("our asset is not among them",
            RealRelease.Replace("\"name\":\"yt-dlp.exe\"", "\"name\":\"yt-dlp_linux\""),
            ModlightToolProblem.MissingAsset);
        Refused("the download points somewhere else",
            RealRelease.Replace("https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe",
                "https://evil.invalid/yt-dlp.exe"),
            ModlightToolProblem.BadUrl);
    }

    /// <summary>
    /// The transport, against a server that answers on the loopback interface.
    /// </summary>
    /// <remarks>
    /// The last case is the point of the whole exercise: a release document served by
    /// something that is not GitHub, describing an artefact hosted by something that is not
    /// GitHub, parses perfectly and is still refused. Whoever answers the question "what is
    /// the newest build" does not thereby get to choose which executable this application
    /// downloads.
    /// </remarks>
    private static async Task StubServesEveryOutcome()
    {
        using HttpClient client = new HttpClient();
        using StubApi stub = new StubApi();

        stub.Release = null;
        ModlightToolFetchResult missing = await Fetch(client, stub);
        Same("nothing published there", "Unreachable", missing.Status.ToString());

        stub.Release = "<html>502 Bad Gateway</html>";
        ModlightToolFetchResult garbage = await Fetch(client, stub);
        Same("an error page is not a release", "Malformed", garbage.Status.ToString());

        stub.Release = "{\"tag_name\":\"2026.08.19\",\"assets\":[" + new string(' ', 3 * 1024 * 1024) + "]}";
        ModlightToolFetchResult huge = await Fetch(client, stub);
        Same("a body too large to be a release", "Unreachable", huge.Status.ToString());

        stub.Release = RealRelease.Replace(
            "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe",
            stub.Origin + "/yt-dlp.exe");
        ModlightToolFetchResult redirected = await Fetch(client, stub);
        Same("a perfectly good release pointing at the wrong host", "Malformed", redirected.Status.ToString());
        True("and it says why", redirected.Detail.Contains("BadUrl", StringComparison.Ordinal));

        Console.WriteLine("  ok   four answers a publisher can give, four refusals");

        // Nothing listening at all - the offline case, which must be a status and not an
        // exception, because it is what every launch on a train looks like.
        using StubApi dead = new StubApi();
        string origin = dead.Origin;
        dead.Dispose();

        ModlightToolFetchResult offline = await ModlightToolClient.FetchAsync(client, Repository, Asset,
            Checksums, CancellationToken.None, origin);

        Same("nothing answered", "Unreachable", offline.Status.ToString());
        Console.WriteLine("  ok   a closed port is a status, not an exception");
    }

    /// <summary>Read the live yt-dlp channel and print what the shipped code makes of it.</summary>
    /// <remarks>
    /// Opt-in and asserts nothing. yt-dlp's release schedule is not ours to hold still, so a
    /// new release must not turn into a red build - but the person bumping the pin in the
    /// catalogue wants exactly this output, and it is the same code path the application
    /// uses.
    /// </remarks>
    private static async Task AskTheRealChannel()
    {
        Console.WriteLine("ModlightToolUpdate: live read-only check against github.com");

        using HttpClient client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Modlight-tests/1.0");

        foreach (string asset in new[] { "yt-dlp.exe", "yt-dlp_arm64.exe" })
        {
            ModlightToolFetchResult fetch = await ModlightToolClient.FetchAsync(client, Repository, asset,
                Checksums, CancellationToken.None);

            Console.WriteLine("  " + asset + ": " + fetch.Status +
                (fetch.Detail.Length > 0 ? " (" + fetch.Detail + ")" : ""));

            if (fetch.Build != null)
            {
                Console.WriteLine("    version: " + fetch.Build.VersionText);
                Console.WriteLine("    size:    " + fetch.Build.Size);
                Console.WriteLine("    sha256:  " + fetch.Build.Sha256);
                Console.WriteLine("    digest:  " + fetch.Build.DigestSource);
                Console.WriteLine("    url:     " + fetch.Build.Url);
            }
        }
    }

    private static Task<ModlightToolFetchResult> Fetch(HttpClient client, StubApi stub) =>
        ModlightToolClient.FetchAsync(client, Repository, Asset, Checksums, CancellationToken.None, stub.Origin);

    private static ModlightToolBuild Build(string digest) =>
        new ModlightToolBuild
        {
            VersionText = "2026.08.19",
            Url = "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe",
            Size = 17840399,
            Sha256 = digest,
            DigestSource = digest.Length > 0 ? "release API" : ""
        };

    private static void Newer(string what, string candidate, string installed)
    {
        if (!ModlightToolRelease.IsNewer(candidate, installed))
        {
            Fail(what);
            return;
        }

        Console.WriteLine("  ok   " + what);
    }

    private static void NotNewer(string what, string candidate, string installed)
    {
        if (ModlightToolRelease.IsNewer(candidate, installed))
        {
            Fail(what);
            return;
        }

        Console.WriteLine("  ok   " + what);
    }

    private static void Refused(string what, string json, ModlightToolProblem expected)
    {
        ModlightToolBuild? build = ModlightToolRelease.TryParseRelease(json, Asset, Checksums, out _,
            out ModlightToolProblem problem);

        if (build != null)
        {
            Fail(what + ": was accepted");
            return;
        }

        Same(what, expected.ToString(), problem.ToString());
    }

    private static void True(string what, bool value)
    {
        if (!value)
        {
            Fail(what + ": expected true");
        }
    }

    private static void False(string what, bool value)
    {
        if (value)
        {
            Fail(what + ": expected false");
        }
    }

    private static void Same(string what, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Fail(what + "\n  expected: " + expected + "\n  actual:   " + actual);
        }
    }

    private static void Fail(string message)
    {
        failures++;
        Console.WriteLine("FAIL: " + message);
    }

    /// <summary>
    /// Something that answers release requests on 127.0.0.1 and nowhere else.
    /// </summary>
    /// <remarks>
    /// Raw TCP for the same reason the update suite's stub is: an HttpListener prefix needs
    /// a URL reservation on Windows, which means running the suite as an administrator,
    /// which means the suite quietly stops being run.
    /// </remarks>
    private sealed class StubApi : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private bool disposed;

        /// <summary>The body to answer a release request with. Null answers 404.</summary>
        public string? Release { get; set; }

        public string Origin { get; }

        public StubApi()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stopping.Cancel();

            try
            {
                listener.Stop();
            }
            catch
            {
            }

            stopping.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(stopping.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    await using NetworkStream stream = client.GetStream();

                    await ReadRequestAsync(stream);

                    byte[] body = Encoding.UTF8.GetBytes(Release ?? "{\"message\":\"Not Found\"}");

                    await WriteAsync(stream, Release == null ? 404 : 200, body);
                }
            }
            catch
            {
            }
        }

        private static async Task ReadRequestAsync(Stream stream)
        {
            byte[] buffer = new byte[8192];
            StringBuilder text = new StringBuilder();

            while (text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
            {
                int read = await stream.ReadAsync(buffer);

                if (read <= 0)
                {
                    return;
                }

                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }

        private static async Task WriteAsync(Stream stream, int status, byte[] body)
        {
            string head = "HTTP/1.1 " + status + " x\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
    }
}
