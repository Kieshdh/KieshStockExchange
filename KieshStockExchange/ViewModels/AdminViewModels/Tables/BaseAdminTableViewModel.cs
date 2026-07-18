using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public abstract partial class BaseTableViewModel<TItem> : BaseViewModel, ILazyTab
{
    // Adaptive page sizing: never fewer than this, even on a tiny window.
    private const int MinPageSize = 20;
    // Approx rendered height of one table row in DIPs; the MinPageSize floor and the
    // CollectionView's own scrolling absorb any error in this estimate.
    private const double RowHeightPx = 40;

    #region Page properties
    [ObservableProperty] private ObservableCollection<TItem> _pagedItems = new();
    [ObservableProperty] private int _pageNumber;
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _totalRows;

    public IReadOnlyList<int> AvailablePageSizes { get; } = new[] { 25, 50, 100, 200 };

    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalRows / Math.Max(1, PageSize)));

    public int CurrentPageDisplay => PageNumber + 1;

    public bool CanGoPrev => PageNumber > 0;
    public bool CanGoNext => PageNumber + 1 < TotalPages;

    public string PagerSummary => $"Page {CurrentPageDisplay} of {TotalPages} · {TotalRows:N0} rows";

    public List<int> VisiblePages => PagerMath.ComputeVisiblePages(CurrentPageDisplay, TotalPages);
    #endregion

    #region Sort / filter state
    [ObservableProperty] private string? _sortKey;
    [ObservableProperty] private bool _sortDesc = true;
    protected string? CurrentFilter;
    #endregion

    #region Services, commands, constructor
    protected readonly IDataBaseService _db;
    protected readonly ILogger _logger;
    private CancellationTokenSource _loadCts = new();
    private bool _initialized;

    public ICommand GoToPageCommand { get; }
    public IAsyncRelayCommand GoPrevCommand { get; }
    public IAsyncRelayCommand GoNextCommand { get; }
    public IAsyncRelayCommand<string> ToggleSortCommand { get; }

    protected BaseTableViewModel(IDataBaseService dbService, ILogger? logger = null)
    {
        _db = dbService;
        _logger = logger ?? NullLogger.Instance;

        GoToPageCommand = new Command<int>(page =>
        {
            PageNumber = Math.Max(0, page - 1);
            _ = RefreshAsync();
        });

        GoPrevCommand = new AsyncRelayCommand(async () =>
        {
            if (!CanGoPrev) return;
            PageNumber--;
            await RefreshAsync();
        });

        GoNextCommand = new AsyncRelayCommand(async () =>
        {
            if (!CanGoNext) return;
            PageNumber++;
            await RefreshAsync();
        });

        ToggleSortCommand = new AsyncRelayCommand<string>(async key =>
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (string.Equals(key, SortKey, StringComparison.Ordinal))
                SortDesc = !SortDesc;
            else
            {
                SortKey = key;
                SortDesc = true;
            }
            await ApplyViewChange();
        });
    }
    #endregion

    #region Abstract contract
    protected abstract Task<(IReadOnlyList<TItem> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct);
    #endregion

    #region ILazyTab
    public virtual async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _loadCts.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        IsBusy = true;
        try
        {
            var (items, total) = await LoadPageAsync(PageNumber * PageSize, PageSize, SortKey, SortDesc, CurrentFilter, ct);
            TotalRows = total;
            PagedItems.Clear();
            foreach (var item in items)
                PagedItems.Add(item);
            NotifyPagerProperties();
            IsBusy = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* superseded by a newer request; a timeout falls through below */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{TableType}: LoadPage failed (page {Page}, size {Size}, filter {Filter}).",
                GetType().Name, PageNumber, PageSize, CurrentFilter ?? "(none)");
            IsBusy = false;
        }
    }
    #endregion

    #region Helpers
    /// <summary>
    /// Size the page so a full page fills <paramref name="dataAreaHeightPx"/> (the table
    /// viewport height minus header/pager chrome), then reload. For lazy tabs not yet
    /// initialized this just stores the size; <see cref="EnsureInitializedAsync"/> loads
    /// at the new size when the tab is first shown. Page resets to 1 on a size change.
    /// </summary>
    public Task ApplyViewportHeightAsync(double dataAreaHeightPx)
    {
        if (dataAreaHeightPx <= 0) return Task.CompletedTask;
        int fit = Math.Max(MinPageSize, (int)Math.Floor(dataAreaHeightPx / RowHeightPx));
        if (fit == PageSize) return Task.CompletedTask;
        PageSize = fit; // OnPageSizeChanged repages (resets to page 1) when initialized
        return Task.CompletedTask;
    }

    protected async Task ApplyViewChange()
    {
        PageNumber = 0;
        await RefreshAsync();
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

    partial void OnPageNumberChanged(int value) => NotifyPagerProperties();
    partial void OnTotalRowsChanged(int value) => NotifyPagerProperties();
    partial void OnPageSizeChanged(int value)
    {
        NotifyPagerProperties();
        if (_initialized) _ = ApplyViewChange(); // first init will pick up the size on its own
    }
    #endregion
}
