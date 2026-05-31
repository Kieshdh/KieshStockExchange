using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.SignalR;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.OtherServices;

public sealed class NotificationService : INotificationService
{
    #region Fields and constructor
    private const int MaxBuffered = 50;

    private readonly ILogger<NotificationService> _logger;
    private readonly IStockService _stock;
    private readonly IDataBaseService _db;
    private readonly IMarketHubClient _hub;
    private readonly IUserSessionService _session;

    // Newest first. ConcurrentQueue would reverse-order; a lock + LinkedList
    // keeps Recent ordering trivial and bounded.
    private readonly LinkedList<Notification> _ring = new();
    private readonly object _ringLock = new();

    // Guards against re-hydrating the inbox on every SnapshotChanged (currency edits,
    // etc.). Only an actual user change triggers a reload.
    private int _hydratedUserId;

    public event EventHandler<Notification>? NotificationAdded;
    public event EventHandler? RecentReset;

    public IReadOnlyList<Notification> Recent
    {
        get
        {
            lock (_ringLock) return _ring.ToArray();
        }
    }

    public NotificationService(ILogger<NotificationService> logger, IStockService stock,
        IDataBaseService db, IMarketHubClient hub, IUserSessionService session)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        // Server is the source of truth: live pushes arrive here, and a login replaces
        // the ring with persisted history (replacing the old silent-baseline behaviour).
        _hub.NotificationReceived += OnServerNotification;
        _session.SnapshotChanged += OnSessionChanged;

