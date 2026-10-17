#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.Tests;

/// <summary>
/// The update path, from "what did the server say" to "may this executable run".
/// </summary>
/// <remarks>
/// <para>
/// This exists because the failure modes here are not cosmetic. An updater that compares
/// versions as text stops offering updates at .10 and nobody notices for a year. An updater
/// that reads "different" as "newer" installs an older build over a newer one. An updater
/// that runs an installer without checking its digest is an arbitrary-code-execution path
/// ending at an elevation prompt users have been trained to accept. And an updater that
/// throws when the network is down greets an offline person with an error on every launch.
/// </para>
/// <para>
/// So the checks below run the real code: the shipped parser, the shipped comparison, the
/// shipped fetch, the shipped hash-and-delete. The only stand-in is the server, which is a
/// few dozen lines of TcpListener answering on 127.0.0.1 - raw sockets rather than
/// HttpListener, which needs a URL reservation and therefore an administrator, and a real
/// socket rather than a stubbed HttpClient, because "the JSON came off a wire" is part of
/// what is being tested.
/// </para>
/// <para>
/// Nothing here touches host.example.com. No release is published in order to test a release
/// channel.
/// </para>
/// <para>Run it with <c>dotnet run --project ShareX.Tests</c>. Exit code 0 is a pass.</para>
/// </remarks>
internal static class ModlightUpdateTests
{
    private static int failures;

    /// <summary>The real manifest, as production answered it on the day this was written.</summary>
    private const string RealManifest =
        "{\"name\":\"Modlight App\",\"version\":\"0.7.0\",\"url\":\"https://h.example.com/saluy6/raw.exe\"," +
        "\"filename\":\"Modlight-0.7.0-setup-x64.exe\",\"size\":82317446," +
        "\"sha256\":\"773b9f112c36a8e9898ab31d553b009b514906fb9b8cbf360b140735b2f5a13b\"," +
        "\"notes\":\"First public build.\",\"publishedAt\":\"2026-08-18T08:49:26.707Z\"}";

    /// <param name="liveHost">
    /// Optionally one origin, e.g. https://host.example.com. Given it, the suite also asks the
    /// REAL channel what it has published and prints the answer - a plain read-only GET on
    /// the public manifest endpoint, nothing written and nothing published. Off by default,
    /// because a test suite that needs the internet is a test suite that fails on a train.
    /// </param>
    internal static int Run(string? liveHost = null)
    {
        failures = 0;

        RealManifestParses();
        VersionsCompareAsNumbersNotText();
        VersionStringsAreReadLeniently();
        NonsenseVersionsAreRefused();
        DigestsAreRecognisedAndCaseDoesNotMatter();
        DownloadUrlsMustBeHttpsOrLoopback();
        InstallerFileNamesCannotEscapeTheTempFolder();
        MalformedManifestsAreRefusedOneReasonAtATime();

        Console.WriteLine("ModlightUpdate: the release channel, against a stub on 127.0.0.1");
        LiveStubServesEveryOutcome().GetAwaiter().GetResult();

        Console.WriteLine("ModlightUpdate: the checksum gate, against real files on disk");
        DigestVerificationPassesAndFailsOnRealFiles().GetAwaiter().GetResult();

        if (!string.IsNullOrEmpty(liveHost))
        {
            AskTheRealChannel(liveHost!).GetAwaiter().GetResult();
        }

        Console.WriteLine(failures == 0
            ? "ModlightUpdate: all checks passed."
            : "ModlightUpdate: " + failures + " check(s) FAILED.");

        return failures;
    }

