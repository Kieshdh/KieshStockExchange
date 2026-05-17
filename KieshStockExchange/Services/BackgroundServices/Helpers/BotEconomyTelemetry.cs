using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Periodic snapshot of aggregate bot wealth + average price drift since
/// session start. Each <see cref="LogSnapshot"/> call records a row in the
/// bounded ring so the Bot Dashboard can export the full series to CSV.
/// </summary>
internal sealed class BotEconomyTelemetry
{
    #region Services and Constructor
    // 60s sampling × 10000 rows ≈ 7 days. Plenty of runway for a diagnostic
    // window; each row is small so the memory cost is negligible.
    private const int RecentSamplesMax = 10000;

    private readonly AiBotContext _ctx;
    private readonly IAccountsCache _accounts;
    private readonly IStockService _stocks;
    private readonly ILogger<BotEconomyTelemetry> _logger;

    private Dictionary<(int StockId, CurrencyType Currency), decimal>? _sessionStartPrices;
    private readonly Queue<EconomySample> _samples = new();
    // Running total of cash injected by BotCashInjector across the session.
    // Guarded by lock(_samples); read into each EconomySample so the CSV
    // carries cumulative growth and per-cycle delta is derivable client-side.
    private decimal _totalInjectedThisSession;

    internal BotEconomyTelemetry(AiBotContext ctx, IAccountsCache accounts,
        IStockService stocks, ILogger<BotEconomyTelemetry> logger)
    {
        _ctx      = ctx      ?? throw new ArgumentNullException(nameof(ctx));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _stocks   = stocks   ?? throw new ArgumentNullException(nameof(stocks));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Snapshot
    internal void Reset()
    {
        _sessionStartPrices = null;
        lock (_samples)
        {
            _samples.Clear();
            _totalInjectedThisSession = 0m;
        }
    }

    internal void RecordInjection(decimal amount)
    {
        if (amount <= 0m) return;
        lock (_samples) _totalInjectedThisSession += amount;
    }

    internal void LogSnapshot(IReadOnlyList<CurrencyType> currencies)
    {
        decimal totalCash = 0m, totalShares = 0m;
        // Iterate the engine's authoritative stock list. _ctx.StocksByUser is a
        // 60s-refreshed index and would race this sampler.
        foreach (var user in _ctx.AiUsersByAiUserId.Values)
        {
            foreach (var currency in currencies)
            {
                var fund = _accounts.GetFund(user.UserId, currency);
                if (fund != null) totalCash += fund.TotalBalance;
            }
            foreach (var sid in _stocks.ById.Keys)
            {
                var pos = _accounts.GetPosition(user.UserId, sid);
                if (pos == null || pos.Quantity <= 0) continue;
                foreach (var currency in currencies)
                    if (_ctx.StockPrices.TryGetValue((sid, currency), out var price))
                        totalShares += CurrencyHelper.Notional(price, pos.Quantity, currency);
            }
        }

        // Anchor lazily so we don't capture an all-zero snapshot before quotes arrive.
        if (_sessionStartPrices is null && _ctx.StockPrices.Count > 0)
            _sessionStartPrices = new Dictionary<(int, CurrencyType), decimal>(_ctx.StockPrices);

        decimal driftSum = 0m, minDrift = 0m, maxDrift = 0m;
        int tracked = 0, minSid = 0, maxSid = 0;
        if (_sessionStartPrices is not null)
        {
            foreach (var kv in _ctx.StockPrices)
            {
                if (!_sessionStartPrices.TryGetValue(kv.Key, out var p0) || p0 <= 0m) continue;
                var drift = (kv.Value - p0) / p0;
                driftSum += drift;
                if (tracked == 0 || drift < minDrift) { minDrift = drift; minSid = kv.Key.Item1; }
                if (tracked == 0 || drift > maxDrift) { maxDrift = drift; maxSid = kv.Key.Item1; }
                tracked++;
            }
        }
        var avgDrift = tracked > 0 ? driftSum / tracked : 0m;
        var totalWealth = totalCash + totalShares;

        decimal injectedSnapshot;
        lock (_samples)
        {
            injectedSnapshot = _totalInjectedThisSession;
            _samples.Enqueue(new EconomySample(
                TimestampUtc:   TimeHelper.NowUtc(),
                TotalCash:      totalCash,
                TotalShares:    totalShares,
                TrackedStocks:  tracked,
                AvgDriftPct:    avgDrift,
                MinDriftPct:    minDrift,
                MinDriftStockId: minSid,
                MaxDriftPct:    maxDrift,
                MaxDriftStockId: maxSid,
                TotalInjectedThisSession: injectedSnapshot));
            while (_samples.Count > RecentSamplesMax) _samples.Dequeue();
        }

        _logger.LogInformation(
            "BotEconomy @ {Time}: wealth {Wealth} (cash {Cash} + shares {Shares}), " +
            "avg drift {Drift:P3} across {Tracked} stocks, injected {Injected}",
            TimeHelper.NowUtc().ToLocalTime().ToString("HH:mm:ss"),
            CurrencyHelper.Format(totalWealth, CurrencyType.USD),
            CurrencyHelper.Format(totalCash,   CurrencyType.USD),
            CurrencyHelper.Format(totalShares, CurrencyType.USD),
            avgDrift, tracked,
            CurrencyHelper.Format(injectedSnapshot, CurrencyType.USD));
    }
    #endregion

    #region Export
    internal int SampleCount
    {
        get { lock (_samples) return _samples.Count; }
    }

    internal string SuggestedExportFileName =>
        $"bot_economy_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}";

    internal async Task<string> ExportCsvAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is required.", nameof(path));

        EconomySample[] snapshot;
        lock (_samples) snapshot = _samples.ToArray();

        var sb = new StringBuilder(512 + snapshot.Length * 128);
        sb.AppendLine("TimestampUtc,TotalCash,TotalShares,TotalWealth,TrackedStocks," +
                      "AvgDriftPct,MinDriftPct,MinDriftStockId,MaxDriftPct,MaxDriftStockId," +
                      "TotalInjectedThisSession");
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = snapshot[i];
            sb.Append(r.TimestampUtc.ToString("O", inv)).Append(',')
              .Append(r.TotalCash.ToString(inv)).Append(',')
              .Append(r.TotalShares.ToString(inv)).Append(',')
              .Append((r.TotalCash + r.TotalShares).ToString(inv)).Append(',')
              .Append(r.TrackedStocks).Append(',')
              .Append(r.AvgDriftPct.ToString(inv)).Append(',')
              .Append(r.MinDriftPct.ToString(inv)).Append(',')
              .Append(r.MinDriftStockId).Append(',')
              .Append(r.MaxDriftPct.ToString(inv)).Append(',')
              .Append(r.MaxDriftStockId).Append(',')
              .Append(r.TotalInjectedThisSession.ToString(inv))
              .Append('\n');
        }

        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
        _logger.LogInformation("Exported {Count} bot economy samples to {Path}.", snapshot.Length, path);
        return path;
    }
    #endregion
}

internal readonly record struct EconomySample(
    DateTime TimestampUtc,
    decimal  TotalCash,
    decimal  TotalShares,
    int      TrackedStocks,
    decimal  AvgDriftPct,
    decimal  MinDriftPct,
    int      MinDriftStockId,
    decimal  MaxDriftPct,
    int      MaxDriftStockId,
    decimal  TotalInjectedThisSession);
