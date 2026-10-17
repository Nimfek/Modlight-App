#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using ShareX.AvaloniaUI.Theming;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShareX;

/// <summary>
/// One file of the account's, as a card in the My space grid.
/// </summary>
/// <remarks>
/// Everything on the card is decided once, here, from the row the server sent. The
/// alternative - converters in the layout - would put the same decisions in a place
/// where they cannot be read next to each other, and two of them are decisions rather
/// than formatting: which of the two links belongs on the clipboard, and which icon
/// stands in for a file that has no picture.
/// </remarks>
internal sealed class CloudFileViewModel : INotifyPropertyChanged, IDisposable
{
    private Bitmap? thumbnail;
    private Bitmap? motionFrame;
    private bool isSelected;

    public CloudFileViewModel(ModlightUpload upload)
    {
        Id = upload.Id ?? "";
        Row = upload;
        Apply(upload);

        // The folder list and the first page of files are fetched at the same time and
        // neither reliably wins, so a card built before the names arrive would sit there
        // holding a folder id it cannot spell. This is how it finds out afterwards.
        CloudFolderNames.Changed += OnFolderNamesChanged;
    }

    private void OnFolderNamesChanged(object? sender, EventArgs e)
    {
        string resolved = CloudFolderNames.Resolve(FolderId);

        if (resolved == FolderName)
        {
            return;
        }

        FolderName = resolved;
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(HasFolder));
    }

    /// <summary>
    /// The row's id, which is what the API takes for every write.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Code"/> on purpose: the code is what the public links are
    /// built from and travels in URLs, while this never leaves a request body. It is also
    /// the only field here that cannot change - a vanity address moves the code, and
    /// <see cref="Apply"/> exists so the card can follow it without being replaced.
    /// </remarks>
    public string Id { get; }

    /// <summary>
    /// The listing row exactly as the server sent it.
    /// </summary>
    /// <remarks>
    /// Kept rather than copied field by field. The menus, the details window and the
    /// download list all need different corners of it, and re-exposing thirty columns as
    /// thirty properties would be thirty chances for a card to disagree with the server
    /// about what it is showing.
    /// </remarks>
    public ModlightUpload Row { get; private set; }

    /// <summary>The short code, which identifies the file to the thumbnail cache and the links.</summary>
    public string Code { get; private set; } = "";

    /// <summary>The folder holding it, or null when the server reported none.</summary>
    /// <remarks>
    /// Used to keep a folder tile's tally right when a file leaves it, and to mark the
    /// folder a file is already in when the move menu is drawn. The grid itself never
    /// filters on this: what is on screen is whatever the listing returned.
    /// </remarks>
    public string? FolderId { get; private set; }

    /// <summary>Bytes, for the same tally.</summary>
    public long Size { get; private set; }

    /// <summary>Whether this file can be acted on at all; a listing row without an id cannot.</summary>
    public bool CanTrash => Id.Length > 0;

    public string Name { get; private set; } = "";

    /// <summary>Where the preview image is, or empty when this file cannot have one.</summary>
    public string ThumbnailUrl { get; private set; } = "";

    /// <summary>
    /// Where a preview that MOVES is, or empty when the server says this file has none.
    /// </summary>
    /// <remarks>
    /// Never guessed from the kind. The server publishes this only for rows that will
    /// actually answer with a tile, and a client that invented one for every video would
    /// collect a 415 for each file whose duration its container will not state.
    /// </remarks>
    public string MotionThumbnailUrl { get; private set; } = "";

    /// <summary>Whether pointing at this card is worth asking the server anything.</summary>
    public bool CanMotion => MotionThumbnailUrl.Length > 0;

    public string CopyLink { get; private set; } = "";
    public string OpenLink { get; private set; } = "";

    public bool HasLink => CopyLink.Length > 0;
    public bool CanOpen => OpenLink.Length > 0;

    /// <summary>
    /// The file's public address with no extension on the end, which every other route
    /// hangs off: /download, /convert, /raw.
    /// </summary>
    /// <remarks>
    /// Derived by taking the extension back off the preview link rather than by
    /// reassembling one from the code, because the shape of that link is the ACCOUNT'S
    /// choice: an admin with <c>skipTagInUrl</c> gets /{code}, everybody else gets
    /// /{tag}/{code}, and the hostname is whichever of the deployment's several names the
    /// owner picked. Rebuilding it here would quietly ignore all three.
    /// </remarks>
    public string MediaBase { get; private set; } = "";

    public bool HasMediaBase => MediaBase.Length > 0;

    /// <summary>The bytes themselves, as a link.</summary>
    public string RawLink { get; private set; } = "";

    /// <summary>The preview page, which is what a person means by "open".</summary>
    public string PageLink { get; private set; } = "";

    /// <summary>The route that answers with an attachment rather than a preview.</summary>
    public string DownloadLink => MediaBase.Length > 0 ? MediaBase + "/download" : "";

    /// <summary>The file's size, spelled the way the web panel spells it.</summary>
    public string SizeText => ModlightFormat.CloudSize(Size);

    /// <summary>
    /// How long ago the file arrived, in words ("5 min ago").
    /// </summary>
    /// <remarks>
    /// Worked out on every read rather than stored with the row, so a window left open
    /// for an hour does not still say "just now" under the file it was showing when it
    /// opened. Nothing ages the captions on a timer: the grid is rebuilt often enough -
    /// every upload, every folder, every refresh - that one would cost more than it
    /// corrects.
    /// </remarks>
    public string AgeText => AgeOf(Row.CreatedAt);

    /// <summary>Size and age on one line, the way the panel's own card writes them.</summary>
    public string Meta
    {
        get
        {
            string age = AgeText;
            return age.Length == 0 ? SizeText : SizeText + "  ·  " + age;
        }
    }

    /// <summary>How many times the file has been fetched. Drawn next to an eye.</summary>
    public string ViewsText => Row.Views.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>
    /// The name of the folder the file is in, or empty when it is in none.
    /// </summary>
    /// <remarks>
    /// Resolved on the client, from <see cref="CloudFolderNames"/>. The listing endpoint
    /// answers <c>folderName: null</c> deliberately - it would otherwise repeat one
    /// string on every row of a folder - and expects whoever draws the card to look the
    /// id up in the folder list it already holds.
    /// </remarks>
    public string FolderName { get; private set; } = "";

    /// <summary>Whether there is a folder worth naming on the card.</summary>
    public bool HasFolder => FolderName.Length > 0;

    /// <summary>Which of the badge's families this file belongs to.</summary>
    public CloudTypeFamily Family { get; private set; } = CloudTypeFamily.Binary;

    /// <summary>The plate colour of that family. Fixed, not a token - see <see cref="CloudFileType"/>.</summary>
    public IBrush TypePlate => CloudFileType.Plate(Family);

    /// <summary>The glyph printed on the plate.</summary>
    public string TypeEmblem => CloudFileType.Emblem(Family);

    /// <summary>The extension printed on the plate: PNG, DOCX, ZIP.</summary>
    public string TypeLabel { get; private set; } = "";

    /// <summary>How large that label may be drawn before a long one has to shrink.</summary>
    public double TypeLabelSize => CloudFileType.LabelSize(TypeLabel);

    /// <summary>The Lucide glyph standing in for a file with no preview.</summary>
    public string Icon { get; private set; } = LucideIcons.file;

    /// <summary>The link stops working at some point, by date or by view count.</summary>
    public bool Expires { get; private set; }

    public bool HasPassword { get; private set; }

    /// <summary>Anyone with the link can see it. The panel calls the other state private.</summary>
    public bool Published { get; private set; }

    /// <summary>
    /// This row is in the server's trash, so it has no working links.
    /// </summary>
    /// <remarks>
    /// The server withholds them deliberately: <c>getMedia</c> filters on
    /// <c>deleted_at IS NULL</c>, so every one of those URLs would 404 while the file sits
    /// in the bin. A menu that offered Copy link here would be offering a dead link.
    /// </remarks>
    public bool IsTrashed { get; private set; }

    /// <summary>
    /// Take a row the server has just written and redraw the card from it.
    /// </summary>
    /// <remarks>
    /// Every write endpoint answers with the affected rows in the listing's own shape,
    /// which is what this is for: publishing a file, moving it, giving it a vanity
    /// address or setting a password all change what the card should say, and re-fetching
    /// the page to find that out would empty the grid, throw away every decoded
    /// thumbnail, lose the selection and scroll back to the top - on every small edit.
    ///
    /// The picture is deliberately NOT touched. A vanity address moves the code, and the
    /// thumbnail cache is keyed by it, but the bytes on screen are still this file's
    /// bytes; dropping them would make every rename flash a grey tile.
    /// </remarks>
    public void Apply(ModlightUpload upload)
    {
        Row = upload;
        Code = upload.Code ?? "";
        Name = FirstNonEmpty(upload.Filename, upload.Code, Strings.MySpace_UntitledFile);
        ThumbnailUrl = upload.ThumbUrl ?? "";
        MotionThumbnailUrl = upload.MotionThumbUrl ?? "";

        // The server's own answer to "which link does this account copy", taken as
        // given: the account's copy_raw switch decides it, and deciding locally would
        // mean one account copying two shapes of link depending on which app it came
        // from. Falling back to the preview page only when it did not answer at all.
        CopyLink = FirstNonEmpty(upload.CopyUrl, upload.Url, upload.RawUrl);

        // Opening is the other way round: the preview page is what a person means by
        // "open", and the raw file is the fallback for something with no page.
        OpenLink = FirstNonEmpty(upload.Url, upload.RawUrl, upload.CopyUrl);

        PageLink = upload.Url ?? "";
        RawLink = upload.RawUrl ?? "";
        MediaBase = BaseOf(upload.Url, upload.Ext);
        IsTrashed = string.IsNullOrEmpty(upload.Url) && string.IsNullOrEmpty(upload.RawUrl);

        FolderId = string.IsNullOrEmpty(upload.FolderId) ? null : upload.FolderId;
        Size = Math.Max(0, upload.Size);
        HasPassword = upload.HasPassword;
        Published = upload.Published;
        Expires = upload.ExpiresAt != null || upload.MaxViews != null;
        Icon = IconFor(upload.Kind);
        Family = CloudFileType.Resolve(upload.MimeType, upload.Ext, upload.Kind);
        TypeLabel = CloudFileType.Label(upload.Ext, upload.MimeType);
        FolderName = CloudFolderNames.Resolve(FolderId);

        OnPropertyChanged(nameof(Code));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ThumbnailUrl));
        OnPropertyChanged(nameof(MotionThumbnailUrl));
        OnPropertyChanged(nameof(CanMotion));
        OnPropertyChanged(nameof(CopyLink));
        OnPropertyChanged(nameof(OpenLink));
        OnPropertyChanged(nameof(PageLink));
        OnPropertyChanged(nameof(RawLink));
        OnPropertyChanged(nameof(MediaBase));
        OnPropertyChanged(nameof(HasMediaBase));
        OnPropertyChanged(nameof(DownloadLink));
        OnPropertyChanged(nameof(IsTrashed));
        OnPropertyChanged(nameof(FolderId));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(Published));
        OnPropertyChanged(nameof(Expires));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(AgeText));
        OnPropertyChanged(nameof(ViewsText));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(Family));
        OnPropertyChanged(nameof(TypePlate));
        OnPropertyChanged(nameof(TypeEmblem));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(TypeLabelSize));
        OnPropertyChanged(nameof(HasLink));
        OnPropertyChanged(nameof(CanOpen));
    }

    /// <summary>
    /// The five shapes of link the panel offers under "Copy as".
    /// </summary>
    /// <remarks>
    /// The same templates as <c>lib/media/link-formats.ts</c>, so a link copied here and
    /// one copied in the browser are the same string. Markdown, BBCode and HTML switch to
    /// picture syntax for an image and point at the RAW url when they do, because an
    /// &lt;img&gt; that points at the preview page renders a web page inside an image tag.
    /// </remarks>
    public string LinkFormat(CloudLinkFormat format)
    {
        bool isImage = Row.Kind == "image";

        return format switch
        {
            CloudLinkFormat.Direct => PageLink,
            CloudLinkFormat.Raw => RawLink,
            CloudLinkFormat.Markdown => isImage ? "![" + Name + "](" + RawLink + ")" : "[" + Name + "](" + PageLink + ")",
            CloudLinkFormat.BBCode => isImage ? "[img]" + RawLink + "[/img]" : "[url]" + PageLink + "[/url]",
            CloudLinkFormat.Html => isImage
                ? "<img src=\"" + RawLink + "\" alt=\"" + Name + "\" />"
                : "<a href=\"" + PageLink + "\">" + Name + "</a>",
            _ => PageLink
        };
    }

    /// <summary>Where to ask the server for this file in another format.</summary>
    public string ConvertLink(string to) =>
        MediaBase.Length > 0 ? MediaBase + "/convert?to=" + Uri.EscapeDataString(to) : "";

    /// <summary>
    /// The card is part of the current selection.
    /// </summary>
    /// <remarks>
    /// Selection lives on the card rather than in a set held by the view, because the
    /// card is what has to draw it and a WrapPanel of several thousand realised borders
    /// cannot be asked to look itself up in a list on every repaint.
    /// </remarks>
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            OnPropertyChanged();
        }
    }

    public Bitmap? Thumbnail
    {
        get => thumbnail;
        private set
        {
            if (ReferenceEquals(thumbnail, value))
            {
                return;
            }

            Bitmap? previous = thumbnail;
            thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
            OnPropertyChanged(nameof(HasNoThumbnail));
            previous?.Dispose();
        }
    }

    public bool HasThumbnail => thumbnail != null;
    public bool HasNoThumbnail => thumbnail == null;

    /// <summary>
    /// The frame of the moving preview currently on screen, or null when this tile is
    /// not moving.
    /// </summary>
    /// <remarks>
    /// UNLIKE <see cref="Thumbnail"/>, this card does NOT own what it is given here, and
    /// the setter deliberately does not dispose what it replaces. The frames belong to one
    /// <see cref="AnimatedThumbnail"/> held by <see cref="CloudMotionPlayer"/>, which
    /// shows each of them several times over a loop; disposing on every swap would destroy
    /// a surface the next tick is about to hand back.
    ///
    /// It sits OVER <see cref="Thumbnail"/> rather than replacing it, which is what makes
    /// the tile safe to leave at any moment: the still is never taken away, so there is
    /// nothing underneath to be missing and nothing to flash back to.
    /// </remarks>
    public Bitmap? MotionFrame
    {
        get => motionFrame;
        private set
        {
            if (ReferenceEquals(motionFrame, value))
            {
                return;
            }

            motionFrame = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMoving));
        }
    }

    /// <summary>Whether the moving preview is what the card is showing right now.</summary>
    public bool IsMoving => motionFrame != null;

    /// <summary>Show one frame. UI thread only; the player keeps ownership of it.</summary>
    public void SetMotionFrame(Bitmap frame) => MotionFrame = frame;

    /// <summary>Go back to the still. UI thread only.</summary>
    public void ClearMotionFrame() => MotionFrame = null;

    /// <summary>Hand the card its picture. UI thread only; the card takes ownership of it.</summary>
    public void SetThumbnail(Bitmap bitmap) => Thumbnail = bitmap;

    /// <summary>
    /// Give the picture back, leaving the card in place showing its icon.
    /// </summary>
    /// <remarks>
    /// What <see cref="ThumbnailBudget{T}"/> calls when the grid is holding more decoded
    /// pixels than it may. Distinct from <see cref="Dispose"/> in meaning even though the
    /// body is the same: this card is still in the listing and will ask for its picture
    /// again the moment it is scrolled back to.
    /// </remarks>
    public void ReleaseThumbnail() => Thumbnail = null;

    public void Dispose()
    {
        // A static event holds whatever subscribes to it for the life of the process, so
        // this line is what keeps the grid's pictures collectable. Every path that drops
        // a card already calls Dispose - it is how the decoded bitmap is released - so
        // there is no second place this could have gone.
        CloudFolderNames.Changed -= OnFolderNamesChanged;

        // The frame first, because it is a borrowed reference and the thing that owns it
        // is about to be disposed by whoever is tearing the listing down.
        MotionFrame = null;
        Thumbnail = null;
    }

    /// <summary>How many bytes a decoded picture of this size occupies.</summary>
    /// <remarks>
    /// Four bytes a pixel: every surface Avalonia decodes into on Windows is 32-bit,
    /// premultiplied BGRA. Row padding is ignored because it cannot exceed three bytes a
    /// row, which is under a thousandth of a tile.
    /// </remarks>
    public static long ThumbnailCost(Bitmap bitmap) =>
        (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4L;

    /// <summary>
    /// Which glyph stands in for a file with no preview.
    /// </summary>
    /// <remarks>
    /// From the server's <c>kind</c>, never from the MIME string. The server consults
    /// its own effectiveType, so a file ShareX stored as application/octet-stream -
    /// which is what Windows reports for .avif and every other format missing from the
    /// registry - is still filed as the image it is. Sniffing the MIME string here would
    /// put a generic icon on exactly the files the panel shows a picture for.
    /// </remarks>
    private static string IconFor(string? kind) => kind switch
    {
        "image" => LucideIcons.file_image,
        "video" => LucideIcons.file_video,
        "audio" => LucideIcons.file_audio,
        "document" => LucideIcons.file_text,
        "archive" => LucideIcons.file_archive,
        "paste" => LucideIcons.clipboard,
        _ => LucideIcons.file
    };

    /// <summary>
    /// The preview link with its extension taken off, or empty when there is no link.
    /// </summary>
    /// <remarks>
    /// The extension in a Modlight URL is DECORATIVE - the server reads the code up to the
    /// first dot - but it is still part of the string the server sent, and hanging
    /// "/download" off the end of it would produce /{code}.png/download, which is nothing.
    /// Only removed when it is really there: a file with no extension has none to take.
    /// </remarks>
    private static string BaseOf(string? url, string? ext)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "";
        }

        if (string.IsNullOrEmpty(ext))
        {
            return url!;
        }

        string suffix = "." + ext;
        return url!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? url!.Substring(0, url!.Length - suffix.Length)
            : url!;
    }

    /// <summary>
    /// How long ago an instant was, in the same buckets the web panel uses.
    /// </summary>
    /// <remarks>
    /// The panel gets this from <c>Intl.RelativeTimeFormat</c> with <c>style: "short"</c>
    /// and <c>numeric: "auto"</c>, which is why the thresholds here are its thresholds
    /// exactly - a minute, an hour, thirty days, twelve months - and why one day comes
    /// out as "yesterday" rather than as a count. .NET ships no equivalent formatter, so
    /// the wording lives in the resources instead.
    ///
    /// Magnitudes are rounded, never truncated, for the reason lib/format.ts gives:
    /// 59 minutes is nearer to an hour than to anything the minute bucket can say. An
    /// instant in the FUTURE - a server clock a few seconds ahead is enough - is clamped
    /// to zero rather than counted backwards, because "in 3 seconds" written under a
    /// file that is already here reads as a bug.
    ///
    /// INTERNAL rather than private because the administration screen dates files too,
    /// and two answers to "how long ago" in one app is how a tile and a list end up
    /// disagreeing about the same file. Static and pure, so sharing it costs nothing.
    /// </remarks>
    internal static string AgeOf(DateTimeOffset? created)
    {
        if (created == null)
        {
            return "";
        }

        double seconds = Math.Max(0, (DateTimeOffset.Now - created.Value).TotalSeconds);

        if (seconds < 60)
        {
            return Strings.MySpace_TimeNow;
        }

        long minutes = (long)Math.Round(seconds / 60);

        if (minutes < 60)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.MySpace_TimeMinutes, minutes);
        }

        long hours = (long)Math.Round(minutes / 60.0);

        if (hours < 24)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.MySpace_TimeHours, hours);
        }

        long days = (long)Math.Round(hours / 24.0);

        if (days == 1)
        {
            return Strings.MySpace_TimeYesterday;
        }

        if (days < 30)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.MySpace_TimeDays, days);
        }

        long months = (long)Math.Round(days / 30.0);

        if (months < 12)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.MySpace_TimeMonths, months);
        }

        long years = (long)Math.Round(months / 12.0);

        return string.Format(CultureInfo.CurrentCulture, YearForm(years), years);
    }

    /// <summary>
    /// Which of the three wordings a count of years takes.
    /// </summary>
    /// <remarks>
    /// Years are the only unit here that needs one. Every other bucket is written with an
    /// abbreviation that declines in neither language - "5 min temu", "3 godz. temu",
    /// "8 mies. temu" - and a single day is a word of its own; but a year has "rok",
    /// "lata" and "lat", and no abbreviation in common use dodges them.
    ///
    /// The selector is the Polish rule. English needs no such rule and gets the right
    /// answer from it anyway, because its "few" and "many" resources are the same string
    /// and its "one" is the only case that differs - which is precisely what this rule
    /// separates out. One rule, two languages, no per-culture branch to get wrong.
    /// </remarks>
    private static string YearForm(long years)
    {
        if (years == 1)
        {
            return Strings.MySpace_TimeYearsOne;
        }

        long last = years % 10;
        long teens = years % 100;

        return last >= 2 && last <= 4 && (teens < 12 || teens > 14)
            ? Strings.MySpace_TimeYearsFew
            : Strings.MySpace_TimeYearsMany;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate!;
            }
        }

        return "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>The five shapes of link the panel offers under "Copy as".</summary>