    /// <summary>
    /// Read the live manifest and print what the shipped code makes of it.
    /// </summary>
    /// <remarks>
    /// Deliberately only <c>/api/releases/app</c>. Not <c>/download/app</c>, which redirects
    /// to the file itself and is a download of somebody&apos;s upload with counters attached -
    /// a checker has no business touching that until a person has said yes.
    ///
    /// Nothing here asserts. The live channel is not ours to hold still, so a change on the
    /// server must not turn into a red build; this prints, and a human reads it.
    /// </remarks>
    private static async Task AskTheRealChannel(string host)
    {
        Console.WriteLine("ModlightUpdate: live read-only check against " + host);

        using HttpClient client = new HttpClient();

        ModlightManifestResult fetch = await ModlightReleaseClient.FetchAsync(client, host, CancellationToken.None);

        Console.WriteLine("  " + ModlightReleaseClient.ManifestUrl(host));
        Console.WriteLine("  status: " + fetch.Status + (fetch.Detail.Length > 0 ? " (" + fetch.Detail + ")" : ""));

        if (fetch.Manifest != null)
        {
            Console.WriteLine("  version:  " + fetch.Manifest.VersionText + "  (parsed " + fetch.Manifest.Version + ")");
            Console.WriteLine("  file:     " + fetch.Manifest.FileName + "  " + fetch.Manifest.Size + " bytes");
            Console.WriteLine("  url:      " + fetch.Manifest.Url);
            Console.WriteLine("  sha256:   " + fetch.Manifest.Sha256);
            Console.WriteLine("  notes:    " + fetch.Manifest.Notes);
        }

        foreach (string installed in new[] { "0.6.0", "0.7.0", "0.8.0" })
        {
            ModlightRelease.TryParseVersion(installed, out Version version);

            ModlightUpdateOutcome outcome = ModlightReleaseClient.Evaluate(version, fetch, out string reason);

            Console.WriteLine("  as version " + installed + " -> " + outcome + " (" + reason + ")");
        }
    }

    // ------------------------------------------------------------------ manifest parsing

    private static void RealManifestParses()
    {
        ModlightReleaseManifest? manifest = ModlightRelease.TryParse(RealManifest, out ModlightManifestProblem problem);

        if (manifest == null)
        {
            Fail("the real production manifest did not parse: " + problem);
            return;
        }

        Same("name", "Modlight App", manifest.Name);
        Same("version text", "0.7.0", manifest.VersionText);
        Same("version", "0.7.0", manifest.Version.ToString());
        Same("url", "https://h.example.com/saluy6/raw.exe", manifest.Url);
        Same("file name", "Modlight-0.7.0-setup-x64.exe", manifest.FileName);
        Same("size", "82317446", manifest.Size.ToString());
        Same("sha256", "773b9f112c36a8e9898ab31d553b009b514906fb9b8cbf360b140735b2f5a13b", manifest.Sha256);
        Same("notes", "First public build.", manifest.Notes);
        True("publishedAt parsed", manifest.PublishedAt != null);
    }

    /// <summary>
    /// The check this whole task hangs on: 0.7.10 is newer than 0.7.9, and text says otherwise.
    /// </summary>
    private static void VersionsCompareAsNumbersNotText()
    {
        // Guard the premise as well as the code. If an ordinal string comparison ever agreed
        // with the numeric answer here, the rest of this test would pass for the wrong reason.
        if (string.CompareOrdinal("0.7.10", "0.7.9") >= 0)
        {
            Fail("the premise changed: text comparison now says 0.7.10 >= 0.7.9");
        }

        Verdict("0.7.9 installed, 0.7.10 published", "0.7.9", "0.7.10", ModlightUpdateVerdict.UpdateAvailable);
        Verdict("0.7.10 installed, 0.7.9 published", "0.7.10", "0.7.9", ModlightUpdateVerdict.ServerIsOlder);
        Verdict("same version", "0.7.0", "0.7.0", ModlightUpdateVerdict.UpToDate);
        Verdict("0.7 and 0.7.0 are one version", "0.7", "0.7.0", ModlightUpdateVerdict.UpToDate);
        Verdict("0.7.0 and 0.7.0.0 are one version", "0.7.0", "0.7.0.0", ModlightUpdateVerdict.UpToDate);
        Verdict("major bump", "0.9.9", "1.0.0", ModlightUpdateVerdict.UpdateAvailable);
        Verdict("a major downgrade is refused", "21.0.0", "0.7.0", ModlightUpdateVerdict.ServerIsOlder);
        Verdict("revision only", "0.7.0.1", "0.7.0.2", ModlightUpdateVerdict.UpdateAvailable);
        Verdict("two digits beat one", "0.9.0", "0.10.0", ModlightUpdateVerdict.UpdateAvailable);
        Verdict("no manifest is not an update", "0.7.0", null, ModlightUpdateVerdict.Unreadable);
    }

    private static void VersionStringsAreReadLeniently()
    {
        ParsesAs("plain", "0.7.0", "0.7.0");
        ParsesAs("tag style", "v0.8.1", "0.8.1");
        ParsesAs("capital tag", "V0.8.1", "0.8.1");
        ParsesAs("pre-release suffix", "0.8.0-beta.2", "0.8.0");
        ParsesAs("build metadata", "0.8.0+abc123", "0.8.0");
        ParsesAs("surrounding space", "  0.8.0  ", "0.8.0");
        ParsesAs("four parts", "1.2.3.4", "1.2.3.4");
    }

