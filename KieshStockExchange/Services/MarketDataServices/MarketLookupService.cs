using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed class MarketLookupService : IMarketLookupService
{
    private const decimal SeedPrice = 100m;

    private readonly IDataBaseService _db;
    private readonly ICandleService _candle;
    private readonly IStockService _stock;
    private readonly ILogger<MarketLookupService> _logger;

    public MarketLookupService(
        IDataBaseService db,
        ICandleService candle,
        IStockService stock,
        ILogger<MarketLookupService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _candle = candle ?? throw new ArgumentNullException(nameof(candle));
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default)
    {
        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
        _stock.TryGetById(stockId, out var stock);
        return stock;
    }

    public async Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default)
    {
        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _stock.All;
    }

    public async Task<decimal?> GetLatestPriceFromStoreAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Latest closed candle's close price (in-memory, no DB).
        var candle = _candle.TryGetLiveSnapshot(stockId, currency, CandleResolution.Default);
        if (candle is not null && candle.Close > 0m) return candle.Close;

        // Latest persisted transaction.
        var tx = await _db.GetLatestTransactionByStockId(stockId, currency, ct).ConfigureAwait(false);
        if (tx is not null && tx.Price > 0m) return tx.Price;

        _logger.LogDebug("No live price found for stock {StockId} in {Currency}", stockId, currency);

        // Latest persisted stock-price row.
        var sp = await _db.GetLatestStockPriceByStockId(stockId, currency, ct).ConfigureAwait(false);
        if (sp is not null && sp.Price > 0m) return sp.Price;

        // USD fallback (single hop — no infinite recursion because the inner call
        // takes the same path with currency==USD).
        if (currency != CurrencyType.USD)
        {
            var usd = await GetLatestPriceFromStoreAsync(stockId, CurrencyType.USD, ct).ConfigureAwait(false);
            if (usd is decimal u && u > 0m)
                return CurrencyHelper.Convert(u, CurrencyType.USD, currency);
        }

        return null;
    }

    public async Task<decimal> GetDateTimePriceAsync(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
    {
        // Try to get the price at or before the specified time
        var tx = await _db.GetLatestTransactionBeforeTime(stockId, currency, time, ct).ConfigureAwait(false);
        if (tx is not null && tx.Price > 0m) return tx.Price;

        // Fallback to latest StockPrice from DB
        var sp = await _db.GetLatestStockPriceBeforeTime(stockId, currency, time, ct).ConfigureAwait(false);
        if (sp is not null && sp.Price > 0m) return sp.Price;

        // Fallback to USD latest price converted
        if (currency != CurrencyType.USD)
        {
            var usdPrice = await GetDateTimePriceAsync(stockId, CurrencyType.USD, time, ct).ConfigureAwait(false);
            if (usdPrice > 0m) return CurrencyHelper.Convert(usdPrice, CurrencyType.USD, currency);
        }
        return SeedPrice;
    }

    public async Task<List<Transaction>> LoadHistoricalTicksAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        var (start, end) = TimeHelper.TodayUtcRange();

        var history = await _db.GetTransactionsByStockIdAndTimeRange(
            stockId, currency, start, end, ct).ConfigureAwait(false);
        if (history.Count == 0)
            _logger.LogWarning("No transactions found for stock {StockId} in {Currency}", stockId, currency);
        history.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return history;
    }

    public async Task<(decimal Price, DateTime TimeUtc)> GetFallbackPriceAndTimeAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Fallback to latest transaction price
        var tx = await _db.GetLatestTransactionByStockId(stockId, currency, ct).ConfigureAwait(false);
        if (tx is not null)
            return (tx.Price, TimeHelper.EnsureUtc(tx.Timestamp));

        // Fallback to latest stock price
        var sp = await _db.GetLatestStockPriceByStockId(stockId, currency, ct).ConfigureAwait(false);
        if (sp is not null)
            return (sp.Price, TimeHelper.EnsureUtc(sp.Timestamp));

        _logger.LogWarning("No transactions or stock prices found for stock {StockId} in {Currency}", stockId, currency);

        // Fallback to USD latest price converted
        if (currency != CurrencyType.USD)
        {
            var usd = await GetLatestPriceFromStoreAsync(stockId, CurrencyType.USD, ct).ConfigureAwait(false);
            if (usd is decimal u && u > 0m)
                return (CurrencyHelper.Convert(u, CurrencyType.USD, currency), TimeHelper.NowUtc());
        }

        _logger.LogWarning("No price data found for stock {StockId} in {Currency}, using default seed price.", stockId, currency);
        return (SeedPrice, TimeHelper.NowUtc());
    }
}
