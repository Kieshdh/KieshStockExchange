using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// §B3 lean reload — SERVER-ONLY bot-maintenance queries, implemented by <see cref="PgDBService"/> and
/// consumed only by <c>AiBotStateService</c>. Kept OFF the shared <c>IDataBaseService</c> deliberately so the
/// client <c>ApiDataBaseService</c> (which never runs the bot loop) is never touched — mirroring the
/// server-only-interface precedent (<c>IDbConnectionFactory</c>, <c>IOrderRegistry</c>).
///
/// Purpose: when <c>Bots:LeanReload</c> is on, the ~60s <c>RefreshAssetsAsync</c> no longer hydrates the
/// ~1.18M armed stops into <c>ctx.OpenOrders</c> (only the ~96k open limits). These queries serve the only two
/// consumers that still need armed-stop info — the open-order cap (a per-bot COUNT) and replace-old (a
/// per-(bot,stock,side) id lookup) — cheaply, so the maint reload stops tracking the stop pool. All read-only.
/// </summary>
public interface IBotMaintenanceQueries
{
    /// <summary>Open LIMIT orders only (Status=Open, Entry=Limit, Stop=None) for the given users — O(limits).</summary>
    Task<List<Order>> GetOpenLimitOrdersForUsersAsync(List<int> userIds, CancellationToken ct = default);

    /// <summary>Per-user count of ALL armed (Pending) stops — INCLUDING bracket children, so the count matches
    /// today's <c>ctx.OpenOrders[user].Count</c> contribution from armed stops (the cap counts them too).</summary>
    Task<Dictionary<int, int>> GetArmedStopCountsByUserAsync(List<int> userIds, CancellationToken ct = default);

    /// <summary>Order-ids of one bot's STANDALONE (ParentOrderId IS NULL) armed stops on a given (stock, side) —
    /// replace-old's victim set, replacing its in-memory <c>ctx.OpenOrders</c> scan when lean reload is on.</summary>
    Task<List<int>> GetStandaloneArmedStopIdsAsync(int userId, int stockId, OrderSide side, CancellationToken ct = default);
}
