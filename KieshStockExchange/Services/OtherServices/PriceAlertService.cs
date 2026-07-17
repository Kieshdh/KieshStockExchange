using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.OtherServices;

/// <summary>
/// Client-side, in-memory price-crossing alert evaluator — the non-chart half of the Alert tool
/// (the chart drawable that lets a user place/drag the alert line is a separate, still-churning
/// refactor, so this deliberately has no drawable/render dependency). Tracks the active
/// PriceAlerts and the last-seen price per (stockId, currency) off the shared
/// IMarketDataService.QuoteUpdated feed — the same DI-registered source SelectedStockService/
/// TrendingService consume, NOT the raw IMarketHubClient, so this stays decoupled from SignalR.
/// On a crossing of an armed alert, pushes a Warning notification and disarms it (one-shot; Add
/// re-arms). No persistence, no server round-trip — fires only while the app is open.
/// </summary>
public sealed class PriceAlertService : IPriceAlertService, IDisposable
{
    private readonly IMarketDataService _market;
    private readonly INotificationService _notifications;
    private readonly IStockService _stocks;
    private readonly ILogger<PriceAlertService> _logger;

    private readonly ConcurrentDictionary<Guid, PriceAlert> _alerts = new();

    // Seeded by the first quote per key so the very first tick never "crosses" from nothing.
    private readonly ConcurrentDictionary<(int stockId, CurrencyType currency), decimal> _lastPrices = new();

    public PriceAlertService(IMarketDataService market, INotificationService notifications,
        IStockService stocks, ILogger<PriceAlertService> logger)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _market.QuoteUpdated += OnQuoteUpdated;
    }

    public Guid Add(int stockId, CurrencyType currency, decimal level,
        AlertCondition condition = AlertCondition.CrossAny, string? note = null)
    {
        var alert = new PriceAlert
        {
            StockId = stockId,
            Currency = currency,
            Level = level,
            Condition = condition,
            Note = note,
            IsArmed = true,
        };
        _alerts[alert.Id] = alert;
        return alert.Id;
    }

    public bool Remove(Guid id) => _alerts.TryRemove(id, out _);

    public IReadOnlyList<PriceAlert> Snapshot() => _alerts.Values.ToList();

    // Quotes arrive off the SignalR receive thread, concurrently across stocks — everything
    // touched here is a ConcurrentDictionary, so no extra locking is needed.
    private void OnQuoteUpdated(object? sender, LiveQuote quote)
    {
        var newPrice = quote.LastPrice;
        if (newPrice <= 0m) return;

        var key = (quote.StockId, quote.Currency);
        var hadLast = _lastPrices.TryGetValue(key, out var lastPrice);
        _lastPrices[key] = newPrice;

        // First quote for this key just seeds the baseline — nothing to compare against yet.
        if (!hadLast || lastPrice == newPrice) return;

        foreach (var alert in _alerts.Values)
        {
            if (!alert.IsArmed || alert.StockId != quote.StockId || alert.Currency != quote.Currency)
                continue;

            if (!AlertCrossing.Crossed(lastPrice, newPrice, alert.Level, alert.Condition))
                continue;

            alert.IsArmed = false; // one-shot; Add() re-arms
            Fire(alert);
        }
    }

    private void Fire(PriceAlert alert)
    {
        var symbol = _stocks.TryGetById(alert.StockId, out var stock) ? stock!.Symbol : $"Stock #{alert.StockId}";
        var levelDisplay = CurrencyHelper.Format(alert.Level, alert.Currency);
        var message = string.IsNullOrWhiteSpace(alert.Note)
            ? $"{symbol} crossed {levelDisplay}"
            : $"{symbol} crossed {levelDisplay} — {alert.Note}";

        // PushNotificationAsync is safe to call off the UI thread: NotificationService only
        // mutates the ring under a lock and marshals NotificationAdded via
        // MainThread.BeginInvokeOnMainThread internally, so no extra dispatch is needed here.
        _ = _notifications.PushNotificationAsync($"{symbol} alert", message, NotificationSeverity.Warning);
        _logger.LogInformation("PriceAlert {AlertId} fired for stock {StockId}/{Currency} @ {Level}",
            alert.Id, alert.StockId, alert.Currency, alert.Level);
    }

    public void Dispose() => _market.QuoteUpdated -= OnQuoteUpdated;
}