        // If a session is already active when we're constructed, hydrate immediately.
        if (_session.IsAuthenticated && _session.UserId > 0)
            _ = HydrateAsync(_session.UserId);
    }
    #endregion

    #region Server sync (push + hydrate + read-state)
    private void OnSessionChanged(object? _, SessionSnapshot snapshot)
    {
        if (_session.IsAuthenticated && _session.UserId > 0)
        {
            if (_session.UserId != _hydratedUserId)
                _ = HydrateAsync(_session.UserId);
        }
        else if (_hydratedUserId != 0)
        {
            _hydratedUserId = 0;
            lock (_ringLock) _ring.Clear();
            RaiseRecentReset();
        }
    }

    private async Task HydrateAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            // GetMessagesByUserId returns newest-first; take a ring's worth.
            var msgs = await _db.GetMessagesByUserId(userId, onlyUnread: false, ct).ConfigureAwait(false);
            var notes = msgs.Take(MaxBuffered).Select(MapToNotification).ToList();

            lock (_ringLock)
            {
                _ring.Clear();
                foreach (var n in notes) _ring.AddLast(n);
            }
            _hydratedUserId = userId;
            _logger.LogInformation("Notification inbox hydrated for user {UserId}: {Count} items.", userId, notes.Count);
            RaiseRecentReset();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hydrate notification inbox for user {UserId}.", userId);
        }
    }

    private void OnServerNotification(object? _, Message m)
    {
        if (m is null) return;
        AddAndRaise(MapToNotification(m));
    }

    public async Task MarkAllReadAsync(CancellationToken ct = default)
    {
        var uid = _session.UserId;
        if (uid > 0)
        {
            try { await _db.MarkAllMessagesRead(uid, null, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "MarkAllMessagesRead failed for user {UserId}.", uid); }
        }
        lock (_ringLock)
            foreach (var n in _ring) n.IsRead = true;
    }

    public async Task MarkReadAsync(Notification notification, CancellationToken ct = default)
    {
        if (notification is null) return;
        notification.IsRead = true;
        if (notification.MessageId <= 0) return; // local-only toast, nothing to persist
        try { await _db.MarkMessageRead(notification.MessageId, null, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "MarkMessageRead failed for message {MessageId}.", notification.MessageId); }
    }

    private static Notification MapToNotification(Message m) => new()
    {
        MessageId = m.MessageId,
        Title = m.Title,
        Message = m.Content,
        Severity = KindToSeverity(m.Kind),
        IsRead = m.IsRead,
        TimestampUtc = m.CreatedAt,
    };

    private static NotificationSeverity KindToSeverity(Message.MessageType kind) => kind switch
    {
        Message.MessageType.Fill => NotificationSeverity.Success,
        Message.MessageType.Warning => NotificationSeverity.Warning,
        Message.MessageType.Error => NotificationSeverity.Error,
        _ => NotificationSeverity.Info,
    };

    private void RaiseRecentReset()
    {
        var handler = RecentReset;
        if (handler is null) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { handler.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.LogError(ex, "RecentReset handler threw."); }
        });
    }
    #endregion

    #region INotificationService Implementation
    public async Task NotifyOrderResultAsync(OrderResult result, CancellationToken ct = default)
    {
        if (result is null)
        {
            await PushNotificationAsync("Order", "Unknown result.", NotificationSeverity.Warning, ct).ConfigureAwait(false);
            return;
        }

        var (title, message, severity) = await BuildFromOrderResultAsync(result, ct).ConfigureAwait(false);
        await PushNotificationAsync(title, message, severity, ct).ConfigureAwait(false);
    }

    public async Task NotifyFillAsync(Order orderAfterFill, Transaction fill, CancellationToken ct = default)
    {
        if (orderAfterFill is null || fill is null)
        {
            await PushNotificationAsync("Order Update", "Invalid fill data.", NotificationSeverity.Warning, ct).ConfigureAwait(false);
            return;
        }

        var (title, message, severity) = await BuildFromFillAsync(orderAfterFill, fill, ct).ConfigureAwait(false);
        await PushNotificationAsync(title, message, severity, ct).ConfigureAwait(false);
    }

    public Task PushNotificationAsync(string title, string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        CancellationToken ct = default)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Notice" : title.Trim();
        message = string.IsNullOrWhiteSpace(message) ? "—" : message.Trim();

        var note = new Notification
        {
            Title = title,
            Message = message,
            Severity = severity
        };

        AddAndRaise(note);
        return Task.CompletedTask;
    }

    // Shared by local PushNotificationAsync and the server NotificationReceived push:
    // append newest-first, evict past the cap, and raise NotificationAdded on the UI
    // thread so ToastHost / TopNavBar can mutate ObservableCollections directly.
    private void AddAndRaise(Notification note)
    {
        Notification? evicted = null;
        lock (_ringLock)
        {
            _ring.AddFirst(note);
            if (_ring.Count > MaxBuffered)
            {
                evicted = _ring.Last!.Value;
                _ring.RemoveLast();
            }
        }

        _logger.LogInformation("Notify [{Severity}]: {Title} - {Message}", note.Severity, note.Title, note.Message);
        if (evicted is not null)
            _logger.LogDebug("Notification ring evicted oldest: {EvictedTitle}", evicted.Title);

        var handler = NotificationAdded;
        if (handler is not null)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { handler.Invoke(this, note); }
                catch (Exception ex) { _logger.LogError(ex, "NotificationAdded handler threw."); }
            });
        }
    }

    public void Clear()
    {
        lock (_ringLock) _ring.Clear();
    }
    #endregion

    #region Private helpers
    private async Task<(string Title, string Message, NotificationSeverity Severity)>
        BuildFromOrderResultAsync(OrderResult r, CancellationToken ct)
    {
        var o = r.PlacedOrder;
        var symbol = o is null ? "Order" : await TryGetSymbolAsync(o.StockId, ct).ConfigureAwait(false);

        if (!r.PlacedSuccessfully)
        {
            var reason = string.IsNullOrWhiteSpace(r.ErrorMessage) ? r.Status.ToString() : r.ErrorMessage;
            return ($"{symbol}: {r.Status}", reason, NotificationSeverity.Error);
        }

        if (o is null)
            return ($"{symbol}: Order update", "Order details unavailable.", NotificationSeverity.Info);

        var type = o.OrderType;
        var qty = o.Quantity;
        var avg = CurrencyHelper.Format(r.AverageFillPrice, o.CurrencyType);
        var rem = r.RemainingQuantity;

        switch (r.Status)
        {
            case OrderStatus.Filled:
                return ($"{symbol}: Order #{o.OrderId} filled",
                    $"{type} {qty} {symbol} @ {avg} (avg)\nOrder #{o.OrderId} completed.",
                    NotificationSeverity.Success);

            case OrderStatus.PartialFill:
                return ($"{symbol}: Order #{o.OrderId} partially filled",
                    $"{type} {r.TotalFilledQuantity}/{qty} {symbol} @ {avg} (avg) - Remaining: {rem}.",
                    NotificationSeverity.Info);

            case OrderStatus.PlacedOnBook:
                return ($"{symbol}: Order #{o.OrderId} placed on book",
                    $"{type} {qty} {symbol} @ {o.PriceDisplay}\nOrder #{o.OrderId} is resting. We'll notify you on fills.",
                    NotificationSeverity.Info);

            default:
                return ($"{symbol}: Order update", r.SuccessMessage, NotificationSeverity.Info);
        }
    }

    private async Task<(string Title, string Message, NotificationSeverity Severity)>
        BuildFromFillAsync(Order o, Transaction tx, CancellationToken ct)
    {
        var symbol = await TryGetSymbolAsync(o.StockId, ct).ConfigureAwait(false);
        var side = o.IsBuyOrder ? "Buy" : "Sell";

        return o.IsOpen
            ? ($"{symbol}: Partial fill",
                $"{side} {tx.Quantity} {symbol} @ {tx.PriceDisplay}\nFilled: {o.AmountFilled}/{o.Quantity} • Remaining: {o.RemainingQuantity}.",
                NotificationSeverity.Info)
            : ($"{symbol}: Order filled",
                $"{side} {tx.Quantity} {symbol} @ {tx.PriceDisplay}\nOrder #{o.OrderId} is now complete.",
                NotificationSeverity.Success);
    }

    private async Task<string> TryGetSymbolAsync(int stockId, CancellationToken ct)
    {
        try
        {
            if (stockId <= 0) return "Stock";

            await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_stock.TryGetById(stockId, out var stock))
                return stock!.Symbol;

            await _stock.RefreshAsync(ct).ConfigureAwait(false);
            if (_stock.TryGetById(stockId, out stock))
                return stock!.Symbol;

            return $"Stock #{stockId}";
        }
        catch { return $"Stock #{stockId}"; }
    }
    #endregion
}
