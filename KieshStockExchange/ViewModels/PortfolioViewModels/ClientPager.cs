using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

/// <summary>
/// Reusable client-side pager state for portfolio tables. The full row list
/// lives in memory; <see cref="SetSource"/> updates the slice exposed via
/// <see cref="PagedItems"/>. Mirrors the bindable surface that the admin
/// <c>TablePagerView</c> already consumes, so the same control works.
/// </summary>
public sealed partial class ClientPager<T> : ObservableObject
{
    [ObservableProperty] private int _pageNumber;
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _totalRows;
    [ObservableProperty] private ObservableCollection<T> _pagedItems = new();

    public IReadOnlyList<int> AvailablePageSizes { get; } = new[] { 25, 50, 100, 200 };

    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalRows / Math.Max(1, PageSize)));
    public int CurrentPageDisplay => PageNumber + 1;
    public bool CanGoPrev => PageNumber > 0;
    public bool CanGoNext => PageNumber + 1 < TotalPages;
    public string PagerSummary => TotalRows == 0
        ? "No rows"
        : $"Page {CurrentPageDisplay} of {TotalPages} · {TotalRows:N0} rows";

    public List<int> VisiblePages => ComputeVisiblePages();

    public ICommand GoToPageCommand { get; }
    public IRelayCommand GoPrevCommand { get; }
    public IRelayCommand GoNextCommand { get; }

    private IReadOnlyList<T> _allItems = Array.Empty<T>();

    public ClientPager()
    {
        GoToPageCommand = new RelayCommand<int>(page =>
        {
            PageNumber = Math.Max(0, page - 1);
            Resync();
        });
        GoPrevCommand = new RelayCommand(() => { if (CanGoPrev) { PageNumber--; Resync(); } });
        GoNextCommand = new RelayCommand(() => { if (CanGoNext) { PageNumber++; Resync(); } });
    }

    /// <summary>Replace the backing list and re-slice into <see cref="PagedItems"/>.
    /// Clamps the current page if it would fall off the end of the new list.</summary>
    public void SetSource(IReadOnlyList<T> items)
    {
        _allItems = items ?? Array.Empty<T>();
        TotalRows = _allItems.Count;
        if (PageNumber >= TotalPages) PageNumber = Math.Max(0, TotalPages - 1);
        Resync();
    }

    private void Resync()
    {
        PagedItems.Clear();
        var skip = PageNumber * PageSize;
        for (int i = 0; i < PageSize && skip + i < _allItems.Count; i++)
            PagedItems.Add(_allItems[skip + i]);
        NotifyPagerProperties();
    }

    private List<int> ComputeVisiblePages()
    {
        var pages = new HashSet<int>();
        int current = CurrentPageDisplay;
        int total = TotalPages;
        pages.Add(1);
        if (total > 1) pages.Add(total);
        for (int i = current - 2; i <= current + 2; i++)
            if (i > 1 && i < total) pages.Add(i);
        return pages.OrderBy(x => x).ToList();
    }

    private void NotifyPagerProperties()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CurrentPageDisplay));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PagerSummary));
        OnPropertyChanged(nameof(VisiblePages));
    }

    partial void OnPageSizeChanged(int value)
    {
        PageNumber = 0;
        Resync();
    }
}
