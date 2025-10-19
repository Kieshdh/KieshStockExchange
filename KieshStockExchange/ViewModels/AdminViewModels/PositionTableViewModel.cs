using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class PositionTableViewModel : BaseTableViewModel<PositionTableObject>
{
    #region Data properties and Constructor
    private readonly IMarketDataService _market;

    public Dictionary<int, decimal> LatestPrices = new();  // Id → StockPrice
    public Dictionary<int, Stock> Stocks = new(); // Id → Stock
    public Dictionary<int, string> StockSymbols => Stocks.ToDictionary(kv => kv.Key, kv => kv.Value.Symbol);
    public CurrencyType BaseCurrency = CurrencyType.USD;

    public ObservableCollection<Stock> PickerStocks { get; } = new();
    private Stock? _pickerSelection;
    public Stock? PickerSelection
    {
        get => _pickerSelection ?? null;
        set
        {
            if (value is null || value == _pickerSelection || value.StockId <= 0) return;
            _pickerSelection = value; // only to reflect the UI's selected item immediately
            // Update all table objects to the new stock
            foreach (var item in AllItems)
                item.CurrentStockId = value.StockId;
            OnPropertyChanged(); // ensures the picker reflects the new selection
        }
    }

    public PositionTableViewModel(IDataBaseService dbService, IMarketDataService market) : base(dbService)
    {
        Title = "Positions"; // from BaseViewModel
        _market = market ?? throw new ArgumentNullException(nameof(market));
    }
    #endregion

    #region Data loading
    protected override async Task<List<PositionTableObject>> LoadItemsAsync()
    {
        IsBusy = true;
        try
        {
            var rows = new List<PositionTableObject>();

            // Fetch all data
            var users = await _dbService.GetUsersAsync();
            var allPositions = await _dbService.GetPositionsAsync(); 
            var stocks = await _dbService.GetStocksAsync();

            // Ensure at least one stock exists
            if (stocks.Count == 0)
            {
                Debug.WriteLine("No stocks found in database.");
                await Shell.Current.DisplayAlert("Error", "No stocks found in database. Please add stocks first.", "OK");
                return rows;
            }
            // Add to picker and set selection
            foreach (var stock in stocks)
                PickerStocks.Add(stock);
            PickerSelection = stocks[0];

            // Set the Stocks dictionary for easy lookup
            Stocks = stocks.ToDictionary(s => s.StockId, s => s);

            // Index existing positions by (UserId -> List<Position>)
            var byUser = allPositions.GroupBy(f => f.UserId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(f => f.StockId, f => f));

            // At last get the current prices for all stocks
            await UpdateStockPrices();

            foreach (var user in users)
            {
                // Get the user's Positions (empty if none).
                if (!byUser.TryGetValue(user.UserId, out var positionsDict))
                    positionsDict = byUser[user.UserId] = new Dictionary<int, Position>();

                // Ensure all stocks are represented
                foreach (var stock in stocks)
                {
                    if (!positionsDict.TryGetValue(stock.StockId, out var pos))
                    {
                        pos = new Position { UserId = user.UserId, StockId = stock.StockId };
                        positionsDict[stock.StockId] = pos;
                    }
                }

                // Create the table object
                rows.Add(new PositionTableObject(_dbService, user.UserId, BaseCurrency, positionsDict, LatestPrices, StockSymbols));
            }

            // Sort by most recent first
            rows = rows.OrderByDescending(r => r.UserId).ToList();
            Debug.WriteLine($"The User table has {rows.Count} users.");
            return rows;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading users: {ex.Message}");
            await Shell.Current.DisplayAlert("Error", "Failed to load users.", "OK");
            return new List<PositionTableObject>();
        }
        finally { IsBusy = false; }
    }
    #endregion

    #region Price updating
    private async Task UpdateStockPrices()
    {

        foreach (var stockId in Stocks.Keys)
        {
            try { LatestPrices[stockId] = await _market.GetLastPriceAsync(stockId, BaseCurrency); }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching price for StockId {stockId}: {ex.Message}");
                LatestPrices[stockId] = 0m; // Default to 0 on error
            }
        }
    }

    public async Task UpdatePricesOfTableObjects(CurrencyType baseCurrency = CurrencyType.USD)
    {
        IsBusy = true;
        BaseCurrency = baseCurrency;
        try
        {
            await UpdateStockPrices();
            foreach (var user in AllItems)
                user.RefreshData(baseCurrency, LatestPrices);
        }
        finally { IsBusy = false; }
    }
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

    private Position CurrentPosition => PosDict.TryGetValue(CurrentStockId, out var p) 
        ? p : new Position { UserId = UserId, StockId = CurrentStockId };

    public string QuantityDisplay
    {
        get => CurrentPosition.Quantity == 0? "-" : CurrentPosition.Quantity.ToString();
        set         
        {
            if (ParsingHelper.TryToInt(value, out var qty) && qty >= 0)
            {
                CurrentPosition.Quantity = qty;
                NotifyAllProperties();
            }
        }
    }

    public string ReservedQuantityDisplay
    {
        get => CurrentPosition.ReservedQuantity == 0 ? "-" : CurrentPosition.ReservedQuantity.ToString();
        set
        {
            if (ParsingHelper.TryToInt(value, out var qty) && qty >= 0 && qty <= CurrentPosition.Quantity)
            {
                CurrentPosition.ReservedQuantity = qty;
                NotifyAllProperties();
            }
        }
    }

    public string PriceDisplay
    {
        get => (Prices.TryGetValue(CurrentStockId, out var price)) 
            ? CurrencyHelper.Format(price, BaseCurrency) : "-";
    }

    public string StockSymbol => Symbols.TryGetValue(CurrentStockId, out var symbol) ? symbol : "-";

    public string StockValueDisplay  => (Prices.TryGetValue(CurrentStockId, out var price)) 
        ? CurrencyHelper.Format(CurrentPosition.Quantity * price, BaseCurrency) : "-";

    public string TotalValueDisplay
    {
        get
        {
            var totalValue = 0m;
            foreach (var (id, pos) in PosDict)
            {
                if (Prices.TryGetValue(id, out var price))
                    totalValue += pos.Quantity * price;
            }
            return totalValue > 0m ? CurrencyHelper.Format(totalValue, BaseCurrency) : "-";
        }
    }
    #endregion

    #region Other properties and Constructor
    private readonly IDataBaseService _db;

    public PositionTableObject( IDataBaseService db, int userId, CurrencyType baseCurrency, Dictionary<int, Position> posDict,
          Dictionary<int, decimal> prices, Dictionary<int, string> symbols)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));

        UserId = userId;
        BaseCurrency = baseCurrency;
        PosDict = posDict;
        Prices = prices;
        Symbols = symbols;

        CurrentStockId = 1; // default to first stock
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

        // Attempt to save changes
        var saved = await SaveAsync();
        if (!saved) // If save failed, revert changes
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
            // Validate all positions
            foreach (var (id, pos) in PosDict)
            {
                if (pos.IsInvalid)
                {
                    Debug.WriteLine($"Invalid position for UserId #{UserId}, StockId #{id}.");
                    return false;
                }
            }

            // Save all positions in one transaction
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
            var posDict = positions.ToDictionary(p => p.StockId, p => p);

            foreach (var stockId in Prices.Keys)
            {
                if (!posDict.ContainsKey(stockId))
                {
                    var pos = new Position { UserId = UserId, StockId = stockId };
                    posDict[stockId] = pos;
                }
            }
            PosDict = posDict;
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
        OnPropertyChanged(nameof(QuantityDisplay));
        OnPropertyChanged(nameof(ReservedQuantityDisplay));
        OnPropertyChanged(nameof(PriceDisplay));
        OnPropertyChanged(nameof(StockSymbol));
        OnPropertyChanged(nameof(StockValueDisplay));
        OnPropertyChanged(nameof(TotalValueDisplay));
    }

    public void RefreshData(CurrencyType baseCurrency, Dictionary<int, decimal> latestPrices)
    {
        BaseCurrency = baseCurrency;
        Prices = latestPrices;

        NotifyAllProperties();
    }
    #endregion
}
