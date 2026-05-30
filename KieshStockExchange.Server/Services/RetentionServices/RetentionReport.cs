namespace KieshStockExchange.Server.Services.RetentionServices;

/// <summary>
/// Outcome of one retention cycle. Counts are "deleted" for a real run and
/// "would delete" for a dry run. Skipped keys are <c>(stockId, currency)</c>
/// pairs whose transactions were held back because their candle coverage failed
/// the gate — they get retried next cycle once the backfill catches up.
/// </summary>
public sealed class RetentionReport
{
    public bool DryRun { get; init; }
    public DateTime OrderCutoffUtc { get; init; }
    public DateTime TransactionCutoffUtc { get; init; }
    public DateTime CandleFineCutoffUtc { get; init; }
    public DateTime CandleCoarseCutoffUtc { get; init; }

    public long OrdersDeleted { get; set; }
    public long TransactionsDeleted { get; set; }
    public long CandlesFineDeleted { get; set; }
    public long CandlesCoarseDeleted { get; set; }

    public List<string> SkippedTransactionKeys { get; } = new();
    public long ElapsedMs { get; set; }

    public long CandlesDeleted => CandlesFineDeleted + CandlesCoarseDeleted;

    public override string ToString() =>
        $"Retention[{(DryRun ? "dry-run" : "live")}]: " +
        $"orders={OrdersDeleted}, tx={TransactionsDeleted}, " +
        $"candlesFine={CandlesFineDeleted}, candlesCoarse={CandlesCoarseDeleted}, " +
        $"skippedTxKeys={SkippedTransactionKeys.Count}, elapsed={ElapsedMs}ms";
}
