using System.Linq;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §fat-tail jumps: realizes a RARE, LARGE, drift-neutral price JUMP so 1-min return kurtosis rises toward the
/// realistic range. On a low-intensity per-stock Poisson arrival (<see cref="IJumpSource"/>) it walks the book
/// with a short burst of REAL marketable orders from a dedicated house aggressor, self-bounded to a target % in
/// ONE bucket, then sustains a few buckets of elevated-vol AFTERSHOCK (so volatility clustering is preserved),
/// and lets the existing value-anchor pull the price back SLOWLY over minutes (mean-reversion ⇒ a tail event,
/// not a level shift). It NEVER touches the fundamental anchor (distinct from <c>ExogShock:AnchorTracksShock</c>).
///
/// Conservation-clean by construction: every order rides the ordinary <see cref="IOrderEntryService"/>
/// reserve→match→settle path (no naked/injected price), so ConservationProbe / ReservationAuditor invariants
/// apply unchanged (CK=0). It bypasses only the DECISION-layer caps (band veto + depth cap live in
/// AiBotDecisionService, which this never enters), so it must SELF-BOUND its event magnitude — the slice loop.
///
/// Loop-thread only: <see cref="RunAsync"/> is driven from the single bot loop AFTER the MM pass and submits
/// OUTSIDE any held book lock (the engine takes the per-(stock,ccy) lock internally, which is non-reentrant).
/// Deterministic: the source RNG is the ONLY randomness and is drawn only when enabled; the burst + aftershock
/// draw NO RNG (the aftershock is a pure function of the drawn magnitude). Disabled ⇒ Tick no-op ⇒ byte-identical.
/// </summary>
internal sealed class JumpService
{
    #region Constants
    private const double MinDtSec = BotMath.TickMinDtSec;
    private const double MaxDtSec = BotMath.TickMaxDtSec;
    // Each primary slice walks ~this fraction of the resting opposite-side depth, then we re-read the mark and
    // stop at target — keeps a single slice from blowing through a thin book while still making fast progress.
    private const double SliceFracOfDepth = 0.20;
    // Aftershock nudges are small (a fraction of depth, decaying) — elevated vol, NOT a fast counter-order
    // (a full counter-revert would drive ret_acf_lag1 out of band; the bulk revert is the ambient anchor).
    private const decimal AftershockBaseFracOfDepth = 0.06m;
    // Space aftershock nudges ~1 candle apart so the elevated vol spreads across successive 1-min buckets.
    private const double AftershockSpacingSec = 55.0;
    #endregion

    #region Services / config / state
    private readonly IOrderEntryService _entry;
    private readonly IOrderBookEngine _books;
    private readonly IAccountsCache _accounts;
    private readonly IStockService _stocks;
    private readonly ILogger<JumpService> _logger;
    private readonly IJumpSource _source;

    private readonly bool    _enabled;
    private readonly int     _aggressorUserId;
    private readonly int     _maxSlices;
    private readonly decimal _slippagePct;
    private readonly int     _aftershockBuckets;
    private readonly double  _aftershockDecay;
    private readonly double  _driftGuardPct;

    // Per-stock aftershock continuation. Loop-thread only ⇒ no locking. No RNG ⇒ replay-stable.
    private sealed class AfterState { public int BucketsLeft; public int Dir; public int K; public DateTime NextFireUtc; }
    private readonly Dictionary<int, AfterState> _after = new();

    private DateTime _lastTickUtc = DateTime.MaxValue; // inert until Reset arms the clock
    private long _simTick;
    #endregion

    internal JumpService(IOrderEntryService entry, IOrderBookEngine books, IAccountsCache accounts,
        IStockService stocks, ILogger<JumpService> logger, IJumpSource source,
        bool enabled, int aggressorUserId, int maxSlices, decimal slippagePct,
        int aftershockBuckets, double aftershockDecay, double driftGuardPct)
    {
        _entry    = entry    ?? throw new ArgumentNullException(nameof(entry));
        _books    = books    ?? throw new ArgumentNullException(nameof(books));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _stocks   = stocks   ?? throw new ArgumentNullException(nameof(stocks));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _source   = source   ?? throw new ArgumentNullException(nameof(source));
        _enabled  = enabled;
        _aggressorUserId   = aggressorUserId;
        _maxSlices         = Math.Max(1, maxSlices);
        _slippagePct       = Math.Max(0m, slippagePct);
        _aftershockBuckets = Math.Max(0, aftershockBuckets);
        _aftershockDecay   = Math.Clamp(aftershockDecay, 0.0, 1.0);
        _driftGuardPct     = Math.Max(0.0, driftGuardPct);
    }

