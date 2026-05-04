using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.AdminViewModels;

// Price, StockValue, TotalValue removed — depend on live FX that the DB cannot order by
public enum PosSortColumn { None, UserId, Quantity, Reserved }
public enum PosSortDir { Asc, Desc }

public partial class PositionTableViewModel : BaseTableViewModel<PositionTableObject>
{
    #region Data properties
    public Dictionary<int, Stock> Stocks = new();
    public Dictionary<int, string> StockSymbols => Stocks.ToDictionary(kv => kv.Key, kv => kv.Value.Symbol);
    public CurrencyType BaseCurrency = CurrencyType.USD;

    public ObservableCollection<Stock> PickerStocks { get; } = new();
    private Stock? _pickerSelection;
    public Stock? PickerSelection
    {
        get => _pickerSelection;
        set
        {
            if (value is null || value == _pickerSelection || value.StockId <= 0) return;
            _pickerSelection = value;
            OnPropertyChanged();
            PageNumber = 0;
            _ = RefreshAsync();
        }
    }
    #endregion

    #region Filter and Sorting
    [ObservableProperty] private string _idFilter = string.Empty;

    partial void OnIdFilterChanged(string value)
    {
        CurrentFilter = string.IsNullOrWhiteSpace(value) ? null : value;
        _ = ApplyViewChange();
    }
    #endregion

    #region Services and Constructor
    private readonly IMarketDataService _market;

    public PositionTableViewModel(IDataBaseService dbService, IMarketDataService market) : base(dbService)
    {
        Title = "Positions";
        SortKey = "UserId";
        SortDesc = true;
        _market = market ?? throw new ArgumentNullException(nameof(market));
    }
    #endregion

    #region Lazy init — stocks must be loaded before first page query
    public override async Task EnsureInitializedAsync()
    {
        await EnsureStocksLoadedAsync();
        await base.EnsureInitializedAsync();
    }

    private async Task EnsureStocksLoadedAsync()
    {
        if (Stocks.Count > 0) return;
        var stocks = await _db.GetStocksAsync();
        if (stocks.Count == 0) return;
        Stocks = stocks.ToDictionary(s => s.StockId, s => s);
        foreach (var stock in stocks)
            PickerStocks.Add(stock);
        _pickerSelection = stocks[0];
        OnPropertyChanged(nameof(PickerSelection));
    }
    #endregion

    #region Data loading
    protected override async Task<(IReadOnlyList<PositionTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        if (_pickerSelection is null) return (Array.Empty<PositionTableObject>(), 0);

        var (positions, total) = await _db.GetPositionsPageAsync(
            _pickerSelection.StockId, skip, take, sortKey ?? "UserId", desc, filter, ct);

        if (positions.Count == 0) return (Array.Empty<PositionTableObject>(), total);

        // Fetch all positions for these users so TotalValue sums across all stocks
        var userIds = positions.Select(p => p.UserId).Distinct().ToList();
        var allUserPositions = await _db.GetPositionsForUsersAsync(userIds, ct);
        var byUser = allUserPositions.GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(p => p.StockId, p => p));

        // Current price for the selected stock only
        decimal latestPrice = 0m;
        try { latestPrice = await _market.GetLastPriceAsync(_pickerSelection.StockId, BaseCurrency, ct); }
        catch (Exception ex) { Debug.WriteLine($"Price fetch failed: {ex.Message}"); }

        var prices = new Dictionary<int, decimal> { { _pickerSelection.StockId, latestPrice } };

        var rows = new List<PositionTableObject>();
        foreach (var pos in positions)
        {
            if (!byUser.TryGetValue(pos.UserId, out var posDict))
                posDict = new Dictionary<int, Position>();
            rows.Add(new PositionTableObject(_db, pos.UserId, BaseCurrency, posDict, prices, StockSymbols));
        }
        return (rows, total);
    }
    #endregion

    #region Commands
    [RelayCommand] private void SetSortDesc(PosSortColumn column)
    {
        SortKey = ColumnToSortKey(column);
        SortDesc = true;
        _ = ApplyViewChange();
    }

    [RelayCommand] private void SetSortAsc(PosSortColumn column)
    {
        SortKey = ColumnToSortKey(column);
        SortDesc = false;
        _ = ApplyViewChange();
    }

    private static string ColumnToSortKey(PosSortColumn column) => column switch
    {
        PosSortColumn.Quantity => "Quantity",
        PosSortColumn.Reserved => "Reserved",
        _                      => "UserId",
    };
    #endregion
}

public partial class PositionTableObject : ObservableObject
{
    #region Data properties
    private Dictionary<int, Position> PosDict;

    public CurrencyType BaseCurrency;

    private Dictionary<int, decimal> Prices;

    private Dictionary<int, string> Symbols;

    [ObservableProperty] private bool _isEditing = false;
    [ObservableProperty] private bool _isViewing = true;
    [ObservableProperty] private string _editText = "Edit";
    [ObservableProperty] private int _userId = 0;
    #endregion

