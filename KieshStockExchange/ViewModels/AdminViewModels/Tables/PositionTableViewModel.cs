using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class PositionTableViewModel : BaseTableViewModel<PositionTableObject>
{
    public const string PivotByStock = "By stock";
    public const string PivotByUser = "By user";

    #region Data properties
    public Dictionary<int, Stock> Stocks = new();
    public Dictionary<int, string> StockSymbols => Stocks.ToDictionary(kv => kv.Key, kv => kv.Value.Symbol);
    public CurrencyType BaseCurrency = CurrencyType.USD;

    public IReadOnlyList<string> PivotOptions { get; } = new[] { PivotByStock, PivotByUser };

    [ObservableProperty] private string _pivot = PivotByStock;
    // SegmentedTabView drives the pivot via SelectedIndex; keep the string
    // `Pivot` as the canonical source so existing IsByStock / IsByUser checks
    // still work.
    [ObservableProperty] private int _pivotIndex;
    public bool IsByStock => Pivot == PivotByStock;
    public bool IsByUser  => Pivot == PivotByUser;

    partial void OnPivotChanged(string value)
    {
        OnPropertyChanged(nameof(IsByStock));
        OnPropertyChanged(nameof(IsByUser));
        _ = ApplyViewChange();
    }

    partial void OnPivotIndexChanged(int value) =>
        Pivot = value == 1 ? PivotByUser : PivotByStock;

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
            if (IsByStock) _ = ApplyViewChange();
        }
    }
    #endregion

    #region Filter / search
    [ObservableProperty] private string _userSearch = string.Empty;
    [ObservableProperty] private bool _hasNonZeroOnly;
    [ObservableProperty] private bool _hasReservedOnly;
    [ObservableProperty] private string _minQuantityText = string.Empty;

    partial void OnUserSearchChanged(string value) => _ = ApplyViewChange();
    partial void OnHasNonZeroOnlyChanged(bool value) => _ = ApplyViewChange();
    partial void OnHasReservedOnlyChanged(bool value) => _ = ApplyViewChange();
    partial void OnMinQuantityTextChanged(string value) => _ = ApplyViewChange();
    #endregion

    #region Services and Constructor
    private readonly IMarketDataService _market;
    private readonly IStockService _stocks;
    private readonly IServiceProvider _services;

    public PositionTableViewModel(IDataBaseService dbService, IMarketDataService market,
        IStockService stocks, IServiceProvider services,
        ILogger<PositionTableViewModel> logger) : base(dbService, logger)
    {
        Title = "Positions";
        SortKey = "UserId";
        SortDesc = true;
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _services = services ?? throw new ArgumentNullException(nameof(services));
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
    protected override Task<(IReadOnlyList<PositionTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
        => IsByUser ? LoadByUserAsync(skip, take, sortKey, desc, ct)
                    : LoadByStockAsync(skip, take, sortKey, desc, filter, ct);

    private async Task<(IReadOnlyList<PositionTableObject> Items, int Total)> LoadByStockAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        if (_pickerSelection is null) return (Array.Empty<PositionTableObject>(), 0);

        // UserSearch ("alice" or "42") narrows the stock's holders to a single
        // user — same UX as the Orders/Transactions search box.
        string? userFilter = null;
        if (!string.IsNullOrWhiteSpace(UserSearch))
        {
            var resolved = await ResolveUserIdAsync(ct);
            if (resolved <= 0) return (Array.Empty<PositionTableObject>(), 0);
            userFilter = resolved.ToString();
        }

        // Price/PosValue/Username are computed in-VM (the DB layer can't see
        // them), so for those sort keys we fetch the full slice, build rows,
        // sort, then page.
        bool inVmSort = IsComputedSort(sortKey);
        var (positions, total) = await _db.GetPositionsPageAsync(
            _pickerSelection.StockId,
            inVmSort ? 0 : skip,
            inVmSort ? int.MaxValue : take,
            inVmSort ? "UserId" : (sortKey ?? "UserId"),
            desc, userFilter, ct);

        var rowsResult = await BuildRowsAsync(positions, total, _pickerSelection.StockId, ct);
        if (!inVmSort) return rowsResult;

        var sorted = ApplyComputedSort(rowsResult.Items, sortKey, desc);
        var paged = sorted.Skip(skip).Take(take).ToList();
        return (paged, sorted.Count);
    }

    private async Task<(IReadOnlyList<PositionTableObject> Items, int Total)> LoadByUserAsync(
        int skip, int take, string? sortKey, bool desc, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId <= 0) return (Array.Empty<PositionTableObject>(), 0);

        var positions = await _db.GetPositionsByUserId(userId, ct);
        IEnumerable<Position> ordered = (sortKey, desc) switch
        {
            ("Quantity", true)  => positions.OrderByDescending(p => p.Quantity),
            ("Quantity", false) => positions.OrderBy(p => p.Quantity),
            ("Reserved", true)  => positions.OrderByDescending(p => p.ReservedQuantity),
            ("Reserved", false) => positions.OrderBy(p => p.ReservedQuantity),
            ("StockId",  true)  => positions.OrderByDescending(p => p.StockId),
            (_,          true)  => positions.OrderByDescending(p => p.StockId),
            (_,          false) => positions.OrderBy(p => p.StockId),
        };
        var pageSlice = ordered.ToList();

        var rowsResult = await BuildRowsAsync(pageSlice, pageSlice.Count, primaryStockIdForRow: 0, ct);
        var maybeSorted = IsComputedSort(sortKey)
            ? ApplyComputedSort(rowsResult.Items, sortKey, desc)
            : rowsResult.Items.ToList();
        var paged = maybeSorted.Skip(skip).Take(take).ToList();
        return (paged, maybeSorted.Count);
    }

    private static bool IsComputedSort(string? sortKey) =>
        sortKey == "Price" || sortKey == "PosValue" || sortKey == "Username";

    private static List<PositionTableObject> ApplyComputedSort(
        IReadOnlyList<PositionTableObject> rows, string? sortKey, bool desc)
    {
        IEnumerable<PositionTableObject> ordered = (sortKey, desc) switch
        {
            ("Price",    true)  => rows.OrderByDescending(r => r.NativePriceForSort),
            ("Price",    false) => rows.OrderBy(r => r.NativePriceForSort),
            ("PosValue", true)  => rows.OrderByDescending(r => r.NativeValueForSort),
            ("PosValue", false) => rows.OrderBy(r => r.NativeValueForSort),
            ("Username", true)  => rows.OrderByDescending(r => r.Username, StringComparer.OrdinalIgnoreCase),
            ("Username", false) => rows.OrderBy(r => r.Username, StringComparer.OrdinalIgnoreCase),
            _                   => rows
        };
        return ordered.ToList();
    }

    private async Task<int> ResolveUserIdAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UserSearch)) return -1;
        var text = UserSearch.Trim();
        if (int.TryParse(text, out var id)) return id;
        var (matches, _) = await _db.GetUsersPageAsync(0, 1, "Username", false, text, ct);
        return matches.Count > 0 ? matches[0].UserId : -1;
    }

    private async Task<(IReadOnlyList<PositionTableObject> Items, int Total)> BuildRowsAsync(
        IReadOnlyList<Position> positions, int total, int primaryStockIdForRow, CancellationToken ct)
    {
        if (positions.Count == 0) return (Array.Empty<PositionTableObject>(), total);

        // Apply Has-Non-Zero + Has-Reserved + min-quantity filters in memory.
        IEnumerable<Position> filtered = positions;
        if (HasNonZeroOnly) filtered = filtered.Where(p => p.Quantity > 0);
        if (HasReservedOnly) filtered = filtered.Where(p => p.ReservedQuantity > 0);
        if (int.TryParse(MinQuantityText, out var minQty) && minQty > 0)
            filtered = filtered.Where(p => p.Quantity >= minQty);
        var visible = filtered.ToList();
        if (visible.Count == 0) return (Array.Empty<PositionTableObject>(), total);

        // Native price per stock — resolved via the stock's primary listing currency
        // so a EUR-listed stock isn't silently converted to USD on the row.
        var stockIdsToPrice = primaryStockIdForRow > 0
            ? new[] { primaryStockIdForRow }
            : visible.Select(p => p.StockId).Distinct().ToArray();

        var nativePrices = new Dictionary<int, (decimal Price, CurrencyType Currency)>();
        foreach (var sid in stockIdsToPrice)
        {
            CurrencyType ccy = CurrencyType.USD;
            _stocks.TryGetCurrency(sid, out ccy);
            try
            {
                var price = await _market.GetLastPriceAsync(sid, ccy, ct);
                nativePrices[sid] = (price, ccy);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Price fetch failed for stock #{sid}: {ex.Message}");
                nativePrices[sid] = (0m, ccy);
            }
        }

        // Per-page username lookup so each row has a display name for the
        // new Username column (and for the Username sort key).
        var userIds = visible.Select(p => p.UserId).Distinct().ToList();
        var users = await _db.GetUsersByIds(userIds, ct);
        var usersById = users.ToDictionary(u => u.UserId, u => u.Username);

        var rows = new List<PositionTableObject>();
        foreach (var pos in visible)
        {
            var rowStockId = primaryStockIdForRow > 0 ? primaryStockIdForRow : pos.StockId;
            nativePrices.TryGetValue(rowStockId, out var native);
            var symbol = Stocks.TryGetValue(rowStockId, out var st) ? st.Symbol : $"#{rowStockId}";
            usersById.TryGetValue(pos.UserId, out var username);
            rows.Add(new PositionTableObject(pos, symbol, native.Price, native.Currency, BaseCurrency,
                rowStockId, username ?? $"#{pos.UserId}", OpenEditAsync));
        }
        return (rows, total);
    }

    private async Task OpenEditAsync(int userId, int stockId)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var positions = await _db.GetPositionsByUserId(userId);
        var position = positions.FirstOrDefault(p => p.StockId == stockId);
        if (position is null)
        {
            await MainThread.InvokeOnMainThreadAsync(() => page.DisplayAlert("Position not found",
                $"No position for user #{userId} in stock #{stockId} (may have been deleted).", "Close"));
            return;
        }

        var symbol = Stocks.TryGetValue(stockId, out var s) ? s.Symbol : $"#{stockId}";
        var popup = _services.GetRequiredService<PositionEditPopup>();
        popup.ViewModel.Initialize(position, symbol);

        EventHandler? savedHandler = null;
        savedHandler = (_, _) => { _ = RefreshAsync(); };
        popup.ViewModel.Saved += savedHandler;
        try { await page.ShowPopupAsync(popup); }
        finally { popup.ViewModel.Saved -= savedHandler; }
    }
    #endregion
}

