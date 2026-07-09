using Dapper;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// §B3 lean reload — <see cref="IBotMaintenanceQueries"/> on PgDBService. Read-only; never mutates.
/// The shared <see cref="GetOpenOrdersForUsersAsync"/> is INTENTIONALLY left unchanged (it still returns armed
/// stops, which AccountsCache.EnsureLoadedAsync uses to re-seed reservations on cold-load) — these are separate
/// narrower queries, not a narrowing of that one.
/// Perf: the count/lookup are only index-only with the partial indexes added in KseDbContext
/// (IX_Orders_ArmedStop_User, IX_Orders_ArmedStandalone_User_Stock_Side); without them they scan the Pending pool.
/// </summary>
public sealed partial class PgDBService : IBotMaintenanceQueries
{
    public async Task<List<Order>> GetOpenLimitOrdersForUsersAsync(List<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return new List<Order>();
        await using var c = await OpenAsync(ct);
        // Clone of GetOpenOrdersForUsersAsync (:151) minus its second OR-branch (the armed-stop branch).
        var rows = await c.QueryAsync<OrderRow>($@"
            SELECT {OrderCols} FROM ""Orders""
            WHERE ""UserId"" = ANY(@ids)
              AND ""Status"" = @open AND ""Entry"" = 'Limit' AND ""Stop"" = 'None'",
            new
            {
                ids = userIds.Distinct().ToArray(),
                open = Order.Statuses.Open,
            });
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    private sealed class ArmedStopCountRow { public int UserId { get; set; } public int Count { get; set; } }

    public async Task<Dictionary<int, int>> GetArmedStopCountsByUserAsync(List<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return new Dictionary<int, int>();
        await using var c = await OpenAsync(ct);
        // COUNT ALL Pending stops (bracket children INCLUDED) so this equals today's armed-stop contribution to
        // ctx.OpenOrders[user].Count. Dapper binds by property name — alias COUNT(*) AS "Count".
        var rows = await c.QueryAsync<ArmedStopCountRow>($@"
            SELECT ""UserId"", COUNT(*) AS ""Count"" FROM ""Orders""
            WHERE ""UserId"" = ANY(@ids) AND ""Status"" = @pending AND ""Stop"" <> 'None'
            GROUP BY ""UserId""",
            new
            {
                ids = userIds.Distinct().ToArray(),
                pending = Order.Statuses.Pending,
            });
        return rows.ToDictionary(r => r.UserId, r => r.Count);
    }

    public async Task<List<int>> GetStandaloneArmedStopIdsAsync(int userId, int stockId, OrderSide side, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        // STANDALONE only (ParentOrderId IS NULL) — replace-old must not cancel bracket SL legs. Side is text
        // by convention (like Currency), so pass side.ToString().
        var rows = await c.QueryAsync<int>($@"
            SELECT ""OrderId"" FROM ""Orders""
            WHERE ""UserId"" = @userId AND ""StockId"" = @stockId AND ""Side"" = @side
              AND ""Status"" = @pending AND ""Stop"" <> 'None' AND ""ParentOrderId"" IS NULL",
            new
            {
                userId,
                stockId,
                side = side.ToString(),
                pending = Order.Statuses.Pending,
            });
        return rows.ToList();
    }
}