    #region Table properties
    private int _currentStockId = 0;
    public int CurrentStockId
    {
        get => _currentStockId;
        set
        {
            if (_currentStockId != value && value > 0)
            {
                _currentStockId = value;
                NotifyAllProperties();
            }
        }
    }

    private Position? CurrentPosition => PosDict.TryGetValue(CurrentStockId, out var p) ? p : null;

    public int Quantity => CurrentPosition?.Quantity ?? 0;

    public int ReservedQuantity => CurrentPosition?.ReservedQuantity ?? 0;

    public decimal? Price => Prices.TryGetValue(CurrentStockId, out var p) ? p : (decimal?)null;

    public decimal? StockValue => Price.HasValue ? Price.Value * Quantity : (decimal?)null;

    public decimal TotalValue
    {
        get
        {
            decimal total = 0m;
            foreach (var (id, pos) in PosDict)
                if (Prices.TryGetValue(id, out var p))
                    total += pos.Quantity * p;
            return total;
        }
    }
    #endregion

    #region Display properties
    public string QuantityDisplay
    {
        get => Quantity == 0 ? "-" : Quantity.ToString();
        set
        {
            if (CurrentPosition is null) return;
            if (ParsingHelper.TryToInt(value, out var qty) && qty >= 0)
            {
                CurrentPosition.Quantity = qty;
                NotifyAllProperties();
            }
        }
    }

    public string ReservedQuantityDisplay
    {
        get => ReservedQuantity == 0 ? "-" : ReservedQuantity.ToString();
        set
        {
            if (CurrentPosition is null) return;
            if (ParsingHelper.TryToInt(value, out var qty) && qty >= 0 && qty <= (CurrentPosition?.Quantity ?? 0))
            {
                CurrentPosition.ReservedQuantity = qty;
                NotifyAllProperties();
            }
        }
    }

    public string PriceDisplay
        => Price.HasValue ? CurrencyHelper.Format(Price.Value, BaseCurrency) : "-";

    public string StockSymbol => Symbols.TryGetValue(CurrentStockId, out var symbol) ? symbol : "-";

    public string StockValueDisplay
        => StockValue.HasValue ? CurrencyHelper.Format(StockValue.Value, BaseCurrency) : "-";

    public string TotalValueDisplay => CurrencyHelper.Format(TotalValue, BaseCurrency);
    #endregion

    #region Other properties and Constructor
    private readonly IDataBaseService _db;

    public PositionTableObject(IDataBaseService db, int userId, CurrencyType baseCurrency, Dictionary<int, Position> posDict,
          Dictionary<int, decimal> prices, Dictionary<int, string> symbols)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        UserId = userId;
        BaseCurrency = baseCurrency;
        PosDict = posDict;
        Prices = prices;
        Symbols = symbols;
        CurrentStockId = prices.Keys.FirstOrDefault(1);
    }
    #endregion

    #region Methods
    [RelayCommand] private async Task ChangeEdit()
    {
        if (IsViewing)
        {
            EditText = "Save";
            IsViewing = false;
            IsEditing = true;
            return;
        }

        var saved = await SaveAsync();
        if (!saved)
        {
            await ResetAsync();
            NotifyAllProperties();
            return;
        }

        EditText = "Edit";
        IsViewing = true;
        IsEditing = false;
        NotifyAllProperties();
    }

    private async Task<bool> SaveAsync()
    {
        try
        {
            foreach (var (id, pos) in PosDict)
            {
                if (pos.IsInvalid)
                {
                    Debug.WriteLine($"Invalid position for UserId #{UserId}, StockId #{id}.");
                    return false;
                }
            }

            await _db.RunInTransactionAsync(async ct =>
            {
                foreach (var pos in PosDict.Values)
                {
                    pos.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpsertPosition(pos, ct);
                }
            });
            Debug.WriteLine($"Successfully saved positions for UserId #{UserId}.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving positions for user #{UserId}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ResetAsync()
    {
        try
        {
            var positions = await _db.GetPositionsByUserId(UserId);
            PosDict = positions.ToDictionary(p => p.StockId, p => p);
            Debug.WriteLine($"Successfully reverted positions for UserId #{UserId}.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error resetting positions for user #{UserId}: {ex.Message}");
            return false;
        }
    }
    #endregion

    #region Refresh data
    private void NotifyAllProperties()
    {
        OnPropertyChanged(nameof(Quantity)); OnPropertyChanged(nameof(QuantityDisplay));
        OnPropertyChanged(nameof(ReservedQuantity)); OnPropertyChanged(nameof(ReservedQuantityDisplay));
        OnPropertyChanged(nameof(Price)); OnPropertyChanged(nameof(PriceDisplay));
        OnPropertyChanged(nameof(StockValue)); OnPropertyChanged(nameof(StockValueDisplay));
        OnPropertyChanged(nameof(TotalValue)); OnPropertyChanged(nameof(TotalValueDisplay));
        OnPropertyChanged(nameof(StockSymbol));
    }

    public void RefreshData(CurrencyType baseCurrency, Dictionary<int, decimal> latestPrices)
    {
        BaseCurrency = baseCurrency;
        Prices = latestPrices;
        NotifyAllProperties();
    }
    #endregion
}
