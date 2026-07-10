using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Services.BackgroundServices.Interfaces;

/// <summary>
/// Background service that uses configured <see cref="AIUser"/> bots
/// to place orders in the market at a fixed cadence.
/// </summary>
public interface IAiTradeService
{
    /// <summary>Interval between trading ticks for the AI loop.</summary>
    TimeSpan TradeInterval { get; }

    /// <summary>How often to run daily housekeeping checks.</summary>
    TimeSpan DailyCheckInterval { get; }

    /// <summary>How often to reload AI users' portfolios and cached prices.</summary>
    TimeSpan ReloadAssetsInterval { get; }

    /// <summary>How often to prune stale or capacity-blocking open limit orders.</summary>
    TimeSpan PruneInterval { get; }

    /// <summary>Currencies that the AI users are allowed to trade.</summary>
    IReadOnlyList<CurrencyType> CurrenciesToTrade { get; }

    /// <summary>Total bots loaded into the trading context.</summary>
    int LoadedBotCount { get; }

    /// <summary>Bots currently flagged as online and eligible to trade.</summary>
    int OnlineBotCount { get; }

    /// <summary>Optional cap on the number of online bots; null means no cap.</summary>
    int? ActiveBotCap { get; }

    /// <summary>User-configured hard ceiling on online bots; null means no ceiling.</summary>
    int? MaxBotCap { get; }

    /// <summary>EWMA-smoothed tick-work duration in milliseconds. 0 until first tick.</summary>
    double TickWorkMsEwma { get; }

    /// <summary>EWMA-smoothed "actionable" tick-work in ms — the Collect+Batch span the scaler can
    /// act on, excluding the cap-exempt cohorts. Always maintained (telemetry). 0 until first tick.</summary>
    double TickWorkActionableMsEwma { get; }

    /// <summary>Raw duration of the most recent tick's work in microseconds.</summary>
    long LastTickWorkMicros { get; }

    /// <summary>When true, the internal scaler adjusts <see cref="ActiveBotCap"/> based on tick-work load.</summary>
    bool AutoScale { get; set; }

    /// <summary>Floor on the active bot count when the scaler scales down.</summary>
    int MinBotCap { get; set; }

    /// <summary>Most recent EWMA / TradeInterval ratio observed by the scaler. 0 before any sample.</summary>
    double LastLoadFraction { get; }

    /// <summary>Number of trading-loop iterations completed in this session.</summary>
    long TickCount { get; }

    /// <summary>Successful order placements since the loop last started.</summary>
    long TradesPlacedThisSession { get; }

    /// <summary>Failed order placements since the loop last started.</summary>
    long FailuresThisSession { get; }

    /// <summary>UTC timestamp of the most recent fill, or null if no fills yet.</summary>
    DateTime? LastTradeAtUtc { get; }

    /// <summary>UTC timestamp of when the current loop started, or null when stopped.</summary>
    DateTime? LoopStartedAtUtc { get; }

    /// <summary>Snapshot of the most recent failure messages (bounded ring).</summary>
    IReadOnlyList<string> RecentFailures { get; }

    /// <summary>
    /// Aggregate failure counts grouped into UI-friendly buckets (insufficient
    /// shares, insufficient funds, engine error, ...). Keys are stable for the
    /// session; values are cumulative since the last <c>StartBotAsync</c>.
    /// </summary>
    IReadOnlyDictionary<FailureCategory, long> FailuresByCategory { get; }

    /// <summary>
    /// Aggregate failure counts grouped by <see cref="Models.Order.StockId"/>.
    /// Useful for spotting a single stock that's eating the failure budget.
    /// </summary>
    IReadOnlyDictionary<int, long> FailuresByStockId { get; }

    /// <summary>
    /// Snapshot of the structured failure records still in the bounded ring.
    /// Order is oldest → newest; older records may have been dropped if the
    /// session has produced more failures than the ring holds.
    /// </summary>
    IReadOnlyList<FailureRecord> RecentFailureRecords { get; }

    /// <summary>
    /// Writes the current <see cref="RecentFailureRecords"/> to a CSV file at
    /// <paramref name="path"/> and returns the path back. Throws if the file
    /// cannot be created; otherwise always succeeds, even when the ring is
    /// empty (an empty CSV with the header row is still useful). The dashboard
    /// resolves the path via a save-file picker so the caller never hard-codes
    /// the location.
    /// </summary>
    Task<string> ExportFailuresCsvAsync(string path, CancellationToken ct = default);

    /// <summary>In-memory CSV body for the failure ringbuffer (header + rows).
    /// Same shape ExportFailuresCsvAsync writes to disk; lets HTTP callers stream
    /// the body without staging a temp file on the server.</summary>
    string BuildFailuresCsv(CancellationToken ct = default);

