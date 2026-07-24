using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Parsed <c>Bots:MarketMaker:*</c> settings, threaded into <see cref="MarketMakerDecisionService"/>.
/// </summary>
internal readonly record struct MmConfig(
    bool Enabled,
    decimal HalfSpreadBps,
    int QuoteSize,
    decimal SkewBps,
    decimal RequoteThresholdBps,
    decimal MaxCashFrac,
    decimal PriceJitterBps,
    decimal OneSidedWidenMult,
    bool UseMicro,
    // §mood fear-widen (Feature 2): in FEAR (global mood < 50) widen the spread + shrink size. Default off ⇒
    // fear=0 passed at the call site ⇒ byte-identical. SpreadMax = max spread multiplier at full fear (mood 0),
    // SizeMin = min size multiplier at full fear.
    bool MoodWiden = false,
    decimal MoodWidenSpreadMax = 1.5m,
    decimal MoodWidenSizeMin = 0.6m);

/// <summary>One side of a quote. A side with <see cref="Qty"/> 0 or <see cref="Price"/> 0 means "post nothing".</summary>
internal readonly record struct MmSide(decimal Price, int Qty);

/// <summary>A two-sided quote for a single (stock, currency).</summary>
internal readonly record struct MmQuote(MmSide Bid, MmSide Ask);

/// <summary>
/// §market-maker-cohort: the PURE, RNG-free, deterministic quote math for the all-weather two-sided
/// resting-liquidity cohort (<see cref="AiStrategy.MarketMakerHouse"/>). Separated from the service so it is
/// unit-testable without standing up the engine — same idiom as <see cref="AiTradeService.StaggerDue"/> and
/// <see cref="AiBotDecisionService.DirectionalBias"/>.
///
/// The design point the legacy fair-weather quoting (<c>Bots:MarketMakerQuoting</c>) lacks: the
/// <see cref="Reference"/> ladder yields a price even when one book side is EMPTY (the up-shock case, no
/// resting asks), so the MM can post asks into a one-sided book — the structural fix for the chaser's
/// one-sided-book down-drift. The skew + hard two-sided position cap keep inventory bounded by design.
/// </summary>
internal static class MarketMakerMath
{
    /// <summary>
    /// The quote reference, via a fallback ladder that survives a one-sided/cold book:
    ///   1. two-sided touch ⇒ mid (or size-weighted micro when <paramref name="useMicro"/>);
    ///   2. last trade; 3. recent-anchor EWMA; 4. seed price.
    /// Returns 0 when nothing is available (caller skips). <paramref name="oneSided"/> is true for rungs 2-4
    /// (no live two-sided touch) so the caller widens the spread while price is undefined.
    /// </summary>
    internal static decimal Reference(decimal bestBid, decimal bestAsk, long bidQty, long askQty,
        decimal lastTrade, decimal ewma, decimal seed, bool useMicro, out bool oneSided)
    {
        if (bestBid > 0m && bestAsk > 0m)
        {
            oneSided = false;
            if (useMicro)
            {
                long denom = bidQty + askQty;
                if (denom > 0L) return (bestBid * askQty + bestAsk * bidQty) / denom;
            }
            return (bestBid + bestAsk) / 2m;
        }

        oneSided = true;
        if (lastTrade > 0m) return lastTrade;
        if (ewma > 0m) return ewma;
        if (seed > 0m) return seed;
        return 0m;
    }