    private static void NonsenseVersionsAreRefused()
    {
        Refuses("empty", "");
        Refuses("whitespace", "   ");
        Refuses("null", null);
        Refuses("a word", "latest");
        Refuses("single component", "7");
        Refuses("hex", "0x7.0");
        Refuses("negative", "-1.0");
        Refuses("five components", "1.2.3.4.5");
        Refuses("letters mixed in", "0.7.0a");
        Refuses("only a v", "v");
    }

    // ------------------------------------------------------------------ digests

    private static void DigestsAreRecognisedAndCaseDoesNotMatter()
    {
        string lower = Sixty4('a');
        string upper = Sixty4('A');

        True("64 lowercase hex is a digest", ModlightRelease.IsDigest(lower));
        True("64 uppercase hex is a digest", ModlightRelease.IsDigest(upper));
        False("63 characters is not", ModlightRelease.IsDigest(new string('a', 63)));
        False("65 characters is not", ModlightRelease.IsDigest(new string('a', 65)));
        False("non-hex is not", ModlightRelease.IsDigest(Sixty4('z')));
        False("empty is not", ModlightRelease.IsDigest(""));
        False("null is not", ModlightRelease.IsDigest(null));

        // The exact shape of the bug that bit the panel duplicate check: two spellings of one
        // value, compared as text, silently disagreeing.
        True("case does not change a digest", ModlightUpdateFile.DigestsMatch(lower, upper));
        False("empty never matches", ModlightUpdateFile.DigestsMatch("", ""));
        False("null never matches", ModlightUpdateFile.DigestsMatch(lower, null));
        False("different digests do not match", ModlightUpdateFile.DigestsMatch(lower, Sixty4('b')));
    }

    // ------------------------------------------------------------------ url and file name

    private static void DownloadUrlsMustBeHttpsOrLoopback()
    {
        True("https", ModlightRelease.IsAcceptableUrl("https://h.example.com/saluy6/raw.exe"));
        True("http on 127.0.0.1", ModlightRelease.IsAcceptableUrl("http://127.0.0.1:5000/app.exe"));
        True("http on localhost", ModlightRelease.IsAcceptableUrl("http://localhost:5000/app.exe"));

        // Plaintext off-machine is the case that matters: whoever can rewrite the bytes on the
        // wire can rewrite the digest that is supposed to catch them.
        False("http elsewhere", ModlightRelease.IsAcceptableUrl("http://h.example.com/saluy6/raw.exe"));
        False("ftp", ModlightRelease.IsAcceptableUrl("ftp://h.example.com/raw.exe"));
        False("file", ModlightRelease.IsAcceptableUrl("file:///C:/Windows/System32/cmd.exe"));
        False("relative", ModlightRelease.IsAcceptableUrl("/saluy6/raw.exe"));
        False("empty", ModlightRelease.IsAcceptableUrl(""));
        False("null", ModlightRelease.IsAcceptableUrl(null));
    }

    private static void InstallerFileNamesCannotEscapeTheTempFolder()
    {
        // The name goes through Path.Combine and the result gets executed, so a separator in
        // it is not a cosmetic problem.
        NoSeparators(@"..\..\Startup\evil.exe");
        NoSeparators("../../etc/passwd");
        NoSeparators(@"C:\Windows\System32\cmd.exe");

        Same("backslashes gone", "Startupevil.exe",
            ModlightRelease.SafeInstallerFileName(@"..\..\Startup\evil.exe", "https://x/y.exe"));
        Same("forward slashes gone", "etcpasswd.exe",
            ModlightRelease.SafeInstallerFileName("../../etc/passwd", "https://x/y.exe"));
        Same("drive letters gone", "CWindowsSystem32cmd.exe",
            ModlightRelease.SafeInstallerFileName(@"C:\Windows\System32\cmd.exe", "https://x/y.exe"));
        Same("leading dots gone", "gitconfig.exe",
            ModlightRelease.SafeInstallerFileName(".gitconfig", "https://x/y.exe"));
        Same("a normal name is kept", "Modlight-0.7.0-setup-x64.exe",
            ModlightRelease.SafeInstallerFileName("Modlight-0.7.0-setup-x64.exe", "https://x/y.exe"));
        Same("extension forced", "installer.exe",
            ModlightRelease.SafeInstallerFileName("installer", "https://x/y.exe"));
        Same("something that is not an exe does not pretend to be one", "setup.msi.exe",
            ModlightRelease.SafeInstallerFileName("setup.msi", "https://x/y.exe"));
        Same("falls back to the url", "raw.exe",
            ModlightRelease.SafeInstallerFileName("", "https://h.example.com/saluy6/raw.exe"));
        Same("falls back again when the url has nothing either", "Modlight-setup.exe",
            ModlightRelease.SafeInstallerFileName("", "https://h.example.com/"));
        Same("absurdly long names are replaced", "Modlight-setup.exe",
            ModlightRelease.SafeInstallerFileName(new string('a', 200) + ".exe", "https://x/y.exe"));
    }