    /// <summary>Clear aftershock state, reseed the source, and arm the tick clock (inert when disabled).</summary>
    internal void Reset(DateTime now)
    {
        _after.Clear();
        _source.Reset();
        _simTick = 0;
        _lastTickUtc = _enabled ? now : DateTime.MaxValue;
    }

    #region Run
    internal async Task RunAsync(AiBotContext ctx, DateTime now, CancellationToken ct)
    {
        if (!_enabled || _lastTickUtc == DateTime.MaxValue) return;
        double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, MinDtSec, MaxDtSec);
        _lastTickUtc = now;
        _simTick++;

        try
        {
            // 1) Advance active aftershocks first (deterministic, no RNG). Spaced ~1 candle apart.
            await RunAftershocksAsync(ctx, now, ct).ConfigureAwait(false);

            // 2) Poll for new arrivals. Poll draws for EVERY stock (stable sequence); we ignore an arrival for a
            //    stock already in an aftershock so bursts don't stack — we filter the RESULT, never the draw, so
            //    the per-tick RNG sequence is unperturbed.
            foreach (var ev in _source.Poll(_simTick, dt))
            {
                if (_after.ContainsKey(ev.StockId)) continue;
                await ExecuteJumpAsync(ctx, now, ev, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* clean stop mid-pass */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jump pass failed on simTick {Tick}.", _simTick);
        }
    }

    // One bounded primary burst, self-limited to the target % within this bucket.
    private async Task ExecuteJumpAsync(AiBotContext ctx, DateTime now, JumpEvent ev, CancellationToken ct)
    {
        int stockId = ev.StockId;
        if (!_stocks.TryGetCurrency(stockId, out var ccy)) { JumpsProbe.RecordSuppressed(); return; }
        decimal seed = SeedPrice(stockId, ccy);
        decimal preMark = Mark(ctx, stockId, ccy, seed);
        if (preMark <= 0m) { JumpsProbe.RecordSuppressed(); return; }

        // Drift guard: if the price has already left the band in the jump's direction, flip the sign so jumps
        // don't stack one-directionally (cheap insurance on top of the ambient mean-reversion).
        int sign = ev.SignedTargetPct >= 0.0 ? 1 : -1;
        if (seed > 0m && _driftGuardPct > 0.0)
        {
            double dev = (double)((preMark - seed) / seed);
            if (sign > 0 && dev >  _driftGuardPct) sign = -1;
            else if (sign < 0 && dev < -_driftGuardPct) sign = 1;
        }
        bool isBuy = sign > 0;
        double targetPct = Math.Abs(ev.SignedTargetPct);

        decimal gross = 0m;
        int slices = 0;
        for (int i = 0; i < _maxSlices; i++)
        {
            ct.ThrowIfCancellationRequested();
            int qty = SliceQty(stockId, ccy, isBuy, Mark(ctx, stockId, ccy, seed), (decimal)SliceFracOfDepth);
            if (qty <= 0) break; // book dry or aggressor exhausted
            var result = await SubmitAsync(isBuy, stockId, qty, ccy, ct).ConfigureAwait(false);
            if (result.TotalFilledQuantity <= 0) break; // nothing matched this tick
            slices++;
            gross += CurrencyHelper.Notional(result.AverageFillPrice, result.TotalFilledQuantity, ccy);

            decimal mark = Mark(ctx, stockId, ccy, seed); // book is updated synchronously by matching ⇒ fresh
            if (preMark > 0m && Math.Abs((mark - preMark) / preMark) >= (decimal)targetPct) break;
        }

        if (slices == 0) { JumpsProbe.RecordSuppressed(); return; }

        decimal postMark = Mark(ctx, stockId, ccy, seed);
        double realized = preMark > 0m ? (double)Math.Abs((postMark - preMark) / preMark) : 0.0;
        JumpsProbe.RecordJump(isBuy, realized, gross);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var sym = _stocks.TryGetSymbol(stockId, out var s) ? s : stockId.ToString();
            _logger.LogInformation("JUMP {Symbol} {Dir} target={Tgt:0.000} realized={Real:0.000} slices={Sl}",
                sym, isBuy ? "BUY" : "SELL", targetPct, realized, slices);
        }

        // Arm the elevated-vol aftershock tail (clustering). First nudge ~1 candle later. No RNG.
        if (_aftershockBuckets > 0)
            _after[stockId] = new AfterState
            {
                BucketsLeft = _aftershockBuckets, Dir = sign, K = 1,
                NextFireUtc = now + TimeSpan.FromSeconds(AftershockSpacingSec)
            };
    }

