using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Diagnostics;
using KieshStockExchange.Services.DataServices;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public interface ILazyTab
{
    Task EnsureInitializedAsync();
    Task RefreshAsync();
}

public abstract partial class BaseTableViewModel<TItem> : BaseViewModel, ILazyTab
{
    #region Page Properties
    [ObservableProperty] private ObservableCollection<TItem> _pagedItems = new();
    [ObservableProperty] private int _pageNumber;
    private int _total;

    public int PageSize { get; set; } = 20;

    public int TotalPages => (int)Math.Ceiling((double)_total / PageSize);

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
    #endregion

    #region Sort / Filter state (set by subclasses)
    protected string? SortKey;
    protected bool SortDesc = true;
    protected string? CurrentFilter;
    #endregion

    #region Services, Commands and Constructor
    protected readonly IDataBaseService _db;
    private CancellationTokenSource _loadCts = new();
    private bool _initialized;

    public ICommand GoToPageCommand { get; }

    protected BaseTableViewModel(IDataBaseService dbService)
    {
        _db = dbService;
        GoToPageCommand = new Command<int>(page =>
        {
            PageNumber = page - 1;
            _ = RefreshAsync();
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
            _total = total;
            PagedItems.Clear();
            foreach (var item in items)
                PagedItems.Add(item);
            OnPropertyChanged(nameof(TotalPages));
            OnPropertyChanged(nameof(VisiblePageNumbers));
            IsBusy = false;
        }
        catch (OperationCanceledException) { /* superseded by a newer request */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{GetType().Name}] LoadPage failed: {ex.Message}");
            IsBusy = false;
        }
    }
    #endregion

    #region Helpers for subclasses
    protected async Task ApplyViewChange()
    {
        PageNumber = 0;
        await RefreshAsync();
    }
    #endregion
}