internal enum CloudLinkFormat
{
    Direct,
    Raw,
    Markdown,
    BBCode,
    Html
}

/// <summary>What a folder tile stands for.</summary>
internal enum CloudFolderKind
{
    /// <summary>A folder the account made in the panel.</summary>
    Folder,

    /// <summary>
    /// The account's loose files - everything in no folder at all.
    /// </summary>
    /// <remarks>
    /// Its id is the server's own sentinel, handed out by GET /api/folders rather than
    /// spelled "root" here, so that a client cannot drift from it. It behaves like a
    /// folder on the shelf and like nothing else anywhere: it cannot be uploaded into by
    /// name, it has no children, and it has no page in the panel.
    /// </remarks>
    Unfiled
}

/// <summary>
/// One folder tile in the My space explorer.
/// </summary>
/// <remarks>
/// Deliberately compact rather than thumbnail-dominant like <see cref="CloudFileViewModel"/>:
/// a folder has no preview worth a quarter of the window, and the point of the shelf is to
/// see the whole of it at once. Icon, name, and the two numbers that actually decide where
/// you click - how many files and how much disk.
///
/// The two numbers arrive from the server as DIRECT contents only. The subtree totals are
/// worked out here, once per folder list, by <see cref="RollUp"/>: every folder and its
/// parent are already in hand, so a recursive query per request would be paying a server
/// for one loop over a list the client is holding anyway.
/// </remarks>
internal sealed class CloudFolderViewModel : INotifyPropertyChanged
{
    private bool isSelected;
    private bool isCurrent;
    private bool isDropTarget;
    private bool justReceived;
    private string icon = LucideIcons.folder;
    private string meta = "";

