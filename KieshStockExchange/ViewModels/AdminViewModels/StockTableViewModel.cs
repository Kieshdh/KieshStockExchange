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

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class StockTableViewModel : BaseTableViewModel<StockTableObject>
{
    private const string AnyCurrencyOption = "Any";

    private readonly IMarketDataService _market;
    private readonly IStockService _stocks;
    private readonly IServiceProvider _services;

    public IReadOnlyList<string> CurrencyFilterOptions { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedCurrencyFilter = AnyCurrencyOption;

    partial void OnSearchTextChanged(string value) => _ = ApplyViewChange();
    partial void OnSelectedCurrencyFilterChanged(string value) => _ = ApplyViewChange();

    public StockTableViewModel(IDataBaseService dbService, IMarketDataService market,
        IStockService stocks, IServiceProvider services,
        ILogger<StockTableViewModel> logger) : base(dbService, logger)
    {
        Title = "Stocks";
        SortKey = "StockId";
        SortDesc = false;
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _services = services ?? throw new ArgumentNullException(nameof(services));

        CurrencyFilterOptions = new[] { AnyCurrencyOption }
            .Concat(CurrencyHelper.SupportedCurrencies.Select(c => c.ToString()))
            .ToList();
    }

    protected override async Task<(IReadOnlyList<StockTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        var stocks = await _market.GetAllStocksAsync(ct);

        // Apply free-text search via IStockService.Search when non-empty; otherwise
        // start from the full list.
        IEnumerable<Stock> source = string.IsNullOrWhiteSpace(SearchText)
            ? stocks
            : _stocks.Search(SearchText, take: int.MaxValue);

        // Currency filter: only include stocks that have a listing in the picked currency.
        if (!string.Equals(SelectedCurrencyFilter, AnyCurrencyOption, StringComparison.Ordinal)
            && Enum.TryParse<CurrencyType>(SelectedCurrencyFilter, out var ccyFilter))
        {
            source = source.Where(s => _stocks.IsListedIn(s.StockId, ccyFilter));
        }

        var rows = source.Select(BuildRow).ToList();

        IEnumerable<StockTableObject> ordered = (sortKey, desc) switch
        {
            ("CompanyName", true)   => rows.OrderByDescending(r => r.Stock.CompanyName, StringComparer.OrdinalIgnoreCase),
            ("CompanyName", false)  => rows.OrderBy(r => r.Stock.CompanyName, StringComparer.OrdinalIgnoreCase),
            ("Symbol",      true)   => rows.OrderByDescending(r => r.Stock.Symbol, StringComparer.OrdinalIgnoreCase),
            ("Symbol",      false)  => rows.OrderBy(r => r.Stock.Symbol, StringComparer.OrdinalIgnoreCase),
            ("Price",       true)   => rows.OrderByDescending(r => r.PrimaryPrice),
            ("Price",       false)  => rows.OrderBy(r => r.PrimaryPrice),
            ("Listings",    true)   => rows.OrderByDescending(r => r.ListingsCount),
            ("Listings",    false)  => rows.OrderBy(r => r.ListingsCount),
            ("ChangePct",   true)   => rows.OrderByDescending(r => r.ChangePct),
            ("ChangePct",   false)  => rows.OrderBy(r => r.ChangePct),
            (_,             true)   => rows.OrderByDescending(r => r.Stock.StockId),
            (_,             false)  => rows.OrderBy(r => r.Stock.StockId),
        };

        var sorted = ordered.ToList();
        var paged = sorted.Skip(skip).Take(take).ToList();
        return (paged, sorted.Count);
    }

    private StockTableObject BuildRow(Stock stock)
    {
        var listings = _stocks.GetListings(stock.StockId);

        // Primary listing first so its price drives sort/24h-change; non-primary listings tail.
        var ordered = listings.OrderByDescending(l => l.IsPrimary).ThenBy(l => l.Currency).ToList();

        var priceParts = new List<string>(ordered.Count);
        decimal primaryPrice = 0m;
        string changeDisp = "—";
        decimal changePct = 0m;
        bool bullish = false, bearish = false;

        foreach (var l in ordered)
        {
            decimal price;
            if (_market.Quotes.TryGetValue((stock.StockId, l.CurrencyType), out var quote)
                && quote.LastPrice > 0m)
            {
                price = quote.LastPrice;
                if (l.IsPrimary)
                {
                    changeDisp = quote.ChangePctDisplay;
                    changePct = quote.ChangePct;
                    bullish = quote.IsBullish;
                    bearish = quote.IsBearish;
                }
            }
            else
            {
                // Fall back to seed price when no quote has been recorded yet.
                price = l.SeedPrice;
            }
            priceParts.Add(CurrencyHelper.Format(price, l.CurrencyType));
            if (l.IsPrimary) primaryPrice = price;
        }

        var priceInline = priceParts.Count == 0 ? "—" : string.Join(" · ", priceParts);

        return new StockTableObject(stock, priceInline, primaryPrice, changeDisp, changePct,
            bullish, bearish, listings.Count, OpenEditAsync);
    }

    private async Task OpenEditAsync(Stock stock)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows?.FirstOrDefault()?.Page;
        if (page is null) return;

        var popup = _services.GetRequiredService<StockEditPopup>();
        popup.ViewModel.Initialize(stock);

        EventHandler? savedHandler = null;
        savedHandler = (_, _) => { _ = RefreshAsync(); };
        popup.ViewModel.Saved += savedHandler;
        try
        {
            await page.ShowPopupAsync(popup);
        }
        finally
        {
            popup.ViewModel.Saved -= savedHandler;
        }
    }
}

public partial class StockTableObject : ObservableObject
{
    public Stock Stock { get; }
    public string PriceInline { get; }
    public decimal PrimaryPrice { get; }
    public string ChangePctDisplay { get; }
    public decimal ChangePct { get; }
    public bool IsBullish { get; }
    public bool IsBearish { get; }
    public int ListingsCount { get; }
    public string ListingsBadge => ListingsCount.ToString();

    public IAsyncRelayCommand EditCommand { get; }

    public StockTableObject(Stock stock, string priceInline, decimal primaryPrice,
        string changePctDisplay, decimal changePct,
        bool isBullish, bool isBearish, int listingsCount,
        Func<Stock, Task> onEdit)
    {
        Stock = stock ?? throw new ArgumentNullException(nameof(stock));
        PriceInline = priceInline;
        PrimaryPrice = primaryPrice;
        ChangePctDisplay = changePctDisplay;
        ChangePct = changePct;
        IsBullish = isBullish;
        IsBearish = isBearish;
        ListingsCount = listingsCount;
        EditCommand = new AsyncRelayCommand(() => onEdit(Stock));
    }
}
