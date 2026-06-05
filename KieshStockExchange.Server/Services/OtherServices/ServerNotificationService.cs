using Dapper;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Data;
using KieshStockExchange.Server.Hubs;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using Microsoft.AspNetCore.SignalR;

namespace KieshStockExchange.Server.Services.OtherServices;

/// <summary>
/// Generates, persists (humans only) and pushes notifications. See
/// <see cref="IServerNotificationService"/>. Reuses the existing Messages table as
/// the persisted per-user inbox (Kind=Fill for trades), so no new table is needed.
/// </summary>
public sealed class ServerNotificationService : IServerNotificationService
{
    private readonly IDataBaseService _db;
    private readonly IHubContext<MarketHub> _hub;
    private readonly IMarketLookupService _lookup;
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<ServerNotificationService> _logger;

    // Human-user set cache. Humans = users with no AIUsers row. Membership changes
    // only on registration/admin, so a short TTL keeps the hot fill path off the DB.
    private static readonly TimeSpan HumanCacheTtl = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _humanGate = new(1, 1);
    private volatile HashSet<int> _humans = new();
    private DateTime _humansFetchedUtc = DateTime.MinValue;

    public ServerNotificationService(IDataBaseService db, IHubContext<MarketHub> hub,
        IMarketLookupService lookup, IDbConnectionFactory factory,
        ILogger<ServerNotificationService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task OnFillsAsync(IReadOnlyList<Transaction> fills, CancellationToken ct = default)
    {
        if (fills is null || fills.Count == 0) return;

        HashSet<int> humans;
        try { humans = await GetHumanUserIdsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification human-set lookup failed; skipping fill batch."); return; }
        if (humans.Count == 0) return;

        foreach (var fill in fills)
        {
            try
            {
                if (humans.Contains(fill.BuyerId))
                    await EmitFillAsync(fill, fill.BuyerId, isBuyer: true, ct).ConfigureAwait(false);
                if (humans.Contains(fill.SellerId))
                    await EmitFillAsync(fill, fill.SellerId, isBuyer: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fill notification failed for stock {StockId} (tx #{TxId}).", fill.StockId, fill.TransactionId);
            }
        }
    }

    public async Task OnOrderResultAsync(OrderResult result, int userId, CancellationToken ct = default)
    {
        if (result is null || userId <= 0) return;

        // Fills are notified by OnFillsAsync; only resting/failed outcomes belong here.
        if (result.Status is OrderStatus.Filled or OrderStatus.PartialFill) return;

        try
        {
            var humans = await GetHumanUserIdsAsync(ct).ConfigureAwait(false);
            if (!humans.Contains(userId)) return;

            var (title, content, kind) = await BuildFromOrderResultAsync(result, ct).ConfigureAwait(false);
            await PersistAndPushAsync(userId, title, content, kind, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order-result notification failed for user {UserId}.", userId);
        }
    }

    private async Task EmitFillAsync(Transaction fill, int userId, bool isBuyer, CancellationToken ct)
    {
        var symbol = await TryGetSymbolAsync(fill.StockId, ct).ConfigureAwait(false);
        var side = isBuyer ? "Buy" : "Sell";
        var price = CurrencyHelper.Format(fill.Price, fill.CurrencyType);

        var title = $"{symbol}: Order filled";
        var content = $"{side} {fill.Quantity} {symbol} @ {price}";
        await PersistAndPushAsync(userId, title, content, Message.MessageType.Fill, ct).ConfigureAwait(false);
    }

    private async Task<(string Title, string Content, Message.MessageType Kind)>
        BuildFromOrderResultAsync(OrderResult r, CancellationToken ct)
    {
        var o = r.PlacedOrder;
        var symbol = o is null ? "Order" : await TryGetSymbolAsync(o.StockId, ct).ConfigureAwait(false);

        if (!r.PlacedSuccessfully)
        {
            var reason = string.IsNullOrWhiteSpace(r.ErrorMessage) ? r.Status.ToString() : r.ErrorMessage;
            return ($"{symbol}: {r.Status}", reason, Message.MessageType.Error);
        }

        if (o is null)
            return ($"{symbol}: Order update", "Order details unavailable.", Message.MessageType.Info);

        // An armed trigger/trailing rests off-book until the market reaches its trigger.
        if (o.IsArmed)
            return ($"{symbol}: Trigger armed",
                    $"{o.OrderType} {o.Quantity} {symbol} @ trigger {o.StopPriceDisplay} — we'll place it when the market reaches your trigger.",
                    Message.MessageType.Info);

        // Only PlacedOnBook reaches here (Filled/PartialFill filtered upstream).
        return ($"{symbol}: Order #{o.OrderId} placed on book",
                $"{o.OrderType} {o.Quantity} {symbol} @ {o.PriceDisplay} — resting. We'll notify you on fills.",
                Message.MessageType.Info);
    }

    private async Task PersistAndPushAsync(int userId, string title, string content,
        Message.MessageType kind, CancellationToken ct)
    {
        var msg = new Message
        {
            UserId = userId,
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(title) ? "Notice" : title,
            Content = string.IsNullOrWhiteSpace(content) ? "—" : content,
            CreatedAt = TimeHelper.NowUtc(),
        };

        await _db.CreateMessage(msg, ct).ConfigureAwait(false); // assigns MessageId

        // Fire-and-forget transport — mirror SignalROrderCacheService: never block the
        // engine path on the hub, and never let a push failure propagate.
        _ = _hub.Clients.Group(MarketHub.GroupNameOrders(userId))
            .SendAsync("NotificationReceived", msg, CancellationToken.None)
            .ContinueWith(t =>
            {
                if (t.IsFaulted) _logger.LogWarning(t.Exception, "Failed to push NotificationReceived for user {UserId}.", userId);
            }, TaskScheduler.Default);
    }

    private async Task<string> TryGetSymbolAsync(int stockId, CancellationToken ct)
    {
        try
        {
            if (stockId <= 0) return "Stock";
            var stock = await _lookup.GetStockAsync(stockId, ct).ConfigureAwait(false);
            return stock?.Symbol ?? $"Stock #{stockId}";
        }
        catch { return $"Stock #{stockId}"; }
    }

    private async Task<HashSet<int>> GetHumanUserIdsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _humansFetchedUtc < HumanCacheTtl) return _humans;

        await _humanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _humansFetchedUtc < HumanCacheTtl) return _humans;

            await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
            // Reuse the EXISTS-AIUsers shape from RetentionService: a human is a user
            // with no matching AIUsers row.
            var ids = await c.QueryAsync<int>(new CommandDefinition(
                @"SELECT ""UserId"" FROM ""Users""
                  WHERE NOT EXISTS (SELECT 1 FROM ""AIUsers"" a WHERE a.""UserId"" = ""Users"".""UserId"")",
                cancellationToken: ct)).ConfigureAwait(false);

            _humans = ids.ToHashSet();
            _humansFetchedUtc = DateTime.UtcNow;
            return _humans;
        }
        finally { _humanGate.Release(); }
    }
}