    /// <summary>A real folder, as the panel sent it.</summary>
    public CloudFolderViewModel(ModlightFolder folder)
    {
        Id = folder.Id ?? "";
        Name = string.IsNullOrWhiteSpace(folder.Name) ? folder.Slug ?? Id : folder.Name!;
        Slug = folder.Slug ?? "";
        ParentId = string.IsNullOrEmpty(folder.ParentId) ? null : folder.ParentId;
        IsPublic = folder.IsPublic;
        SortOrder = folder.SortOrder;
        Kind = CloudFolderKind.Folder;
        DirectCount = Math.Max(0, folder.FileCount);
        DirectSize = Math.Max(0, folder.Size);

        ApplyTotals(DirectCount, DirectSize, 0, 0);

        // Every folder the account has is built here, which makes this the one place a
        // file card can be told what its folder is called. Not the unfiled shelf below:
        // a file in no folder answers folderId null, so nothing would ever look it up.
        CloudFolderNames.Note(Id, Name);
    }

    private CloudFolderViewModel(string id, string name, int fileCount, long size)
    {
        Id = id;
        Name = name;
        Slug = "";
        ParentId = null;
        Kind = CloudFolderKind.Unfiled;
        DirectCount = Math.Max(0, fileCount);
        DirectSize = Math.Max(0, size);

        ApplyTotals(DirectCount, DirectSize, 0, 0);
    }