    /// <summary>
    /// Inventory-skewed two-sided touch quote. Pure and deterministic — the only "randomness" is an RNG-free
    /// per-(bot,stock) price dither (<see cref="BotMath.HashUnit01(int,int)"/>) that disperses quotes off the
    /// round-number grid so the cohort does not rebuild an order wall.
    ///
    /// <para>Skew: normalised inventory <c>q = clamp(inv/cap, -1, 1)</c> shifts the reference
    /// <c>refSkewed = ref*(1 - s*q)</c> (long ⇒ lower both quotes to encourage sells) AND shrinks the side that
    /// would grow |inv|. Hard caps clamp <c>bidQty ≤ cap-inv</c> and <c>askQty ≤ cap+inv</c>, so the MM's own
    /// quotes can never push |inv| past <paramref name="cap"/> — a counterparty can only reduce the over-full
    /// side toward flat. The ask is allowed beyond current holdings (short via §F14), bounded by that same cap;
    /// the cash/collateral cap is applied by the caller against live <c>AvailableBalance</c>.</para>
    /// Returns <c>default</c> (no quote) when there is no reference, no inventory room, or the rounded pair
    /// would lock/cross.
    /// </summary>
    /// <summary>§mood fear-widen: half-spread × (1 + (SpreadMax−1)·fear). fear 0 ⇒ unchanged, fear 1 ⇒ ×SpreadMax. Pure.</summary>
    internal static decimal MoodWidenSpread(decimal halfSpread, double fear, decimal spreadMax)
        => halfSpread * (1m + (spreadMax - 1m) * (decimal)Math.Clamp(fear, 0.0, 1.0));

    /// <summary>§mood fear-widen: quote size × (1 − (1−SizeMin)·fear), floored at the tick. fear 0 ⇒ unchanged,
    /// fear 1 ⇒ ×SizeMin. Pure.</summary>
    internal static int MoodWidenSize(int quoteSize, double fear, decimal sizeMin)
        => (int)Math.Floor(quoteSize * (1m - (1m - sizeMin) * (decimal)Math.Clamp(fear, 0.0, 1.0)));

    internal static MmQuote Quote(decimal reference, bool oneSided, int inv, int cap, CurrencyType ccy,
        in MmConfig cfg, int aiUserId, int stockId, double fear = 0.0)
    {
        if (reference <= 0m || cap <= 0) return default;

        decimal h = cfg.HalfSpreadBps / 10000m;
        if (oneSided && cfg.OneSidedWidenMult > 0m) h *= cfg.OneSidedWidenMult;

        // §mood fear-widen (Feature 2): thin the book in fear — a wider spread + smaller size. Quote PLACEMENT only
        // (no taker) ⇒ trivially CK-safe. fear 0 (default / off) ⇒ no change ⇒ byte-identical.
        int quoteSize = cfg.QuoteSize;
        if (cfg.MoodWiden && fear > 0.0)
        {
            h = MoodWidenSpread(h, fear, cfg.MoodWidenSpreadMax);
            quoteSize = MoodWidenSize(quoteSize, fear, cfg.MoodWidenSizeMin);
        }

        decimal q = (decimal)inv / cap;
        if (q > 1m) q = 1m; else if (q < -1m) q = -1m;

        decimal s = cfg.SkewBps / 10000m;
        decimal refSkewed = reference * (1m - s * q);

        // Symmetric RNG-free dither in [-jit, +jit] keyed on (bot, stock): same bot+stock always lands on the
        // same offset (reproducible), adjacent ids decorrelate, and the cohort spreads across the grid.
        decimal jit = cfg.PriceJitterBps / 10000m;
        decimal dither = jit <= 0m ? 0m : (decimal)(BotMath.HashUnit01(aiUserId, stockId) * 2.0 - 1.0) * jit;

        decimal bidPx = CurrencyHelper.RoundPrice(refSkewed * (1m - h + dither), ccy);
        decimal askPx = CurrencyHelper.RoundPrice(refSkewed * (1m + h + dither), ccy);
        if (askPx <= bidPx || bidPx <= 0m) return default; // never post a locked/crossed or non-positive pair

        decimal qPos = q > 0m ? q : 0m;   // long  ⇒ shrink bid
        decimal qNeg = q < 0m ? q : 0m;   // short ⇒ shrink ask
        int bidQty = (int)Math.Floor(quoteSize * (1m - qPos));
        int askQty = (int)Math.Floor(quoteSize * (1m + qNeg));

        int bidRoom = cap - inv; if (bidRoom < 0) bidRoom = 0;
        int askRoom = cap + inv; if (askRoom < 0) askRoom = 0;
        if (bidQty > bidRoom) bidQty = bidRoom;
        if (askQty > askRoom) askQty = askRoom;
        if (bidQty < 0) bidQty = 0;
        if (askQty < 0) askQty = 0;

        return new MmQuote(new MmSide(bidPx, bidQty), new MmSide(askPx, askQty));
    }
}
