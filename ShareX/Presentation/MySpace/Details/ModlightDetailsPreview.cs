#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia;
using Avalonia.Media.Imaging;
using ShareX.UploadersLib.FileUploaders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The bigger picture the details drawer shows, fetched for one file at a time.
/// </summary>
/// <remarks>
/// A separate request from the one the grid makes, and deliberately: the grid asks for
/// a 480 px tile because that is what a 180 px card needs on a 200% display, and
/// stretching that across a 400 px drawer is visibly soft. The panel's own drawer asks
/// for w=1024 for the same reason, so this asks for the same thing and lands on the
/// same server-side cache entry.
///
/// It NEVER falls back to the raw file. The URL is always the row's own thumbUrl with
/// its size raised, which keeps two guarantees the serving layer attaches to a resize
/// parameter: it is treated as a preview, so it spends no view and burns no one-time
/// link, and it is refused rather than answered for a file too large to transform - a
/// fallback to the original would pull a 700 MB recording down to fill a preview box.
/// </remarks>
internal static class ModlightDetailsPreview
{
    /// <summary>The edge asked of the server, matching the panel's own details drawer.</summary>
    private const int RequestEdge = 1024;

    /// <summary>
    /// The widest picture the drawer will keep decoded.
    /// </summary>
    /// <remarks>
    /// One image at a time, so this is a fence rather than a budget - but it is still a
    /// fence: a server that one day answers this URL with something larger than it was
    /// asked for must not turn one open drawer into tens of megabytes of bitmap.
    /// </remarks>
    private const int DecodeWidth = 1024;

    /// <summary>
    /// The tile the grid already fetched, decoded, or null when it never did.
    /// </summary>
    /// <remarks>
    /// Shown first so the drawer has a picture in it on the frame it opens. It is decoded
    /// from the cache's ENCODED bytes rather than borrowed from the card, because a card's
    /// bitmap belongs to the card: <see cref="MySpaceViewModel.Remove"/> disposes the file
    /// it removes, and a drawer holding that bitmap would be drawing a disposed handle.
    /// Decoding our own copy costs a millisecond and owns what it produces.
    /// </remarks>
    public static Task<Bitmap?> WarmAsync(CloudFileViewModel file)
    {
        byte[]? bytes = CloudThumbnailCache.Get(file.Code);
        return bytes == null ? Task.FromResult<Bitmap?>(null) : Task.Run(() => Decode(bytes));
    }

    /// <summary>The full-size preview, or null when the server has none to give.</summary>
    public static async Task<Bitmap?> LoadAsync(CloudFileViewModel file, CancellationToken cancellationToken)
    {
        ModlightAccount? account = ModlightSignIn.Current;
        string url = Enlarge(file.ThumbnailUrl);

        if (account == null || url.Length == 0)
        {
            return null;
        }

        string host = account.Host;
        string token = account.Token;

        byte[]? bytes = await Task.Run(
            () => ModlightLibrary.GetThumbnailAsync(host, token, url, cancellationToken), cancellationToken);

        if (bytes == null || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return await Task.Run(() => Decode(bytes), cancellationToken);
    }

    /// <summary>
    /// The same preview URL, asked for at drawer size.
    /// </summary>
    /// <remarks>
    /// Rewritten rather than rebuilt from the file's own address, because what the server
    /// hands out in <c>thumbUrl</c> is the thing it promises to answer - which parameters
    /// it carries and in what shape is its business, not this client's. Only w and h are
    /// touched; anything else in the query is passed through untouched, so a future
    /// parameter cannot be dropped by a client that predates it.
    /// </remarks>
    private static string Enlarge(string url)
    {
        if (url.Length == 0)
        {
            return "";
        }

        int mark = url.IndexOf('?');

        if (mark < 0)
        {
            return url + "?w=" + RequestEdge + "&h=" + RequestEdge;
        }

        List<string> kept = new();
        bool sawWidth = false;
        bool sawHeight = false;

        foreach (string part in url.Substring(mark + 1).Split('&'))
        {
            if (part.StartsWith("w=", StringComparison.Ordinal))
            {
                sawWidth = true;
                kept.Add("w=" + RequestEdge);
            }
            else if (part.StartsWith("h=", StringComparison.Ordinal))
            {
                sawHeight = true;
                kept.Add("h=" + RequestEdge);
            }
            else if (part.Length > 0)
            {
                kept.Add(part);
            }
        }

        if (!sawWidth)
        {
            kept.Add("w=" + RequestEdge);
        }

        if (!sawHeight)
        {
            kept.Add("h=" + RequestEdge);
        }

        StringBuilder rebuilt = new StringBuilder(url.Substring(0, mark)).Append('?');

        for (int index = 0; index < kept.Count; index++)
        {
            if (index > 0)
            {
                rebuilt.Append('&');
            }

            rebuilt.Append(kept[index]);
        }

        return rebuilt.ToString();
    }

    /// <summary>
    /// Encoded bytes into a picture no wider than the drawer can use.
    /// </summary>
    /// <remarks>
    /// Shrunk AFTER decoding rather than by asking the decoder for a width, for the reason
    /// the grid states at its own Decode: DecodeToWidth is told a number, not a limit, and
    /// the server does not enlarge a small original - so a 96 px thumbnail asked for at
    /// 1024 would come back as a 1024 px blur costing a hundred times the memory of the
    /// picture it was made from.
    /// </remarks>
    private static Bitmap? Decode(byte[] bytes)
    {
        Bitmap? decoded = null;

        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            decoded = new Bitmap(stream);

            if (decoded.PixelSize.Width <= DecodeWidth || decoded.PixelSize.Width <= 0)
            {
                return decoded;
            }

            int height = Math.Max(1, (int)Math.Round(
                decoded.PixelSize.Height * (double)DecodeWidth / decoded.PixelSize.Width));

            Bitmap scaled = decoded.CreateScaledBitmap(
                new PixelSize(DecodeWidth, height), BitmapInterpolationMode.HighQuality);

            decoded.Dispose();
            return scaled;
        }
        catch
        {
            // A picture that will not decode is not worth a message. The drawer keeps the
            // placeholder its kind earned it, exactly as a card does.
            decoded?.Dispose();
            return null;
        }
    }
}