    /// <summary>The shelf holding everything that is in no folder.</summary>
    public static CloudFolderViewModel CreateUnfiled(string rootValue, int fileCount, long size) =>
        new(rootValue, Strings.MySpace_Unfiled, fileCount, size);

    /// <summary>The id the listing endpoint takes for this shelf. Never null or empty.</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>The slug its public gallery is addressed by, when it has one.</summary>
    public string Slug { get; }

    /// <summary>The folder this one sits in, or null when it sits at the top.</summary>
    public string? ParentId { get; }

    public bool IsPublic { get; }

    /// <summary>The order the panel lists them in, so both surfaces agree.</summary>
    public int SortOrder { get; }

    public CloudFolderKind Kind { get; }

    /// <summary>A real folder, so it can be uploaded into and shown in the panel.</summary>
    public bool IsRealFolder => Kind == CloudFolderKind.Folder;

    /// <summary>Files directly inside, as the server counted them.</summary>
    public int DirectCount { get; private set; }

    /// <summary>Bytes directly inside, as the server counted them.</summary>
    public long DirectSize { get; private set; }

    /// <summary>Files here and in everything nested below.</summary>
    public int FileCount { get; private set; }

    /// <summary>Bytes here and in everything nested below.</summary>
    public long Size { get; private set; }

