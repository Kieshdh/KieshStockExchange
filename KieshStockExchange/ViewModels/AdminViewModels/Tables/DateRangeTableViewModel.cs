using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

// Intermediate base for the admin Order + Transaction tables: shared date/time range filter,
// stock-picker, username resolver and quick-range commands. Concrete tables add their own
// entity-specific filters and implement LoadPageAsync.
public abstract partial class DateRangeTableViewModel<TItem> : BaseTableViewModel<TItem>
{
    protected const string AnyOption = "Any";

    // The stock-picker sentinel meaning "no stock filter". Provided per-VM so ownership of the
    // shared sentinel instance stays with the concrete table.
    protected abstract Stock AnyStock { get; }

    #region Filter state
    [ObservableProperty] private DateTime _fromDate = DateTime.UtcNow.AddMinutes(-5);
    [ObservableProperty] private DateTime _toDate = DateTime.UtcNow;
    [ObservableProperty] private TimeSpan _fromTime = DateTime.UtcNow.AddMinutes(-5).TimeOfDay;
    [ObservableProperty] private TimeSpan _toTime = DateTime.UtcNow.TimeOfDay;
    [ObservableProperty] private string _usernameSearch = string.Empty;
    [ObservableProperty] private bool _hideAiBots = false;

    private Stock? _selectedStockFilter;
    public Stock? SelectedStockFilter
    {
        get => _selectedStockFilter;
        set
        {
            if (value == _selectedStockFilter) return;
            _selectedStockFilter = value;
            OnPropertyChanged();
            _ = ApplyViewChange();
        }
    }

    public ObservableCollection<Stock> PickerStocks { get; } = new();

    partial void OnFromDateChanged(DateTime value) => _ = ApplyViewChange();
    partial void OnToDateChanged(DateTime value) => _ = ApplyViewChange();
    partial void OnFromTimeChanged(TimeSpan value) => _ = ApplyViewChange();
    partial void OnToTimeChanged(TimeSpan value) => _ = ApplyViewChange();
    partial void OnUsernameSearchChanged(string value) => _ = ApplyViewChange();
    partial void OnHideAiBotsChanged(bool value) => _ = ApplyViewChange();
    #endregion

    #region Shared fields and constructor
    protected Dictionary<int, Stock> _stocksById = new();
    protected List<int>? _aiUserIds;

    protected DateRangeTableViewModel(IDataBaseService dbService, ILogger? logger = null)
        : base(dbService, logger)
    {
    }
    #endregion

    #region Initialization and shared load helpers
    public override async Task EnsureInitializedAsync()
    {
        await EnsureStocksLoadedAsync();
        await base.EnsureInitializedAsync();
    }

    private async Task EnsureStocksLoadedAsync()
    {
        if (PickerStocks.Count > 0) return;
        var stocks = await _db.GetStocksAsync();
        _stocksById = stocks.ToDictionary(s => s.StockId);
        PickerStocks.Add(AnyStock);
        foreach (var s in stocks) PickerStocks.Add(s);
        _selectedStockFilter ??= AnyStock;
        OnPropertyChanged(nameof(SelectedStockFilter));

        var aiUsers = await _db.GetAIUsersAsync();
        _aiUserIds = aiUsers.Select(a => a.UserId).Distinct().ToList();
    }

    // Refetch the stock lookup if a page load runs before EnsureStocksLoadedAsync populated it.
    protected async Task EnsureStocksByIdAsync(CancellationToken ct)
    {
        if (_stocksById.Count == 0)
        {
            var stocks = await _db.GetStocksAsync(ct);
            _stocksById = stocks.ToDictionary(s => s.StockId);
        }
    }

    protected async Task<int?> ResolveUserIdFilterAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UsernameSearch)) return null;

        var text = UsernameSearch.Trim();
        if (int.TryParse(text, out var id)) return id;

        var (matches, _) = await _db.GetUsersPageAsync(0, 1, "Username", false, text, ct);
        return matches.Count > 0 ? matches[0].UserId : -1; // -1: nothing matches
    }
    #endregion

    #region Quick-range commands
    [RelayCommand] private void SetLast5Min()  => SetRange(TimeSpan.FromMinutes(5));
    [RelayCommand] private void SetLast15Min() => SetRange(TimeSpan.FromMinutes(15));
    [RelayCommand] private void SetLastHour()  => SetRange(TimeSpan.FromHours(1));
    [RelayCommand] private void SetLast1Day()  => SetRange(TimeSpan.FromDays(1));

    private void SetRange(TimeSpan window)
    {
        var now = DateTime.UtcNow;
        var start = now - window;
        FromDate = start;
        FromTime = start.TimeOfDay;
        ToDate = now;
        ToTime = now.TimeOfDay;
    }
    #endregion
}
