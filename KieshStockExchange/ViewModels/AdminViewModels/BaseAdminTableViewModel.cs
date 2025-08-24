using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public abstract partial class BaseTableViewModel<TItem> : BaseViewModel
{
    protected readonly IDataBaseService _dbService;

    // Backing storage for all rows
    protected List<TItem> AllItems = new();

    [ObservableProperty]
    private ObservableCollection<TItem> _pagedItems = new();

    [ObservableProperty] private int _pageNumber;

    /// <summary>How many rows to show per page</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>Total number of pages</summary>
    public int TotalPages =>
        (int)Math.Ceiling((double)(AllItems?.Count ?? 0) / PageSize);

    /// <summary>Which page-buttons to show in the pager</summary>
    public List<int> VisiblePageNumbers
    {
        get
        {
            var pages = new HashSet<int>();
            int current = PageNumber + 1;
            int total = TotalPages;

            pages.Add(1);
            if (total > 1) pages.Add(total);

            for (int i = current - 2; i <= current + 2; i++)
                if (i > 1 && i < total)
                    pages.Add(i);

            return pages.OrderBy(x => x).ToList();
        }
    }

    /// <summary>Command to jump to a given page</summary>
    public ICommand GoToPageCommand { get; }

    protected BaseTableViewModel(IDataBaseService dbService)
    {
        _dbService = dbService;
        GoToPageCommand = new Command<int>(page =>
        {
            PageNumber = page - 1;
            RefreshPagedItems();
        });
    }

    /// <summary>Call this once on startup to load data and seed the first page.</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            AllItems = await LoadItemsAsync();
            PageNumber = 0;
            RefreshPagedItems();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Derived classes implement this to fetch & map their rows.</summary>
    protected abstract Task<List<TItem>> LoadItemsAsync();

    /// <summary>Re-compute which items appear on the current page.</summary>
    private void RefreshPagedItems()
    {
        PagedItems.Clear();
        if (AllItems == null) return;

        int start = PageNumber * PageSize;
        for (int i = 0; i < PageSize && start + i < AllItems.Count; i++)
            PagedItems.Add(AllItems[start + i]);

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(VisiblePageNumbers));
        Debug.WriteLine($"Paged {PagedItems.Count} items for page {PageNumber + 1}/{TotalPages}");
    }
}

