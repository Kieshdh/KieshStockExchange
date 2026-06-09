using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary> Periodic snapshot of aggregate bot wealth + average price drift since session start. </summary>
internal sealed class BotEconomyTelemetry
{
    #region Services and Constructor
    // 60s × 10000 ≈ 7 days of samples.
    private const int RecentSamplesMax = 10000;

    private readonly AiBotContext _ctx;
    private readonly IAccountsCache _accounts;
    private readonly IFxRateService _fxRates;
    private readonly ILogger<BotEconomyTelemetry> _logger;

    // §3.7 value-drain guard. The arbitrage cohort + house must stay a small fraction of total
    // market value. _houseUserId is the platform desk (no shares, cash only); when the combined
    // fraction crosses _drainCeilingPct the throttle engages and ArbitrageDecisionService stops
    // opening new round-trips. Read/written only on the single bot-loop thread.
    private readonly int _houseUserId;
    private readonly decimal _drainCeilingPct;
    internal bool ArbThrottleEngaged { get; private set; }

    private Dictionary<(int StockId, CurrencyType Currency), decimal>? _sessionStartPrices;
    private readonly Queue<EconomySample> _samples = new();
    // Guarded by lock(_samples).
    private decimal _totalInjectedThisSession;
    private readonly RingBufferStore<EconomySample> _store;

