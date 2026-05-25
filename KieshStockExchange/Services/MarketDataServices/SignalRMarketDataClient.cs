using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.SignalR;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// Phase 3 finish — client-side IMarketDataService backed entirely by the
/// MarketHub. The engine no longer runs in-process; live quotes arrive as
/// "QuoteUpdated" pushes from <see cref="IMarketHubClient"/>. Subscribe/
/// Unsubscribe map onto hub group join/leave. Convenience lookups
/// (<see cref="GetStockAsync"/>, <see cref="GetAllStocksAsync"/>,
/// <see cref="GetLastPriceAsync"/>) defer to the existing in-app catalogue
/// cache and the HTTP-backed <see cref="IMarketLookupService"/>.
/// </summary>
public sealed class SignalRMarketDataClient : IMarketDataService, IAsyncDisposable
{
    private readonly IMarketHubClient _hub;
    private readonly IStockService _stocks;
    private readonly IMarketLookupService _lookup;
    private readonly ILogger<SignalRMarketDataClient> _logger;

    private readonly ConcurrentDictionary<(int stockId, CurrencyType currency), LiveQuote> _quotes = new();
    private readonly ConcurrentDictionary<(int stockId, CurrencyType currency), int> _subRefs = new();

    public IReadOnlyDictionary<(int stockId, CurrencyType currency), LiveQuote> Quotes => _quotes;
    public IReadOnlyCollection<(int, CurrencyType)> Subscribed => _subRefs.Keys.ToArray();

    public event EventHandler<LiveQuote>? QuoteUpdated;

    public SignalRMarketDataClient(IMarketHubClient hub, IStockService stocks,
        IMarketLookupService lookup, ILogger<SignalRMarketDataClient> logger)
    {
        _hub = hub;
        _stocks = stocks;
        _lookup = lookup;
        _logger = logger;
        _hub.QuoteUpdated += OnHubQuoteUpdated;
    }

    private void OnHubQuoteUpdated(object? sender, LiveQuote pushed)
    {
        // The wire instance is a fresh deserialization per push. Replace the dict
        // entry wholesale — INPC consumers re-read off the latest snapshot.
        _quotes[(pushed.StockId, pushed.Currency)] = pushed;
        try { QuoteUpdated?.Invoke(this, pushed); }
        catch (Exception ex) { _logger.LogWarning(ex, "QuoteUpdated subscriber threw for {Stock}/{Currency}", pushed.StockId, pushed.Currency); }
    }

    public Task SubscribeAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
        => SubscribeAsync(stockId, currency, forUi: true, ct);

    public async Task SubscribeAsync(int stockId, CurrencyType currency, bool forUi, CancellationToken ct = default)
    {
        // forUi is a server-side optimisation only (dispatch + debounce). Ignored
        // on the client now that the tick path is server-owned.
        _ = forUi;
        var key = (stockId, currency);
        var first = _subRefs.AddOrUpdate(key, 1, static (_, c) => c + 1) == 1;
        if (first)
            await _hub.JoinQuotesAsync(stockId, currency, ct).ConfigureAwait(false);
    }

    public Task Unsubscribe(int stockId, CurrencyType currency, CancellationToken ct = default)
        => Unsubscribe(stockId, currency, forUi: true, ct);

    public async Task Unsubscribe(int stockId, CurrencyType currency, bool forUi, CancellationToken ct = default)
    {
        _ = forUi;
        var key = (stockId, currency);
        var c = _subRefs.AddOrUpdate(key, 0, static (_, c) => Math.Max(0, c - 1));
        if (c == 0)
        {
            _subRefs.TryRemove(key, out _);
            _quotes.TryRemove(key, out _);
            await _hub.LeaveQuotesAsync(stockId, currency, ct).ConfigureAwait(false);
        }
    }

    public Task SubscribeAllAsync(CurrencyType currency, CancellationToken ct = default)
        => SubscribeAllAsync(currency, forUi: true, ct);

    public async Task SubscribeAllAsync(CurrencyType currency, bool forUi, CancellationToken ct = default)
    {
        await _stocks.EnsureLoadedAsync(ct).ConfigureAwait(false);
        foreach (var s in _stocks.All)
        {
            ct.ThrowIfCancellationRequested();
            await SubscribeAsync(s.StockId, currency, forUi, ct).ConfigureAwait(false);
        }
    }

    public Task UnsubscribeAllAsync(CurrencyType currency, CancellationToken ct = default)
        => UnsubscribeAllAsync(currency, forUi: true, ct);

    public async Task UnsubscribeAllAsync(CurrencyType currency, bool forUi, CancellationToken ct = default)
    {
        foreach (var key in _subRefs.Keys.Where(k => k.currency == currency).ToList())
        {
            ct.ThrowIfCancellationRequested();
            await Unsubscribe(key.stockId, currency, forUi, ct).ConfigureAwait(false);
        }
    }

    // Tick handling is server-owned now. These remain on the interface so the
    // contract is the same shape both sides; on the client they are no-ops.
    public Task OnTick(Transaction tick, CancellationToken ct = default) => Task.CompletedTask;
    public Task OnTicksAsync(IReadOnlyList<Transaction> ticks, CancellationToken ct = default) => Task.CompletedTask;

    public async Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Ensure the subscription is live; the LiveQuote will populate on the
        // next QuoteUpdated push. Callers that need an immediate seed price
        // call GetLastPriceAsync separately.
        await SubscribeAsync(stockId, currency, forUi: true, ct).ConfigureAwait(false);
    }

    public async Task<decimal> GetLastPriceAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Mirror the server's GetLastPriceAsync: check the live quote first
        // (already populated from the hub), then fall back to the store lookup.
        if (_quotes.TryGetValue((stockId, currency), out var q) && q.LastPrice > 0m)
            return q.LastPrice;
        var fromStore = await _lookup.GetLatestPriceFromStoreAsync(stockId, currency, ct).ConfigureAwait(false);
        return fromStore is decimal p && p > 0m ? p : 100m;
    }

    public Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default)
        => Task.FromResult(_stocks.TryGetById(stockId, out var s) ? s : null);

    public async Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default)
    {
        await _stocks.EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _stocks.All;
    }

    public ValueTask DisposeAsync()
    {
        _hub.QuoteUpdated -= OnHubQuoteUpdated;
        return ValueTask.CompletedTask;
    }
}
