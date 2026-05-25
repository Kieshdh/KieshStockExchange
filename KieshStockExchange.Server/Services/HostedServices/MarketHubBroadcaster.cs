using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Hubs;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace KieshStockExchange.Server.Services.HostedServices;

// Phase 3 Step 6: bridges engine-side events into SignalR group broadcasts.
//
// Subscribes to:
//   IMarketDataService.QuoteUpdated      → quotes:{stockId}:{currency}
//   ICandleService.CandleClosed          → quotes:{stockId}:{currency}
//   IUserPortfolioService.SnapshotChanged → portfolio:{userId}
//
// Lives as IHostedService so the subscription wires up at server boot and
// detaches cleanly on shutdown. The IOrderCacheService bridge for orders:{userId}
// runs through SignalROrderCacheService instead (the engine already calls
// IOrderCacheService.NotifyOrdersMutated as part of every settle/cancel path,
// so reusing that surface is cheaper than adding a parallel event).
public sealed class MarketHubBroadcaster : IHostedService
{
    private readonly IHubContext<MarketHub> _hub;
    private readonly IMarketDataService _market;
    private readonly ICandleService _candles;
    private readonly IUserPortfolioService _portfolio;
    private readonly ILogger<MarketHubBroadcaster> _logger;

    public MarketHubBroadcaster(IHubContext<MarketHub> hub, IMarketDataService market,
        ICandleService candles, IUserPortfolioService portfolio, ILogger<MarketHubBroadcaster> logger)
    {
        _hub = hub;
        _market = market;
        _candles = candles;
        _portfolio = portfolio;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _market.QuoteUpdated += OnQuoteUpdated;
        _candles.CandleClosed += OnCandleClosed;
        _portfolio.SnapshotChanged += OnPortfolioChanged;
        _logger.LogInformation("MarketHubBroadcaster subscribed to engine events.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _market.QuoteUpdated -= OnQuoteUpdated;
        _candles.CandleClosed -= OnCandleClosed;
        _portfolio.SnapshotChanged -= OnPortfolioChanged;
        return Task.CompletedTask;
    }

    private void OnQuoteUpdated(object? sender, LiveQuote quote)
    {
        var group = MarketHub.GroupNameQuotes(quote.StockId, quote.Currency);
        _ = _hub.Clients.Group(group).SendAsync("QuoteUpdated", quote)
            .ContinueWith(t =>
            {
                if (t.IsFaulted) _logger.LogWarning(t.Exception, "QuoteUpdated push failed for {Group}", group);
            }, TaskScheduler.Default);
    }

    private void OnCandleClosed(object? sender, Candle candle)
    {
        var group = MarketHub.GroupNameQuotes(candle.StockId, candle.CurrencyType);
        _ = _hub.Clients.Group(group).SendAsync("CandleClosed", candle)
            .ContinueWith(t =>
            {
                if (t.IsFaulted) _logger.LogWarning(t.Exception, "CandleClosed push failed for {Group}", group);
            }, TaskScheduler.Default);
    }

    private void OnPortfolioChanged(object? sender, EventArgs e)
    {
        // IUserPortfolioService.SnapshotChanged carries no userId on the wire today;
        // the snapshot itself is on _portfolio.Snapshot. For now we broadcast a
        // bare "refresh" signal to all portfolio: groups — Phase 5's per-user
        // identity will route this per logged-in user instead.
        if (_portfolio.Snapshot is null) return;
        var userId = 0; // placeholder until user-scoped portfolio snapshots land
        _ = _hub.Clients.Group(MarketHub.GroupNamePortfolio(userId))
            .SendAsync("PortfolioChanged", _portfolio.Snapshot)
            .ContinueWith(t =>
            {
                if (t.IsFaulted) _logger.LogWarning(t.Exception, "PortfolioChanged push failed");
            }, TaskScheduler.Default);
    }
}
