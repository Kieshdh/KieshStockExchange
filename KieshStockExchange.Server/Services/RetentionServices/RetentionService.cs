using System.Diagnostics;
using Dapper;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Data;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace KieshStockExchange.Server.Services.RetentionServices;

/// <summary>
/// Three-tier history prune (Wave 8 §3). See <see cref="IRetentionService"/>.
/// <para>
/// Tier 1 (Orders): delete bot-owned <em>terminal</em> orders older than the
/// window. Open orders and human-owned orders are never touched. Resolved via a
/// PK boundary because <c>Orders</c> has no time index (only User/Stock+Status).
/// </para>
/// <para>
/// Tier 2 (Transactions): gated. Candles are derived from transactions, so before
/// deleting a window's transactions we backfill the coarse candles
/// (<c>BackfillUpwardAsync</c> regenerates 15m/1h/4h/1d), then verify per
/// <c>(stockId, currency)</c> that the verification-resolution candles (default
/// 15m — the finest resolution the backfill regenerates and the resolution
/// old-range charts actually render) cover the trades being removed. A key that
/// fails verification is skipped this cycle and retried next. Only transactions
/// where <em>both</em> counterparties are bots are deleted (human trade history is
/// preserved). Served by IX_Tx_Stock_Curr_Time.
/// </para>
/// <para>
/// Tier 3 (Candles): tiered cap. Fine candles (≤ FineMaxBucketSeconds → 15s/1m/5m)
/// keep a moderate window; coarse candles (&gt; that) keep ~forever. No gate.
/// </para>
/// Each batch autocommits (no giant transaction) so dead tuples become vacuumable
/// immediately and locks stay brief — the engine keeps trading through the prune.
/// Whole-table boundary/count scans on the 20M-row tables exceed Npgsql's 30s
/// default, so every command carries an explicit <c>Retention:CommandTimeoutSeconds</c>.
/// </summary>
public sealed class RetentionService : IRetentionService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ICandleService _candles;
    private readonly IConfiguration _config;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        IDbConnectionFactory factory,
        ICandleService candles,
        IConfiguration config,
        ILogger<RetentionService> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _config  = config  ?? throw new ArgumentNullException(nameof(config));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    private sealed record RetentionOptions(
        int OrderWindowHours,
        int TransactionWindowHours,
        int CandleFineDays,
        int CandleFineMaxBucketSeconds,
        int CandleCoarseDays,
        int BatchSize,
        long MaxDeletesPerCycle,
        bool VerifyCandlesBeforeTxPrune,
        double CandleCoverageTolerance,
        int CandleVerifyBucketSeconds,
        int CandleGapFillLookbackDays,
        int CommandTimeoutSeconds);

    private RetentionOptions ReadOptions() => new(
        OrderWindowHours:           _config.GetValue("Retention:OrderWindowHours", 48),
        TransactionWindowHours:     _config.GetValue("Retention:TransactionWindowHours", 48),
        CandleFineDays:             _config.GetValue("Retention:CandleFineDays", 90),
        CandleFineMaxBucketSeconds: _config.GetValue("Retention:CandleFineMaxBucketSeconds", 300),
        CandleCoarseDays:           _config.GetValue("Retention:CandleCoarseDays", 3650),
        BatchSize:                  Math.Max(1, _config.GetValue("Retention:BatchSize", 20000)),
        MaxDeletesPerCycle:         Math.Max(0, _config.GetValue("Retention:MaxDeletesPerCycle", 2_000_000L)),
        VerifyCandlesBeforeTxPrune: _config.GetValue("Retention:VerifyCandlesBeforeTxPrune", true),
        CandleCoverageTolerance:    _config.GetValue("Retention:CandleCoverageTolerance", 0.95),
        // 15m (900s). The Tier-2 gap-fill (CandleService.FillCandleGapsAsync) densifies
        // the fine rungs first, so verifying at 15m — the resolution old-range charts
        // render — closes reliably. Drop to 300 (5m) once coverage proves complete.
        CandleVerifyBucketSeconds:  _config.GetValue("Retention:CandleVerifyBucketSeconds", 900),
        // Window the gap-fill covers each cycle. First cycle on a backlog is the costly
        // one (idempotent thereafter); lower it if that cycle runs long.
        CandleGapFillLookbackDays:  Math.Max(1, _config.GetValue("Retention:CandleGapFillLookbackDays", 60)),
        // Whole-table scans exceed Npgsql's 30s default; floor at 30.
        CommandTimeoutSeconds:      Math.Max(30, _config.GetValue("Retention:CommandTimeoutSeconds", 300)));

    public async Task<RetentionReport> RunOnceAsync(bool dryRun, CancellationToken ct = default)
    {
        var opt = ReadOptions();
        var now = TimeHelper.NowUtc();
        var sw = Stopwatch.StartNew();

        var report = new RetentionReport
        {
            DryRun = dryRun,
            OrderCutoffUtc        = now.AddHours(-opt.OrderWindowHours),
            TransactionCutoffUtc  = now.AddHours(-opt.TransactionWindowHours),
            CandleFineCutoffUtc   = now.AddDays(-opt.CandleFineDays),
            CandleCoarseCutoffUtc = now.AddDays(-opt.CandleCoarseDays),
        };

        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await PruneOrdersAsync(conn, opt, report, ct).ConfigureAwait(false);
        await PruneTransactionsAsync(conn, opt, report, dryRun, ct).ConfigureAwait(false);
        await PruneCandlesAsync(conn, opt, report, ct).ConfigureAwait(false);

        report.ElapsedMs = sw.ElapsedMilliseconds;
        _logger.LogInformation("{Report}", report.ToString());
        return report;

        // Local funcs capture `dryRun` for the count-vs-delete branch.
        async Task PruneOrdersAsync(NpgsqlConnection c, RetentionOptions o, RetentionReport r, CancellationToken token)
        {
            // OrderId is an int identity column → monotonic with CreatedAt, so resolve
            // a PK boundary once (Orders has no CreatedAt index) and delete by range.
            var boundary = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                @"SELECT MAX(""OrderId"") FROM ""Orders"" WHERE ""CreatedAt"" < @cutoff",
                new { cutoff = r.OrderCutoffUtc }, commandTimeout: o.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false);
            if (boundary is null) return;

            const string where =
                @"""OrderId"" <= @boundary
                  AND ""Status"" IN (@filled, @cancelled)
                  AND EXISTS (SELECT 1 FROM ""AIUsers"" a WHERE a.""UserId"" = ""Orders"".""UserId"")";
            var args = new { boundary, filled = Order.Statuses.Filled, cancelled = Order.Statuses.Cancelled };

            r.OrdersDeleted = dryRun
                ? await CountAsync(c, "Orders", where, args, o.CommandTimeoutSeconds, token).ConfigureAwait(false)
                : await BatchDeleteAsync(c, "Orders", where, args, o.BatchSize, o.MaxDeletesPerCycle, o.CommandTimeoutSeconds, token).ConfigureAwait(false);
        }

        async Task PruneTransactionsAsync(NpgsqlConnection c, RetentionOptions o, RetentionReport r, bool dry, CancellationToken token)
        {
            // Gate step 1: make candles whole before verifying, so post-prune chart
            // ranges still render. Skipped on dry runs (both are writes).
            //   (a) gap-fill: synthesize missing fine candles (5m/1m) from finer rungs —
            //       this is what lets the gate verify at 15m/5m instead of only 1h.
            //   (b) upward backfill: aggregate the now-denser 5m up to 15m/1h/4h/1d.
            if (!dry)
            {
                var gapTo = TimeHelper.NowUtc();
                var gapFrom = gapTo.AddDays(-o.CandleGapFillLookbackDays);
                await _candles.FillCandleGapsAsync(CurrencyHelper.SupportedCurrencies, gapFrom, gapTo, token).ConfigureAwait(false);
                await _candles.BackfillUpwardAsync(CurrencyHelper.SupportedCurrencies, token).ConfigureAwait(false);
            }

            // Every (stockId, currency) with transactions older than the cutoff.
            var keys = (await c.QueryAsync<TxKey>(new CommandDefinition(
                @"SELECT DISTINCT ""StockId"", ""Currency"" FROM ""Transactions"" WHERE ""Timestamp"" < @cutoff",
                new { cutoff = r.TransactionCutoffUtc }, commandTimeout: o.CommandTimeoutSeconds,
                cancellationToken: token)).ConfigureAwait(false)).ToList();

            // Both counterparties must be bots → delete (preserve any human trade).
            const string botBoth =
                @"EXISTS (SELECT 1 FROM ""AIUsers"" ab  WHERE ab.""UserId""  = ""Transactions"".""BuyerId"")
                  AND EXISTS (SELECT 1 FROM ""AIUsers"" asl WHERE asl.""UserId"" = ""Transactions"".""SellerId"")";
            string deleteWhere =
                @"""StockId"" = @sid AND ""Currency"" = @ccy AND ""Timestamp"" < @cutoff AND " + botBoth;

            long budget = o.MaxDeletesPerCycle;
            foreach (var key in keys)
            {
                if (token.IsCancellationRequested || (!dry && budget <= 0)) break;
                var (sid, ccy) = (key.StockId, key.Currency);

                if (o.VerifyCandlesBeforeTxPrune)
                {
                    var verified = await VerifyCandleCoverageAsync(
                        c, sid, ccy, r.TransactionCutoffUtc, o.CandleVerifyBucketSeconds,
                        o.CandleCoverageTolerance, o.CommandTimeoutSeconds, token).ConfigureAwait(false);
                    if (!verified)
                    {
                        r.SkippedTransactionKeys.Add($"{sid}/{ccy}");
                        _logger.LogWarning(
                            "Retention: candle coverage gate failed for stock={Stock} ccy={Ccy}; keeping its transactions this cycle.",
                            sid, ccy);
                        continue;
                    }
                }

                var args = new { sid, ccy, cutoff = r.TransactionCutoffUtc };
                if (dry)
                {
                    r.TransactionsDeleted += await CountAsync(c, "Transactions", deleteWhere, args, o.CommandTimeoutSeconds, token).ConfigureAwait(false);
                }
                else
                {
                    var deleted = await BatchDeleteAsync(c, "Transactions", deleteWhere, args, o.BatchSize, budget, o.CommandTimeoutSeconds, token)
                        .ConfigureAwait(false);
                    r.TransactionsDeleted += deleted;
                    budget -= deleted;
                }
            }
        }

        async Task PruneCandlesAsync(NpgsqlConnection c, RetentionOptions o, RetentionReport r, CancellationToken token)
        {
            const string fineWhere   = @"""BucketSeconds"" <= @fineMax AND ""OpenTime"" < @cutoff";
            const string coarseWhere = @"""BucketSeconds"" >  @fineMax AND ""OpenTime"" < @cutoff";
            var fineArgs   = new { fineMax = o.CandleFineMaxBucketSeconds, cutoff = r.CandleFineCutoffUtc };
            var coarseArgs = new { fineMax = o.CandleFineMaxBucketSeconds, cutoff = r.CandleCoarseCutoffUtc };

            if (dryRun)
            {
                r.CandlesFineDeleted   = await CountAsync(c, "Candles", fineWhere, fineArgs, o.CommandTimeoutSeconds, token).ConfigureAwait(false);
                r.CandlesCoarseDeleted = await CountAsync(c, "Candles", coarseWhere, coarseArgs, o.CommandTimeoutSeconds, token).ConfigureAwait(false);
            }
            else
            {
                r.CandlesFineDeleted   = await BatchDeleteAsync(c, "Candles", fineWhere, fineArgs, o.BatchSize, o.MaxDeletesPerCycle, o.CommandTimeoutSeconds, token).ConfigureAwait(false);
                r.CandlesCoarseDeleted = await BatchDeleteAsync(c, "Candles", coarseWhere, coarseArgs, o.BatchSize, o.MaxDeletesPerCycle, o.CommandTimeoutSeconds, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Verifies the verification-resolution candles cover the deletable transactions
    /// for one key. Expected coverage is the number of distinct windows (at the
    /// verification resolution) that actually contained a trade — robust to genuine
    /// no-trade gaps (a quiet window produces neither a trade nor a candle). Passes
    /// when persisted candle count ≥ trade-bearing windows × tolerance. The
    /// resolution must be one the backfill regenerates (15m+), so the
    /// backfill→verify→delete loop can close.
    /// </summary>
    private static async Task<bool> VerifyCandleCoverageAsync(
        NpgsqlConnection c, int stockId, string currency, DateTime cutoffUtc,
        int verifyBucketSeconds, double tolerance, int commandTimeout, CancellationToken ct)
    {
        var span = await c.QuerySingleAsync<CoverageRow>(new CommandDefinition(
            @"SELECT COUNT(DISTINCT floor(extract(epoch FROM ""Timestamp"") / @bucket))::bigint AS ""Windows"",
                     MIN(""Timestamp"") AS ""Oldest""
              FROM ""Transactions""
              WHERE ""StockId"" = @sid AND ""Currency"" = @ccy AND ""Timestamp"" < @cutoff",
            new { sid = stockId, ccy = currency, cutoff = cutoffUtc, bucket = verifyBucketSeconds },
            commandTimeout: commandTimeout, cancellationToken: ct)).ConfigureAwait(false);

        if (span.Windows == 0 || span.Oldest is null) return true; // nothing to cover

        // Floor the oldest trade time to its verification-bucket boundary so the count
        // window aligns with candle OpenTimes.
        var oldest = span.Oldest.Value;
        var oldestEpoch = new DateTimeOffset(DateTime.SpecifyKind(oldest, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var flooredEpoch = oldestEpoch - (oldestEpoch % verifyBucketSeconds);
        var oldestBucket = DateTimeOffset.FromUnixTimeSeconds(flooredEpoch).UtcDateTime;

        var candleCount = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            @"SELECT COUNT(*) FROM ""Candles""
              WHERE ""StockId"" = @sid AND ""Currency"" = @ccy AND ""BucketSeconds"" = @bucket
                    AND ""OpenTime"" >= @from AND ""OpenTime"" < @cutoff",
            new { sid = stockId, ccy = currency, bucket = verifyBucketSeconds, from = oldestBucket, cutoff = cutoffUtc },
            commandTimeout: commandTimeout, cancellationToken: ct)).ConfigureAwait(false);

        return candleCount >= span.Windows * tolerance;
    }

    // Dapper maps these by column name (ValueTuple mapping is unreliable).
    private sealed class TxKey
    {
        public int StockId { get; set; }
        public string Currency { get; set; } = "";
    }

    private sealed class CoverageRow
    {
        public long Windows { get; set; }
        public DateTime? Oldest { get; set; }
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection c, string table, string where, object args, int commandTimeout, CancellationToken ct)
        => await c.ExecuteScalarAsync<long>(new CommandDefinition(
            $@"SELECT COUNT(*) FROM ""{table}"" WHERE {where}", args,
            commandTimeout: commandTimeout, cancellationToken: ct)).ConfigureAwait(false);

    /// <summary>
    /// Deletes rows matching <paramref name="where"/> in autocommitted ctid-keyed
    /// batches until drained or <paramref name="maxRows"/> is reached. The LIMIT is
    /// an int we control (batch size), never user input.
    /// </summary>
    private static async Task<long> BatchDeleteAsync(
        NpgsqlConnection c, string table, string where, object args,
        int batchSize, long maxRows, int commandTimeout, CancellationToken ct)
    {
        long total = 0;
        while (!ct.IsCancellationRequested && total < maxRows)
        {
            int take = (int)Math.Min(batchSize, maxRows - total);
            var sql =
                $@"DELETE FROM ""{table}"" WHERE ctid IN (
                       SELECT ctid FROM ""{table}"" WHERE {where} LIMIT {take})";
            int affected = await c.ExecuteAsync(new CommandDefinition(sql, args,
                commandTimeout: commandTimeout, cancellationToken: ct)).ConfigureAwait(false);
            total += affected;
            if (affected < take) break; // fewer than asked → predicate drained
        }
        return total;
    }
}