    /// <summary>Folders nested directly below this one.</summary>
    public int SubfolderCount { get; private set; }

    /// <summary>Folders nested below at any depth - what a delete would take with it.</summary>
    public int DescendantCount { get; private set; }

    /// <summary>How far down the tree this sits. Zero at the top.</summary>
    public int Depth { get; private set; }

    /// <summary>
    /// The indent a side-list row draws its contents at.
    /// </summary>
    /// <remarks>
    /// On the row's CONTENT rather than on the row itself, so the list keeps one left
    /// edge and a hover still fills the full width. A row that starts further right on
    /// every level turns the list into a staircase of differently sized targets.
    /// </remarks>
    public Thickness ContentIndent => new(Depth * 14, 0, 0, 0);

    /// <summary>The glyph on the tile. Open when there is nothing inside, closed when there is.</summary>
    public string Icon
    {
        get => icon;
        private set
        {
            if (icon == value)
            {
                return;
            }

            icon = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The one line under the name: how many files, how much disk, how many subfolders.</summary>
    public string Meta
    {
        get => meta;
        private set
        {
            if (meta == value)
            {
                return;
            }

            meta = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The name plus everything the tile had to shorten.</summary>
    public string Tooltip => Name + "\n" + Meta;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// This is the folder being shown right now.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsSelected"/> because the two are different facts and the
    /// side list shows both: the folder you are IN is not on the shelf at all (you are
    /// looking at its contents), while the folder you have CLICKED is a tile on the shelf
    /// waiting for a second click or a menu.
    /// </remarks>
    public bool IsCurrent
    {
        get => isCurrent;
        set
        {
            if (isCurrent == value)
            {
                return;
            }

            isCurrent = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// A file has just landed in this folder, and the tile is saying so.
    /// </summary>
    /// <remarks>
    /// On for about a second and a half after an upload. The owner asked for it in those
    /// words - "maybe an animation on the folder the file jumped into" - and it earns its
    /// place by answering "where did that go" without a sentence: the toast in the corner
    /// names the folder, and this points at it.
    /// </remarks>
    public bool JustReceived
    {
        get => justReceived;
        set
        {
            if (justReceived == value)
            {
                return;
            }

            justReceived = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Files from Explorer are hovering over this tile and would land in it.</summary>
    public bool IsDropTarget
    {
        get => isDropTarget;
        set
        {
            if (isDropTarget == value)
            {
                return;
            }

            isDropTarget = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Take one file's worth off this folder's own tally, or add one on.
    /// </summary>
    /// <remarks>
    /// Applied locally instead of re-fetching the folder list, and that is a decision
    /// rather than a shortcut. Re-fetching would replace every view model on the shelf,
    /// which throws away the tile the user has selected, the tile a drag is hovering over
    /// and - the reason this exists - the tile that is in the middle of flashing to say a
    /// file just landed in it. The server's own numbers come back on the next reload;
    /// until then this is right to within whatever happened in another window.
    ///
    /// The caller re-runs <see cref="RollUp"/> afterwards, because a file added here also
    /// belongs to every folder above this one.
    /// </remarks>
    public void AdjustDirect(int files, long bytes)
    {
        DirectCount = Math.Max(0, DirectCount + files);
        DirectSize = Math.Max(0, DirectSize + bytes);
    }

    /// <summary>
    /// Add up every folder's subtree from the flat list the server sent.
    /// </summary>
    /// <remarks>
    /// One pass per folder, walking up the parent chain and adding its direct contents to
    /// every ancestor, which is O(folders x depth) on a list of seventeen. The visited set
    /// is not defensive tidiness: <c>parentId</c> is a string that arrived over the network,
    /// and a cycle in it would otherwise be an infinite loop on the UI thread - the client
    /// has no business trusting the server's tree to be a tree.
    /// </remarks>
    public static void RollUp(IReadOnlyList<CloudFolderViewModel> folders)
    {
        Dictionary<string, CloudFolderViewModel> byId = new(folders.Count, StringComparer.Ordinal);

        foreach (CloudFolderViewModel folder in folders)
        {
            byId[folder.Id] = folder;
        }

        Dictionary<string, int> subfolders = new(StringComparer.Ordinal);
        Dictionary<string, int> descendants = new(StringComparer.Ordinal);
        Dictionary<string, int> files = new(StringComparer.Ordinal);
        Dictionary<string, long> bytes = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (CloudFolderViewModel folder in folders)
        {
            files[folder.Id] = files.TryGetValue(folder.Id, out int f) ? f + folder.DirectCount : folder.DirectCount;
            bytes[folder.Id] = bytes.TryGetValue(folder.Id, out long b) ? b + folder.DirectSize : folder.DirectSize;

            seen.Clear();
            seen.Add(folder.Id);

            bool direct = true;
            int depth = 0;

            for (string? parentId = folder.ParentId;
                 parentId != null && byId.ContainsKey(parentId) && seen.Add(parentId);
                 parentId = byId[parentId].ParentId)
            {
                depth++;
                files[parentId] = files.TryGetValue(parentId, out int pf) ? pf + folder.DirectCount : folder.DirectCount;
                bytes[parentId] = bytes.TryGetValue(parentId, out long pb) ? pb + folder.DirectSize : folder.DirectSize;
                descendants[parentId] = descendants.TryGetValue(parentId, out int pd) ? pd + 1 : 1;

                if (direct)
                {
                    subfolders[parentId] = subfolders.TryGetValue(parentId, out int ps) ? ps + 1 : 1;
                    direct = false;
                }
            }

            folder.Depth = depth;
        }

        foreach (CloudFolderViewModel folder in folders)
        {
            folder.ApplyTotals(
                files.TryGetValue(folder.Id, out int f) ? f : folder.DirectCount,
                bytes.TryGetValue(folder.Id, out long b) ? b : folder.DirectSize,
                subfolders.TryGetValue(folder.Id, out int s) ? s : 0,
                descendants.TryGetValue(folder.Id, out int d) ? d : 0);
        }
    }

    private void ApplyTotals(int fileCount, long size, int subfolderCount, int descendantCount)
    {
        FileCount = fileCount;
        Size = size;
        SubfolderCount = subfolderCount;
        DescendantCount = descendantCount;

        // Open when there is nothing inside. The one place on the shelf where the glyph
        // itself carries information rather than decoration: an empty folder is the thing
        // you are usually looking for when you are looking at folders.
        Icon = fileCount == 0 && subfolderCount == 0 ? LucideIcons.folder_open : LucideIcons.folder;
        Meta = BuildMeta(fileCount, size, subfolderCount);

        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(SubfolderCount));
        OnPropertyChanged(nameof(DescendantCount));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>
    /// The tile's second line.
    /// </summary>
    /// <remarks>
    /// Counted rather than pluralised: "Files: 12" is the shape already used in the
    /// header, and it is the only shape that survives Polish, where the noun after a
    /// number takes three different forms depending on the number.
    /// </remarks>
    private static string BuildMeta(int fileCount, long size, int subfolderCount)
    {
        StringBuilder text = new();

        if (fileCount > 0)
        {
            text.Append(string.Format(Strings.MySpace_FileCount, fileCount))
                .Append("  ·  ")
                .Append(ModlightFormat.CloudSize(size));
        }
        else if (subfolderCount == 0)
        {
            text.Append(Strings.MySpace_FolderEmpty);
        }

        if (subfolderCount > 0)
        {
            if (text.Length > 0)
            {
                text.Append("  ·  ");
            }

            text.Append(string.Format(Strings.MySpace_SubfolderCount, subfolderCount));
        }

        return text.ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// One segment of the path across the top of My space.
/// </summary>
/// <remarks>
/// A flat list rebuilt on every move rather than a linked structure: the path is at most
/// as deep as the folder tree, it is thrown away whenever it changes, and the alternative
/// - keeping a stack of where the user has been - answers a different question ("back")
/// from the one a breadcrumb answers ("up").
/// </remarks>
internal sealed class FolderCrumbViewModel
{
    public FolderCrumbViewModel(string? id, string name, bool isCurrent, bool showSeparator)
    {
        Id = id;
        Name = name;
        IsCurrent = isCurrent;
        ShowSeparator = showSeparator;
    }

    /// <summary>The folder this segment goes to, or null for the top of the library.</summary>
    public string? Id { get; }

    public string Name { get; }

    /// <summary>The folder being shown. Marked, and not a way to go anywhere.</summary>
    public bool IsCurrent { get; }

    /// <summary>Everything but the first segment is preceded by a chevron.</summary>
    public bool ShowSeparator { get; }
}

/// <summary>
/// What every folder of the account's is called, by id.
/// </summary>
/// <remarks>
/// GET /api/uploads answers <c>folderName: null</c> on purpose - it would otherwise
/// repeat one string on every row of a folder - and expects the client to resolve names
/// from GET /api/folders, which this app already calls to draw its folder tiles. So the
/// mapping happens here and the server is asked for nothing.
///
/// A static index rather than a name copied onto each card as it is built, because the
/// two lists are fetched CONCURRENTLY: LoadFoldersAsync runs beside the listing, neither
/// reliably arrives first, and whichever half lost the race would leave its cards blank
/// for ever. Cards read this at bind time and follow <see cref="Changed"/> afterwards.
///
/// Names are merged, never removed. A folder that is gone has no files left pointing at
/// it, so a stale entry is a row nobody can read; whereas clearing the map on each
/// reload would blank every card for as long as the new list takes to arrive. The map is
/// therefore bounded by how many folders the account has ever had in one session, which
/// is a number in the tens.
/// </remarks>
internal static class CloudFolderNames
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, string> names = new(StringComparer.Ordinal);

    /// <summary>A folder list has arrived and some card may now be able to name itself.</summary>
    public static event EventHandler? Changed;

    /// <summary>Record what one folder is called. Silent when nothing actually changed.</summary>
    public static void Note(string id, string name)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
        {
            return;
        }

        lock (gate)
        {
            if (names.TryGetValue(id, out string? existing) && existing == name)
            {
                return;
            }

            names[id] = name;
        }

        // Outside the lock: a handler on the other side of this raises PropertyChanged,
        // which reaches bindings, which is not work to do while holding a lock that the
        // next folder in the same loop is about to want.
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>What that folder is called, or empty for no folder and for one not yet known.</summary>
    public static string Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "";
        }

        lock (gate)
        {
            return names.TryGetValue(id!, out string? name) ? name : "";
        }
    }
}

/// <summary>
/// The families a file's type badge can belong to, finer than the server's <c>kind</c>.
/// </summary>
/// <remarks>
/// <c>kind</c> exists to drive a SQL filter and is deliberately coarse: a PDF, a
/// spreadsheet, a shell script and a README are one bucket called "document". That is
/// fine for a dropdown and useless on a tile, where the whole job is telling those four
/// apart at a glance. This is the display axis, and it is the same one the web panel
/// uses - see <c>TypeFamily</c> in components/dashboard/media.ts.
/// </remarks>
internal enum CloudTypeFamily
{
    Image,
    Video,
    Audio,
    Pdf,
    Document,
    Data,
    Code,
    Archive,
    Font,
    App,
    Binary
}

/// <summary>
/// What a file with no picture of its own is drawn as.
/// </summary>
/// <remarks>
/// A port of the web panel's <c>typeFamily</c> / <c>FAMILY_PLATE</c> / <c>FAMILY_EMBLEM</c>
/// tables, so one file wears one colour and one emblem in the app and in the browser
/// rather than two conventions the user has to learn twice.
///
/// THE PLATE IS A FIXED COLOUR, not a theme token, and that is the panel's decision
/// rather than a shortcut here. The point of the plate is that it is opaque: the surface
/// underneath stops mattering, so the only contrast left to guarantee is white-on-plate,
/// which is a constant across all fourteen skins in both modes. A plate painted from a
/// token would be the same colour for every family and the badge would stop answering
/// the question it exists for. The one part that does take a token is the rim, which is
/// drawn in the foreground colour: an opaque dark plate on a dark skin is otherwise a
/// rectangle with no silhouette, and a rim that inverts with the skin is what keeps the
/// sheet looking like a sheet. Everything outside the plate - the caption, the tile - is
/// on tokens as usual.
/// </remarks>
internal static class CloudFileType
{
    private static readonly HashSet<string> ArchiveMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/zip",
        "application/x-7z-compressed",
        "application/vnd.rar",
        "application/x-rar-compressed",
        "application/gzip",
        "application/x-tar",
        "application/x-bzip2",
        "application/x-xz",
        "application/zstd"
    };

    private static readonly HashSet<string> DocumentMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/msword",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.oasis.opendocument.text",
        "application/vnd.oasis.opendocument.presentation",
        "application/rtf",
        "application/epub+zip"
    };

    /// <summary>Rows and columns. Claimed BEFORE the document set, which lists Excel too.</summary>
    private static readonly HashSet<string> DataMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/csv",
        "text/tab-separated-values",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.sqlite3"
    };

    private static readonly HashSet<string> CodeMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "text/css",
        "text/javascript",
        "text/xml",
        "application/xhtml+xml",
        "application/javascript",
        "application/x-javascript",
        "application/json",
        "application/xml",
        "application/x-sh",
        "application/sql",
        "application/jwt",
        "application/wasm"
    };

    /// <summary>Programs, not documents. .apk and .jar are zips underneath; nobody cares.</summary>
    private static readonly HashSet<string> AppMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.microsoft.portable-executable",
        "application/x-msdownload",
        "application/x-msi",
        "application/vnd.android.package-archive",
        "application/java-archive",
        "application/vnd.debian.binary-package",
        "application/x-rpm",
        "application/x-apple-diskimage"
    };

    /// <summary>
    /// The families an extension can rescue when the stored MIME type says nothing.
    /// </summary>
    /// <remarks>
    /// Deliberately short. The server's sniffer owns the full table and has already run:
    /// it is what turned a .avif that Windows filed as application/octet-stream into an
    /// image. This is only the fallback for a row whose stored type is still the generic
    /// one - most of an imported library - and it only needs the families
    /// the server's coarse <c>kind</c> cannot separate, which is why it is programs,
    /// archives, source and spreadsheets rather than a second copy of sniff.ts.
    /// </remarks>
    private static readonly Dictionary<string, CloudTypeFamily> ExtFamilies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pdf"] = CloudTypeFamily.Pdf,
            ["zip"] = CloudTypeFamily.Archive,
            ["7z"] = CloudTypeFamily.Archive,
            ["rar"] = CloudTypeFamily.Archive,
            ["gz"] = CloudTypeFamily.Archive,
            ["tar"] = CloudTypeFamily.Archive,
            ["bz2"] = CloudTypeFamily.Archive,
            ["xz"] = CloudTypeFamily.Archive,
            ["zst"] = CloudTypeFamily.Archive,
            ["exe"] = CloudTypeFamily.App,
            ["msi"] = CloudTypeFamily.App,
            ["apk"] = CloudTypeFamily.App,
            ["jar"] = CloudTypeFamily.App,
            ["deb"] = CloudTypeFamily.App,
            ["rpm"] = CloudTypeFamily.App,
            ["dmg"] = CloudTypeFamily.App,
            ["appimage"] = CloudTypeFamily.App,
            ["csv"] = CloudTypeFamily.Data,
            ["tsv"] = CloudTypeFamily.Data,
            ["xls"] = CloudTypeFamily.Data,
            ["xlsx"] = CloudTypeFamily.Data,
            ["ods"] = CloudTypeFamily.Data,
            ["db"] = CloudTypeFamily.Data,
            ["sqlite"] = CloudTypeFamily.Data,
            ["html"] = CloudTypeFamily.Code,
            ["htm"] = CloudTypeFamily.Code,
            ["css"] = CloudTypeFamily.Code,
            ["js"] = CloudTypeFamily.Code,
            ["mjs"] = CloudTypeFamily.Code,
            ["ts"] = CloudTypeFamily.Code,
            ["tsx"] = CloudTypeFamily.Code,
            ["jsx"] = CloudTypeFamily.Code,
            ["json"] = CloudTypeFamily.Code,
            ["xml"] = CloudTypeFamily.Code,
            ["yml"] = CloudTypeFamily.Code,
            ["yaml"] = CloudTypeFamily.Code,
            ["sh"] = CloudTypeFamily.Code,
            ["bat"] = CloudTypeFamily.Code,
            ["cmd"] = CloudTypeFamily.Code,
            ["ps1"] = CloudTypeFamily.Code,
            ["py"] = CloudTypeFamily.Code,
            ["rb"] = CloudTypeFamily.Code,
            ["php"] = CloudTypeFamily.Code,
            ["lua"] = CloudTypeFamily.Code,
            ["sql"] = CloudTypeFamily.Code,
            ["c"] = CloudTypeFamily.Code,
            ["cpp"] = CloudTypeFamily.Code,
            ["h"] = CloudTypeFamily.Code,
            ["cs"] = CloudTypeFamily.Code,
            ["go"] = CloudTypeFamily.Code,
            ["rs"] = CloudTypeFamily.Code,
            ["java"] = CloudTypeFamily.Code,
            ["ttf"] = CloudTypeFamily.Font,
            ["otf"] = CloudTypeFamily.Font,
            ["woff"] = CloudTypeFamily.Font,
            ["woff2"] = CloudTypeFamily.Font,
            ["txt"] = CloudTypeFamily.Document,
            ["log"] = CloudTypeFamily.Document,
            ["md"] = CloudTypeFamily.Document,
            ["rtf"] = CloudTypeFamily.Document,
            ["doc"] = CloudTypeFamily.Document,
            ["docx"] = CloudTypeFamily.Document,
            ["odt"] = CloudTypeFamily.Document,
            ["ppt"] = CloudTypeFamily.Document,
            ["pptx"] = CloudTypeFamily.Document,
            ["odp"] = CloudTypeFamily.Document,
            ["epub"] = CloudTypeFamily.Document,
            ["srt"] = CloudTypeFamily.Document
        };

    /// <summary>
    /// Which family a row belongs to.
    /// </summary>
    /// <remarks>
    /// The MIME type decides whenever it says anything at all, in the panel's own order:
    /// the top-level type first, then PDF, then rows-and-columns before documents,
    /// source before archives, archives before programs. Only when the answer would be
    /// "we know nothing" does the extension get a turn, and only then does the server's
    /// coarse <c>kind</c> get the last word - it is never worse than nothing, and for
    /// image, video and audio it is the sniffer's own verdict.
    /// </remarks>
    public static CloudTypeFamily Resolve(string? mime, string? ext, string? kind)
    {
        CloudTypeFamily family = FromMime(mime);

        if (family != CloudTypeFamily.Binary)
        {
            return family;
        }

        if (!string.IsNullOrEmpty(ext) && ExtFamilies.TryGetValue(ext!, out CloudTypeFamily byExt))
        {
            return byExt;
        }

        return FromKind(kind);
    }

    private static CloudTypeFamily FromMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime))
        {
            return CloudTypeFamily.Binary;
        }

        string m = mime!.Trim();
        int slash = m.IndexOf('/');
        string top = slash < 0 ? m : m.Substring(0, slash);

        if (top.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            return CloudTypeFamily.Image;
        }

        if (top.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            return CloudTypeFamily.Video;
        }

        if (top.Equals("audio", StringComparison.OrdinalIgnoreCase))
        {
            return CloudTypeFamily.Audio;
        }

        if (top.Equals("font", StringComparison.OrdinalIgnoreCase))
        {
            return CloudTypeFamily.Font;
        }

        if (m.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return CloudTypeFamily.Pdf;
        }

        if (DataMimes.Contains(m))
        {
            return CloudTypeFamily.Data;
        }

        if (CodeMimes.Contains(m) ||
            m.StartsWith("text/x-", StringComparison.OrdinalIgnoreCase) ||
            m.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
            m.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return CloudTypeFamily.Code;
        }

        if (ArchiveMimes.Contains(m))
        {
            return CloudTypeFamily.Archive;
        }

        if (AppMimes.Contains(m))
        {
            return CloudTypeFamily.App;
        }

        // application/x-subrip is subtitles: text a person reads, filed nowhere else.
        if (top.Equals("text", StringComparison.OrdinalIgnoreCase) ||
            DocumentMimes.Contains(m) ||
            m.Equals("application/x-subrip", StringComparison.OrdinalIgnoreCase))
        {
            return CloudTypeFamily.Document;
        }

        return CloudTypeFamily.Binary;
    }

