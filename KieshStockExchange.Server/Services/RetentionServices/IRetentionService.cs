namespace KieshStockExchange.Server.Services.RetentionServices;

/// <summary>
/// Server-side database history retention (Wave 8 §3). Bounds the two big
/// append-only tables (<c>Orders</c>, <c>Transactions</c>) with a time-window
/// prune and caps fine-grained <c>Candles</c>, while protecting open orders,
/// human-owned rows, and chart history. Runs raw batched SQL through
/// <c>IDbConnectionFactory</c>; does not touch the shared IDataBaseService contract.
/// </summary>
public interface IRetentionService
{
    /// <summary>
    /// Runs one retention cycle. When <paramref name="dryRun"/> is true, nothing
    /// is deleted and no candle backfill is performed — the report carries the
    /// counts that <em>would</em> be deleted, for inspection/calibration.
    /// </summary>
    Task<RetentionReport> RunOnceAsync(bool dryRun, CancellationToken ct = default);
}