    private static void NoSeparators(string input)
    {
        string actual = ModlightRelease.SafeInstallerFileName(input, "https://x/y.exe");

        if (actual.Contains('\\') || actual.Contains('/') || actual.Contains(':') ||
            Path.GetFileName(actual) != actual)
        {
            Fail("file name is not a plain file name: " + input + " -> " + actual);
        }
    }

    // ------------------------------------------------------------------ malformed input

    private static void MalformedManifestsAreRefusedOneReasonAtATime()
    {
        Rejects("empty body", "", ModlightManifestProblem.Empty);
        Rejects("whitespace body", "   \r\n ", ModlightManifestProblem.Empty);
        Rejects("an html error page", "<html><body>502 Bad Gateway</body></html>", ModlightManifestProblem.NotJson);
        Rejects("truncated json", "{\"version\":\"0.8.0\",\"url\":\"https:", ModlightManifestProblem.NotJson);
        Rejects("a json array", "[\"0.8.0\"]", ModlightManifestProblem.NotJson);
        Rejects("a bare string", "\"0.8.0\"", ModlightManifestProblem.NotJson);

        Rejects("no version", Manifest(version: null), ModlightManifestProblem.MissingVersion);
        Rejects("version sent as a number",
            "{\"version\":8,\"url\":\"https://x/y.exe\",\"sha256\":\"" + Sixty4('a') + "\"}",
            ModlightManifestProblem.MissingVersion);
        Rejects("unreadable version", Manifest(version: "latest"), ModlightManifestProblem.BadVersion);

        Rejects("no url", Manifest(url: null), ModlightManifestProblem.MissingUrl);
        Rejects("plaintext url", Manifest(url: "http://h.example.com/raw.exe"), ModlightManifestProblem.BadUrl);
        Rejects("not even a url", Manifest(url: "raw.exe"), ModlightManifestProblem.BadUrl);

        // The one that must never be waved through: no digest means no way to know what was
        // downloaded, and the end of this pipeline runs the file.
        Rejects("no digest", Manifest(sha256: null), ModlightManifestProblem.MissingDigest);
        Rejects("short digest", Manifest(sha256: "abc123"), ModlightManifestProblem.BadDigest);
        Rejects("non-hex digest", Manifest(sha256: Sixty4('z')), ModlightManifestProblem.BadDigest);

        // Missing optionals are not a reason to refuse an otherwise good release.
        ModlightReleaseManifest? lean = ModlightRelease.TryParse(
            Manifest(name: null, filename: null, size: null, notes: null, publishedAt: null),
            out ModlightManifestProblem problem);

        if (lean == null)
        {
            Fail("a manifest carrying only the required fields was refused: " + problem);
        }
        else
        {
            Same("a missing size reads as unknown", "0", lean.Size.ToString());
            Same("a missing filename falls back to the url", "y.exe", lean.FileName);
        }
    }

    // ------------------------------------------------------------------ the live stub