    private static CloudTypeFamily FromKind(string? kind) => kind switch
    {
        "image" => CloudTypeFamily.Image,
        "video" => CloudTypeFamily.Video,
        "audio" => CloudTypeFamily.Audio,
        "archive" => CloudTypeFamily.Archive,
        // A paste is text somebody typed. Without a MIME type that is all anyone knows.
        "paste" => CloudTypeFamily.Document,
        "document" => CloudTypeFamily.Document,
        _ => CloudTypeFamily.Binary
    };

    /// <summary>The plate colour, the panel's exact hues. Immutable, so it is shared safely.</summary>
    private static readonly IReadOnlyDictionary<CloudTypeFamily, IBrush> Plates =
        new Dictionary<CloudTypeFamily, IBrush>
        {
            [CloudTypeFamily.Image] = Frozen("#6D28D9"),
            [CloudTypeFamily.Video] = Frozen("#BE123C"),
            [CloudTypeFamily.Audio] = Frozen("#B45309"),
            [CloudTypeFamily.Pdf] = Frozen("#B91C1C"),
            [CloudTypeFamily.Document] = Frozen("#0369A1"),
            [CloudTypeFamily.Data] = Frozen("#047857"),
            [CloudTypeFamily.Code] = Frozen("#4338CA"),
            [CloudTypeFamily.Archive] = Frozen("#C2410C"),
            [CloudTypeFamily.Font] = Frozen("#A21CAF"),
            [CloudTypeFamily.App] = Frozen("#475569"),
            [CloudTypeFamily.Binary] = Frozen("#52525B")
        };

