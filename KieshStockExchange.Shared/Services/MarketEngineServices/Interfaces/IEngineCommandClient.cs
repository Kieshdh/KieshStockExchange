using KieshStockExchange.Services.MarketEngineServices.CommandDtos;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

/// <summary>
/// Routes the engine's multi-write transactional operations through a single HTTP
/// bundle endpoint each. Replaces the pattern of
/// <c>_db.BeginTransactionAsync() + multiple _db.X(...) + tx.CommitAsync()</c> on
/// the client side, since HTTP transport doesn't carry SQLite transactions.
///
/// Each bundle method maps 1:1 to a <c>POST /engine/...</c> or <c>/portfolio/...</c>
/// endpoint on the server. The server opens a single <c>RunInTransactionAsync</c>
/// over the bundle payload's writes.
/// </summary>
public interface IEngineCommandClient
{
    /// <summary>
    /// Persists a freshly placed order plus any cache-side reservation deltas (fund for
    /// buy orders, position for sell orders). Returns the order with its assigned
    /// OrderId so the engine can register it in IOrderRegistry.
    /// </summary>
    Task<SettleSingleOrderResult> SettleSingleOrderAsync(SettleSingleOrderCommand cmd, CancellationToken ct = default);

    /// <summary>
    /// Bulk-insert orders for the batch-match path; server returns the list with
    /// OrderIds assigned in payload order so the matcher can refer to canonical refs.
    /// </summary>
    Task<PlaceOrdersBatchResult> PlaceOrdersBatchAsync(PlaceOrdersBatchCommand cmd, CancellationToken ct = default);

    /// <summary>
    /// Persists the entire output of a per-book settlement: trade rows, order
    /// updates, fund/position updates, and any new position rows. Returns the trades
    /// (with TransactionIds) and any new positions (with PositionIds).
    /// </summary>
    Task<SettleTradeGroupResult> SettleTradeGroupAsync(SettleTradeGroupCommand cmd, CancellationToken ct = default);

    /// <summary>
    /// Persists an order modify operation plus any reservation deltas. No return
    /// payload — the engine already mutated its in-memory copies before calling.
    /// </summary>
    Task ApplyOrderChangeAsync(ApplyOrderChangeCommand cmd, CancellationToken ct = default);

    /// <summary>
    /// Persists a deposit or withdrawal: Fund upsert + FundTransaction audit row.
    /// Returns false on insufficient funds (Withdrawal) or any validation failure.
    /// </summary>
    Task<bool> DepositWithdrawAsync(DepositWithdrawCommand cmd, CancellationToken ct = default);

    /// <summary>
    /// Persists a paired FX conversion: source fund withdrawal + target fund deposit
    /// + paired ConversionOut / ConversionIn audit rows. Returns false on insufficient
    /// funds in the source currency.
    /// </summary>
    Task<bool> ConvertInternalAsync(ConvertInternalCommand cmd, CancellationToken ct = default);
}