    /// <summary>The whole check, over a socket, for every outcome the channel can produce.</summary>
    private static async Task LiveStubServesEveryOutcome()
    {
        using HttpClient client = new HttpClient();
        StubServer server = new StubServer();
        Version installed = new Version(0, 7, 0);

        try
        {
            // 1. A newer release is published.
            server.Manifest = Manifest(version: "0.8.0", url: server.Origin + "/app.exe");
            await Expect(client, server.Origin, installed, ModlightUpdateOutcome.UpdateAvailable, "newer available");

            // The manifest that came off the wire has to arrive intact, digest included -
            // this is the value the downloader will check the installer against.
            ModlightManifestResult newer =
                await ModlightReleaseClient.FetchAsync(client, server.Origin, CancellationToken.None);

            Same("the digest survives the round trip",
                "773b9f112c36a8e9898ab31d553b009b514906fb9b8cbf360b140735b2f5a13b",
                newer.Manifest?.Sha256 ?? "<none>");

            // 2. The published release is the one already installed.
            server.Manifest = Manifest(version: "0.7.0", url: server.Origin + "/app.exe");
            await Expect(client, server.Origin, installed, ModlightUpdateOutcome.UpToDate, "same version");

            // 3. The server is behind. Never a downgrade.
            server.Manifest = Manifest(version: "0.6.9", url: server.Origin + "/app.exe");
            await Expect(client, server.Origin, installed, ModlightUpdateOutcome.UpToDate, "older on server");

            // 4. Nothing published: a 404 with a JSON body, exactly as the panel answers.
            server.Manifest = null;
            await Expect(client, server.Origin, installed, ModlightUpdateOutcome.UpToDate, "nothing published");

            ModlightManifestResult none =
                await ModlightReleaseClient.FetchAsync(client, server.Origin, CancellationToken.None);

            Same("404 is reported as NotPublished, not as a failure",
                ModlightFetchStatus.NotPublished.ToString(), none.Status.ToString());

            // 5. The server answers with something that is not a manifest at all.
            server.Manifest = "<html>503</html>";
            await Expect(client, server.Origin, installed, ModlightUpdateOutcome.CheckFailed, "malformed");

            // 6. Well-formed JSON with no checksum is still malformed, because it cannot be acted on.
            server.Manifest = Manifest(sha256: null, url: server.Origin + "/app.exe");
            await Expect(client, server.Origin, installed, ModlightUpdateOutcome.CheckFailed,
                "manifest without a digest");

            // 7. A body far larger than any manifest is refused rather than held in memory.
            server.Manifest = "{\"junk\":\"" + new string('x', 200000) + "\"}";
            await Expect(client, server.Origin, installed, ModlightUpdateOutcome.CheckFailed, "oversized body");

            // 8. This build cannot tell what version it is: not an update, whatever is published.
            server.Manifest = Manifest(version: "9.9.9", url: server.Origin + "/app.exe");
            await Expect(client, server.Origin, null, ModlightUpdateOutcome.CheckFailed,
                "installed version unknown");
        }
        finally
        {
            server.Dispose();
        }

        // 9. Nobody listening at all - the offline case, which has to stay quiet.
        ModlightManifestResult offline =
            await ModlightReleaseClient.FetchAsync(client, server.Origin, CancellationToken.None);

        Same("a refused connection is Unreachable",
            ModlightFetchStatus.Unreachable.ToString(), offline.Status.ToString());
        Same("and that is a failed check, not an update",
            ModlightUpdateOutcome.CheckFailed.ToString(),
            ModlightReleaseClient.Evaluate(installed, offline, out _).ToString());

        // 10. A host that is not a host at all does not even get a request.
        ModlightManifestResult nonsense =
            await ModlightReleaseClient.FetchAsync(client, "not a url", CancellationToken.None);

        Same("an unusable release host is Unreachable",
            ModlightFetchStatus.Unreachable.ToString(), nonsense.Status.ToString());
    }

    private static async Task Expect(HttpClient client, string origin, Version? installed,
        ModlightUpdateOutcome expected, string what)
    {
        ModlightManifestResult fetch = await ModlightReleaseClient.FetchAsync(client, origin, CancellationToken.None);
        ModlightUpdateOutcome actual = ModlightReleaseClient.Evaluate(installed, fetch, out string reason);

        // Printed, not merely asserted. These are the outcomes somebody actually wants to
        // watch happen, and a suite that only says "passed" never shows them.
        Console.WriteLine("  " + what.PadRight(30) + " -> " + actual + " (" + reason + ")");

        if (actual != expected)
        {
            Fail("stub: " + what + "\n  expected: " + expected + "\n  actual:   " + actual + " (" + reason + ")");
        }
    }

    // ------------------------------------------------------------------ real files

