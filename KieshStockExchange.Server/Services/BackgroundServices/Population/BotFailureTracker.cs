using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Owns the bounded failure ring + per-category/per-stock aggregates used by
/// the bot dashboard and CSV export. AiTradeService calls <see cref="Record"/>
/// on every Phase-3 failure and exposes the snapshots through delegate
/// properties so the live <see cref="ConcurrentDictionary"/>s stay private.
/// </summary>
internal sealed class BotFailureTracker
{
    #region Services and Constructor
    // Bounded ring for the dashboard + CSV export. At 20k bots the failure rate
    // can hit ~5k/min — 5000 records covers roughly one minute. Aggregates
    // below are unbounded and survive ring eviction so totals stay accurate
    // even when raw rows roll off.
    private const int RecentFailuresMax = 5000;

    private readonly IStockService _stocks;
    private readonly ILogger<BotFailureTracker> _logger;

    private readonly Queue<FailureRecord> _recentFailures = new();
    private readonly ConcurrentDictionary<FailureCategory, long> _failuresByCategory = new();
    private readonly ConcurrentDictionary<int, long> _failuresByStockId = new();
    private readonly RingBufferStore<FailureRecord> _store;

    internal BotFailureTracker(IStockService stocks, ILogger<BotFailureTracker> logger)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = new RingBufferStore<FailureRecord>("data/telemetry/bot_failures.ndjson");

        // Replay the tail back into the in-memory ring + aggregates so the
        // dashboard shows the prior session's failures after a server restart.
        var prior = _store.LoadTail(RecentFailuresMax);
        foreach (var r in prior)
        {
            _failuresByCategory.AddOrUpdate(r.Category, 1L, static (_, n) => n + 1);
            if (r.StockId > 0)
                _failuresByStockId.AddOrUpdate(r.StockId, 1L, static (_, n) => n + 1);
            _recentFailures.Enqueue(r);
        }
        if (prior.Count > 0)
            _logger.LogInformation("BotFailureTracker: replayed {Count} failure record(s) from disk.", prior.Count);
    }
    #endregion

    #region Recording
    internal void Record(FailureRecord record)
    {
        // Aggregates first (lock-free) so even if ring eviction races we never
        // lose a count. Stock-id 0 lumps together pre-stockId engine errors —
        // skip it to keep the per-stock breakdown legible.
        _failuresByCategory.AddOrUpdate(record.Category, 1L, static (_, n) => n + 1);
        if (record.StockId > 0)
            _failuresByStockId.AddOrUpdate(record.StockId, 1L, static (_, n) => n + 1);

        lock (_recentFailures)
        {
            _recentFailures.Enqueue(record);
            while (_recentFailures.Count > RecentFailuresMax) _recentFailures.Dequeue();
        }
        _store.Append(record);
    }

    internal void Reset()
    {
        lock (_recentFailures) _recentFailures.Clear();
        _failuresByCategory.Clear();
        _failuresByStockId.Clear();
    }

    /// <summary>Clear the in-memory ring + aggregates AND truncate the persisted NDJSON so old failures
    /// don't replay on the next restart. Backs the dashboard's "Clear failures" action.</summary>
    internal void ClearAll()
    {
        Reset();
        _store.Clear();
    }
    #endregion

    #region Snapshots
    internal IReadOnlyList<string> RecentFailures
    {
        get
        {
            lock (_recentFailures)
            {
                if (_recentFailures.Count == 0) return Array.Empty<string>();
                var copy = new string[_recentFailures.Count];
                int i = 0;
                foreach (var r in _recentFailures) copy[i++] = FormatLine(r);
                return copy;
            }
        }
    }

    internal IReadOnlyList<FailureRecord> RecentFailureRecords
    {
        get { lock (_recentFailures) return _recentFailures.ToArray(); }
    }

    internal IReadOnlyDictionary<FailureCategory, long> FailuresByCategory
    {
        // Materialise so callers can't observe the ConcurrentDictionary mutating
        // beneath them. Cheap (~7 entries max).
        get
        {
            var copy = new Dictionary<FailureCategory, long>(_failuresByCategory.Count);
            foreach (var kv in _failuresByCategory) copy[kv.Key] = kv.Value;
            return copy;
        }
    }

    internal IReadOnlyDictionary<int, long> FailuresByStockId
    {
        get
        {
            var copy = new Dictionary<int, long>(_failuresByStockId.Count);
            foreach (var kv in _failuresByStockId) copy[kv.Key] = kv.Value;
            return copy;
        }
    }
    #endregion

    #region Export
    internal string SuggestedExportFileName =>
        $"bot_failures_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}";

    // Phase 3 split: BuildCsv produces the body in-memory so the admin HTTP
    // endpoint can stream it directly; ExportCsvAsync stays as a thin file
    // writer for any future on-server export job.
    internal string BuildCsv(CancellationToken ct = default)
    {
        FailureRecord[] snapshot;
        lock (_recentFailures) snapshot = _recentFailures.ToArray();

        var sb = new StringBuilder(2048 + snapshot.Length * 96);
        sb.AppendLine("TimestampUtc,AiUserId,UserId,StockId,Symbol,Side,Type,Quantity,Price,Category,Status,ErrorMessage");
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = snapshot[i];
            _stocks.TryGetSymbol(r.StockId, out var symbol);
            sb.Append(r.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.AiUserId).Append(',')
              .Append(r.UserId).Append(',')
              .Append(r.StockId).Append(',')
              .Append(EscapeCsv(symbol ?? string.Empty)).Append(',')
              .Append(EscapeCsv(r.Side)).Append(',')
              .Append(EscapeCsv(r.OrderType)).Append(',')
              .Append(r.Quantity).Append(',')
              .Append(r.Price.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Category).Append(',')
              .Append(r.Status).Append(',')
              .Append(EscapeCsv(r.ErrorMessage))
              .Append('\n');
        }
        return sb.ToString();
    }

    internal async Task<string> ExportCsvAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is required.", nameof(path));
        var csv = BuildCsv(ct);
        await File.WriteAllTextAsync(path, csv, ct).ConfigureAwait(false);
        _logger.LogInformation("Exported bot failure records to {Path}.", path);
        return path;
    }

    private static string FormatLine(FailureRecord r) =>
        $"{r.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  AIUser {r.AiUserId} stock {r.StockId}: " +
        $"{r.Category.DisplayName()} — {r.ErrorMessage}";

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        bool needsQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    #endregion
}