public partial class PositionTableObject : ObservableObject
{
    public Position Position { get; }
    public string Symbol { get; }
    public string Username { get; }
    public CurrencyType NativeCurrency { get; }
    public CurrencyType BaseCurrency { get; }
    public decimal NativePrice { get; }
    public int CurrentStockId { get; }

    public int UserId => Position.UserId;
    public int Quantity => Position.Quantity;
    public int ReservedQuantity => Position.ReservedQuantity;

    // Sort keys — native side reads as-is; FX comparison uses base equivalent.
    public decimal NativePriceForSort => NativePrice;
    public decimal NativeValueForSort => NativePrice * Quantity;

    public string QuantityDisplay => Quantity == 0 ? "-" : Quantity.ToString();
    public string ReservedQuantityDisplay => ReservedQuantity == 0 ? "-" : ReservedQuantity.ToString();
    public string PriceDisplay => NativePrice > 0m
        ? CurrencyHelper.Format(NativePrice, NativeCurrency)
        : "-";
    public string StockValueDisplay => NativePrice > 0m
        ? CurrencyHelper.Format(CurrencyHelper.Notional(NativePrice, Quantity, NativeCurrency), NativeCurrency)
        : "-";

    public string ValueBaseDisplay
    {
        get
        {
            if (NativePrice <= 0m) return "-";
            var nativeNotional = CurrencyHelper.Notional(NativePrice, Quantity, NativeCurrency);
            var converted = CurrencyHelper.Convert(nativeNotional, NativeCurrency, BaseCurrency);
            return CurrencyHelper.Format(converted, BaseCurrency);
        }
    }

    public string StockSymbol => Symbol;

    public IAsyncRelayCommand EditCommand { get; }

    public PositionTableObject(Position position, string symbol, decimal nativePrice,
        CurrencyType nativeCurrency, CurrencyType baseCurrency,
        int currentStockId, string username, Func<int, int, Task> onEdit)
    {
        Position = position ?? throw new ArgumentNullException(nameof(position));
        Symbol = symbol;
        NativePrice = nativePrice;
        NativeCurrency = nativeCurrency;
        BaseCurrency = baseCurrency;
        CurrentStockId = currentStockId;
        Username = username;
        EditCommand = new AsyncRelayCommand(() => onEdit(UserId, CurrentStockId));
    }
}