    /// <summary>
    /// Hash a file that really was fetched over the wire, then corrupt it and hash it again.
    /// </summary>
    /// <remarks>
    /// The transport here is a plain HttpClient rather than the FileDownloader the app uses,
    /// because FileDownloader sits behind HelpersLib and this project deliberately carries no
    /// Windows dependencies. What is under test is the part that decides whether an executable
    /// may run, and that part is the shipped code, unmodified.
    /// </remarks>
    private static async Task DigestVerificationPassesAndFailsOnRealFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ModlightUpdateTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            byte[] payload = new byte[1500000];
            new Random(20260818).NextBytes(payload);

            string expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

            using HttpClient client = new HttpClient();
            using StubServer server = new StubServer { Payload = payload };

            // ---- the good file, fetched over the socket
            string good = Path.Combine(folder, "good.exe");
            await Download(client, server.Origin + "/app.exe", good);

            Same("the download arrived whole", payload.Length.ToString(), new FileInfo(good).Length.ToString());
            Same("computed digest", expected,
                await ModlightUpdateFile.ComputeSha256Async(good, CancellationToken.None));

            ModlightVerification pass =
                await ModlightUpdateFile.VerifyOrDeleteAsync(good, expected, CancellationToken.None);

            Console.WriteLine("  intact file      verified=" + pass.Matches + "  still on disk=" + File.Exists(good));
            True("a correct file verifies", pass.Matches);
            True("and is still there afterwards", File.Exists(good));

            // A digest published in uppercase must not reject a perfectly good file.
            ModlightVerification upper =
                await ModlightUpdateFile.VerifyOrDeleteAsync(good, expected.ToUpperInvariant(), CancellationToken.None);

            True("an uppercase expected digest still verifies", upper.Matches);
            True("and still leaves the file alone", File.Exists(good));

            // ---- the corrupted file
            string bad = Path.Combine(folder, "bad.exe");
            await Download(client, server.Origin + "/app.exe", bad);

            byte[] tampered = await File.ReadAllBytesAsync(bad);
            tampered[1000000] ^= 0xFF;
            await File.WriteAllBytesAsync(bad, tampered);

            Same("one byte changed, same length", payload.Length.ToString(), new FileInfo(bad).Length.ToString());
            True("the file is there before verification", File.Exists(bad));

            ModlightVerification fail =
                await ModlightUpdateFile.VerifyOrDeleteAsync(bad, expected, CancellationToken.None);

            Console.WriteLine("  corrupted file   verified=" + fail.Matches + "  still on disk=" + File.Exists(bad) +
                "  (" + fail.Error + ")");
            Console.WriteLine("      expected " + expected);
            Console.WriteLine("      actual   " + fail.Actual);
            False("a corrupted file does not verify", fail.Matches);
            Same("and says why", "digest mismatch", fail.Error ?? "");
            False("AND IS DELETED", File.Exists(bad));

            // ---- the truncated file, which is the shape a dropped connection leaves behind
            string half = Path.Combine(folder, "half.exe");
            await File.WriteAllBytesAsync(half, payload[..(payload.Length / 2)]);

            ModlightVerification truncated =
                await ModlightUpdateFile.VerifyOrDeleteAsync(half, expected, CancellationToken.None);

            Console.WriteLine("  truncated file   verified=" + truncated.Matches +
                "  still on disk=" + File.Exists(half));
            False("a truncated file does not verify", truncated.Matches);
            False("and is deleted too", File.Exists(half));

            // ---- no digest to check against is a refusal, not a pass
            string unverifiable = Path.Combine(folder, "unverifiable.exe");
            await File.WriteAllBytesAsync(unverifiable, payload);

            ModlightVerification blank =
                await ModlightUpdateFile.VerifyOrDeleteAsync(unverifiable, "", CancellationToken.None);

            False("no expected digest is never a pass", blank.Matches);
            False("and the file does not survive it either", File.Exists(unverifiable));

            // ---- a file that is not there at all
            ModlightVerification missing = await ModlightUpdateFile.VerifyOrDeleteAsync(
                Path.Combine(folder, "nothing-here.exe"), expected, CancellationToken.None);