    // Small decaying nudges for a few buckets after a jump: alternating sign (vol without net drift), sized off
    // depth and gated ~1 candle apart. Pure function of the stored state ⇒ no RNG, replay-stable.
    private async Task RunAftershocksAsync(AiBotContext ctx, DateTime now, CancellationToken ct)
    {
        if (_after.Count == 0) return;
        foreach (var stockId in _after.Keys.OrderBy(k => k).ToList()) // stable order for determinism
        {
            var st = _after[stockId];
            if (now < st.NextFireUtc) continue;
            if (!_stocks.TryGetCurrency(stockId, out var ccy)) { _after.Remove(stockId); continue; }
            decimal seed = SeedPrice(stockId, ccy);

            // Even k = original direction, odd k = opposite ⇒ elevated |return| with ~no net drift.
            int dir = (st.K % 2 == 1) ? -st.Dir : st.Dir;
            bool isBuy = dir > 0;
            decimal frac = AftershockBaseFracOfDepth * (decimal)Math.Pow(_aftershockDecay, st.K - 1);
            int qty = SliceQty(stockId, ccy, isBuy, Mark(ctx, stockId, ccy, seed), frac);
            if (qty > 0)
            {
                var result = await SubmitAsync(isBuy, stockId, qty, ccy, ct).ConfigureAwait(false);
                if (result.TotalFilledQuantity > 0) JumpsProbe.RecordAftershock();
            }

            st.BucketsLeft--; st.K++;
            st.NextFireUtc = now + TimeSpan.FromSeconds(AftershockSpacingSec);
            if (st.BucketsLeft <= 0) _after.Remove(stockId);
        }
    }
    #endregion

    #region Helpers
    private Task<OrderResult> SubmitAsync(bool isBuy, int stockId, int qty, CurrencyType ccy, CancellationToken ct)
        => isBuy
            ? _entry.PlaceSlippageMarketBuyOrderAsync(_aggressorUserId, stockId, qty, _slippagePct, ccy, ct)
            : _entry.PlaceSlippageMarketSellOrderAsync(_aggressorUserId, stockId, qty, _slippagePct, ccy, ct);

    // Shares to submit this slice: a fraction of the resting opposite-side depth, clamped to the aggressor's live
    // available cash (buy) / shares (sell) so a depleted side short-fills rather than over-promising settlement.
    private int SliceQty(int stockId, CurrencyType ccy, bool isBuy, decimal px, decimal fracOfDepth)
    {
        if (!_books.TryGetLoaded(stockId, ccy, out var book) || book is null) return 0;
        long oppDepth = book.SumQuantity(buySide: !isBuy); // the resting liquidity a marketable order consumes
        if (oppDepth <= 0L) return 0;
        int want = (int)Math.Ceiling(oppDepth * fracOfDepth);
        if (want <= 0) return 0;

        if (isBuy)
        {
            if (px <= 0m) return 0;
            decimal cash = _accounts.GetFund(_aggressorUserId, ccy)?.AvailableBalance ?? 0m;
            int byCash = (int)Math.Floor(cash / px);
            return Math.Min(want, Math.Max(0, byCash));
        }
        int availShares = Math.Max(0, _accounts.GetPosition(_aggressorUserId, stockId)?.AvailableQuantity ?? 0);
        return Math.Min(want, availShares);
    }

    // Live mark on the loop thread: book mid (the book is mutated synchronously by matching, so it is fresh
    // post-fill — unlike ctx.StockPrices, which OnQuoteUpdated restamps asynchronously on the quote-drain thread).
    private decimal Mark(AiBotContext ctx, int stockId, CurrencyType ccy, decimal seed)
    {
        if (_books.TryGetLoaded(stockId, ccy, out var book) && book is not null)
        {
            decimal bid = book.PeekBestBuy()?.Price ?? 0m;
            decimal ask = book.PeekBestSell()?.Price ?? 0m;
            if (bid > 0m && ask > 0m) return (bid + ask) / 2m;
            if (bid > 0m) return bid;
            if (ask > 0m) return ask;
        }
        if (ctx.StockPrices.TryGetValue((stockId, ccy), out var last) && last > 0m) return last;
        return seed;
    }

    private decimal SeedPrice(int stockId, CurrencyType ccy)
    {
        var listings = _stocks.GetListings(stockId);
        for (int i = 0; i < listings.Count; i++)
            if (listings[i].CurrencyType == ccy) return listings[i].SeedPrice;
        return 0m;
    }
    #endregion
}