    /// <summary>Clear all recorded failures — in-memory ring, aggregates, AND the persisted NDJSON (so old
    /// failures don't replay on restart). Backs the dashboard's "Clear failures" button.</summary>
    void ClearFailures();

    /// <summary>
    /// Suggested CSV file name (without an extension) for the save-file dialog
    /// the dashboard pops when the user exports failures. Centralised here so
    /// the format stays in lockstep with the file the service writes.
    /// </summary>
    string SuggestedFailuresExportFileName { get; }

    /// <summary>Number of mutation rows the reservation ledger currently holds.</summary>
    int ReservationLedgerEntryCount { get; }

    /// <summary>Suggested file name (without extension) for the ledger export dialog.</summary>
    string SuggestedLedgerExportFileName { get; }

    /// <summary>
    /// Writes every traced <c>Fund.ReservedBalance</c> mutation for the whitelisted
    /// users (see <see cref="Services.PortfolioServices.Helpers.IReservationLedger"/>)
    /// to a CSV at <paramref name="path"/>. Used to hunt the reservation leak by
    /// summing the per-call deltas offline.
    /// </summary>
    Task<string> ExportReservationLedgerCsvAsync(string path, CancellationToken ct = default);

    /// <summary>In-memory CSV body for the reservation ledger.</summary>
    string BuildReservationLedgerCsv(CancellationToken ct = default);

    /// <summary>Rows currently buffered by the economy-telemetry sampler.</summary>
    int EconomySampleCount { get; }

    /// <summary>Suggested file name (without extension) for the economy export dialog.</summary>
    string SuggestedEconomyExportFileName { get; }

    /// <summary>
    /// Writes every recorded economy snapshot (total wealth + per-snapshot drift
    /// summary) to a CSV at <paramref name="path"/>. Used to confirm wealth
    /// conservation and locate the stocks driving the 3.3 price drift.
    /// </summary>
    Task<string> ExportEconomyCsvAsync(string path, CancellationToken ct = default);

    /// <summary>In-memory CSV body for the economy ringbuffer.</summary>
    string BuildEconomyCsv(CancellationToken ct = default);

    /// <summary>Rows currently buffered by the sentiment sampler (one per stock per snapshot).</summary>
    int SentimentSampleCount { get; }

    /// <summary>Suggested file name (without extension) for the sentiment export dialog.</summary>
    string SuggestedSentimentExportFileName { get; }

    /// <summary>
    /// Writes every recorded per-stock sentiment snapshot (four per-stock factors
    /// + two global moods + combined sum) to a CSV at <paramref name="path"/>.
    /// </summary>
    Task<string> ExportSentimentCsvAsync(string path, CancellationToken ct = default);

    /// <summary>In-memory CSV body for the sentiment ringbuffer.</summary>
    string BuildSentimentCsv(CancellationToken ct = default);

    /// <summary>Raised after each trading-loop tick and on lifecycle changes.</summary>
    event EventHandler? StatsChanged;

    /// <summary>
    /// Adjusts the cadence and trading universe of the AI loop at runtime.
    /// Any null argument keeps the current value.
    /// </summary>
    void Configure(TimeSpan? tradeInterval = null,
        TimeSpan? dailyCheckInterval = null, TimeSpan? reloadAssetsInterval = null,
        IEnumerable<CurrencyType>? currencies = null, TimeSpan? pruneInterval = null);

    /// <summary>Sets a runtime cap on the number of online bots; null removes the cap.</summary>
    void SetActiveBotCap(int? cap);

    /// <summary>Sets the user-configured hard ceiling; null removes the ceiling.
    /// If the new max is lower than the current ActiveBotCap, the active cap is clamped down.</summary>
    void SetMaxBotCap(int? cap);

    /// <summary>Returns a snapshot of all loaded bot UserIds.</summary>
    IReadOnlyCollection<int> GetAiUserIds();

    /// <summary>§dashboard: maps each loaded bot's UserId to its trading strategy (for per-strategy
    /// aggregation of transactions, which key on UserId). Snapshot; safe to call off the loop thread.</summary>
    IReadOnlyDictionary<int, AiStrategy> GetBotStrategies();

    /// <summary>§dashboard: the latest per-strategy wealth/win/trade snapshot from the economy telemetry
    /// (refreshed on the ~60s economy log tick). Empty until the first priced snapshot.</summary>
    IReadOnlyList<StrategySnapshotRow> GetStrategySnapshot();

    /// <summary> Bounded ring of per-tick activity samples; survives bot stop/restart. </summary>
    IReadOnlyList<BotActivitySample> GetActivitySamples();

    /// <summary>
    /// Starts the background trading loop if it is not already running.
    /// Safe to call multiple times.
    /// </summary>
    Task StartBotAsync(CancellationToken ct = default);

    /// <summary>
    /// Requests the background trading loop to stop and waits for it to finish.
    /// </summary>
    Task StopBotAsync();
}
