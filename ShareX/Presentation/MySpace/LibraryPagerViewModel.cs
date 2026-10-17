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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ShareX;

/// <summary>One button on the page strip, or the gap between two runs of them.</summary>
internal sealed class LibraryPageStep
{
    public LibraryPageStep(int page, bool current)
    {
        Page = page;
        IsCurrent = current;
        Label = page > 0 ? page.ToString("N0") : "\u2026";
    }

    /// <summary>The page this goes to, or zero for the gap.</summary>
    public int Page { get; }

    public bool IsGap => Page <= 0;

    public bool IsPage => Page > 0;

    public bool IsCurrent { get; }

    public string Label { get; }
}

/// <summary>One entry in the "show X per page" list.</summary>
internal sealed class LibraryPageSize
{
    public LibraryPageSize(int size)
    {
        Size = size;
        Label = string.Format(Strings.MySpace_PerPage, size);
    }

    public int Size { get; }

    public string Label { get; }
}

/// <summary>
/// The bar under the grid: which page of the library is showing, and how to get to
/// another one.
/// </summary>
/// <remarks>
/// Everything it says comes from what the SERVER answered - page, page size and total are
/// read off the response rather than from what was asked for - because the server clamps
/// both, and a bar counting in a size the server refused would number pages that do not
/// exist. That is the same rule <see cref="MySpaceViewModel"/> already followed when it
/// decided a listing had ended.
///
/// Free of Avalonia so that nothing about the shape of the bar can reach into the paging
/// arithmetic, which lives in <see cref="LibraryPager"/> and is pinned by tests. This is
/// only the part with labels on it.
/// </remarks>
internal sealed class LibraryPagerViewModel : INotifyPropertyChanged
{
    private readonly Action<int> goToPage;
    private readonly Action<int> setPageSize;

    /// <summary>
    /// True while the selected size is being written from a server answer.
    /// </summary>
    /// <remarks>
    /// Without it, showing what the server said would call back into "the user picked a
    /// size", which reloads the page, which answers with a size, which writes it here
    /// again. The loop is the reason a flag like this exists and not a preference for one.
    /// </remarks>
    private bool echoing;

    private int page = 1;
    private int perPage = LibraryPager.DefaultPageSize;
    private int total;
    private string showingText = "";
    private string pageOfText = "";
    private string jumpText = "";
    private LibraryPageSize? selectedSize;

    public LibraryPagerViewModel(Action<int> goToPage, Action<int> setPageSize)
    {
        this.goToPage = goToPage;
        this.setPageSize = setPageSize;

        foreach (int size in LibraryPager.PageSizes)
        {
            Sizes.Add(new LibraryPageSize(size));
        }

        selectedSize = Find(perPage);
        Update(1, perPage, 0);
    }

    /// <summary>The numbered buttons and the gaps between them.</summary>
    public ObservableCollection<LibraryPageStep> Steps { get; } = new();

    /// <summary>The sizes offered by the "show X per page" list.</summary>
    public ObservableCollection<LibraryPageSize> Sizes { get; } = new();

    /// <summary>There are files, so there is something to say about them.</summary>
    public bool IsVisible => total > 0;

    /// <summary>There is more than one page, so the strip and the arrows are worth showing.</summary>
    public bool HasPages => PageCount > 1;

    /// <summary>
    /// The listing is longer than the smallest page size, so choosing one is a real choice.
    /// </summary>
    /// <remarks>The panel hides the selector on the same test, for the same reason.</remarks>
    public bool ShowSizes => total > LibraryPager.PageSizes[0];

    /// <summary>
    /// The strip is eliding pages, so there are pages no button can reach.
    /// </summary>
    /// <remarks>
    /// The jump box appears with the gap and not before it: while every page has a button,
    /// a box asking for a number is a second way to do a thing that is already one click.
    /// </remarks>
    public bool ShowJump => PageCount > LibraryPager.StripLimit;

    public bool CanPrevious => page > 1;

    public bool CanNext => page < PageCount;

    public int PageCount => LibraryPager.PageCount(total, perPage);

    /// <summary>"1-120 of 6,926".</summary>
    public string ShowingText
    {
        get => showingText;
        private set => SetField(ref showingText, value);
    }

    /// <summary>"Page 6 of 58", for the jump box that has no room for a sentence.</summary>
    public string PageOfText
    {
        get => pageOfText;
        private set => SetField(ref pageOfText, value);
    }

    /// <summary>What is typed in the jump box.</summary>
    public string JumpText
    {
        get => jumpText;
        set => SetField(ref jumpText, value);
    }

    /// <summary>The chosen page size. Writing it is a user action; echoing it is not.</summary>
    public LibraryPageSize? SelectedSize
    {
        get => selectedSize;
        set
        {
            if (ReferenceEquals(selectedSize, value))
            {
                return;
            }

            selectedSize = value;
            OnPropertyChanged();

            if (!echoing && value != null && value.Size != perPage)
            {
                setPageSize(value.Size);
            }
        }
    }

    /// <summary>Take the page, the size and the total from the answer that just arrived.</summary>
    public void Update(int currentPage, int currentPerPage, int currentTotal)
    {
        total = Math.Max(0, currentTotal);
        perPage = LibraryPager.ClampPageSize(currentPerPage > 0 ? currentPerPage : perPage);
        page = LibraryPager.ClampPage(currentPage, LibraryPager.PageCount(total, perPage));

        LibraryPageSize? size = Find(perPage);

        if (!ReferenceEquals(size, selectedSize))
        {
            echoing = true;
            SelectedSize = size;
            echoing = false;
        }

        int first = LibraryPager.First(page, perPage);
        int last = LibraryPager.Last(page, perPage, total);

        ShowingText = total > 0
            ? string.Format(Strings.MySpace_PageShowing, first.ToString("N0"), last.ToString("N0"), total.ToString("N0"))
            : "";

        PageOfText = string.Format(Strings.MySpace_PageOf, page.ToString("N0"), PageCount.ToString("N0"));
        JumpText = "";

        Steps.Clear();

        foreach (int step in LibraryPager.Strip(page, PageCount))
        {
            Steps.Add(new LibraryPageStep(step, step == page));
        }

        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(ShowSizes));
        OnPropertyChanged(nameof(ShowJump));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(PageCount));
    }

    /// <summary>Nothing is listed. Used when the account goes away.</summary>
    public void Reset() => Update(1, perPage, 0);

    public void Previous() => GoTo(page - 1);

    public void Next() => GoTo(page + 1);

    /// <summary>Go to a page, clamped to one that exists. A no-op for the page already shown.</summary>
    public void GoTo(int target)
    {
        int clamped = LibraryPager.ClampPage(target, PageCount);

        if (clamped == page)
        {
            return;
        }

        goToPage(clamped);
    }

    /// <summary>Take whatever is in the jump box, if it is a page number.</summary>
    /// <remarks>
    /// Silent on rubbish rather than complaining about it: the box is three characters
    /// wide next to the page it names, so a typed "x" is visibly not a page and an error
    /// message would be telling somebody what they can already see.
    /// </remarks>
    public void Jump()
    {
        if (int.TryParse(jumpText.Trim(), out int target) && target > 0)
        {
            GoTo(target);
        }

        JumpText = "";
    }

    private LibraryPageSize? Find(int size)
    {
        foreach (LibraryPageSize option in Sizes)
        {
            if (option.Size == size)
            {
                return option;
            }
        }

        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