            False("a missing file does not verify", missing.Matches);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch
            {
            }
        }
    }

    private static async Task Download(HttpClient client, string url, string path)
    {
        await using Stream source = await client.GetStreamAsync(url);
        await using FileStream target = File.Create(path);
        await source.CopyToAsync(target);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Build a manifest body, leaving out whatever is passed as null.</summary>
    private static string Manifest(
        string? name = "Modlight App",
        string? version = "0.8.0",
        string? url = "https://h.example.com/y.exe",
        string? filename = "Modlight-0.8.0-setup-x64.exe",
        string? size = "82317446",
        string? sha256 = "773b9f112c36a8e9898ab31d553b009b514906fb9b8cbf360b140735b2f5a13b",
        string? notes = "Notes.",
        string? publishedAt = "2026-08-18T08:49:26.707Z")
    {
        List<string> fields = new List<string>();

        void Text(string key, string? value)
        {
            if (value != null)
            {
                fields.Add("\"" + key + "\":\"" + value + "\"");
            }
        }

        Text("name", name);
        Text("version", version);
        Text("url", url);
        Text("filename", filename);

        if (size != null)
        {
            fields.Add("\"size\":" + size);
        }

        Text("sha256", sha256);
        Text("notes", notes);
        Text("publishedAt", publishedAt);

        return "{" + string.Join(",", fields) + "}";
    }

    private static string Sixty4(char c) => new string(c, 64);

    private static void Verdict(string what, string installed, string? published, ModlightUpdateVerdict expected)
    {
        if (!ModlightRelease.TryParseVersion(installed, out Version current))
        {
            Fail(what + ": test input " + installed + " does not parse");
            return;
        }

        ModlightReleaseManifest? manifest = null;

        if (published != null)
        {
            manifest = ModlightRelease.TryParse(Manifest(version: published), out ModlightManifestProblem problem);

            if (manifest == null)
            {
                Fail(what + ": test input " + published + " does not parse: " + problem);
                return;
            }
        }

        Same(what, expected.ToString(), ModlightRelease.Decide(current, manifest).ToString());
    }

    private static void ParsesAs(string what, string input, string expected)
    {
        if (!ModlightRelease.TryParseVersion(input, out Version version))
        {
            Fail(what + ": " + input + " was refused");
            return;
        }

        Same(what, expected, version.ToString());
    }

    private static void Refuses(string what, string? input)
    {
        if (ModlightRelease.TryParseVersion(input, out Version version))
        {
            Fail(what + ": " + (input ?? "<null>") + " was accepted as " + version);
        }
    }

    private static void Rejects(string what, string json, ModlightManifestProblem expected)
    {
        ModlightReleaseManifest? manifest = ModlightRelease.TryParse(json, out ModlightManifestProblem problem);

        if (manifest != null)
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
    /// A release channel that answers on 127.0.0.1 and nowhere else.
    /// </summary>
    /// <remarks>
    /// Raw TCP rather than HttpListener: an HttpListener prefix needs a URL reservation on
    /// Windows, which means running the suite as an administrator, which means the suite
    /// quietly stops being run. Forty lines of socket owe nobody anything.
    /// </remarks>
    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly Task loop;
        private bool disposed;

        /// <summary>The body to answer the manifest endpoint with. Null answers 404, as the panel does.</summary>
        public string? Manifest { get; set; }

        /// <summary>The bytes served at /app.exe.</summary>
        public byte[] Payload { get; set; } = [];

        public string Origin { get; }

        public StubServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptAsync);
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

                    string path = PathOf(await ReadRequestAsync(stream));

                    if (path == "/app.exe")
                    {
                        await WriteAsync(stream, 200, "application/octet-stream", Payload);
                    }
                    else if (path == ModlightReleaseClient.ManifestPath)
                    {
                        await WriteAsync(stream,
                            Manifest == null ? 404 : 200,
                            "application/json",
                            Encoding.UTF8.GetBytes(Manifest ?? "{\"error\":\"no release published\"}"));
                    }
                    else
                    {
                        await WriteAsync(stream, 404, "text/plain", Encoding.UTF8.GetBytes("no"));
                    }
                }
            }
            catch
            {
            }
        }

        private static async Task<string> ReadRequestAsync(Stream stream)
        {
            byte[] buffer = new byte[8192];
            StringBuilder text = new StringBuilder();

            while (text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
            {
                int read = await stream.ReadAsync(buffer);

                if (read <= 0)
                {
                    break;
                }

                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return text.ToString();
        }

        private static string PathOf(string request)
        {
            string[] parts = request.Split(' ');

            return parts.Length > 1 ? parts[1] : "";
        }

        private static async Task WriteAsync(Stream stream, int status, string contentType, byte[] body)
        {
            string head = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(body);
            await stream.FlushAsync();
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

            try
            {
                loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            stopping.Dispose();
        }
    }
}
