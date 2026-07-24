using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using System.Collections.Concurrent;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Plain data container shared by AiBotStateService and AiBotDecisionService.
/// Fund/Position state is read live from AccountsCache (the single source of truth);
/// StocksByUser is lightweight metadata for per-user iteration.
/// Price caches use ConcurrentDictionary because OnQuoteUpdated fires on external threads.
/// </summary>
internal sealed class AiBotContext
{
    #region Services and Constructor
    private readonly IAccountsCache _accounts;
    private readonly bool _personalSentiment;
    // §impact-decouple A: gates the reaction-reference reads (ReactionRefOr). Off ⇒ every read returns its
    // legacy fallback ⇒ byte-identical. The matching reference EWMA is maintained in AiTradeService.OnQuoteUpdated.
    private readonly bool _reactionRef;

    internal AiBotContext(IAccountsCache accounts, bool personalSentiment = true, bool reactionRef = false)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _personalSentiment = personalSentiment;
        _reactionRef = reactionRef;
    }
    #endregion

    #region State
    internal readonly Dictionary<int, AIUser>  AiUsersByAiUserId = new();
    internal readonly Dictionary<int, AIUser>  AiUsersByUserId   = new();
    internal readonly Dictionary<int, Random>  AiUserRngs        = new();

    // Metadata index: which stocks each user has a position in. Rebuilt every 60s
    // by RefreshAssetsAsync. Actual Fund/Position instances come from _accounts on
    // each access — no shadow copy, no drift.
    internal readonly Dictionary<int, HashSet<int>>          StocksByUser     = new();

    internal readonly Dictionary<int, Dictionary<int, Order>> OpenOrders = new();

    // §B2 (Bots:PruneLimitOnly): a per-bot LIMIT-ONLY mirror of OpenOrders (userId→orderId→Order, only
    // IsOpenLimitOrder). PruneWorstOrdersAsync iterates THIS instead of OpenOrders so the ~30s prune is
    // O(limits) not O(limits + armed-stops) — the armed-stop pool no longer inflates the maint phase.
    // Maintained in lock-step with OpenOrders at cold-load / placement / limit-cancel (AiBotStateService),
    // ONLY when the flag is on. Decision-path reads (open-order cap, MM balance, reserved-qty aggregates)
    // keep reading the FULL OpenOrders. Empty/unused when the flag is off ⇒ byte-identical.
    internal readonly Dictionary<int, Dictionary<int, Order>> OpenLimitOrders = new();

    // §B3 lean reload (Bots:LeanReload): per-bot count of armed (Pending) stops, refreshed each reload by a
    // cheap GROUP-BY instead of hydrating the ~1.18M stop Orders into OpenOrders. The open-order cap adds this
    // to OpenOrders.Count so it stays byte-identical to today's hydrated .Count. Empty when the flag is off ⇒
    // the cap adds 0 ⇒ byte-identical. Counts ALL Pending stops (incl bracket children), matching today.
    internal readonly Dictionary<int, int> ArmedStopCount = new();

    // Three-stage price cache: StockPrices is the raw last quote, PreviousPrices
    // is the prior raw value for tick-to-tick deltas, SmoothedPrices is EWMA
    // (α=0.15, ~6-tick window) that ChooseOrderType reads to dampen spike noise.
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal> StockPrices    = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal> PreviousPrices = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal> SmoothedPrices = new();

    // §smoothed-price half-life: per-key last-update time for the OPTIONAL time-based EWMA (when
    // Bots:SmoothedPriceHalfLifeSec > 0). Empty/unused on the legacy fixed-α path.
    internal readonly ConcurrentDictionary<(int, CurrencyType), DateTime> SmoothedPriceUpdatedUtc = new();

    // §impact-decouple A (Bots:ImpactDecoupleReference): a >1-min EWMA reference price the directional
    // REACTION reads instead of the ~1s-lagged Previous/Smoothed price, so the cohort stops fading its own
    // 1-min impact (the ret_acf_lag1≈-0.43 driver). Maintained per quote in AiTradeService.OnQuoteUpdated
    // with its OWN dedicated timestamp dict (NOT SmoothedPriceUpdatedUtc — that is empty when the smoothed
    // half-life is 0, the prod default, which would freeze dt=0). Cross-thread (quote-thread writer,
    // loop-thread reader) ⇒ ConcurrentDictionary. Empty/untouched when the flag is off.
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal>  ReactionRefPrices     = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), DateTime> ReactionRefUpdatedUtc = new();

    internal readonly ConcurrentDictionary<int, DateTime> BurstEndTimes  = new();

    // §A1 inertia: per-bot directional STANCE (dir +1 buy / -1 sell) that persists until `until`, so a
    // bot's order flow is one-directional over the stance window instead of re-rolling buy/sell every
    // tick. Keyed by aiUserId, mirroring BurstEndTimes. Empty (and never touched) when the flag is off.
    internal readonly ConcurrentDictionary<int, (sbyte dir, DateTime until)> Stances = new();

    // §reaction-persistence split: per-bot CONTINUOUS directional pressure (a slowly-decaying AR(1) memory) plus
    // its per-bot last-update tick for the time-based decay. Replaces the §A1 blindness stance with a two-clock
    // model — a FAST reaction feeds `Pressures`, which decays over a per-bot half-life (minutes) to carry trend +
    // correlation. ConcurrentDictionary (see the per-bot-state note at the region header): keyed by aiUserId, so
    // single-writer-per-key under the parallel sweep. Keyed by aiUserId (the decision granularity: one
    // ChooseOrderType call per bot per tick in its home currency). Empty (never touched) when the flag is off.
    internal readonly ConcurrentDictionary<int, decimal>  Pressures           = new();
    internal readonly ConcurrentDictionary<int, long>     PressureUpdatedTicks = new();

    // R5 §B: per-(userId, currency) PERCEIVED anchor tilt (EWMA). A bot's slowly-updating view of the
    // true anchor tilt — staggered by its Lateness so the cohort's correction spreads across minutes
    // instead of snapping back in lockstep. NOT cleared per tick (it's persistent state); cleared only
    // on ClearAll. Empty (and never touched) when Bots:AnchorReactionLag is off.
    internal readonly ConcurrentDictionary<(int userId, CurrencyType ccy), decimal> AnchorTiltLag = new();

    // #1: per-(userId, currency) PERCEIVED directional tilt (EWMA). Same Lateness-staggered lag as the
    // anchor, but on the FAST directional/sentiment loop — the slope reaction the bounce diagnostic
    // implicated as the genuine 1-min mean-reversion driver. NOT cleared per tick; cleared on ClearAll.
    internal readonly ConcurrentDictionary<(int userId, CurrencyType ccy), decimal> DirectionalLag = new();

    // §perceived-price desync (Bots:PerceivedPriceDesync): per-(userId, stockId, currency) PERCEIVED PRICE EWMAs —
    // a fast and a slow one — that each bot reacts to instead of the shared live price / shared sentiment slope.
    // The smoothing rate is dispersed per bot by BOTH Lateness AND a salted hash of AiUserId, so even same-Lateness,
    // same-tick bots perceive different prices — breaking the cohort lockstep that DirectionalReactionLag (keyed on
    // Lateness only) could not. ConcurrentDictionary (single-writer per (userId,…) key under the parallel sweep).
    // NOT cleared per tick; cleared on ClearAll. Empty (and never touched) when the flag is off.
    internal readonly ConcurrentDictionary<(int userId, int stockId, CurrencyType ccy), decimal> PerceivedFast = new();
    internal readonly ConcurrentDictionary<(int userId, int stockId, CurrencyType ccy), decimal> PerceivedSlow = new();

    // §impact-decouple B (Bots:ImpactDecoupleHold): per-(userId, currency) sample-and-hold of the combined
    // directional stance plus the tick it was last changed. A bot may change its stance at most once per the
    // hold window, so it cannot fade its own move within the minute it caused (the HARD refractory the soft
    // EWMA lag above lacked). ConcurrentDictionary (single-writer per (userId,ccy) key under the parallel sweep).
    // NOT cleared per tick; cleared only on ClearAll. Empty (and never touched) when the flag is off.
    internal readonly ConcurrentDictionary<(int userId, CurrencyType ccy), (decimal value, long lastChangeTicks)> ReactionHold = new();

    // Reset by CheckDailyRefresh so a tx that fills across the day boundary isn't double-counted.
    internal readonly HashSet<int>              ProcessedTxIds = new();

    internal DateOnly LastRefreshDate = DateOnly.MinValue;

    // Empty placeholders let callers keep reading .TotalBalance / .Quantity
    // without null checks. Safe to share since the bot reads them as immutable.
    private static readonly Fund     EmptyFund     = new();
    private static readonly Position EmptyPosition = new();
    #endregion

    #region Per-tick memoization caches (patch 0001)
    // Cleared at the top of each tick by AiTradeService.CollectPendingOrdersAsync via ClearTickCaches.
    // The genuinely-shared per-STOCK caches below (OverBand*/Fundamental/SeedPrice + RefillGateCache) are
    // ConcurrentDictionary: they are warmed once per tick by PrecomputeSharedTickCaches (serial today) and
    // read by every bot, so under the future parallel-collect sweep they are pure-read shared state. The
    // remaining per-BOT memo caches (Committed/Watchlist*) stay plain Dictionary for now — they are only
    // touched by the single bot-loop thread and are retyped when the parallel branch lands.
    // (OnQuoteUpdated writes only to StockPrices/PreviousPrices/SmoothedPrices, all already Concurrent.)
    internal long TickId;
    // §impact-decouple B: the loop's single per-tick now.Ticks, stamped beside TickId in
    // CollectPendingOrdersAsync so HeldDirectional reads one deterministic clock for every bot in the tick
    // (no per-bot NowUtc() call, no within-tick boundary skew).
    internal long TickNowTicks;
    internal readonly ConcurrentDictionary<(int, CurrencyType), bool>     OverBandBuyCache  = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), bool>     OverBandSellCache = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal>  FundamentalCache  = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal>  SeedPriceCache    = new();
    internal readonly Dictionary<int, AiBotDecisionService.CommittedTotals> CommittedCache = new();
    internal readonly Dictionary<(int userId, CurrencyType), decimal> WatchlistMomentumCache    = new();
    internal readonly Dictionary<(int userId, CurrencyType), decimal> WatchlistSentimentCache   = new();
    internal readonly Dictionary<(int userId, CurrencyType), decimal> WatchlistValueGapCache    = new();
    internal readonly Dictionary<(int userId, CurrencyType), decimal> WatchlistRecentGapCache   = new();
    internal readonly Dictionary<int, decimal> WatchlistSharedSentimentCache = new();
    internal readonly Dictionary<(int userId, bool fast), decimal> WatchlistSlopeCache = new();
    // R4 §0009 Stage 2: per-(bot, currency) max long/short notional from the watchlist.
    // Memoizes ComputeInventoryBias's walk + feeds the BotDecisionProbe's invNotional column.
    internal readonly Dictionary<(int userId, CurrencyType), (decimal longNotional, decimal shortNotional)>
        WatchlistInventoryNotionalCache = new();

    // §refill-throttle (Bots:RefillThrottle): the mover-response gate. Non-null ONLY when the lever is
    // enabled (assigned from AiTradeService), so every refill-throttle call site is byte-identical and
    // draw-free when off. The per-tick cache memoizes the gate's (sign,intensity) per stock so the first
    // acting bot on a stock advances the control loop once and the rest read it. Warmed deterministically by
    // PrecomputeSharedTickCaches so the parallel region is pure-read on it; ConcurrentDictionary for that reason.
    internal RefillThrottleGate? RefillGate;
    internal readonly ConcurrentDictionary<int, (sbyte sign, decimal intensity)> RefillGateCache = new();

    internal void ClearTickCaches()
    {
        OverBandBuyCache.Clear(); OverBandSellCache.Clear();
        FundamentalCache.Clear(); SeedPriceCache.Clear();
        CommittedCache.Clear();
        WatchlistMomentumCache.Clear(); WatchlistSentimentCache.Clear();
        WatchlistValueGapCache.Clear(); WatchlistRecentGapCache.Clear();
        WatchlistSharedSentimentCache.Clear(); WatchlistSlopeCache.Clear();
        WatchlistInventoryNotionalCache.Clear();
        RefillGateCache.Clear();
    }

    // §patch 0003: per-(bot, tick) eligible-watchlist cache. Today every advanced builder
    // re-iterates user.Watchlist filtered by IsListedIn — once per BuildBracketAsync /
    // BuildShortOpenAsync / BuildProtectiveStopAsync / ChooseStockId etc. Precomputing once per
    // tick is a strict win. Stale check uses Tick == TickId (not Clear per tick) so a bot that
    // never has a decision-tick fired doesn't get its array clobbered uselessly.
    internal sealed class WatchlistView { public int[] Order = Array.Empty<int>(); public long Tick; }
    internal readonly Dictionary<int, WatchlistView> WatchlistByBot = new();
    #endregion

    #region Accessors
    internal Fund GetFund(int userId, CurrencyType currency)
        => _accounts.GetFund(userId, currency) ?? EmptyFund;

    internal Position GetPosition(int userId, int stockId)
        => _accounts.GetPosition(userId, stockId) ?? EmptyPosition;

    internal Random GetRandom(int aiUserId)
    {
        if (!AiUserRngs.ContainsKey(aiUserId))
        {
            if (!AiUsersByAiUserId.TryGetValue(aiUserId, out var ai))
                throw new KeyNotFoundException($"AIUser not found for aiUserId {aiUserId}");
            AiUserRngs[aiUserId] = new Random(DailySeed(ai.Seed, ai.AiUserId, TimeHelper.Today()));
        }
        return AiUserRngs[aiUserId];
    }

    internal decimal Decimal01(int aiUserId) => (decimal)GetRandom(aiUserId).NextDouble();

    internal static int DailySeed(int baseSeed, int userId, DateOnly date)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + baseSeed;
            h = h * 31 + userId;
            h = h * 31 + date.Year;
            h = h * 31 + date.Month;
            h = h * 31 + date.Day;
            return h & int.MaxValue;
        }
    }
    #endregion

    #region Refill throttle (§refill-throttle)
    /// <summary>
    /// Advance + read the per-stock mover gate for this tick: (sign ∈ {-1,0,+1}, intensity ∈ [0,1]).
    /// (0,0) when the lever is off. Cached per tick — the first acting bot on a stock advances the control
    /// loop, the rest read the cache. Signal = fill-derived realized return (cur-prev)/prev (non-circular,
    /// self-extinguishing), read from the loop-thread price caches; no RNG, no wall-clock.
    /// </summary>
    internal (sbyte sign, decimal intensity) MoverGate(int stockId, CurrencyType currency)
    {
        if (RefillGate is not { } gate) return ((sbyte)0, 0m);
        if (RefillGateCache.TryGetValue(stockId, out var hit)) return hit;

        var key = (stockId, currency);
        decimal cur  = StockPrices.TryGetValue(key, out var c) ? c : 0m;
        decimal prev = PreviousPrices.TryGetValue(key, out var p) ? p : 0m;
        decimal signal = (cur > 0m && prev > 0m) ? (cur - prev) / prev : 0m; // RealizedReturnFast (default source)
        decimal price  = cur > 0m ? cur : prev;

        var result = gate.Step(stockId, signal, price, TickId);
        RefillGateCache[stockId] = result;
        return result;
    }

    /// <summary>Resisting-side offset multiplier for a limit order (1.0 = no-op / lever off). Pure math, no RNG.</summary>
    internal decimal RefillWidenFactor(int stockId, CurrencyType currency, bool isBuy)
    {
        if (RefillGate is not { } gate) return 1m;
        var (sign, intensity) = MoverGate(stockId, currency);
        decimal factor = gate.WidenFactor(isBuy, sign, intensity);
        if (factor != 1m) RefillThrottleProbe.RecordWiden();
        return factor;
    }

    /// <summary>
    /// True when a resisting-side resting limit should be SKIPPED (not re-posted) this tick. The seeded
    /// <paramref name="draw"/> is invoked LAST and ONLY when the gate is enabled, skip-repost is configured,
    /// and the order resists the move ⇒ the flag-off draw stream is byte-identical.
    /// </summary>
    internal bool RefillShouldSkip(int stockId, CurrencyType currency, bool isBuy, Func<decimal> draw)
    {
        if (RefillGate is not { } gate || !gate.SkipRepostEnabled) return false;
        var (sign, _) = MoverGate(stockId, currency);
        if (!RefillThrottleGate.ResistsMove(isBuy, sign)) return false;
        RefillThrottleProbe.RecordSkipDraw();
        return draw() < gate.Config.SkipRepostProb;
    }
    #endregion

    #region Inertia stance (§A1)
    /// <summary>
    /// §A1 inertia: return the bot's persistent directional stance, rolling a fresh one when none is active
    /// or the current one has expired. While a stance holds (<paramref name="now"/> &lt; until) this is a pure
    /// dictionary read — NO RNG draw. On a (re)roll it consumes EXACTLY two seeded draws in a fixed order —
    /// first the direction (from the current <paramref name="buyProb"/>), then the duration — so a flag-on run
    /// is reproducible. Callers invoke this ONLY when the inertia flag is on, so the flag-off draw sequence is
    /// byte-identical to before. Returns +1 (buy) or -1 (sell).
    /// </summary>
    internal sbyte RollOrHoldStance(int aiUserId, decimal buyProb, DateTime now, double minSec, double maxSec)
    {
        if (Stances.TryGetValue(aiUserId, out var st) && now < st.until)
            return st.dir;

        sbyte dir = Decimal01(aiUserId) < buyProb ? (sbyte)1 : (sbyte)-1;   // draw 1: side
        double lo = Math.Min(minSec, maxSec);
        double hi = Math.Max(minSec, maxSec);
        double secs = lo + (hi - lo) * (double)Decimal01(aiUserId);         // draw 2: duration
        Stances[aiUserId] = (dir, now + TimeSpan.FromSeconds(secs));
        return dir;
    }
    #endregion

    #region Reaction/persistence split (§reaction-persistence)
    /// <summary>
    /// §reaction-persistence: advance and return the bot's continuous directional PRESSURE — an AR(1) memory
    /// <c>pressure = keep·pressure + (1−keep)·fresh</c> with a TIME-based keep <c>0.5^(dt/halfLife)</c> off the
    /// deterministic tick clock <paramref name="nowTicks"/> (NOT wall-clock, so a flag-on run is reproducible).
    /// FIRST sight seeds pressure 0 (neutral — no first-decision taker) and records the tick, returning 0. Δt≤0 or
    /// halfLife≤0 ⇒ keep 1 ⇒ no change. Pure / RNG-FREE — it advances no seeded draw, so callers gate it on the
    /// flag only and the OFF path is byte-identical. Keyed by aiUserId (one decision per bot per tick).
    /// </summary>
    internal decimal UpdatePressure(int aiUserId, decimal fresh, long nowTicks, double halfLifeSec)
    {
        if (!PressureUpdatedTicks.TryGetValue(aiUserId, out var lastTicks))
        {
            PressureUpdatedTicks[aiUserId] = nowTicks;
            Pressures[aiUserId] = 0m;
            return 0m;
        }

        double dt = (nowTicks - lastTicks) / (double)TimeSpan.TicksPerSecond;
        PressureUpdatedTicks[aiUserId] = nowTicks;
        decimal prev = Pressures.TryGetValue(aiUserId, out var pv) ? pv : 0m;
        decimal keep = (decimal)BotMath.HalfLifeKeep(dt, halfLifeSec);
        decimal next = keep * prev + (1m - keep) * fresh;
        Pressures[aiUserId] = next;
        return next;
    }

    /// <summary>
    /// §reaction-persistence TAKER override: whether persistent pressure should CROSS THE SPREAD, and the resolved
    /// side. The seeded <paramref name="draw"/> is invoked LAST and ONLY when <paramref name="enabled"/> and
    /// <c>|pressure| ≥ threshold</c> (mirrors the trend-taker gate) ⇒ the flag-off / below-threshold draw stream is
    /// byte-identical. Delegates the probability to AiBotDecisionService.TrendTakerDecision (reuse). Pure aside from
    /// the single draw.
    /// </summary>
    internal bool PressureTakerOverride(bool enabled, decimal pressure, decimal threshold, decimal effectiveGain,
        Func<decimal> draw, out bool isMarket, out bool isBuy)
    {
        isMarket = false;
        isBuy    = false;
        if (!enabled || Math.Abs(pressure) < threshold) return false;
        var (over, mkt, buy) = AiBotDecisionService.TrendTakerDecision(
            pressure, effectiveGain, contrarian: false, draw(), threshold);
        isMarket = mkt;
        isBuy    = buy;
        return over;
    }
    #endregion

    #region Personal sentiment
    // Idiosyncratic per-(bot,stock) mood added on top of the shared market sentiment.
    // Stateless and pure (hash-based) so it scales to all bots × watchlists with no
    // stored AR(1) matrix and no per-tick reroll loop.
    private const decimal PersonalLeanAmp     = 0.10m;  // fixed disposition
    private const decimal PersonalDriftAmp    = 0.10m;  // slow time-varying drift
    private const decimal PersonalReactAmp    = 0.12m;  // price-reaction weight
    private const decimal ReturnGain          = 20m;    // ±5% recent move → ±1
    private static readonly TimeSpan PersonalDriftPeriod = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Per-bot idiosyncratic sentiment for one stock: a fixed lean, a slow drift, and
    /// a price-reaction whose sign follows the bot's strategy (contrarian buys dips,
    /// momentum/panic follow the move). Returns 0 when the feature is disabled.
    /// </summary>
    internal decimal PersonalSentiment(AIUser user, int stockId, CurrencyType currency)
    {
        if (!_personalSentiment) return 0m;

        long bucket = TimeHelper.NowUtc().Ticks / PersonalDriftPeriod.Ticks;
        var lean  = HashUnit(user.Seed, user.AiUserId, stockId, 0);
        var drift = HashUnit(user.Seed, user.AiUserId, stockId, bucket);

        decimal react = 0m;
        var sign = PriceReactionSign(user.Strategy);
        if (sign != 0m &&
            SmoothedPrices.TryGetValue((stockId, currency), out var cur) && cur > 0m &&
            PreviousPrices.TryGetValue((stockId, currency), out var prev) && prev > 0m)
        {
            // §impact-decouple A: baseline is the >1-min reference when on, else prev (byte-identical off).
            var baseline = ReactionRefOr((stockId, currency), prev);
            var ret = (cur - baseline) / baseline;
            react = sign * ClampSigned(ret * ReturnGain, 1m);
        }

        return PersonalLeanAmp * lean + PersonalDriftAmp * drift + PersonalReactAmp * react;
    }

    // Pure hash → [-1, +1]. Same mix as DailySeed, extended with stockId + bucket.
    // Does NOT advance any Random, so it's call-order-independent and reproducible.
    private static decimal HashUnit(int baseSeed, int userId, int stockId, long bucket)
    {
        unchecked
        {
            long h = 17;
            h = h * 31 + baseSeed;
            h = h * 31 + userId;
            h = h * 31 + stockId;
            h = h * 31 + bucket;
            // Final avalanche so adjacent buckets/stocks don't correlate.
            h ^= h >> 33; h *= unchecked((long)0xff51afd7ed558ccd); h ^= h >> 33;
            // Map the low 32 bits to [-1, +1].
            var u = (double)((ulong)h & 0xFFFFFFFF) / 0xFFFFFFFF;  // [0,1]
            return (decimal)(u * 2.0 - 1.0);
        }
    }

    // Sign of the price-reaction, from the deterministic default extreme-reaction
    // style for the strategy (NOT PickExtremeReactionStyle — that advances RNG).
    // +1 = follow price (FOMO/Panic), -1 = fade price (Contrarian), 0 = none.
    private static decimal PriceReactionSign(AiStrategy strategy) => strategy switch
    {
        AiStrategy.TrendFollower => 1m,
        AiStrategy.Scalper       => 1m,
        AiStrategy.MeanReversion => -1m,
        AiStrategy.MarketMaker   => -1m,
        _                        => 0m,
    };

    private static decimal ClampSigned(decimal x, decimal mag) =>
        x < -mag ? -mag : x > mag ? mag : x;
    #endregion

    #region Impact-decouple (§reaction reference + refractory)
    /// <summary>
    /// §impact-decouple A: the price the directional reaction should measure against. When
    /// Bots:ImpactDecoupleReference is on AND a >1-min reference is seeded for this key, returns that
    /// reference (so the reaction responds to the multi-minute trend, not the cohort's own 1-min impact);
    /// otherwise the supplied <paramref name="fallback"/> (the legacy SmoothedPrice / PreviousPrice). Pure,
    /// RNG-free; off ⇒ returns fallback ⇒ byte-identical.
    /// </summary>
    internal decimal ReactionRefOr((int, CurrencyType) key, decimal fallback)
        => _reactionRef && ReactionRefPrices.TryGetValue(key, out var r) && r > 0m ? r : fallback;

    /// <summary>
    /// §impact-decouple B: per-bot &gt;1-min refractory (sample-and-hold) on the combined directional stance.
    /// A bot may change its held stance at most once per <paramref name="windowTicks"/>, so it cannot fade
    /// its own move within the minute it caused. Pure given the stored entry, <paramref name="nowTicks"/>,
    /// <paramref name="windowTicks"/> and <paramref name="value"/> — NO RNG, NO clock read (nowTicks is the
    /// loop's per-tick value, passed in). windowTicks ≤ 0 ⇒ no-op (returns value). Callers invoke this ONLY
    /// when the flag is on, so the flag-off path is byte-identical.
    /// </summary>
    internal decimal HeldDirectional(int userId, CurrencyType ccy, decimal value, long nowTicks, long windowTicks)
    {
        if (windowTicks <= 0L) return value;                                    // disabled / mis-set ⇒ no-op
        var key = (userId, ccy);
        if (ReactionHold.TryGetValue(key, out var st) && nowTicks - st.lastChangeTicks < windowTicks)
        {
            ImpactHoldProbe.Record(held: true);
            return st.value;                                                    // within refractory ⇒ hold
        }
        ReactionHold[key] = (value, nowTicks);                                  // expired / first ⇒ adopt + reset
        ImpactHoldProbe.Record(held: false);
        return value;
    }
    #endregion

    #region Financial Computations
    internal decimal PortfolioValueByCurrency(int userId, CurrencyType currency)
    {
        decimal total = GetFund(userId, currency).TotalBalance;
        if (!StocksByUser.TryGetValue(userId, out var stocks)) return total;
        foreach (var stockId in stocks)
        {
            var pos = _accounts.GetPosition(userId, stockId);
            if (pos is null || pos.Quantity <= 0) continue;
            if (StockPrices.TryGetValue((stockId, currency), out var price))
                total += CurrencyHelper.Notional(price, pos.Quantity, currency);
        }
        return total;
    }

    /// <summary>
    /// §direct-flow chaser: portfolio value marked at SEED price instead of the live last-trade. Used to size
    /// the chase order off a base that does NOT inflate as the chaser pushes price up — removing the
    /// positive-feedback loop where a mark-to-live base would grow the order (and its own cap) tick-over-tick.
    /// <paramref name="seedOf"/> supplies the per-(stock,currency) seed price (the decision service's SeedPrice).
    /// </summary>
    internal decimal SeedPortfolioValue(int userId, CurrencyType currency, Func<int, CurrencyType, decimal> seedOf)
    {
        decimal total = GetFund(userId, currency).TotalBalance;
        if (!StocksByUser.TryGetValue(userId, out var stocks)) return total;
        foreach (var stockId in stocks)
        {
            var pos = _accounts.GetPosition(userId, stockId);
            if (pos is null || pos.Quantity <= 0) continue;
            var sp = seedOf(stockId, currency);
            if (sp > 0m) total += CurrencyHelper.Notional(sp, pos.Quantity, currency);
        }
        return total;
    }

    internal decimal FundsPercentagePortfolio(int userId, CurrencyType currency)
    {
        // Net-worth ratio used by the cash-reserve target. Uses TotalBalance —
        // open buy reservations haven't actually deployed cash yet, so a bot
        // with working bids shouldn't read as cash-poor here. Operational
        // liquidity is enforced via AvailableBalance in ComputeOrderQuantityAsync.
        var cash  = GetFund(userId, currency).TotalBalance;
        var total = PortfolioValueByCurrency(userId, currency);
        if (total <= 0m) return cash > 0m ? 1m : 0m;
        return Clamp01(cash / total);
    }

    // Uses SmoothedPrices (EWMA) vs PreviousPrices to dampen noise from single large quotes.
    internal decimal ComputeWatchlistMomentum(AIUser user, CurrencyType currency)
    {
        var watch = user.Watchlist;
        if (watch == null || watch.Count == 0) return 0m;

        decimal total = 0m;
        int count = 0;
        foreach (var stockId in watch)
        {
            var key = (stockId, currency);
            if (SmoothedPrices.TryGetValue(key, out var curr) && curr > 0m &&
                PreviousPrices.TryGetValue(key, out var prev) && prev > 0m)
            {
                // §impact-decouple A: measure the move against the >1-min reference, not the ~1s prior price.
                // Off ⇒ baseline == prev ⇒ (curr-prev)/prev byte-identical.
                var baseline = ReactionRefOr(key, prev);
                total += (curr - baseline) / baseline;
                count++;
            }
        }
        return count > 0 ? total / count : 0m;
    }
    #endregion

    #region Helpers
    internal void ClearAll()
    {
        AiUsersByAiUserId.Clear();
        AiUsersByUserId.Clear();
        AiUserRngs.Clear();
        StocksByUser.Clear();
        OpenOrders.Clear();
        OpenLimitOrders.Clear();
        ArmedStopCount.Clear();
        StockPrices.Clear();
        PreviousPrices.Clear();
        SmoothedPrices.Clear();
        SmoothedPriceUpdatedUtc.Clear();
        BurstEndTimes.Clear();
        Stances.Clear();
        Pressures.Clear();
        PressureUpdatedTicks.Clear();
        AnchorTiltLag.Clear();
        DirectionalLag.Clear();
        PerceivedFast.Clear();
        PerceivedSlow.Clear();
        ReactionRefPrices.Clear();
        ReactionRefUpdatedUtc.Clear();
        ReactionHold.Clear();
        ProcessedTxIds.Clear();
        LastRefreshDate = DateOnly.MinValue;
        // §patch 0001: per-tick caches also cleared on full reset.
        ClearTickCaches();
        TickId = 0;
        // §patch 0003: bot-keyed eligible-watchlist cache also cleared on full reset.
        WatchlistByBot.Clear();
    }

    private static decimal Clamp01(decimal x) => x < 0m ? 0m : x > 1m ? 1m : x;

    // R5 §B: EWMA the perceived anchor tilt toward the true tilt at a per-bot rate set by Lateness.
    internal decimal LaggedAnchorTilt(int userId, CurrencyType ccy, decimal target, decimal lateness,
        decimal minAlpha, decimal maxAlpha)
        => LaggedEwma(AnchorTiltLag, userId, ccy, target, lateness, minAlpha, maxAlpha);

    // #1: EWMA the perceived directional tilt — same mechanism, on the fast slope/sentiment loop.
    internal decimal LaggedDirectional(int userId, CurrencyType ccy, decimal target, decimal lateness,
        decimal minAlpha, decimal maxAlpha)
        => LaggedEwma(DirectionalLag, userId, ccy, target, lateness, minAlpha, maxAlpha);

    // Per-(bot,ccy) Lateness-staggered EWMA toward a target. alpha = maxAlpha − L·(maxAlpha − minAlpha):
    // L=0 → maxAlpha (fast); L=1 → minAlpha (slow). Seeds at the target on first sight (no startup
    // transient). Pure/deterministic — no RNG. Per-decision EWMA, so bots that decide more often track
    // faster in wall-clock terms (active traders react faster).
    // §bot-parallelism Phase 0: IDictionary accepts both the (now Concurrent) AnchorTiltLag/DirectionalLag
    // stores; each (userId,ccy) key is single-writer-per-bot so the TryGetValue→indexer RMW stays race-free.
    private static decimal LaggedEwma(IDictionary<(int userId, CurrencyType ccy), decimal> store,
        int userId, CurrencyType ccy, decimal target, decimal lateness, decimal minAlpha, decimal maxAlpha)
    {
        var L = Clamp01(lateness);
        var alpha = maxAlpha - L * (maxAlpha - minAlpha);
        var key = (userId, ccy);
        var prev = store.TryGetValue(key, out var p) ? p : target;
        var next = prev + alpha * (target - prev);
        store[key] = next;
        return next;
    }
    #endregion

    #region Perceived-price desync (§PerceivedPriceDesync)
    /// <summary>
    /// §perceived-price desync: advance this bot's fast &amp; slow perceived-price EWMAs toward <paramref name="live"/>
    /// and return the two EWMA-gap slopes (live − perceived)/perceived — a per-bot stand-in for the SHARED sentiment
    /// slope. The smoothing alpha is dispersed per bot by Lateness AND a salted hash of <paramref name="aiUserId"/>
    /// (distinct <paramref name="saltFast"/>/<paramref name="saltSlow"/>), so the cohort's perceptions fan out across
    /// the [min,max] band instead of moving in lockstep. Seeds at <paramref name="live"/> on first sight (no startup
    /// transient ⇒ first-call slope is exactly 0). The dict key uses <paramref name="userId"/> (mirroring DirectionalLag);
    /// the dispersion hashes <paramref name="aiUserId"/> (mirroring the salted chaser/stagger cohorts). Pure/deterministic
    /// — NO RNG, NO clock. Callers invoke this ONLY when the flag is on, so the flag-off path is byte-identical.
    /// </summary>
    internal (decimal dsFast, decimal dsSlow) PerceivedSlope(int userId, int aiUserId, int stockId, CurrencyType ccy,
        decimal live, decimal lateness, int saltFast, int saltSlow, decimal minAlpha, decimal maxAlpha)
    {
        var key = (userId, stockId, ccy);
        var aFast = PerceivedAlpha(lateness, aiUserId, saltFast, minAlpha, maxAlpha);
        var aSlow = PerceivedAlpha(lateness, aiUserId, saltSlow, minAlpha, maxAlpha);
        var pFast = PerceivedStep(PerceivedFast.TryGetValue(key, out var pf) ? pf : live, live, aFast);
        var pSlow = PerceivedStep(PerceivedSlow.TryGetValue(key, out var ps) ? ps : live, live, aSlow);
        PerceivedFast[key] = pFast;
        PerceivedSlow[key] = pSlow;
        var dsFast = pFast > 0m ? (live - pFast) / pFast : 0m;
        var dsSlow = pSlow > 0m ? (live - pSlow) / pSlow : 0m;
        return (dsFast, dsSlow);
    }

    /// <summary>
    /// §perceived-price desync: per-bot EWMA alpha, dispersed by Lateness AND a salted id hash. For a fixed bot the
    /// alpha is monotone-decreasing in Lateness (L=0 ⇒ fastest, near maxAlpha; L=1 ⇒ slowest, near minAlpha); the
    /// salted hash spreads same-Lateness bots across the band so the cohort never reacts in lockstep. Always returns
    /// a value in [minAlpha, maxAlpha]. Pure (uses <see cref="BotMath.HashUnit01(int)"/>) — call-order independent, no RNG.
    /// </summary>
    internal static decimal PerceivedAlpha(decimal lateness, int aiUserId, int salt, decimal minAlpha, decimal maxAlpha)
    {
        var L = Clamp01(lateness);
        // Equal-weight blend of the per-bot disposition (Lateness) with a salted hash. Higher slowness ⇒ smaller
        // alpha. The blend keeps alpha monotone in L for any fixed bot while guaranteeing same-L bots disperse.
        var h = (decimal)BotMath.HashUnit01(aiUserId ^ salt);
        var slowness = 0.5m * L + 0.5m * h;                  // [0,1]
        return maxAlpha - slowness * (maxAlpha - minAlpha);
    }

    /// <summary>
    /// §perceived-price desync: one EWMA step toward <paramref name="live"/>. The caller seeds <paramref name="prev"/>
    /// at <paramref name="live"/> on first sight, so the first step returns live (no opening jolt). Pure, RNG-free.
    /// </summary>
    internal static decimal PerceivedStep(decimal prev, decimal live, decimal alpha)
        => prev + alpha * (live - prev);
    #endregion
}