    internal BotEconomyTelemetry(AiBotContext ctx, IAccountsCache accounts,
        IFxRateService fxRates, ILogger<BotEconomyTelemetry> logger,
        int houseUserId = 20002, decimal drainCeilingPct = 5.0m)
    {
        _ctx      = ctx      ?? throw new ArgumentNullException(nameof(ctx));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _fxRates  = fxRates  ?? throw new ArgumentNullException(nameof(fxRates));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _houseUserId     = houseUserId;
        _drainCeilingPct = drainCeilingPct;
        _store    = new RingBufferStore<EconomySample>("data/telemetry/bot_economy.ndjson");

        var prior = _store.LoadTail(RecentSamplesMax);
        foreach (var s in prior) _samples.Enqueue(s);
        if (prior.Count > 0)
            _logger.LogInformation("BotEconomyTelemetry: replayed {Count} sample(s) from disk.", prior.Count);
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
        var cashByCurrency   = new Dictionary<CurrencyType, decimal>();
        var sharesByCurrency = new Dictionary<CurrencyType, decimal>();
        // §3.7 arbitrage-cohort sub-totals, tracked alongside the fleet walk for the drain guard.
        var arbCashByCurrency   = new Dictionary<CurrencyType, decimal>();
        var arbSharesByCurrency = new Dictionary<CurrencyType, decimal>();
        foreach (var c in currencies)
        {
            cashByCurrency[c] = 0m;
            sharesByCurrency[c] = 0m;
            arbCashByCurrency[c] = 0m;
            arbSharesByCurrency[c] = 0m;
        }

        foreach (var user in _ctx.AiUsersByAiUserId.Values)
        {
            bool isArb = user.Strategy == AiStrategy.Arbitrage;
            foreach (var currency in currencies)
            {
                var fund = _accounts.GetFund(user.UserId, currency);
                if (fund != null)
                {
                    cashByCurrency[currency] += fund.TotalBalance;
                    if (isArb) arbCashByCurrency[currency] += fund.TotalBalance;
                }
            }
            // Walk only the stocks this bot actually holds (avg ~13.5 of 50), mirroring
            // AiBotContext.PortfolioValueByCurrency — not the whole universe per bot.
            if (_ctx.StocksByUser.TryGetValue(user.UserId, out var heldStocks))
            foreach (var sid in heldStocks)
            {
                var pos = _accounts.GetPosition(user.UserId, sid);
                if (pos == null || pos.Quantity <= 0) continue;
                foreach (var currency in currencies)
                    if (_ctx.StockPrices.TryGetValue((sid, currency), out var price))
                    {
                        var notional = CurrencyHelper.Notional(price, pos.Quantity, currency);
                        sharesByCurrency[currency] += notional;
                        if (isArb) arbSharesByCurrency[currency] += notional;
                    }
            }
        }

        // Anchor lazily so we don't capture an all-zero snapshot.
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

        // Headline USD wealth via live FX mid.
        decimal totalCashUsd = 0m, totalSharesUsd = 0m;
        decimal arbCohortWealthUsd = 0m, houseWealthUsd = 0m;
        foreach (var c in currencies)
        {
            var mid = _fxRates.GetMidRate(c, CurrencyType.USD);
            totalCashUsd   += CurrencyHelper.RoundMoney(cashByCurrency[c]   * mid, CurrencyType.USD);
            totalSharesUsd += CurrencyHelper.RoundMoney(sharesByCurrency[c] * mid, CurrencyType.USD);
            arbCohortWealthUsd += CurrencyHelper.RoundMoney(
                (arbCashByCurrency[c] + arbSharesByCurrency[c]) * mid, CurrencyType.USD);
            // The house is a cash desk (no shares) and lives outside the fleet, so read it directly.
            var houseFund = _accounts.GetFund(_houseUserId, c);
            if (houseFund != null)
                houseWealthUsd += CurrencyHelper.RoundMoney(houseFund.TotalBalance * mid, CurrencyType.USD);
        }
        var totalWealthUsd = totalCashUsd + totalSharesUsd;

        // §3.7 drain fraction: cohort + house over total market value (fleet wealth + house). The
        // cohort is already inside the fleet total; the house is added since it's outside it.
        var drainDenom = totalWealthUsd + houseWealthUsd;
        var arbHouseFractionPct = drainDenom > 0m
            ? (arbCohortWealthUsd + houseWealthUsd) / drainDenom * 100m
            : 0m;
        var throttleWas = ArbThrottleEngaged;
        ArbThrottleEngaged = _drainCeilingPct > 0m && arbHouseFractionPct > _drainCeilingPct;
        if (ArbThrottleEngaged != throttleWas)
            _logger.LogWarning(
                "Arbitrage value-drain throttle {State}: cohort+house {Frac:F2}% of market (ceiling {Ceil:F2}%).",
                ArbThrottleEngaged ? "ENGAGED" : "released", arbHouseFractionPct, _drainCeilingPct);

        decimal injectedSnapshot;
        EconomySample sample;
        lock (_samples)
        {
            injectedSnapshot = _totalInjectedThisSession;
            sample = new EconomySample(
                TimestampUtc:   TimeHelper.NowUtc(),
                TotalCashUsd:   totalCashUsd,
                TotalSharesUsd: totalSharesUsd,
                CashByCurrency:   new Dictionary<CurrencyType, decimal>(cashByCurrency),
                SharesByCurrency: new Dictionary<CurrencyType, decimal>(sharesByCurrency),
                TrackedStocks:  tracked,
                AvgDriftPct:    avgDrift,
                MinDriftPct:    minDrift,
                MinDriftStockId: minSid,
                MaxDriftPct:    maxDrift,
                MaxDriftStockId: maxSid,
                TotalInjectedThisSession: injectedSnapshot,
                ArbCohortWealthUsd: arbCohortWealthUsd,
                HouseWealthUsd:     houseWealthUsd,
                ArbHouseFractionPct: arbHouseFractionPct);
            _samples.Enqueue(sample);
            while (_samples.Count > RecentSamplesMax) _samples.Dequeue();
        }
        _store.Append(sample);

        // Pass RAW numbers (format specifiers keep the rendered text as "$x") so the telemetry sink
        // forwards them as a numeric Metrics map and the web viewer can range-aggregate the DATA
        // (latest wealth, drift min/avg/max) per time bucket instead of re-parsing this string.
        _logger.LogInformation(
            "BotEconomy @ {Time}: wealth ${Wealth:N2} (cash ${Cash:N2} + shares ${Shares:N2}), " +
            "avg drift {Drift:P3} across {Tracked} stocks, injected ${Injected:N2}; " +
            "arb cohort ${Arb:N2} + house ${House:N2} = {Frac:F2}% of market",
            TimeHelper.NowUtc().ToLocalTime().ToString("HH:mm:ss"),
            totalWealthUsd, totalCashUsd, totalSharesUsd,
            avgDrift, tracked, injectedSnapshot,
            arbCohortWealthUsd, houseWealthUsd, arbHouseFractionPct);
    }
    #endregion

