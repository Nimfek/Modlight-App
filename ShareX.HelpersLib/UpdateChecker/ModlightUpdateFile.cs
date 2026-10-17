#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.HelpersLib;

/// <summary>The result of hashing a downloaded file and comparing it with what was promised.</summary>
/// <param name="Matches">True only when a digest was computed and it is the expected one.</param>
/// <param name="Actual">The digest that was computed, or an empty string when it could not be.</param>
/// <param name="Error">Why it could not be computed, or null.</param>
public readonly record struct ModlightVerification(bool Matches, string Actual, string? Error);

/// <summary>
/// The installer file on disk: hashing it, and getting rid of it when the hash is wrong.
/// </summary>
/// <remarks>
/// This is the last gate before a downloaded executable is handed to
/// <see cref="System.Diagnostics.Process"/> with an elevation prompt behind it, so it is
/// written to be dull: no HelpersLib, no UI, nothing that needs a window to exist. It
/// compiles into ShareX.Tests unchanged, which is how the failing case gets proven rather
/// than asserted.
/// </remarks>
public static class ModlightUpdateFile
{
    /// <summary>Big enough that hashing 80 MB is one sequential pass, small enough to stay off the large object heap.</summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>Hash a file. Lowercase hex, the same form the manifest publishes.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        using SHA256 sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Whether two digests are the same digest.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, deliberately. The manifest publishes lowercase hex and this code
    /// produces lowercase hex, so an ordinal compare would work today and would keep working
    /// right up until somebody's tooling emitted uppercase - at which point every update
    /// would be rejected as corrupt, with a message saying the file failed verification.
    /// That is a silent failure with a loud symptom pointing at the wrong thing.
    /// </remarks>
    public static bool DigestsMatch(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hash the downloaded file, and delete it unless it is exactly what was promised.
    /// </summary>
    /// <remarks>
    /// Deleting on failure is the point, not tidiness. What is left behind otherwise is an
    /// executable of unknown provenance sitting in the temp directory under a name that says
    /// "Modlight setup", which is the file a confused person double-clicks. A digest that
    /// could not be computed at all - unreadable file, disk error, cancelled hash - counts as
    /// a failure for the same reason: not verified and verified are different states, and
    /// only one of them may lead to an installer running.
    /// </remarks>
    public static async Task<ModlightVerification> VerifyOrDeleteAsync(string path, string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!ModlightRelease.IsDigest(expectedSha256))
        {
            TryDelete(path);
            return new ModlightVerification(false, "", "no digest to check against");
        }

        string actual;

        try
        {
            actual = await ComputeSha256Async(path, cancellationToken);
        }
        catch (Exception exception)
        {
            TryDelete(path);
            return new ModlightVerification(false, "", exception.GetType().Name + ": " + exception.Message);
        }

        if (DigestsMatch(actual, expectedSha256))
        {
            return new ModlightVerification(true, actual, null);
        }

        TryDelete(path);
        return new ModlightVerification(false, actual, "digest mismatch");
    }

    /// <summary>Delete a file if it is there, and say whether it is gone now.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
