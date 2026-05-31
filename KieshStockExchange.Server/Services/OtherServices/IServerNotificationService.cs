using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices;

namespace KieshStockExchange.Server.Services.OtherServices;

/// <summary>
/// Server-side notification generator. Replaces the old client-side
/// NotificationBridgeService: the engine now hands fills/placement results here,
/// and this service persists a <see cref="Message"/> row (humans only) and pushes
/// it live to the user's orders:{userId} SignalR group as "NotificationReceived".
/// Bots never produce a row. Every path is catch-logged so a notification failure
/// can never break settlement.
/// </summary>
public interface IServerNotificationService
{
    /// <summary>
    /// Emit fill notifications for the human counterparties of a settled fill batch.
    /// Call at every fill-publish site, paired with IMarketDataService.OnTicksAsync.
    /// </summary>
    Task OnFillsAsync(IReadOnlyList<Transaction> fills, CancellationToken ct = default);

    /// <summary>
    /// Emit a placement-outcome notification (resting on book, or a failure) for a
    /// human placer. Fills are covered by <see cref="OnFillsAsync"/>, so fully/partly
    /// filled results are intentionally skipped here to avoid double-notifying.
    /// </summary>
    Task OnOrderResultAsync(OrderResult result, int userId, CancellationToken ct = default);
}