    #region Export
    internal int SampleCount
    {
        get { lock (_samples) return _samples.Count; }
    }

    internal string SuggestedExportFileName =>
        $"bot_economy_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}";

    internal string BuildCsv(CancellationToken ct = default)
    {
        EconomySample[] snapshot;
        lock (_samples) snapshot = _samples.ToArray();

        // CSV columns: one TotalCash_/TotalShares_ pair per currency seen in
        // the snapshot, plus the headline USD totals. Reads on a fixed
        // currency-set known up front would be tighter, but Person.py +
        // FxRateService both pin the runtime currencies to USD/EUR, so the
        // header is stable across exports.
        var seenCurrencies = new SortedSet<CurrencyType>();
        foreach (var s in snapshot)
        {
            foreach (var c in s.CashByCurrency.Keys) seenCurrencies.Add(c);
            foreach (var c in s.SharesByCurrency.Keys) seenCurrencies.Add(c);
        }

        var sb = new StringBuilder(512 + snapshot.Length * 160);
        sb.Append("TimestampUtc,TotalCashUsd,TotalSharesUsd,TotalWealthUsd,TrackedStocks,")
          .Append("AvgDriftPct,MinDriftPct,MinDriftStockId,MaxDriftPct,MaxDriftStockId,")
          .Append("TotalInjectedThisSession,ArbCohortWealthUsd,HouseWealthUsd,ArbHouseFractionPct");
        foreach (var c in seenCurrencies)
            sb.Append(",TotalCash_").Append(c).Append(",TotalShares_").Append(c);
        sb.Append('\n');

        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = snapshot[i];
            sb.Append(r.TimestampUtc.ToString("O", inv)).Append(',')
              .Append(r.TotalCashUsd.ToString(inv)).Append(',')
              .Append(r.TotalSharesUsd.ToString(inv)).Append(',')
              .Append((r.TotalCashUsd + r.TotalSharesUsd).ToString(inv)).Append(',')
              .Append(r.TrackedStocks).Append(',')
              .Append(r.AvgDriftPct.ToString(inv)).Append(',')
              .Append(r.MinDriftPct.ToString(inv)).Append(',')
              .Append(r.MinDriftStockId).Append(',')
              .Append(r.MaxDriftPct.ToString(inv)).Append(',')
              .Append(r.MaxDriftStockId).Append(',')
              .Append(r.TotalInjectedThisSession.ToString(inv)).Append(',')
              .Append(r.ArbCohortWealthUsd.ToString(inv)).Append(',')
              .Append(r.HouseWealthUsd.ToString(inv)).Append(',')
              .Append(r.ArbHouseFractionPct.ToString(inv));
            foreach (var c in seenCurrencies)
            {
                r.CashByCurrency.TryGetValue(c, out var cash);
                r.SharesByCurrency.TryGetValue(c, out var shares);
                sb.Append(',').Append(cash.ToString(inv))
                  .Append(',').Append(shares.ToString(inv));
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    internal async Task<string> ExportCsvAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is required.", nameof(path));
        await File.WriteAllTextAsync(path, BuildCsv(ct), ct).ConfigureAwait(false);
        _logger.LogInformation("Exported bot economy samples to {Path}.", path);
        return path;
    }
    #endregion
}

internal sealed record EconomySample(
    DateTime TimestampUtc,
    decimal  TotalCashUsd,
    decimal  TotalSharesUsd,
    IReadOnlyDictionary<CurrencyType, decimal> CashByCurrency,
    IReadOnlyDictionary<CurrencyType, decimal> SharesByCurrency,
    int      TrackedStocks,
    decimal  AvgDriftPct,
    decimal  MinDriftPct,
    int      MinDriftStockId,
    decimal  MaxDriftPct,
    int      MaxDriftStockId,
    decimal  TotalInjectedThisSession,
    decimal  ArbCohortWealthUsd,
    decimal  HouseWealthUsd,
    decimal  ArbHouseFractionPct);