    private static IBrush Frozen(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    public static IBrush Plate(CloudTypeFamily family) =>
        Plates.TryGetValue(family, out IBrush? brush) ? brush : Plates[CloudTypeFamily.Binary];

    /// <summary>
    /// The glyph printed on the plate.
    /// </summary>
    /// <remarks>
    /// A different table from <see cref="CloudFileViewModel.Icon"/>, and deliberately so -
    /// the panel keeps two for the same reason. The plate is already a sheet of paper, so
    /// putting the file-shaped glyph on it draws a small file inside a large file, which
    /// reads as a rendering mistake rather than as a type. Inside the plate the emblem has
    /// to be the subject alone: a frame, a note, a bracket.
    /// </remarks>
    public static string Emblem(CloudTypeFamily family) => family switch
    {
        CloudTypeFamily.Image => LucideIcons.image,
        CloudTypeFamily.Video => LucideIcons.film,
        CloudTypeFamily.Audio => LucideIcons.music,
        CloudTypeFamily.Pdf => LucideIcons.book_text,
        CloudTypeFamily.Document => LucideIcons.align_left,
        CloudTypeFamily.Data => LucideIcons.table_2,
        CloudTypeFamily.Code => LucideIcons.code,
        CloudTypeFamily.Archive => LucideIcons.archive,
        CloudTypeFamily.Font => LucideIcons.type,
        CloudTypeFamily.App => LucideIcons.app_window,
        _ => LucideIcons.binary
    };

    /// <summary>Short uppercase type label for the plate: PNG, MP4, PDF.</summary>
    public static string Label(string? ext, string? mime)
    {
        if (!string.IsNullOrEmpty(ext))
        {
            return Clip(ext!.ToUpperInvariant());
        }

        if (string.IsNullOrEmpty(mime))
        {
            return "FILE";
        }

        int slash = mime!.IndexOf('/');
        string sub = slash < 0 || slash == mime!.Length - 1 ? "" : mime!.Substring(slash + 1);
        int cut = sub.IndexOfAny(new[] { '.', '+', ';' });

        if (cut >= 0)
        {
            sub = sub.Substring(0, cut);
        }

        return sub.Length == 0 ? "FILE" : Clip(sub.ToUpperInvariant());
    }

    private static string Clip(string label) => label.Length <= 8 ? label : label.Substring(0, 8);

    /// <summary>
    /// How large the label may be drawn.
    /// </summary>
    /// <remarks>
    /// Real extensions are three to five characters, but "markdown" and "webmanifest"
    /// exist, so the size steps down rather than cutting a word in half - a trimmed label
    /// reads as a typo, and the panel steps it down for the same reason.
    /// </remarks>
    public static double LabelSize(string label) => label.Length switch
    {
        <= 3 => 13,
        4 => 11,
        5 => 10,
        _ => 8
    };
}
