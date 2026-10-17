#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using System;

namespace ShareX;

/// <summary>
/// An address the Download tab is willing to hand to yt-dlp.
/// </summary>
/// <remarks>
/// This type exists so that "the user typed something" and "we are about to put this on a
/// command line" are different types, and the only way to get from one to the other is
/// through <see cref="TryParse"/>.
///
/// The check is deliberately a whitelist rather than a blacklist. A downloader's URL box
/// is the app's widest untrusted input: whatever is in it becomes an argument to a child
/// process, and yt-dlp accepts far more than http - <c>file:</c> would read local files
/// and any of the archive or search prefixes do things the person pasting a video link
/// never asked for. Absolute http/https with a real host, or nothing.
///
/// No site list. yt-dlp knows about eighteen hundred extractors and gains more every
/// release; a client-side list of "supported sites" could only ever be a way to refuse
/// links that would have worked.
/// </remarks>
internal sealed class DownloadLink
{
    /// <summary>
    /// Longer than any real address and short enough that a paste accident cannot become
    /// a command line the operating system has to think about.
    /// </summary>
    private const int MaxLength = 2048;

    private DownloadLink(string url, string source)
    {
        Url = url;
        Source = source;
    }

    /// <summary>The address, exactly as it will be passed to yt-dlp.</summary>
    public string Url { get; }

    /// <summary>The site, for the queue row: "youtube.com", "tiktok.com".</summary>
    public string Source { get; }

    /// <summary>
    /// Whether this address is one of YouTube's.
    /// </summary>
    /// <remarks>
    /// The one place the tab treats a site specially, and it is not a feature list: it
    /// decides whether to force H.264 in the format ranking. See
    /// <see cref="YtDlpRun"/> for why that distinction has to exist.
    /// </remarks>
    public bool IsYouTube { get; private init; }

    public static bool TryParse(string? input, out DownloadLink link)
    {
        link = null!;

        string value = input?.Trim() ?? "";

        if (value.Length == 0 || value.Length > MaxLength)
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        // The parsed form, not the typed one: it has already had whitespace, control
        // characters and a missing path normalised out of it, and it is the string every
        // later decision is made about.
        string host = uri.Host.ToLowerInvariant();

        link = new DownloadLink(uri.AbsoluteUri, Trim(host))
        {
            IsYouTube = IsYouTubeHost(host)
        };

        return true;
    }

    private static string Trim(string host) =>
        host.StartsWith("www.", StringComparison.Ordinal) ? host.Substring(4) : host;

    /// <summary>
    /// Suffix matching with the dot included, never <c>Contains</c>.
    /// </summary>
    /// <remarks>
    /// "youtube.com.example.org" contains "youtube.com" and belongs to somebody else. The
    /// only thing riding on this answer is a format preference, so getting it wrong is
    /// cheap - but a host check written the loose way is the shape that ends up copied
    /// into somewhere it is not cheap.
    /// </remarks>
    private static bool IsYouTubeHost(string host)
    {
        foreach (string domain in new[] { "youtube.com", "youtu.be", "youtube-nocookie.com" })
        {
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
