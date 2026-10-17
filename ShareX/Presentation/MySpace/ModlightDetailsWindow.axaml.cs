#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>One labelled line in the details window.</summary>
/// <remarks>
/// A pair rather than a control per fact, because the list is data: which facts a file
/// has depends on the file - not every one has dimensions, and a row reading
/// "Dimensions: -" is a row that wastes a line to say nothing.
/// </remarks>
internal sealed class DetailRowViewModel
{
    public DetailRowViewModel(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public string Value { get; }
}

/// <summary>
/// Everything the listing already knows about one file, laid out to be read.
/// </summary>
/// <remarks>
/// A window rather than the panel's right-hand drawer, which is the one place this
/// deliberately does not copy the web layout. A drawer that slides over the grid is a web
/// idiom; on Windows the same job is a properties dialog, and making it one also means
/// the grid keeps its scroll position and its selection while the dialog is up.
///
/// Nothing here is fetched. Every figure comes from the row the grid was drawn from, so
/// opening this costs no request and cannot spend a view - which matters, because a
/// one-time link that burned itself when its owner looked at its properties would be a
/// genuinely destructive way to inspect a file.
/// </remarks>
public partial class ModlightDetailsWindow : Window
{
    private string copyLink = "";
    private string openLink = "";

    public ModlightDetailsWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
    }

    /// <summary>Show one file's details. Returns when the window is closed.</summary>
    /// <param name="folderName">The folder's name, or empty for "no folder".</param>
    internal static Task ShowAsync(Window owner, CloudFileViewModel file, string folderName)
    {
        ModlightDetailsWindow window = new()
        {
            Title = file.Name
        };

        window.Fill(file, folderName);
        return window.ShowDialog(owner);
    }

    private void Fill(CloudFileViewModel file, string folderName)
    {
        ModlightUpload row = file.Row;

        copyLink = file.CopyLink;
        openLink = file.OpenLink;

        FileName.Text = file.Name;
        Headline.Text = Headline0(row);

        List<DetailRowViewModel> facts = new()
        {
            new DetailRowViewModel(Strings.MySpace_DetailsType,
                string.IsNullOrEmpty(row.MimeType) ? "-" : row.MimeType!),
            new DetailRowViewModel(Strings.MySpace_DetailsSize, ModlightFormat.CloudSize(row.Size))
        };

        // Only when both are known. Half a pair of dimensions is not a dimension.
        if (row.Width > 0 && row.Height > 0)
        {
            facts.Add(new DetailRowViewModel(Strings.MySpace_DetailsDimensions,
                row.Width + " × " + row.Height + " px"));
        }

        if (row.CreatedAt != null)
        {
            facts.Add(new DetailRowViewModel(Strings.MySpace_DetailsUploaded,
                row.CreatedAt.Value.LocalDateTime.ToString("f", CultureInfo.CurrentCulture)));
        }

        facts.Add(new DetailRowViewModel(Strings.MySpace_DetailsCode, file.Code));
        facts.Add(new DetailRowViewModel(Strings.MySpace_DetailsFolder,
            folderName.Length > 0 ? folderName : Strings.MySpace_Unfiled));
        facts.Add(new DetailRowViewModel(Strings.MySpace_DetailsVisibility,
            file.Published ? Strings.MySpace_VisibilityPublic : Strings.MySpace_VisibilityPrivate));

        Facts.ItemsSource = facts;

        // A trashed row has no working links, so it is given none rather than five that
        // 404 - which is the same choice the server makes when it withholds them.
        Links.ItemsSource = file.IsTrashed
            ? new List<DetailRowViewModel>()
            : new List<DetailRowViewModel>
            {
                new(Strings.MySpace_LinkFormatDirect, file.LinkFormat(CloudLinkFormat.Direct)),
                new(Strings.MySpace_LinkFormatRaw, file.LinkFormat(CloudLinkFormat.Raw)),
                new(Strings.MySpace_LinkFormatMarkdown, file.LinkFormat(CloudLinkFormat.Markdown)),
                new(Strings.MySpace_LinkFormatBBCode, file.LinkFormat(CloudLinkFormat.BBCode)),
                new(Strings.MySpace_LinkFormatHtml, file.LinkFormat(CloudLinkFormat.Html))
            };

        Views.Text = row.Views.ToString(CultureInfo.CurrentCulture);
        Downloads.Text = row.Downloads.ToString(CultureInfo.CurrentCulture);

        List<DetailRowViewModel> gates = new();

        if (row.ExpiresAt != null)
        {
            gates.Add(new DetailRowViewModel("", string.Format(Strings.MySpace_BadgeExpires,
                row.ExpiresAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))));
        }

        if (row.MaxViews != null)
        {
            gates.Add(new DetailRowViewModel("", string.Format(Strings.MySpace_BadgeMaxViews,
                row.Views, row.MaxViews.Value)));
        }

        if (row.HasPassword)
        {
            gates.Add(new DetailRowViewModel("", Strings.MySpace_BadgePassword));
        }

        Gates.ItemsSource = gates;
        NoGates.IsVisible = gates.Count == 0;

        CopyLinkButton.IsEnabled = file.HasLink;
        OpenButton.IsEnabled = file.CanOpen;
    }

    /// <summary>The one grey line under the name: what kind of thing this is, and how big.</summary>
    private static string Headline0(ModlightUpload row)
    {
        string ext = string.IsNullOrEmpty(row.Ext) ? "" : row.Ext!.ToUpperInvariant();
        string size = ModlightFormat.CloudSize(row.Size);

        string text = ext.Length > 0 ? ext + "  ·  " + size : size;

        if (row.Width > 0 && row.Height > 0)
        {
            text += "  ·  " + row.Width + " × " + row.Height;
        }

        return text;
    }

    private void OnCopyRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DetailRowViewModel row } && row.Value.Length > 0)
        {
            ClipboardHelpers.CopyText(row.Value);
        }
    }

    private void OnCopyLinkClick(object? sender, RoutedEventArgs e)
    {
        if (copyLink.Length > 0)
        {
            ClipboardHelpers.CopyText(copyLink);
        }
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (openLink.Length > 0)
        {
            URLHelpers.OpenURL(openLink);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
