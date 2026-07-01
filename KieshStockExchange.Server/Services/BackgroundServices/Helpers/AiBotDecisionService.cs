using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

// §P6: an advanced-order decision, submitted via the entry/arm route (not the batch matcher).
//   StopMarketSell/TrailingStopSell (P6a): protect a held long.
//   StopMarketBuy (taker-symmetry): protect a held SHORT — a capped market buy-stop ABOVE market that
//     covers the short on a rally. Mirrors StopMarketSell so protective stops exist in BOTH directions
//     (the long-only sell-stops were the entire taker sell-skew / down-drift). Flag-gated, default off.
//   ShortOpen (P6b): flat-only market short.  LongBracket (P6b)/ShortBracket (P6c): bracketed entry.
internal enum BotAdvancedKind { StopMarketSell, TrailingStopSell, ShortOpen, LongBracket, ShortBracket, StopMarketBuy }

internal sealed record BotAdvancedDecision(
    BotAdvancedKind Kind, int StockId, int Quantity, CurrencyType Currency,
    decimal StopPrice = 0m, decimal TrailOffset = 0m, bool TrailIsPercent = false,
    decimal? BuyBudget = null, decimal? StopSlippagePct = null,
    IReadOnlyList<(decimal Price, int Quantity)>? TakeProfits = null,
    // Round 2 §0007: flip portion of a Path-2 bracket entry. 0 for round-trip-only,
    // a Path-1-minimal short bracket, plain orders, and any other advanced kind. The
    // entry-side wiring sets parent.FlipQuantity from this value just before PlaceBracketAsync
    // so the BracketCoordinator (§0008) can size the SL pool correctly.
    int FlipQuantity = 0);

/// <summary>
/// Stateless order computation — given a context and a user, produces an Order or null.
/// </summary>
internal sealed class AiBotDecisionService
{
    #region Services and Constructor
    // Max nudge applied to buyProb by the clamped sentiment value. Config-tunable (was a const) so it can be
    // LOWERED as §A2 herding takes over the directional weight — augment, don't double-count (plan §9).
    private readonly decimal _sentimentMaxBias;
    // Probability of forcing a market order, per unit of |sentiment| > 1.
    private const decimal OverflowGain     = 0.25m;
    // Cash kept un-spent on every buy so tiny rounding/race gaps don't trip Phase 1.6.
    private const decimal BuySafetyBuffer  = 5m;
    // Fat tails: tailShape∈[0,1] maps to a power exponent 1..(1+this) applied to the
    // uniform size draw. 1 = uniform (today); higher = more mass near Min, longer tail.
    private const double  TailExponentScale = 4.0;

    private readonly IMarketDataService _market;
    private readonly IAccountsCache _accounts;
    private readonly IOrderBookEngine _books;
    private readonly IStockService _stocks;
    private readonly BotSentimentService _sentiment;
    private readonly FundamentalService _funds;     // §P6 slowly-drifting per-stock fundamental
    private readonly StockProfileService _profiles; // §P6 per-stock personality (volatility class)
    private readonly ILogger<AiBotDecisionService> _logger;

    // §1 order-size fat tails (shared config; per-bot variation comes from the draw).
    private readonly bool    _fatTails;
    private readonly decimal _tradeSizeTailShape;
    private readonly decimal _blockTradeProb;
    private readonly decimal _blockTradeMultiple;

    // §2 market-maker quoting.
    private readonly bool    _mmQuoting;
    private readonly decimal _quoteHalfSpreadPrc;

    // Liquidity tuning: global multipliers over each bot's per-bot Excel values. >1 rests limit
    // orders further from market (wider band) and allows more rungs, deepening the book so market
    // sweeps hit walls instead of empty space.
    private readonly decimal _limitOffsetMult;
    private readonly decimal _maxOpenOrdersMult;

    // §P6 "tightness dial": one global multiplier over EVERY order-placement distance — limit tiers
    // (Close/Mid/Far), protective-stop + bracket-SL trigger distance, and bracket take-profit distance.
    // <1 holds the whole book closer to the current price (calmer market); 1 = unchanged. Does NOT touch
    // trade size or the slippage caps. Distinct from _limitOffsetMult, which scales only the limit ladder.
    private readonly decimal _distanceMult;

    // Market-order probability multiplier: scales each bot's UseMarketProb (more takers ⇒ more volume,
    // fewer flat candles). 1 = unchanged.
    private readonly decimal _marketProbMult;

    // Value anchor: a restoring force toward each stock's fundamental (seed) price. Without it the
    // price is a driftless momentum walk with no pull back to value, so it wanders unbounded. Strength
    // is the max buy/sell-probability tilt; Scale is the deviation fraction at which the tilt saturates.
    private readonly decimal _valueAnchorStrength;
    private readonly decimal _valueAnchorScale;
    private readonly bool    _valueTargetSelection; // concentrate the anchor via stock selection (destabilizing at high gain)
    private readonly decimal _overheatCap;          // refuse to buy above / sell below fundamental by more than this (0 = off)
    // Defensive ceiling on the effective per-stock cap (after OverheatCapMult and AnchorFastSlack).
    // 0 = no clamp. Guarantees the absolute-deviation promise regardless of personality multipliers.
    private readonly decimal _absoluteCapMax;
    // §geometric price-bands: interpret the caps as log-symmetric (buy>anchor×F, sell<anchor/F, F=1+cap) not linear
    // (±cap). Off ⇒ byte-identical. Fixes "200%" to the intended ×3 up / ÷3 down (a linear −cap floor goes negative).
    private readonly bool _geometricBand;
    private readonly decimal _marketSlippagePrc;    // low cap on every market order's slippage so none sweeps far

    // §P6 balancing: tiered-limit selection probabilities (Far = remainder), low slippage cap applied to
    // every bot protective/bracket stop fire (percent), and the max fraction of resting opposite-side
    // depth a single bot market order may sweep (structural anti-sweep).
    private readonly decimal _tierCloseProb;
    private readonly decimal _tierMidProb;
    private readonly decimal _stopSlippagePct;
    private readonly decimal _maxSweepFractionOfDepth;

    // §P6 advanced-order generation for the bot soak (all off by default).
    private readonly bool    _advancedEnabled;
    // Taker-symmetry (council design): fraction of protective-trigger decisions routed to a BUY-stop
    // (up-trigger, cash-gated) vs a sell-stop (down-trigger, share-gated). 0 ⇒ legacy sell-only,
    // byte-identical. The realized buy/sell split is this fraction (a seeded draw, no inventory fallback).
    private readonly decimal _buyStopFraction;
    // §3.6 P6: the per-kind advanced-order probabilities are now PER-BOT (AIUser.*Prob, seeded by
    // strategy in Tools/Person.py), read directly off `user` in ComputeAdvancedDecisionAsync.
    private readonly decimal _stopOffsetMin;     // SL distance from entry/market (fraction)
    private readonly decimal _stopOffsetMax;
    private readonly decimal _tpOffsetMin;       // TP distance from entry (fraction)
    private readonly decimal _tpOffsetMax;
    private readonly decimal _bracketSlippagePct;// short-bracket SL slippage cap (percent)
    private readonly int     _advancedMaxQty;    // cap qty on advanced/bracket orders (keeps sizes modest)

    // §v2 imbalance/activity/range — emergent-correlation pillars (all default off / inert).
    private readonly BotRegimeService   _regime;   // §A2/A3/A4 shared regime
    private readonly BotActivityService _activity; // §Pillar B activity field
    // §A1 inertia
    private readonly bool    _inertia;
    private readonly double  _inertiaMinSec, _inertiaMaxSec;
    private readonly decimal _inertiaLeak;
    // §A2 herding
    private readonly bool    _herding;
    private readonly decimal _followerFraction, _herdTilt;
    // §A3 momentum dominance (follower-scoped trend > reversion)
    private readonly bool    _momentumDominance;
    private readonly decimal _momentumStrength;
    // §A4 role split (flatten noise cohort directionally)
    private readonly bool    _roleSplit;
    private readonly decimal _noiseDamp;
    // §A5 fast-anchor slack (widen the intraday band veto only)
    private readonly decimal _anchorFastSlack;
    // §Pillar B selection
    private readonly bool    _activityEnabled;
    private readonly double  _activityGamma;
    // §C1/C3 microstructure
    private readonly bool    _rangeActivityImpact;
    private readonly decimal _rangeMaxSlippage;
    private readonly decimal _fatImpactProb;
    // Down-drift fix: Greed extreme-reaction style (buy-both mirror of Panic) + Scalper Panic/Greed split.
    private readonly bool    _greedStyle;
    private readonly decimal _greedSplit;
    // Down-drift fix: optional continuous cash homeostasis (smooth restoring toward the band midpoint).
    private readonly bool    _cashHomeostasisContinuous;
    private readonly decimal _cashMaxShift, _cashEdgeBuy, _cashEdgeSell;

    // Sentiment-dynamics §: the slope-aware phase model. When _sentimentDynamics is on, DirectionalBias(s,
    // ds_fast, ds_slow, L) REPLACES the old level-only momentum + sentiment-bias terms, and ApplyExtremeReaction
    // is skipped (no double-acting). All off by default ⇒ flag-off is byte-identical to today.
    private readonly bool    _sentimentDynamics;
    private readonly decimal _slopeScaleFast, _slopeScaleSlow;      // σ in tanh(ds/σ)
    private readonly decimal _momentumConviction, _scalperConviction;
    private readonly decimal _reversionConviction, _reversalConviction;
    private readonly decimal _marketMakerLean;
    private readonly decimal _aggressionBoost;                       // symmetric taker push: useMarket += boost·|bias|

    // Price-memory anchors §: medium-term EWMA + long-term daily-TWAP per (stock,currency). The
    // medium tier is the missing negative-feedback against fast moves; the long tier replaces the
    // OU walk as the value-anchor target when UseDailyAnchor=true (Fundamental() gates onto it).
    private readonly BotPriceMemoryService _priceMemory;
    private readonly bool    _useDailyAnchor;
    private readonly bool    _recentAnchorEnabled;
    private readonly decimal _recentAnchorStrength;
    private readonly decimal _recentAnchorScale;
    // R5 anchor-timing fix (Options B + C). All default-off ⇒ byte-identical.
    private readonly bool    _anchorReactionLag;     // B: per-bot Lateness EWMA on the anchor tilt
    private readonly decimal _anchorLagMinAlpha;     // B: EWMA alpha for max-Lateness bots (L=1, slowest)
    private readonly decimal _anchorLagMaxAlpha;     // B: EWMA alpha for min-Lateness bots (L=0, fastest)
    private readonly decimal _anchorDeadbandPrc;     // C: deviation band where anchors hold no pull (0 = off)
    // Order-wall declumping: round-number snapping config. Default prob 0.30 + spread 0 ⇒ today's exact snap.
    private readonly decimal _roundSnapProb;          // fraction of limit orders that snap toward a round level
    private readonly decimal _roundSnapSpread;        // 0 = exact snap (wall); >0 disperses within ±spread*unit
    // Microstructure bid-ask bounce: >0 tightens the touch-setting orders (MM symmetric quote + non-MM close
    // tier) toward mid by (1-prc) so consecutive fills zig-zag less across the spread. 0 = off (byte-identical).
    private readonly decimal _touchTightenPrc;
    // #1: Lateness-staggered lag on the FAST directional/sentiment loop (the genuine 1-min MR driver).
    private readonly bool    _directionalReactionLag; // default off ⇒ byte-identical
    private readonly decimal _dirLagMinAlpha;         // EWMA alpha for max-Lateness bots (slowest)
    private readonly decimal _dirLagMaxAlpha;         // EWMA alpha for min-Lateness bots (fastest)
    // §impact-decouple B: HARD per-bot refractory on the combined directional stance. Unlike the soft EWMA
    // lag above (which still re-responds every tick), a bot holds its stance for the whole window so it cannot
    // fade its own move within the minute it caused. Default off ⇒ byte-identical. Acts on the SAME directional
    // term as _directionalReactionLag — not meant to be co-enabled with it.
    private readonly bool _reactionHold;
    private readonly long _reactionHoldWindowTicks;   // (long)(HoldWindowSec * TimeSpan.TicksPerSecond); 0 ⇒ off

    // §perceived-price desync: each bot reacts to its OWN fast/slow perceived-price EWMA (Lateness + salted-hash
    // dispersed) instead of the SHARED sentiment slope — breaking the cohort lockstep that pins ret_acf_lag1. The
    // cleaner "what price does the bot even SEE" version of DirectionalReactionLag: when on, it SUPERSEDES that flag
    // (its tilt-lag is skipped) and is NOT meant to be co-enabled with it / the ImpactDecouple* flags. The slope is
    // measured as the EWMA gap (live − perceived)/perceived, scaled by its own Tanh scales (the perceived-return gap
    // is a different unit from the sentiment slope). Default off ⇒ byte-identical.
    private readonly bool    _perceivedDesync;
    private readonly decimal _perceivedMinAlpha;       // EWMA alpha for the slowest bots
    private readonly decimal _perceivedMaxAlpha;       // EWMA alpha for the fastest bots
    private readonly decimal _perceivedSlopeScaleFast; // Tanh scale for the per-bot FAST perceived slope
    private readonly decimal _perceivedSlopeScaleSlow; // Tanh scale for the per-bot SLOW perceived slope
    private const int PerceivedSaltFast = 0x70E1;      // distinct from ChaserSalt 0x5A17 / ChaserCadenceSalt 0x3B9F
    private const int PerceivedSaltSlow = 0x1D2B;

    // §exogenous-information chaser cohort: a salted, per-(bot,shock) hash-selected slice of eligible bots
    // (non-MM) adds strength·tanh(shock/scale) to the directional accumulator, supplying persistent 1-min
    // directional flow INTO the bounded news shock. Strength is the smooth primary ACF dial; Fraction is a
    // coarse cohort-size cap. Default strength/fraction 0 ⇒ no tilt ⇒ byte-identical. ChaserSalt keeps this
    // cohort independent of the RegimeDrift IsFollower split.
    private const int ChaserSalt = 0x5A17;
    // §chaser-v2: a distinct salt for the cadence duty-cycle so a bot's "due window" is independent of its
    // chase-cohort membership (which keys on ChaserSalt).
    private const int ChaserCadenceSalt = 0x3B9F;
    private readonly double _chaserFraction;
    private readonly double _chaserNotionalFrac;     // §direct-flow chaser: primary dial (0 ⇒ off, byte-identical)
    private readonly double _chaserMaxNotionalFrac;  // §direct-flow chaser: per-order notional cap (frac of seed PV)
    // §chaser-v2 ratio-fix co-dials (default 0 ⇒ byte-identical). SellSymFrac caps a chase-SELL to the same
    // structural buy-ceiling the bot's chase-BUY would face (drift down); BuyRoomRelaxFrac relaxes the chase-BUY
    // position-room clamp toward cash-only (gross up). IntervalTicks (0 ⇒ off) is a per-bot chase cadence.
    private readonly double _chaserSellSymFrac;
    private readonly double _chaserBuyRoomRelaxFrac;
    private readonly int    _chaserIntervalTicks;
    private readonly double _exogCap;                // shock hard-clamp Cap, reused for sizing intensity
    private readonly Func<int, double>? _shockOf;
    private readonly Func<int, int>?    _shockIdOf;
    private readonly Func<bool>?        _anyShockActive;
    // Hybrid pressure formula §: when on, directional+herd push the cohort multiplicatively
    // around 0.5 (preserves diversity at extremes); anchors stay additive (structural override).
    private readonly bool    _multiplicativeDirectional;
    private readonly decimal _diversityGain;
    // §cap-from-seed: when on, IsOverBandAsync measures deviation from the per-(stock,currency)
    // seed instead of Fundamental(). Decouples the absolute cap from any moving target (OU walk or
    // daily TWAP) so the hard ceiling never ratchets up with the anchor. Anchor PULL still uses
    // Fundamental() — only the hard veto changes target.
    private readonly bool    _capFromSeed;
    // §adaptive anchor: when on, the overheat cap re-centers on the moving anchor
    // (BotPriceMemoryService.GetAdaptiveAnchor) instead of the fixed seed, and a separate
    // total-excursion-from-seed veto (_maxTotalExcursion) is the provably-binding runaway guard.
    private readonly bool    _adaptiveAnchor;
    private readonly decimal _maxTotalExcursion;
    // §patch 0001: per-tick memoization of pure-function reads (Fundamental, SeedPrice,
    // IsOverBand, GetMidPrice, watchlist aggregators, ComputeCommitted). When on, the per-tick
    // caches on AiBotContext are read on every call; cleared at the top of each tick by
    // AiTradeService.CollectPendingOrdersAsync. Default true; flag-off path is byte-identical
    // because cache miss falls through to the original computation.
    private readonly bool    _memoizeTickValues;
    // Round 2 §0007 (Path 2) — retained as the sole bracket-eligibility flag in R3 §0006.
    // When on, ShortBracket AND LongBracket are eligible on any position sign; if the entry
    // quantity exceeds the held inventory (with sign), the entry FLIPS the position in one
    // trade. inventoryPortion = min(Q, |held|) is the round-trip portion; flipPortion =
    // Q − inventoryPortion is the new-direction portion. The FlipQuantity field is persisted
    // on the parent so the BracketCoordinator (round 2 §0008) sizes the SL pool to flipPortion
    // only — the round-trip portion is self-funding.
    //
    // The legacy Path-1-minimal `_bracketRoundTrip` flag was retired in R3 §0006 — it was a
    // strict subset of this flag (ShortBracket eligible on flat-or-long, qty-clamped to held)
    // and the round-2 soak baked _bracketFlip = true in production, making the subset
    // unreachable in any shipped configuration. Operators that set the legacy
    // Bots:Advanced:BracketRoundTrip key get a one-shot LogWarning at startup (see
    // AiTradeService ctor).
    private readonly bool    _bracketFlip;
    // Round 2 §0011 (E1): inventory-aware kind biasing. When on, the bracket-cohort kind selection
    // is biased toward position-mean-reversion: heavy long → ShortBracket; heavy short → LongBracket.
    // _inventoryBiasThresholdPrc defines "heavy" as |Quantity| * lastPrice > threshold * portfolio.
    // Default OFF ⇒ today's cumulative prob-roll selection unchanged.
    private readonly bool    _inventoryBias;
    private readonly decimal _inventoryBiasThresholdPrc;
    // Round 2 Q1 follow-up: asymmetric pull on the short→long direction. The round-2 soak found
    // that E1 over-pulls long-heavy bots into ShortBracket but lacks symmetric short-bot pull
    // (substrate asymmetry: far fewer short positions exist). Higher values → easier to trigger
    // the heavy-short bias (effective threshold divided by mult). Default 1.0 = symmetric =
    // byte-identical to round 2.
    private readonly decimal _inventoryBiasShortMult;

    // R4 §0009 Stage 4 — Option D: liquidity-aware limit-offset asymmetry. When on, the limit
    // offset for a non-MM limit order is tilted by the book imbalance so that placing into the
    // thick side gets pushed further from mid (less aggressive, doesn't add to the wall) while
    // placing into the thin side gets pulled closer to mid (more aggressive, fills the gap).
    // Default OFF (gain = 0 ⇒ no adjustment ⇒ byte-identical limit-offset path).
    private readonly bool    _liquidityAwarePlacement;
    private readonly decimal _liquidityAwareGain;

    internal AiBotDecisionService(IMarketDataService market, IAccountsCache accounts,
        IOrderBookEngine books, IStockService stocks, BotSentimentService sentiment,
        FundamentalService funds, StockProfileService profiles,
        BotRegimeService regime, BotActivityService activity,
        BotPriceMemoryService priceMemory,
        ILogger<AiBotDecisionService> logger,
        bool fatTails = true, decimal tradeSizeTailShape = 0.5m,
        decimal blockTradeProb = 0.01m, decimal blockTradeMultiple = 4m,
        bool mmQuoting = true, decimal quoteHalfSpreadPrc = 0.003m,
        decimal limitOffsetMult = 1m, decimal maxOpenOrdersMult = 1m,
        decimal distanceMult = 1m, decimal marketProbMult = 1m,
        decimal valueAnchorStrength = 0m, decimal valueAnchorScale = 0.15m,
        bool valueTargetSelection = false, decimal overheatCap = 0m,
        decimal absoluteCapMax = 0m,
        bool geometricBand = false,
        decimal marketSlippagePrc = 0.003m,
        decimal tierCloseProb = 0.6m, decimal tierMidProb = 0.3m,
        decimal stopSlippagePct = 0.3m, decimal maxSweepFractionOfDepth = 0.25m,
        bool advancedEnabled = false,
        decimal stopOffsetMin = 0.02m, decimal stopOffsetMax = 0.05m,
        decimal tpOffsetMin = 0.03m, decimal tpOffsetMax = 0.08m,
        decimal bracketSlippagePct = 5m, int advancedMaxQty = 50,
        decimal sentimentMaxBias = 0.20m,
        bool inertia = false, double inertiaMinSec = 30.0, double inertiaMaxSec = 600.0,
        decimal inertiaLeak = 0.10m,
        bool herding = false, decimal followerFraction = 0.25m, decimal herdTilt = 0.10m,
        bool momentumDominance = false, decimal momentumStrength = 0m,
        bool roleSplit = false, decimal noiseDamp = 1.0m,
        decimal anchorFastSlack = 0m,
        bool activityEnabled = false, double activityGamma = 1.0,
        bool rangeActivityImpact = false, decimal rangeMaxSlippage = 0.02m,
        decimal fatImpactProb = 0m,
        bool greedStyle = false, decimal greedSplit = 0.5m,
        bool cashHomeostasisContinuous = false, decimal cashMaxShift = 0.15m,
        decimal cashEdgeBuy = 0.95m, decimal cashEdgeSell = 0.05m,
        bool sentimentDynamics = false,
        decimal slopeScaleFast = 0.01m, decimal slopeScaleSlow = 0.005m,
        decimal momentumConviction = 0.15m, decimal scalperConviction = 0.20m,
        decimal reversionConviction = 0.15m, decimal reversalConviction = 0.10m,
        decimal marketMakerLean = 0.05m, decimal aggressionBoost = 0.20m,
        // Price-memory anchors + hybrid pressure §: defaults preserve today's behaviour exactly.
        bool useDailyAnchor = false,
        bool recentAnchorEnabled = false,
        decimal recentAnchorStrength = 0.35m, decimal recentAnchorScale = 0.04m,
        bool multiplicativeDirectional = false, decimal diversityGain = 1.5m,
        bool capFromSeed = false,
        bool adaptiveAnchor = false, decimal maxTotalExcursion = 0.35m,
        // §patch 0001: per-tick memoization (Fundamental/SeedPrice/IsOverBand/etc). Pure
        // function-result cache scoped to one bot-loop tick. Default on; off is byte-identical.
        bool memoizeTickValues = true,
        // Round 2 §0007 (Path 2): bracket-flip eligibility — both kinds on any sign + persisted
        // FlipQuantity for pool sizing. The R2 Path-1-minimal `bracketRoundTrip` flag was retired
        // in R3 §0006 (strict subset, unreachable with bracketFlip baked on).
        bool bracketFlip = false,
        // Round 2 §0011 (E1): inventory-aware kind biasing.
        bool inventoryBias = false,
        decimal inventoryBiasThresholdPrc = 0.05m,
        // Round 2 Q1 follow-up: asymmetric short-side multiplier. Default 1.0 = byte-identical
        // to round 2. Set higher (suggest 2.5) to make heavy-short detection easier to trigger,
        // increasing the symmetric short→LongBracket pull that round-2 found too weak.
        decimal inventoryBiasShortMult = 1m,
        // R4 §0009 Stage 4 — Option D: liquidity-aware limit-offset asymmetry. Default off
        // (gain = 0 ⇒ no adjustment). When on, the limit offset is tilted by book imbalance.
        bool liquidityAwarePlacement = false,
        decimal liquidityAwareGain = 0m,
        // R5 §B+C anchor-timing fix. All default-off ⇒ byte-identical.
        bool anchorReactionLag = false,
        decimal anchorLagMinAlpha = 0.05m,
        decimal anchorLagMaxAlpha = 0.30m,
        decimal anchorDeadbandPrc = 0m,
        // Order-wall declumping (round-number snap). Defaults reproduce the prior exact 30% snap.
        decimal roundSnapProb = 0.30m,
        decimal roundSnapSpread = 0m,
        // Microstructure bid-ask bounce: tighten the touch toward mid. Default 0 ⇒ byte-identical.
        decimal touchTightenPrc = 0m,
        // #1: Lateness-staggered lag on the directional/sentiment loop. Default off ⇒ byte-identical.
        bool directionalReactionLag = false,
        decimal dirLagMinAlpha = 0.05m,
        decimal dirLagMaxAlpha = 0.30m,
        // §perceived-price desync (supersedes DirectionalReactionLag). Default off ⇒ byte-identical.
        bool perceivedPriceDesync = false,
        decimal perceivedMinAlpha = 0.05m,
        decimal perceivedMaxAlpha = 0.45m,
        decimal perceivedSlopeScaleFast = 0.01m,
        decimal perceivedSlopeScaleSlow = 0.02m,
        // Taker-symmetry: fraction of protective triggers routed to buy-stops. 0 ⇒ byte-identical sell-only.
        decimal buyStopFraction = 0m,
        // §impact-decouple B: hard per-bot refractory on the directional stance. Default off ⇒ byte-identical.
        bool reactionHold = false,
        double reactionHoldWindowSec = 90.0,
        // §direct-flow chaser cohort. NotionalFrac default 0 ⇒ no chase order ⇒ byte-identical. The delegates
        // read the live exogenous shock + its generation id; null when the feature is off. The retired buyProb
        // tilt (chaserStrength/chaserScale) is gone — those are no longer ctor params.
        double chaserFraction = 0.0, double chaserNotionalFrac = 0.0, double chaserMaxNotionalFrac = 0.0,
        // §chaser-v2 ratio-fix co-dials + per-bot chase cadence; all default 0 ⇒ byte-identical.
        double chaserSellSymFrac = 0.0, double chaserBuyRoomRelaxFrac = 0.0, int chaserIntervalTicks = 0,
        double exogCap = 0.06,
        Func<int, double>? shockOf = null, Func<int, int>? shockIdOf = null, Func<bool>? anyShockActive = null)
    {
        _market      = market      ?? throw new ArgumentNullException(nameof(market));
        _accounts    = accounts    ?? throw new ArgumentNullException(nameof(accounts));
        _books       = books       ?? throw new ArgumentNullException(nameof(books));
        _stocks      = stocks      ?? throw new ArgumentNullException(nameof(stocks));
        _sentiment   = sentiment   ?? throw new ArgumentNullException(nameof(sentiment));
        _funds       = funds       ?? throw new ArgumentNullException(nameof(funds));
        _profiles    = profiles    ?? throw new ArgumentNullException(nameof(profiles));
        _regime      = regime      ?? throw new ArgumentNullException(nameof(regime));
        _activity    = activity    ?? throw new ArgumentNullException(nameof(activity));
        _priceMemory = priceMemory ?? throw new ArgumentNullException(nameof(priceMemory));
        _logger      = logger      ?? throw new ArgumentNullException(nameof(logger));
        _fatTails           = fatTails;
        _tradeSizeTailShape = tradeSizeTailShape;
        _blockTradeProb     = blockTradeProb;
        _blockTradeMultiple = blockTradeMultiple;
        _mmQuoting          = mmQuoting;
        _quoteHalfSpreadPrc = quoteHalfSpreadPrc;
        _limitOffsetMult    = limitOffsetMult <= 0m ? 1m : limitOffsetMult;
        _maxOpenOrdersMult  = maxOpenOrdersMult <= 0m ? 1m : maxOpenOrdersMult;
        _distanceMult       = distanceMult <= 0m ? 1m : distanceMult;
        _marketProbMult     = marketProbMult <= 0m ? 1m : marketProbMult;
        _valueAnchorStrength = Math.Max(0m, valueAnchorStrength);
        _valueAnchorScale    = valueAnchorScale <= 0m ? 0.15m : valueAnchorScale;
        _valueTargetSelection = valueTargetSelection;
        _overheatCap        = Math.Max(0m, overheatCap);
        _absoluteCapMax     = Math.Max(0m, absoluteCapMax);
        _geometricBand      = geometricBand;
        _marketSlippagePrc  = marketSlippagePrc <= 0m ? 0.003m : marketSlippagePrc;
        _tierCloseProb      = Clamp01(tierCloseProb);
        _tierMidProb        = Clamp01(tierMidProb);
        _stopSlippagePct    = Math.Max(0m, stopSlippagePct);
        _maxSweepFractionOfDepth = Math.Max(0m, maxSweepFractionOfDepth);
        _advancedEnabled    = advancedEnabled;
        _buyStopFraction    = Math.Clamp(buyStopFraction, 0m, 1m);
        _stopOffsetMin      = stopOffsetMin;
        _stopOffsetMax      = stopOffsetMax;
        _tpOffsetMin        = tpOffsetMin;
        _tpOffsetMax        = tpOffsetMax;
        _bracketSlippagePct = bracketSlippagePct;
        _advancedMaxQty     = advancedMaxQty;
        _sentimentMaxBias   = Math.Max(0m, sentimentMaxBias);
        _inertia            = inertia;
        _inertiaMinSec      = Math.Max(1.0, inertiaMinSec);
        _inertiaMaxSec      = Math.Max(_inertiaMinSec, inertiaMaxSec);
        _inertiaLeak        = Clamp01(inertiaLeak);
        _herding            = herding;
        _followerFraction   = Clamp01(followerFraction);
        _herdTilt           = Math.Max(0m, herdTilt);
        _momentumDominance  = momentumDominance;
        _momentumStrength   = Math.Clamp(momentumStrength, 0m, 1m);
        _roleSplit          = roleSplit;
        _noiseDamp          = Clamp01(noiseDamp);
        _anchorFastSlack    = Math.Max(0m, anchorFastSlack);
        _activityEnabled    = activityEnabled;
        _activityGamma      = Math.Max(0.0, activityGamma);
        _rangeActivityImpact = rangeActivityImpact;
        _rangeMaxSlippage   = Math.Max(0m, rangeMaxSlippage);
        _fatImpactProb      = Clamp01(fatImpactProb);
        _greedStyle         = greedStyle;
        _greedSplit         = Clamp01(greedSplit);
        _cashHomeostasisContinuous = cashHomeostasisContinuous;
        _cashMaxShift       = Math.Max(0m, cashMaxShift);
        _cashEdgeBuy        = Clamp01(cashEdgeBuy);
        _cashEdgeSell       = Clamp01(cashEdgeSell);
        _sentimentDynamics  = sentimentDynamics;
        _slopeScaleFast     = slopeScaleFast <= 0m ? 0.01m  : slopeScaleFast;
        _slopeScaleSlow     = slopeScaleSlow <= 0m ? 0.005m : slopeScaleSlow;
        _momentumConviction = Math.Max(0m, momentumConviction);
        _scalperConviction  = Math.Max(0m, scalperConviction);
        _reversionConviction = Math.Max(0m, reversionConviction);
        _reversalConviction = Math.Max(0m, reversalConviction);
        _marketMakerLean    = Math.Max(0m, marketMakerLean);
        _aggressionBoost    = Math.Max(0m, aggressionBoost);
        // Price-memory anchors + hybrid pressure §
        _useDailyAnchor            = useDailyAnchor;
        _recentAnchorEnabled       = recentAnchorEnabled;
        _recentAnchorStrength      = Math.Max(0m, recentAnchorStrength);
        _recentAnchorScale         = recentAnchorScale <= 0m ? 0.04m : recentAnchorScale;
        _multiplicativeDirectional = multiplicativeDirectional;
        _diversityGain             = Math.Max(0m, diversityGain);
        _capFromSeed               = capFromSeed;
        _adaptiveAnchor            = adaptiveAnchor;
        _maxTotalExcursion         = Math.Clamp(maxTotalExcursion, 0m, 0.99m);
        _memoizeTickValues         = memoizeTickValues;
        _bracketFlip               = bracketFlip;
        _inventoryBias             = inventoryBias;
        _inventoryBiasThresholdPrc = Math.Max(0m, inventoryBiasThresholdPrc);
        _inventoryBiasShortMult    = Math.Max(1m, inventoryBiasShortMult);
        // R4 §0009 Stage 4 — Option D: clamp gain to [0, 1] so the offset multiplier stays in [0, 2].
        _liquidityAwarePlacement   = liquidityAwarePlacement;
        _liquidityAwareGain        = liquidityAwareGain < 0m ? 0m : (liquidityAwareGain > 1m ? 1m : liquidityAwareGain);
        // R5 §B: clamp alpha floor to [0,1], then the fast cap to [floor,1] so the band is never inverted.
        _anchorReactionLag         = anchorReactionLag;
        _anchorLagMinAlpha         = anchorLagMinAlpha < 0m ? 0m : (anchorLagMinAlpha > 1m ? 1m : anchorLagMinAlpha);
        _anchorLagMaxAlpha         = anchorLagMaxAlpha < _anchorLagMinAlpha ? _anchorLagMinAlpha : (anchorLagMaxAlpha > 1m ? 1m : anchorLagMaxAlpha);
        // R5 §C: deviation band where anchors hold no pull (0 = off).
        _anchorDeadbandPrc         = anchorDeadbandPrc < 0m ? 0m : anchorDeadbandPrc;
        // Order-wall declumping: clamp prob to [0,1]; spread floored at 0 (0 ⇒ exact snap, byte-identical).
        _roundSnapProb             = roundSnapProb < 0m ? 0m : (roundSnapProb > 1m ? 1m : roundSnapProb);
        _roundSnapSpread           = roundSnapSpread < 0m ? 0m : roundSnapSpread;
        // Microstructure bounce: tightening fraction in [0,1]; 0 ⇒ off / byte-identical.
        _touchTightenPrc           = touchTightenPrc < 0m ? 0m : (touchTightenPrc > 1m ? 1m : touchTightenPrc);
        // #1: directional-loop lag — same band-clamp shape as the R5 anchor lag.
        _directionalReactionLag    = directionalReactionLag;
        _dirLagMinAlpha            = dirLagMinAlpha < 0m ? 0m : (dirLagMinAlpha > 1m ? 1m : dirLagMinAlpha);
        _dirLagMaxAlpha            = dirLagMaxAlpha < _dirLagMinAlpha ? _dirLagMinAlpha : (dirLagMaxAlpha > 1m ? 1m : dirLagMaxAlpha);
        // §perceived-price desync: alphas use the same band-clamp shape as the directional/anchor lags; scales
        // floored to their defaults if mis-set (≤0) so a bad config can't divide-by-zero in DirectionalBias's Tanh.
        _perceivedDesync           = perceivedPriceDesync;
        _perceivedMinAlpha         = perceivedMinAlpha < 0m ? 0m : (perceivedMinAlpha > 1m ? 1m : perceivedMinAlpha);
        _perceivedMaxAlpha         = perceivedMaxAlpha < _perceivedMinAlpha ? _perceivedMinAlpha : (perceivedMaxAlpha > 1m ? 1m : perceivedMaxAlpha);
        _perceivedSlopeScaleFast   = perceivedSlopeScaleFast <= 0m ? 0.01m : perceivedSlopeScaleFast;
        _perceivedSlopeScaleSlow   = perceivedSlopeScaleSlow <= 0m ? 0.02m : perceivedSlopeScaleSlow;
        // §impact-decouple B: precompute the hold window in ticks once (guard ≤0 ⇒ off / no-op in HeldDirectional).
        _reactionHold              = reactionHold;
        _reactionHoldWindowTicks   = reactionHoldWindowSec > 0.0 ? (long)(reactionHoldWindowSec * TimeSpan.TicksPerSecond) : 0L;
        _chaserFraction            = Math.Clamp(chaserFraction, 0.0, 1.0);
        _chaserNotionalFrac        = Math.Max(0.0, chaserNotionalFrac);
        _chaserMaxNotionalFrac     = Math.Max(0.0, chaserMaxNotionalFrac);
        _chaserSellSymFrac         = Math.Max(0.0, chaserSellSymFrac);
        _chaserBuyRoomRelaxFrac    = Math.Clamp(chaserBuyRoomRelaxFrac, 0.0, 1.0);
        _chaserIntervalTicks       = Math.Max(0, chaserIntervalTicks);
        _exogCap                   = Math.Max(1e-6, exogCap); // guard /0 in ChaseNotionalCap
        _shockOf                   = shockOf;
        _shockIdOf                 = shockIdOf;
        _anyShockActive            = anyShockActive;
    }
    #endregion

    #region Public Interface
    internal bool CanPlaceMoreOrder(AiBotContext ctx, AIUser user)
    {
        // A bot with persistent errors goes quiet for the day to avoid log spam
        if (user.ErrorsToday >= 10) return false;

        var openCap = (int)Math.Ceiling(user.MaxOpenOrders * _maxOpenOrdersMult);
        if (ctx.OpenOrders.TryGetValue(user.UserId, out var orders) && orders.Count >= openCap)
            return false;

        // No daily-trades cap — it would only force churning bots dormant mid-session;
        // MaxOpenOrders + ErrorsToday throttle instead. TradesToday still counts for the UI.
        return true;
    }

    internal async Task<Order?> ComputeOrderAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, CancellationToken ct = default)
    {
        // §direct-flow chaser: a selected chaser of a live shock submits a marketable order INTO the shock —
        // real persistent directional volume (the retired buyProb tilt could not move VWAP ret_acf). This is a
        // COMPLETE, draw-free substitution of the normal decision (0 seeded draws, 0 wall-clock reads). The gate
        // short-circuits on _chaserNotionalFrac==0 BEFORE _anyShockActive(), so OFF is byte-identical; resolved
        // here, ABOVE the first RNG draw (ChooseOrderType), so the OFF/non-chase draw stream is unperturbed.
        int chaseStockId = 0; OrderType chaseType = default; decimal chaseNotionalCap = 0m;
        // §chaser-v2 cadence: ChaseCadenceDue gates this bot to ≤1 chase per IntervalTicks window. IntervalTicks≤1
        // ⇒ always due (no-op), so OFF stays byte-identical and a cadence skip just falls through to the normal
        // (non-chase) decision below. Pure hash on (aiUserId, tickId) ⇒ 0 RNG draws, no wall-clock.
        if (_chaserNotionalFrac > 0.0 && _chaserFraction > 0.0 &&
            ChaseCadenceDue(user.AiUserId, ctx.TickId, _chaserIntervalTicks, ChaserCadenceSalt) &&
            _shockOf is not null && _shockIdOf is not null && _anyShockActive is not null && _anyShockActive())
        {
            var pick = ChaseSelect(ctx, user, currency);
            if (pick is { } p)
            {
                var seedPv = ctx.SeedPortfolioValue(user.UserId, currency, (s, c) => SeedPrice(s, c, ctx));
                var capN   = ChaseNotionalCap(p.Shock, _exogCap, _chaserNotionalFrac, _chaserMaxNotionalFrac, seedPv);
                if (capN > 0m)
                {
                    chaseStockId     = p.StockId;
                    chaseType        = p.Shock > 0.0 ? OrderType.SlippageMarketBuy : OrderType.SlippageMarketSell;
                    chaseNotionalCap = capN;
                }
            }
        }
        bool isChase = chaseStockId > 0;

        var type    = isChase ? chaseType : ChooseOrderType(ctx, user, currency);
        // §perf C4: snapshot every "already committed" total in ONE walk of this user's open orders, then
        // reuse it below — the sell path used to re-walk OpenOrders once per sell candidate inside ChooseStockId.
        // §patch 0001: routed through GetCommitted so the same tick's other consumers hit the cache.
        var committed = GetCommitted(ctx, user.UserId);
        var stockId = isChase ? chaseStockId : ChooseStockId(ctx, user, type, currency, committed);
        if (stockId <= 0) return null;

        // When the chosen stock's raw sentiment crosses ±1, force the order into a slippage-capped market
        // order in the bot's style-appropriate direction with probability proportional to the overflow.
        // No-op when the override would point at zero shares (sell with no position).
        // Sentiment-dynamics §: SKIPPED when the slope-aware phase model is on — that engine already owns the
        // directional behaviour (incl. the FOMO/Contrarian/Panic intent), so running both would double-act.
        // §direct-flow chaser: a chase order already carries its direction from the shock sign — skip the override.
        if (!isChase && !_sentimentDynamics)
            type = ApplyExtremeReaction(ctx, user, stockId, currency, type);

        // Value-band veto: don't chase price past the band — refuse to buy a stock already far above
        // fundamental or sell one far below it. Cuts the fuel that lets a minority of stocks escape.
        if (IsBuyOrder(type) && IsOverBand(ctx, stockId, currency, isBuy: true)) return null;
        if (IsSellOrder(type) && IsOverBand(ctx, stockId, currency, isBuy: false)) return null;

        // §refill-throttle (Bots:RefillThrottle) skip-repost: on a confirmed mover a resisting-side resting
        // LIMIT is the wall the move pushes into. With probability SkipRepostProb, don't re-post it this tick
        // so the wall thins and the mid can move. Gated to resting limits (not chase / market / slippage); the
        // seeded draw is taken LAST and ONLY when the gate is enabled and the order resists ⇒ flag-off
        // byte-identical. Returns BEFORE any price/qty compute or reservation, so nothing is stranded.
        if (!isChase && !IsTrueMarketOrder(type) && !IsSlippageOrder(type)
            && ctx.RefillShouldSkip(stockId, currency, IsBuyOrder(type),
                                    () => ctx.Decimal01(user.AiUserId)))
        {
            RefillThrottleProbe.RecordSkip(IsBuyOrder(type));
            return null;
        }

        var price    = await ComputeOrderPriceAsync(ctx, user, type, stockId, currency, ct).ConfigureAwait(false);
        // §direct-flow chaser: a slippage order with Price 0 (no live market price) fails validation and would
        // silently drop — count it as a suppressed chase rather than a phantom no-op.
        if (isChase && price <= 0m) { ChaserProbe.RecordSuppressed(IsBuyOrder(type)); return null; }
        var quantity = await ComputeOrderQuantityAsync(ctx, user, type, stockId, currency, committed,
            isChase ? chaseNotionalCap : 0m, ct).ConfigureAwait(false);
        // §direct-flow chaser: qty 0 means cash/shares exhausted on the chase name (e.g. a share-gated sell with
        // no free inventory) — record the distinct "selected but clamped to 0" bucket so a flat probe is diagnosable.
        if (quantity <= 0) { if (isChase) ChaserProbe.RecordSuppressed(IsBuyOrder(type)); return null; }

        decimal? buyBudget = null;
        if (type == OrderType.TrueMarketBuy)
        {
            var mktPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
            buyBudget = mktPrice > 0m ? CurrencyHelper.Notional(mktPrice, quantity, currency) : null;
            if (buyBudget is null or <= 0m) return null;
        }

        // §C3 fat-tailed impact: a minority of MARKET orders get a deeper sweep (slippage cap relaxed to the
        // §C1 Range max), so spikes print even in calm periods. The single roll is taken HERE — at a fixed
        // position after every other draw — only when the feature is on (off ⇒ no draw, byte-identical), and
        // applied only to slippage orders. Price impact only; the structural anti-sweep DEPTH cap is untouched.
        var slippageFrac = EffectiveSlippage(user, stockId);
        // §direct-flow chaser: skip the fat-impact RNG draw on a chase tick (it must consume 0 seeded draws).
        if (!isChase && _fatImpactProb > 0m)
        {
            bool fat = ctx.Decimal01(user.AiUserId) < _fatImpactProb;
            if (fat && IsSlippageOrder(type))
                slippageFrac = Math.Min(user.SlippageTolerancePrc, Math.Max(slippageFrac, _rangeMaxSlippage));
        }

        // §direct-flow chaser: telemetry — a real chase order was built (placed into the batch). RecordOrder
        // takes side + signed-ready notional so the soak can prove orders fire AND watch per-window net flow.
        if (isChase)
            ChaserProbe.RecordOrder(IsBuyOrder(type), CurrencyHelper.Notional(price, quantity, currency));

        // §3.6 decomposition: bots place plain (non-stop) orders — set the dimensions directly.
        return new Order
        {
            UserId = user.UserId, StockId = stockId, CurrencyType = currency,
            Quantity = quantity, Price = price,
            SlippagePercent = IsSlippageOrder(type) ? slippageFrac * 100m : null,
            BuyBudget = buyBudget,
            Side = IsBuyOrder(type) ? OrderSide.Buy : OrderSide.Sell,
            Entry = (type is OrderType.LimitBuy or OrderType.LimitSell) ? EntryType.Limit : EntryType.Market,
            Stop = StopKind.None,
        };
    }

    /// <summary>
    /// §P6a: decide whether the bot attaches a PROTECTIVE stop to an existing long this decision — a
    /// sell-stop-market below market or a P5 trailing-sell-stop. Returns null when disabled, when the gate
    /// doesn't fire, or when the bot has no free (un-protected) long shares. <b>When disabled it returns at
    /// the very top consuming NO seeded RNG</b>, so the plain-order stream stays byte-identical vs pre-P6.
    /// Submitted via the entry/arm route (not the batch matcher); fires off-loop via the stop watcher.
    /// </summary>
    internal async Task<BotAdvancedDecision?> ComputeAdvancedDecisionAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, CancellationToken ct = default)
    {
        if (!_advancedEnabled) return null;
        // §3.6 P6: the per-kind probabilities are PER-BOT (seeded by strategy in Tools/Person.py), not global
        // config. The Bots:Advanced:Enabled master switch above still gates the whole feature.
        // §patch 0004: snapshot the five prob fields once at the top via an `in`-passed ref struct, instead
        // of re-reading user.X five times below. Pure micro-opt — avoids potential volatile re-reads in
        // the hot decision loop and keeps the cumulative pick obviously deterministic.
        var probs = new AdvProbsSnapshot(user);
        decimal advProb = probs.StopProb + probs.TrailingProb + probs.ShortProb
                        + probs.LongBracketProb + probs.ShortBracketProb;
        if (advProb <= 0m) return null;
        decimal r = ctx.Decimal01(user.AiUserId);   // single seeded roll: gate + kind selection
        if (r >= advProb) return null;

        // Cumulative kind pick from the same roll (no extra draw). A builder that can't find an eligible
        // stock returns null → the caller falls through to a normal plain order this tick.
        // StopProb and TrailingProb both now gate a slippage-capped STATIC protective stop — bots never
        // arm an uncapped trailing fire (the §P6 "bound ALL stop fires" guarantee, bot-side).
        decimal c = probs.StopProb;
        if (r < c)                            return await BuildProtectiveStopAsync(ctx, user, currency, ct).ConfigureAwait(false);
        c += probs.TrailingProb; if (r < c)   return await BuildProtectiveStopAsync(ctx, user, currency, ct).ConfigureAwait(false);
        c += probs.ShortProb;    if (r < c)   return await BuildShortOpenAsync(ctx, user, currency, ct).ConfigureAwait(false);

        // Round 2 §0011 (E1): inventory-aware kind biasing. The cumulative ordering above puts
        // LongBracket before ShortBracket; when _inventoryBias is on AND the bot is heavy-long
        // (or heavy-short) we INVERT the kind selection between the two bracket buckets so the
        // population flows toward position-mean-reversion. Single roll reused — no new RNG draw,
        // so flag-off path is RNG-byte-identical.
        decimal cAfterShort = c;
        c += probs.LongBracketProb;
        if (r < c)
        {
            bool isShort = false;
            int bias = 0;
            if (_inventoryBias && _inventoryBiasThresholdPrc > 0m)
            {
                bias = ComputeInventoryBias(ctx, user, currency);
                if (bias > 0) isShort = true;          // heavy long → flip to ShortBracket
                // heavy short and r is in the LongBracket bucket → keep LongBracket (the natural mean-reversion direction)
            }
            // R4 §0009 Stage 2: probe the inversion decision (kindPre=0 LongBracket bucket).
            BotDecisionProbe.RecordAdvancedIntent(user.AiUserId, (int)user.Strategy,
                kindPre: 0, bias: bias, kindPost: isShort ? 1 : 0);
            var dec = await BuildBracketAsync(ctx, user, currency, isShort: isShort, ct).ConfigureAwait(false);
            BotDecisionProbe.RecordAdvancedResult(user.AiUserId, (int)user.Strategy,
                kindPost: isShort ? 1 : 0, qty: dec?.Quantity ?? 0,
                flipQty: dec?.FlipQuantity ?? 0, success: dec is not null);
            return dec;
        }
        // r in the ShortBracket bucket — same inversion logic for the symmetric half.
        bool isShortKind = true;
        int biasShort = 0;
        if (_inventoryBias && _inventoryBiasThresholdPrc > 0m)
        {
            biasShort = ComputeInventoryBias(ctx, user, currency);
            if (biasShort < 0) isShortKind = false;   // heavy short → flip to LongBracket
        }
        // R4 §0009 Stage 2: probe the inversion decision (kindPre=1 ShortBracket bucket).
        BotDecisionProbe.RecordAdvancedIntent(user.AiUserId, (int)user.Strategy,
            kindPre: 1, bias: biasShort, kindPost: isShortKind ? 1 : 0);
        var decShort = await BuildBracketAsync(ctx, user, currency, isShort: isShortKind, ct).ConfigureAwait(false);
        BotDecisionProbe.RecordAdvancedResult(user.AiUserId, (int)user.Strategy,
            kindPost: isShortKind ? 1 : 0, qty: decShort?.Quantity ?? 0,
            flipQty: decShort?.FlipQuantity ?? 0, success: decShort is not null);
        return decShort;
    }

    // Round 2 §0011 (E1): inventory bias direction.
    //   > 0 → heavy long (prefer ShortBracket to round-trip out of the over-long position)
    //   < 0 → heavy short (prefer LongBracket to round-trip-cover the over-short position)
    //   = 0 → roughly flat (no preference; cumulative roll picks)
    // "Heavy" threshold: max |stock notional| / portfolio value > InventoryBiasThresholdPrc.
    // Uses the per-tick EligibleWatchlist cache and ctx position reads (no DB calls).
    private int ComputeInventoryBias(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        var portfolio = ctx.PortfolioValueByCurrency(user.UserId, currency);
        if (portfolio <= 0m) return 0;
        var (maxLongNotional, maxShortNotional) = WatchlistInventoryNotional(ctx, user, currency);
        // Round 2 Q1: short-side threshold divided by _inventoryBiasShortMult so short-heavy
        // detection triggers more easily, compensating for the substrate asymmetry that left
        // the round-2 bear tail asymmetric. _inventoryBiasShortMult defaults to 1.0 (symmetric)
        // ⇒ byte-identical to round 2.
        if (maxLongNotional  > _inventoryBiasThresholdPrc * portfolio) return +1;
        if (maxShortNotional > _inventoryBiasThresholdPrc * portfolio / _inventoryBiasShortMult) return -1;
        return 0;
    }

    // R4 §0009 Stage 2: max long / max short notional across the bot's eligible watchlist,
    // memoized per-(bot, currency, tick). Refactor of the walk previously inlined in
    // ComputeInventoryBias — both the bias logic and the BotDecisionProbe (which reads the
    // signed difference long − short) hit the same cache so flag-on probe cost is one dict
    // read, not a second watchlist walk.
    private (decimal longNotional, decimal shortNotional) WatchlistInventoryNotional(
        AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        var key = (user.UserId, currency);
        if (ctx.WatchlistInventoryNotionalCache.TryGetValue(key, out var cached)) return cached;

        var watch = EligibleWatchlist(ctx, user, currency);
        decimal maxLongNotional = 0m, maxShortNotional = 0m;
        for (int i = 0; i < watch.Length; i++)
        {
            var pos = _accounts.GetPosition(user.UserId, watch[i]);
            if (pos is null || pos.Quantity == 0) continue;
            if (!ctx.SmoothedPrices.TryGetValue((watch[i], currency), out var price) || price <= 0m) continue;
            var notional = CurrencyHelper.Notional(price, Math.Abs(pos.Quantity), currency);
            if (pos.Quantity > 0) { if (notional > maxLongNotional) maxLongNotional = notional; }
            else                  { if (notional > maxShortNotional) maxShortNotional = notional; }
        }
        var result = (maxLongNotional, maxShortNotional);
        ctx.WatchlistInventoryNotionalCache[key] = result;
        return result;
    }

    // §patch 0004: ref-struct snapshot of the five advanced-order probability fields. Stack-allocated,
    // no GC pressure. The `ref struct` constraint guarantees it never escapes the decision method.
    private readonly ref struct AdvProbsSnapshot
    {
        public readonly decimal StopProb, TrailingProb, ShortProb, LongBracketProb, ShortBracketProb;
        public AdvProbsSnapshot(AIUser u)
        {
            StopProb = u.StopProb; TrailingProb = u.TrailingProb; ShortProb = u.ShortProb;
            LongBracketProb = u.LongBracketProb; ShortBracketProb = u.ShortBracketProb;
        }
    }

    // P6a: arm a slippage-capped, fundamental-relative static TRIGGER order (bots never arm uncapped
    // trailing stops; see ComputeAdvancedDecisionAsync). §patch 0003: uses EligibleWatchlist.
    // BuyStopFraction == 0 (default): legacy SELL-ONLY — protect the first watchlist long with free shares
    // (sell-stop below market). BYTE-IDENTICAL (no RNG drawn).
    // BuyStopFraction > 0 (council design — taker-flow symmetry): one seeded draw picks the trigger
    // DIRECTION (buy if draw < fraction, else sell), then the matching RESOURCE is required — free shares
    // for a sell-stop, free CASH for a buy-stop — with NO fallback to the other side, so the realized
    // buy/sell split is the enforced fraction (not the net-long inventory). A buy-stop fires a capped
    // market BUY above market (covers a short if held, else a breakout buy), the up-trigger mirror of the
    // sell-stop. Start the fraction conservative: the buy side is structurally stronger (cash is ~unlimited
    // vs a finite long inventory + momentum), so taker-VOLUME balance is the soak gate, not order count.
    private async Task<BotAdvancedDecision?> BuildProtectiveStopAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, CancellationToken ct)
    {
        var watch = EligibleWatchlist(ctx, user, currency);
        if (watch.Length == 0) return null;

        bool wantBuy = _buyStopFraction > 0m && ctx.Decimal01(user.AiUserId) < _buyStopFraction;

        if (!wantBuy)
        {
            // Sell-stop: first watchlist long with free shares (legacy path). No resource ⇒ skip (no fallback).
            foreach (var id in watch)
            {
                var avail = _accounts.GetPosition(user.UserId, id)?.AvailableQuantity ?? 0;
                if (avail > 0)
                    return await BuildCappedTriggerAsync(ctx, user, currency, id,
                        Math.Min(avail, _advancedMaxQty), isBuy: false, ct).ConfigureAwait(false);
            }
            return null;
        }

        // Buy-stop (up-trigger, cash-gated): cover a held short if any, else a breakout buy on the first
        // eligible name. Sized from free cash at the trigger anchor, capped like the sell side.
        decimal availCash = _accounts.GetFund(user.UserId, currency)?.AvailableBalance ?? 0m;
        if (availCash <= 0m) return null;
        int stockId = 0;
        foreach (var id in watch)
        {
            if ((_accounts.GetPosition(user.UserId, id)?.Quantity ?? 0) < 0) { stockId = id; break; }
        }
        if (stockId <= 0) stockId = watch[0];
        var price = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (price <= 0m) return null;
        int qty = Math.Min(_advancedMaxQty, (int)Math.Floor(availCash / price));   // reserve ≈ qty × anchor
        if (qty <= 0) return null;
        return await BuildCappedTriggerAsync(ctx, user, currency, stockId, qty, isBuy: true, ct).ConfigureAwait(false);
    }

    // Shared trigger builder: a slippage-capped, fundamental-relative static stop forced strictly PAST market
    // (below for a sell-stop, above for a buy-stop) so it's always valid and varied triggers don't pile at one
    // level and chain-fire. Returns the StopMarketSell/StopMarketBuy decision.
    private async Task<BotAdvancedDecision?> BuildCappedTriggerAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, int stockId, int qty, bool isBuy, CancellationToken ct)
    {
        var price = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (price <= 0m) return null;
        var offset = StopOffset(ctx, user);
        var fund = Fundamental(stockId, currency, ctx);
        var refPrice = fund > 0m ? (price + fund) / 2m : price;
        decimal stopPrice = isBuy
            ? CurrencyHelper.RoundMoney(Math.Max(refPrice * (1m + offset), price * (1m + 0.002m)), currency)
            : CurrencyHelper.RoundMoney(Math.Min(refPrice * (1m - offset), price * (1m - 0.002m)), currency);
        if (stopPrice <= 0m) return null;
        return new BotAdvancedDecision(
            isBuy ? BotAdvancedKind.StopMarketBuy : BotAdvancedKind.StopMarketSell,
            stockId, qty, currency, StopPrice: stopPrice, StopSlippagePct: _stopSlippagePct);
    }

    // P6b: open a flat-only cash-collateralized short (market sell on a stock the bot doesn't hold). Flat-only
    // so the bot never traverses the long→short flip (risk #7). Collateral is buying-power-neutral at open, so
    // sizing is just an exposure cap, not a cash constraint.
    private async Task<BotAdvancedDecision?> BuildShortOpenAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, CancellationToken ct)
    {
        int stockId = FirstFlatStock(ctx, user, currency);
        if (stockId <= 0) return null;
        var price = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (price <= 0m) return null;
        // §P6: don't pile a fresh short into a stock already far below fundamental (downward runaway fuel).
        if (IsOverBand(ctx, stockId, currency, isBuy: false)) return null;
        int qty = AdvancedExposureQty(ctx, user, currency, price);
        if (qty <= 0) return null;
        // §P6 anti-sweep: the market-sell entry can't take more than a fraction of the resting bids.
        qty = ApplyDepthCap(qty, isBuy: false, stockId, currency);
        if (qty <= 0) return null;
        return new BotAdvancedDecision(BotAdvancedKind.ShortOpen, stockId, qty, currency);
    }

    // P6b/P6c: open a bracketed entry. Long = market buy + sell-stop below + buy-limit… *sell*-limit TPs above;
    // short = flat-only market sell + slippage-capped buy-stop above + buy-limit TPs below. Sized so the entry
    // (long: cash; short: SL cash pool) is affordable, and kept small via _advancedMaxQty.
    private async Task<BotAdvancedDecision?> BuildBracketAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, bool isShort, CancellationToken ct)
    {
        // Round 2 §0007 (Path 2): with _bracketFlip on, BOTH kinds are eligible on any position
        // sign. When the entry qty exceeds the held inventory (with sign), the entry flips the
        // position in one trade — flipPortion = entryQty − inventoryPortion is persisted on the
        // parent so the BracketCoordinator (§0008) sizes the SL pool to flipPortion only.
        //
        // _bracketFlip default OFF for byte-identical flag-off. R3 §0006 retired the
        // intermediate _bracketRoundTrip flag — the Path-1 minimal "flat-or-long, qty-clamped"
        // branch was a strict subset of Path 2's flat-flip and unreachable in any shipped config
        // since round 2 baked _bracketFlip = true. The decision now collapses to flip-or-flat-only.
        int stockId = isShort
            ? (_bracketFlip
                ? FirstAnyStock(ctx, user, currency)
                : FirstFlatStock(ctx, user, currency))
            : (_bracketFlip
                ? FirstAnyStock(ctx, user, currency)
                : FirstLongableStock(ctx, user, currency));
        if (stockId <= 0) return null;
        var price = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (price <= 0m) return null;

        // §P6: brackets respect the same value-band veto as plain orders — don't open a long into a stock
        // already far above fundamental (its market entry is the fuel that fed the runaway), nor a short
        // into one already far below.
        if (IsOverBand(ctx, stockId, currency, isBuy: !isShort)) return null;

        // SL distance (per-bot, de-clustered, bounded inside Far walls) + two TP distances (sorted so
        // TP1 is nearer market than TP2).
        var slOff = StopOffset(ctx, user);
        // §P6: take-profit distances are PER-BOT (TpOffsetMin/MaxPrc, baked tight in the Excel pipeline),
        // falling back to the global Advanced:TpOffsetPrc config for un-regenerated bots. The tightness
        // dial still applies for any leftover global-config use (1.0 in production now the values are baked).
        var tpLo = user.TpOffsetMaxPrc > 0m ? user.TpOffsetMinPrc : _tpOffsetMin;
        var tpHi = user.TpOffsetMaxPrc > 0m ? user.TpOffsetMaxPrc : _tpOffsetMax;
        var o1 = Lerp(tpLo, tpHi, ctx.Decimal01(user.AiUserId)) * _distanceMult;
        var o2 = Lerp(tpLo, tpHi, ctx.Decimal01(user.AiUserId)) * _distanceMult;
        var tpNear = Math.Min(o1, o2); var tpFar = Math.Max(o1, o2);
        if (tpFar <= tpNear) tpFar = tpNear + 0.005m * _distanceMult;   // keep TP2 strictly past TP1

        var fund = _accounts.GetFund(user.UserId, currency);
        decimal avail = fund?.AvailableBalance ?? 0m;

        decimal stopPrice; decimal? slippage; decimal? buyBudget; int qty;
        if (isShort)
        {
            stopPrice = CurrencyHelper.RoundMoney(price * (1m + slOff), currency);     // SL above entry
            slippage  = _bracketSlippagePct;                                           // capped (uncapped rejected)
            buyBudget = null;
            decimal slWorst = stopPrice * (1m + _bracketSlippagePct / 100m);           // worst-case buyback / share
            if (slWorst <= 0m) return null;
            qty = (int)Math.Floor((avail - BuySafetyBuffer) / slWorst);                // SL cash pool must fit
        }
        else
        {
            // §P6: fundamental-relative SL strictly below entry, and a low slippage cap on the fire
            // (was uncapped null — the downward-cascade source). Share-reserved, so the cap is safe.
            var fundVal  = Fundamental(stockId, currency, ctx);
            var refPrice = fundVal > 0m ? (price + fundVal) / 2m : price;
            var slCand   = refPrice * (1m - slOff);
            var slCeil   = price * (1m - 0.002m);
            stopPrice = CurrencyHelper.RoundMoney(Math.Min(slCand, slCeil), currency);  // SL below entry
            slippage  = _stopSlippagePct;                                               // capped fire
            qty = (int)Math.Floor((avail - BuySafetyBuffer) / price);                  // entry cash must fit
            buyBudget = 0m; // set after qty is known
        }
        qty = Math.Min(qty, _advancedMaxQty);
        // §P6 anti-sweep: the market entry leg can't take more than a fraction of the resting opposite
        // side (long entry buys asks, short entry sells bids) — the same structural cap as plain orders.
        qty = ApplyDepthCap(qty, isBuy: !isShort, stockId, currency);

        // Round 2 §0007 (Path 2): flip vs round-trip split. The held inventory's sign determines
        // whether this entry is rounding-trip (held has the SAME sign-orientation: long held for
        // a sell-entry, short held for a buy-entry) or pure new direction.
        var heldQty = _accounts.GetPosition(user.UserId, stockId)?.Quantity ?? 0;
        int flipQty = 0;
        if (_bracketFlip)
        {
            // entry side: short bracket sells, long bracket buys.
            // round-trip applies when held sign opposes entry side direction (sell on +X long, buy on −X short).
            int inventoryPortion = 0;
            if (isShort && heldQty > 0)            inventoryPortion = Math.Min(qty, heldQty);
            else if (!isShort && heldQty < 0)      inventoryPortion = Math.Min(qty, -heldQty);
            // anything past inventoryPortion is the flip portion (opens a new position).
            flipQty = Math.Max(0, qty - inventoryPortion);

            // Round 2 §0012 (extension E5): when BOTH a round-trip and a flip qty fit the bot's
            // budget AND inventoryPortion > 0, bias the qty by RoundtripBiasPrc — a high bias
            // bot clamps to inventoryPortion (no flip); a low bias bot keeps the full qty (the
            // flip portion stays). Single seeded RNG draw — no new RNG state introduced.
            if (inventoryPortion > 0 && flipQty > 0)
            {
                if (ctx.Decimal01(user.AiUserId) < user.RoundtripBiasPrc)
                {
                    qty = inventoryPortion;
                    flipQty = 0;
                }
            }
        }
        // R3 §0006: the legacy Path-1-minimal `_bracketRoundTrip` qty-clamp branch was
        // removed here. When _bracketFlip is OFF the upstream picker (FirstFlatStock /
        // FirstLongableStock) already constrains us to clean inventory, so no flip is
        // possible and no clamp is needed.
        if (qty < 2) return null;   // need ≥2 for a 2-leg scale-out
        if (!isShort)
        {
            // Round 2 §0007: for a long-bracket-on-short, the cover portion (min(qty, |held|))
            // releases reserved collateral instead of spending fresh cash — only the flip portion
            // actually claims new cash. We still budget the full qty for the entry leg (the
            // engine settlement's cover-release wires this), so the buyBudget is conservative.
            buyBudget = CurrencyHelper.RoundMoney(price * qty, currency);
        }

        var tp1Qty = qty / 2; var tp2Qty = qty - tp1Qty;
        var tps = isShort
            ? new List<(decimal, int)> { (CurrencyHelper.RoundMoney(price * (1m - tpNear), currency), tp1Qty),
                                         (CurrencyHelper.RoundMoney(price * (1m - tpFar),  currency), tp2Qty) }
            : new List<(decimal, int)> { (CurrencyHelper.RoundMoney(price * (1m + tpNear), currency), tp1Qty),
                                         (CurrencyHelper.RoundMoney(price * (1m + tpFar),  currency), tp2Qty) };

        return new BotAdvancedDecision(
            isShort ? BotAdvancedKind.ShortBracket : BotAdvancedKind.LongBracket,
            stockId, qty, currency, StopPrice: stopPrice, BuyBudget: buyBudget,
            StopSlippagePct: slippage, TakeProfits: tps,
            FlipQuantity: flipQty);
    }

    // Round 2 §0007: bracket eligible on any position sign — the Path-2 picker. Identical to
    // EligibleWatchlist's per-tick view; no per-position predicate is applied here because Path 2
    // wants the bracket to potentially flip. The first stock the bot can fund a bracket on is
    // accepted (the funding check happens inside BuildBracketAsync).
    private int FirstAnyStock(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        var watch = EligibleWatchlist(ctx, user, currency);
        return watch.Length > 0 ? watch[0] : 0;
    }

    // §patch 0003: per-(bot, tick) eligible-watchlist precompute. Today every advanced builder
    // re-iterated user.Watchlist filtered by IsListedIn — once per BuildBracketAsync /
    // BuildShortOpenAsync / BuildProtectiveStopAsync / ChooseStockId. With wider Path 2
    // eligibility (patch 0007) the picker has even more candidates; precomputing is the
    // foundation that makes that affordable. Stale check via Tick == TickId, not per-tick Clear.
    private int[] EligibleWatchlist(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        if (!ctx.WatchlistByBot.TryGetValue(user.AiUserId, out var v))
            ctx.WatchlistByBot[user.AiUserId] = v = new AiBotContext.WatchlistView();
        if (v.Tick == ctx.TickId) return v.Order;

        var watch = user.Watchlist;
        if (watch is null || watch.Count == 0) { v.Order = Array.Empty<int>(); v.Tick = ctx.TickId; return v.Order; }
        var list = new List<int>(watch.Count);
        foreach (var id in watch) if (_stocks.IsListedIn(id, currency)) list.Add(id);
        v.Order = list.ToArray();
        v.Tick = ctx.TickId;
        return v.Order;
    }

    // First watchlist stock the bot is FLAT on (per the engine view) — for flat-only shorts/short-brackets.
    // §patch 0003: uses EligibleWatchlist cache.
    private int FirstFlatStock(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        var watch = EligibleWatchlist(ctx, user, currency);
        foreach (var id in watch)
            if ((_accounts.GetPosition(user.UserId, id)?.Quantity ?? 0) == 0) return id;
        return 0;
    }

    // First watchlist stock the bot is flat-or-long on (never short) — for long brackets.
    // §patch 0003: uses EligibleWatchlist cache.
    private int FirstLongableStock(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        var watch = EligibleWatchlist(ctx, user, currency);
        foreach (var id in watch)
            if ((_accounts.GetPosition(user.UserId, id)?.Quantity ?? 0) >= 0) return id;
        return 0;
    }

    // Modest exposure-capped quantity for an advanced entry: a slice of portfolio, ≤ _advancedMaxQty.
    private int AdvancedExposureQty(AiBotContext ctx, AIUser user, CurrencyType currency, decimal price)
    {
        if (price <= 0m) return 0;
        var portfolio = ctx.PortfolioValueByCurrency(user.UserId, currency);
        if (portfolio <= 0m) return 0;
        var notional = Lerp(user.MinTradeAmountPrc, user.MaxTradeAmountPrc, ctx.Decimal01(user.AiUserId)) * portfolio;
        int qty = (int)Math.Floor(notional / price);
        return Math.Min(Math.Max(qty, 0), _advancedMaxQty);
    }
    #endregion

    #region Order Decision Logic
    private OrderType ChooseOrderType(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        // §2 Market-maker quoting: post a resting limit on the under-represented side
        // so the bot maintains a two-sided quote near mid over successive ticks. Skips
        // the directional logic below — MM bots provide liquidity, not direction. They
        // still react to shocks via ApplyExtremeReaction downstream.
        if (_mmQuoting && user.Strategy == AiStrategy.MarketMaker)
            return ChooseMarketMakerQuote(ctx, user);

        // §v2: buyProb is built from a HOMEOSTATIC part (kept for every bot — it keeps the fleet solvent)
        // and a DIRECTIONAL part (momentum + sentiment) that §A4 can damp for the noise cohort so the
        // directional cohort's imbalance isn't averaged away. The §A2 herd tilt and the value-anchor tilt
        // are separate terms (anchor is a bounding force, never damped). With every v2 flag off this sums to
        // exactly today's expression (same terms, same order, no new RNG) → byte-identical.
        var notMM = user.Strategy != AiStrategy.MarketMaker; // §A1–A3 exclude MM (it returned above when quoting on)

        // 1. Homeostatic base: directional bias seed + cash-reserve restoring shift.
        var cashPrc      = ctx.FundsPercentagePortfolio(user.UserId, currency);
        var homeostatic  = CashHomeostasis(user.BuyBiasPrc, cashPrc,
            user.MinCashReservePrc, user.MaxCashReservePrc,
            _cashHomeostasisContinuous, _cashMaxShift, _cashEdgeBuy, _cashEdgeSell);

        // 2. Directional: strategy momentum bias (EWMA smoothed) + watchlist sentiment tilt.
        var momentum       = ctx.ComputeWatchlistMomentum(user, currency);
        var momentumSignal = ClampSigned(momentum * 20m, 1m); // ±5% move → ±1
        var directional    = 0m;

        if (_sentimentDynamics)
        {
            // Sentiment-dynamics §: the slope-aware phase model REPLACES the old level-only momentum + sentiment
            // terms. Each strategy maps (level s, fast/slow slope ds, per-bot lateness L) to a signed buyProb
            // shift — momentum follows/chases, mean-reversion fades the extreme and its reversal. Shared
            // sentiment only (the slope is per-stock shared), watchlist-averaged. Deterministic, no RNG.
            var s   = ClampSigned(AverageWatchlistSharedSentiment(user), 1m);
            var dsF = AverageWatchlistSlope(user, fast: true);
            var dsS = AverageWatchlistSlope(user, fast: false);
            var slopeScaleFast = _slopeScaleFast;
            var slopeScaleSlow = _slopeScaleSlow;
            // §perceived-price desync: replace the SHARED sentiment slope with THIS bot's own salt+Lateness-dispersed
            // perceived-price slope, so the cohort stops feeding DirectionalBias the same lockstep slope each tick.
            // Its own Tanh scales apply (the perceived-return gap is a different unit). Off ⇒ shared slope unchanged.
            if (_perceivedDesync)
            {
                (dsF, dsS) = AveragePerceivedSlope(ctx, user, currency);
                slopeScaleFast = _perceivedSlopeScaleFast;
                slopeScaleSlow = _perceivedSlopeScaleSlow;
            }
            directional = DirectionalBias(user.Strategy, s, dsF, dsS, user.Lateness,
                _momentumConviction, _scalperConviction, _reversionConviction, _reversalConviction,
                _marketMakerLean, slopeScaleFast, slopeScaleSlow);
        }
        else
        {
            // §A3 momentum dominance: follower-scoped TF>MR so a regime can recruit a trend (Lux–Marchesi).
            // Strength only WEAKENS the reversion coefficient (×(1−s)), never flips its sign. Off ⇒ both ×1.
            decimal tfMul = 1m, mrMul = 1m;
            if (_momentumDominance && _momentumStrength > 0m && notMM &&
                _regime.IsFollower(user.AiUserId, _followerFraction))
            {
                tfMul = 1m + _momentumStrength;
                mrMul = 1m - _momentumStrength;
            }
            switch (user.Strategy)
            {
                // Equal magnitude on both sides so the net effect across the 25/25 TF/MR split is zero in
                // expectation (until §A3 tilts it under a regime).
                case AiStrategy.TrendFollower:
                    directional += 0.175m * tfMul * momentumSignal; // Chase the move
                    break;
                case AiStrategy.MeanReversion:
                    directional -= 0.175m * mrMul * momentumSignal; // Fade the move
                    break;
                // MarketMaker, Scalper, Random: no directional momentum bias
            }

            // Linear sentiment bias. Watchlist-averaged so the tilt reflects the broad mood for stocks this bot
            // cares about. Clamped to ±1 here — extremes drive the forced market order in ComputeOrderAsync.
            var sentimentClamped = ClampSigned(AverageWatchlistSentiment(ctx, user, currency), 1m);
            directional += sentimentClamped * _sentimentMaxBias;
        }

        // #1: per-bot Lateness lag on the fast directional/sentiment reaction (per-(bot,ccy) EWMA,
        // persists across ticks). Staggers the cohort's slope reaction so the synchronized next-minute
        // overcorrection — the genuine ~−0.31 mean-reversion the bounce diagnostic isolated — is smeared.
        // §perceived-price desync supersedes the tilt-lag: when on, the desync already dispersed the reaction at the
        // PRICE level above, so skip the (cohort-uniform) output-tilt EWMA to avoid double-lagging / co-enabling.
        if (_directionalReactionLag && !_perceivedDesync)
            directional = ctx.LaggedDirectional(user.UserId, currency, directional, user.Lateness,
                _dirLagMinAlpha, _dirLagMaxAlpha);

        // §impact-decouple B: HARD per-bot refractory — hold the combined directional stance for the whole
        // window so the bot cannot reverse it within the minute it acted (kills the 1-min self-fade without
        // the soft lag's residual every-tick response). Applied before role-split/herd/anchor and before the
        // §taker aggression term reads |directional| (so a held stance also freezes that, by design).
        if (_reactionHold)
            directional = ctx.HeldDirectional(user.UserId, currency, directional,
                ctx.TickNowTicks, _reactionHoldWindowTicks);

        // §direct-flow chaser: the old buyProb tilt (directional += AverageWatchlistChase) is RETIRED here — the
        // chaser now emits real marketable order flow, resolved at the top of ComputeOrderAsync. Nothing is added
        // to `directional` for the shock; this keeps the directional accumulator byte-identical to the no-chaser path.

        // §A4 role split: flatten the noise cohort's DIRECTIONAL part (cash homeostasis untouched) so it
        // stops diluting the directional cohort. Followers (and everyone when the flag is off) keep ×1.
        var noiseFactor = (_roleSplit && notMM && !_regime.IsFollower(user.AiUserId, _followerFraction))
            ? (1m - _noiseDamp) : 1m;

        // §A2 herding: a sharp common regime tilt a fraction of bots commit to together (Kirman). 0 for
        // non-followers and when the flag is off.
        var herdTilt = (_herding && notMM) ? _regime.HerdTilt(user.AiUserId, _followerFraction, _herdTilt) : 0m;

        // LONG-TERM value anchor: tilt toward Fundamental() — the OU walk by default, or the
        // hard-clamped previous-day TWAP from BotPriceMemoryService when UseDailyAnchor=true. The
        // restoring force that keeps price bounded — never damped by the role split. The gap is
        // NOT clamped at ±Scale so the pull keeps growing past saturation (deeper deviation ⇒
        // stronger pull). The final Clamp01 on buyProb is the hard ceiling.
        var anchorTilt = 0m;
        if (_valueAnchorStrength > 0m)
        {
            // R5 §C: dead-band the raw deviation (fraction of price) before scaling — inside the band the
            // anchor exerts zero pull (price wanders freely, ret_acf→0 there); only the excess corrects.
            var gap = ApplyAnchorDeadband(AverageWatchlistValueGap(ctx, user, currency)) / _valueAnchorScale;
            anchorTilt = gap * _valueAnchorStrength;
        }

        // MEDIUM-TERM mean-reversion: pull back toward the per-stock recent EWMA so a stock that
        // rips faster than its own short-window average feels a negative-feedback tilt away from
        // any cap rather than pinning at it. Always reads _priceMemory.GetRecentEwma — the
        // medium-term anchor target is independent of the long-anchor switch. Default OFF ⇒
        // contributes 0 and BotPriceMemoryService.Tick is also inert (anyConsumer=false).
        if (_recentAnchorEnabled && _recentAnchorStrength > 0m)
        {
            var rgap = ApplyAnchorDeadband(AverageWatchlistRecentGap(ctx, user, currency)) / _recentAnchorScale;  // R5 §C
            anchorTilt += rgap * _recentAnchorStrength;
        }

        // R5 §B: per-bot Lateness lag on the combined anchor tilt (per-(bot,ccy) EWMA, persists across ticks).
        // Decouples the synchronized next-minute snap-back that pins ret_acf_lag1 ≈ −0.43.
        if (_anchorReactionLag)
            anchorTilt = ctx.LaggedAnchorTilt(user.UserId, currency, anchorTilt, user.Lateness,
                _anchorLagMinAlpha, _anchorLagMaxAlpha);

        // Hybrid pressure formula §: when on, directional+herd push the cohort multiplicatively
        // around 0.5 (preserves diversity at extremes so sell-biased bots stay sell-biased under
        // strong buy directional — the natural counter-pressure the additive form crushes by
        // saturation). Anchors stay additive — structural override of personality. When off,
        // BuyProbHybrid collapses literal-byte-for-byte to today's additive line 607.
        var buyProb = BuyProbHybrid(homeostatic, directional, noiseFactor, herdTilt, anchorTilt,
            _multiplicativeDirectional, _diversityGain);

        // 3. Strategy-aware market-order probability (scaled by the global MarketProbMult ⇒ more takers/volume)
        var effectiveUseMarket = Math.Min(1m, user.UseMarketProb * _marketProbMult);
        if (_sentimentDynamics)
        {
            // Sentiment-dynamics §: momentum must TAKE liquidity to move price (§1b: limits won't trend).
            // Push useMarket up by the directional conviction — SYMMETRICALLY for buys and sells so a bull
            // bias lifts the ask and a bear bias hits the bid (no taker-flow skew → no down-drift).
            switch (user.Strategy)
            {
                case AiStrategy.Scalper:
                case AiStrategy.TrendFollower:
                    effectiveUseMarket = Math.Min(1m, effectiveUseMarket + _aggressionBoost * Math.Abs(directional));
                    break;
                case AiStrategy.MarketMaker:
                    effectiveUseMarket = Math.Max(0m, effectiveUseMarket - 0.15m);
                    break;
            }
        }
        else
        {
            switch (user.Strategy)
            {
                case AiStrategy.Scalper:
                    effectiveUseMarket = Math.Min(1m, effectiveUseMarket + 0.15m * Math.Abs(momentumSignal));
                    break;
                case AiStrategy.MarketMaker:
                    effectiveUseMarket = Math.Max(0m, effectiveUseMarket - 0.15m);
                    break;
            }
        }

        // §A1 inertia: hold a persistent directional STANCE across ticks instead of re-rolling buy/sell
        // every tick — this is the Cont–Bouchaud ingredient that stops tick-to-tick self-cancellation
        // (the LLN flat-chart root cause). On a fresh/expired stance RollOrHoldStance consumes exactly two
        // seeded draws (side, duration) in a fixed order; while a stance holds it draws nothing. Called
        // ONLY when the flag is on, so the flag-off draw sequence is byte-identical. The isBuy draw below is
        // still consumed (keeping isMarket + all downstream draws in position) — do not optimize it away.
        if (_inertia && notMM)
        {
            var dir = ctx.RollOrHoldStance(user.AiUserId, buyProb, TimeHelper.NowUtc(), _inertiaMinSec, _inertiaMaxSec);
            buyProb = dir > 0 ? Math.Max(buyProb, 1m - _inertiaLeak) : Math.Min(buyProb, _inertiaLeak);
        }

        // 4. Resolve to concrete order type. Bots never place TRUE (uncapped) market orders — every
        // market order is slippage-capped (EffectiveSlippage) so no single order can sweep a thin book
        // far and start a cascade.
        var isBuy    = ctx.Decimal01(user.AiUserId) < buyProb;
        var isMarket = ctx.Decimal01(user.AiUserId) < effectiveUseMarket;

        // R4 §0009 Stage 2: plain-path decision probe. directional * noiseFactor is the masked
        // form that actually contributes to buyProb. Signed inventory notional (long - short)
        // is read from the per-tick cache that ComputeInventoryBias also uses, so the probe
        // never adds a second watchlist walk.
        if (BotDecisionProbe.Enabled)
        {
            var (lng, sht) = WatchlistInventoryNotional(ctx, user, currency);
            BotDecisionProbe.RecordPlain(
                botId: user.AiUserId, strategy: (int)user.Strategy,
                cashPrc: cashPrc, invNotionalSigned: lng - sht,
                homeostatic: homeostatic, directionalEffective: directional * noiseFactor,
                anchor: anchorTilt, herd: herdTilt,
                buyProb: buyProb, isBuy: isBuy, isMarket: isMarket);
        }

        return isBuy
            ? isMarket ? OrderType.SlippageMarketBuy : OrderType.LimitBuy
            : isMarket ? OrderType.SlippageMarketSell : OrderType.LimitSell;
    }

    // Quote the side with fewer resting limit orders so the bot tends toward a
    // balanced two-sided book. A sell with no inventory is filtered out later in
    // ChooseStockId, so the bot simply skips that tick until a bid fills.
    private static OrderType ChooseMarketMakerQuote(AiBotContext ctx, AIUser user)
    {
        int buys = 0, sells = 0;
        if (ctx.OpenOrders.TryGetValue(user.UserId, out var orders))
        {
            foreach (var o in orders.Values)
            {
                if (!o.IsLimitOrder) continue;
                if (o.IsBuyOrder) buys++; else sells++;
            }
        }
        // R4 §0009 Stage 3 (A1): symmetric tie-break. The old `buys <= sells` defaulted to BUY
        // on every tied tick (the steady-state condition for an MM with an empty or balanced
        // ladder), compounding into a 2.95× net buy-quote bias and a 32%-thicker bid wall
        // (Stage 2 Block 4). Strict inequalities still steer toward the under-represented side;
        // ties go 50/50 via the per-bot seeded RNG. The draw is consumed only on the MM path
        // (early return at :813), so non-MM RNG sequences stay byte-identical.
        bool choseBuy;
        if      (buys < sells) choseBuy = true;
        else if (buys > sells) choseBuy = false;
        else                   choseBuy = ctx.Decimal01(user.AiUserId) < 0.5m;

        // R4 §0009 Stage 2: MM quote-side probe — schema unchanged so the Stage 2 analysis
        // script parses cleanly and the A/B soak can compare quote-side ratios directly.
        BotDecisionProbe.RecordMm(user.AiUserId, buys, sells, choseBuy);
        return choseBuy ? OrderType.LimitBuy : OrderType.LimitSell;
    }

    private int ChooseStockId(AiBotContext ctx, AIUser user, OrderType type, CurrencyType currency,
        CommittedTotals committed)
    {
        var rng   = ctx.GetRandom(user.AiUserId);
        // §patch 0003: uses EligibleWatchlist cache (was inline Where().ToList() per call).
        var watch = EligibleWatchlist(ctx, user, currency);
        if (watch.Length == 0) return 0;

        if (IsSellOrder(type))
        {
            var candidates = new List<int>();
            foreach (var id in watch)
            {
                var pos          = ctx.GetPosition(user.UserId, id);
                var committedSell = committed.SellSharesByStock.GetValueOrDefault(id);
                var ctxAvail     = pos.Quantity - committedSell;
                // Cross-check against the engine's AvailableQuantity to avoid
                // generating orders that would fail Phase 1.5 on stale ctx.
                var enginePos   = _accounts.GetPosition(user.UserId, id);
                var engineAvail = enginePos?.AvailableQuantity ?? 0;
                if (Math.Min(ctxAvail, engineAvail) > 0) candidates.Add(id);
            }
            return candidates.Count > 0 ? PickStock(candidates, rng, currency, ctx, buySide: false) : 0;
        }

        return PickStock(watch, rng, currency, ctx, buySide: true);
    }

    /// <summary>
    /// Roulette-wheel pick weighted by 1/StockId^alpha (lower ids = bigger cap = more weight), boosted
    /// toward the stock whose correction this order serves — overvalued on a sell, undervalued on a buy.
    /// The boost concentrates the value anchor on whatever is breaking loose instead of diluting it.
    /// </summary>
    private int PickStock(IList<int> stockIds, Random rng, CurrencyType currency, AiBotContext ctx, bool buySide)
    {
        double total = 0;
        Span<double> cum = stockIds.Count <= 256 ? stackalloc double[stockIds.Count] : new double[stockIds.Count];
        for (int i = 0; i < stockIds.Count; i++)
        {
            double w = BaseWeight(stockIds[i]);
            // §Pillar B: concentrate volume on hot/trending names — weight by S^gamma. Off ⇒ S≡1 ⇒
            // identical weights ⇒ identical pick for the same RNG draw (no decimal Pow; S ≥ Floor > 0).
            if (_activityEnabled)
            {
                double s = (double)_activity.S(stockIds[i]);
                if (s > 0) w *= Math.Pow(s, _activityGamma);
            }
            if (_valueAnchorStrength > 0m && _valueTargetSelection)
            {
                var f = Fundamental(stockIds[i], currency, ctx);
                if (f > 0m && ctx.SmoothedPrices.TryGetValue((stockIds[i], currency), out var p) && p > 0m)
                {
                    double gap = (double)((f - p) / f);        // >0 undervalued, <0 overvalued
                    double corrective = buySide ? gap : -gap;  // a buy fixes undervalued; a sell fixes overvalued
                    if (corrective > 0)
                        w *= 1.0 + ValuePickGain * corrective / (double)_valueAnchorScale;
                }
            }
            total += w;
            cum[i] = total;
        }
        double r = rng.NextDouble() * total;
        for (int i = 0; i < stockIds.Count; i++)
            if (r < cum[i]) return stockIds[i];
        return stockIds[^1];
    }

    private const double RuntimeWeightAlpha = 0.7;
    // Selection boost per unit of normalized deviation when the value anchor is on: a stock 1×Scale
    // off fundamental gets (1 + ValuePickGain)× the weight on the corrective side.
    private const double ValuePickGain = 12.0;

    // 1/StockId^alpha is constant per id (alpha is a compile-time const), so memoize it instead of
    // recomputing Math.Pow for every candidate on every decision. The cached double is bit-identical
    // to the previous inline computation, so selection (for a given RNG draw) is unchanged.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double> _baseWeightByStockId = new();
    private static double BaseWeight(int stockId)
        => _baseWeightByStockId.GetOrAdd(stockId, static id => 1.0 / Math.Pow(id, RuntimeWeightAlpha));
    #endregion

    #region Price and Quantity Computation
    private async Task<decimal> ComputeOrderPriceAsync(AiBotContext ctx, AIUser user, OrderType type,
        int stockId, CurrencyType currency, CancellationToken ct)
    {
        if (IsTrueMarketOrder(type)) return 0m;

        var marketPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (marketPrice <= 0m) return 0m;

        if (IsSlippageOrder(type)) return CurrencyHelper.RoundMoney(marketPrice, currency);

        // §2 MM quoting: a symmetric two-sided quote at mid ± half-spread. Only when a
        // real mid exists (a two-sided book); otherwise fall through to normal offsets.
        if (_mmQuoting && user.Strategy == AiStrategy.MarketMaker)
        {
            var mid = await GetMidPriceAsync(stockId, currency, ct).ConfigureAwait(false);
            if (mid is > 0m)
            {
                // Microstructure bounce: narrow the symmetric quote toward mid so the touch MMs set is
                // tighter. Symmetric ⇒ no directional taker tilt; off (0) ⇒ half unchanged ⇒ byte-identical.
                var half = _touchTightenPrc > 0m ? _quoteHalfSpreadPrc * (1m - _touchTightenPrc) : _quoteHalfSpreadPrc;
                var quote = IsBuyOrder(type)
                    ? mid.Value * (1m - half)
                    : mid.Value * (1m + half);
                return CurrencyHelper.RoundMoney(quote, currency);
            }
        }

        // Limit anchor: midprice when both sides are present, last-trade otherwise.
        // Last-trade ratchets upward whenever buys fill at the ask faster than
        // sells at the bid; midprice stays roughly put under that imbalance.
        var anchor = await GetMidPriceAsync(stockId, currency, ct).ConfigureAwait(false)
                     ?? marketPrice;

        // §P6 tiered ladder: pick Close / Mid / Far, then widen the chosen band by the liquidity
        // multiplier and add bidirectional jitter. Close churns at the touch; Far rests standing walls
        // that absorb fired (slippage-capped) stops instead of letting them sweep empty space.
        var (tierMin, tierMax, isCloseTier) = PickLimitTier(ctx, user);
        // Microstructure bounce: pull the close-tier band (the non-MM orders nearest mid that set the touch)
        // toward mid by (1-prc). Close tier only — Mid/Far are standing-wall depth. Off ⇒ no-op, byte-identical.
        tierMin = TightenOffset(tierMin, isCloseTier, _touchTightenPrc);
        tierMax = TightenOffset(tierMax, isCloseTier, _touchTightenPrc);
        var minOff = tierMin * _limitOffsetMult * _distanceMult;
        var maxOff = tierMax * _limitOffsetMult * _distanceMult;
        var offset = Clamp01(Lerp(minOff, maxOff, ctx.Decimal01(user.AiUserId)));
        var jitter = (ctx.Decimal01(user.AiUserId) * 2m - 1m) * user.AggressivenessPrc;
        offset = Math.Max(minOff, Math.Min(maxOff, offset * (1m + jitter)));

        // R4 §0009 Stage 4 — Option D: tilt the limit offset by book imbalance so adding to the
        // thick side gets pushed further from mid (less crossable) while adding to the thin side
        // gets pulled closer (more crossable). Addresses the Stage 3 A1 finding that randomized
        // MM ties built a thicker ask wall with less-aggressive limits that don't get crossed,
        // crashing throughput. Off ⇒ byte-identical (gain*0 = 0).
        if (_liquidityAwarePlacement && _liquidityAwareGain > 0m)
        {
            try
            {
                var book = await _books.GetAsync(stockId, currency, ct).ConfigureAwait(false);
                if (book is not null)
                {
                    var bidDepth = book.SumQuantity(buySide: true);
                    var askDepth = book.SumQuantity(buySide: false);
                    var totalDepth = bidDepth + askDepth;
                    if (totalDepth > 0L)
                    {
                        // imbalance ∈ [-1, +1]; positive means bid thicker (sell-takers find easy liquidity).
                        var imbalance = (decimal)(bidDepth - askDepth) / (decimal)totalDepth;
                        // BUY adds to bid (dirSign=+1): bid thick ⇒ offset UP (less aggressive bid);
                        //                                ask thick ⇒ offset DOWN (more aggressive bid → crosses thin ask).
                        // SELL adds to ask (dirSign=-1): bid thick ⇒ offset DOWN (more aggressive ask → meets bid demand);
                        //                                ask thick ⇒ offset UP (less aggressive ask).
                        var dirSign = IsBuyOrder(type) ? 1m : -1m;
                        offset = offset * (1m + dirSign * imbalance * _liquidityAwareGain);
                        offset = Math.Max(minOff, Math.Min(maxOff, offset));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* defensive: book lookup or sum failed — keep original offset */ }
        }

        // §refill-throttle (Bots:RefillThrottle): on a confirmed mover, widen the RESISTING side's offset so
        // the wall the move is pushing into stops instantly reforming at the old level. Applied AFTER the
        // liquidity-aware tilt (which re-clamps offset) so it isn't undone, then re-clamped to the tier band.
        // Factor is 1.0 (no-op) unless the gate is enabled AND this order resists the move ⇒ byte-identical
        // when off; pure math, no RNG.
        offset = Math.Min(maxOff, offset * ctx.RefillWidenFactor(stockId, currency, IsBuyOrder(type)));

        var limitPrice = IsBuyOrder(type) ? anchor * (1m - offset) : anchor * (1m + offset);

        // Round-number attraction: a fraction of limit orders snap toward a psychologically significant
        // level. Soft-snap (RoundSnapSpread>0) disperses them within ±spread·unit so volume forms a
        // natural cluster near the level instead of one impassable wall. The dispersion draw is taken
        // ONLY when spread>0 (ternary short-circuits), so prob 0.30 + spread 0 ⇒ today's exact snap.
        if (ctx.Decimal01(user.AiUserId) < _roundSnapProb)
            limitPrice = SnapToRoundNumber(limitPrice, _roundSnapSpread,
                _roundSnapSpread > 0m ? ctx.Decimal01(user.AiUserId) : 0m);

        // §bounce lever (b): the printed/resting limit price uses the finer price-quote grid
        // (RoundPrice). Dial 0 ⇒ identical to RoundMoney. Cash/reservation stay on RoundMoney.
        return CurrencyHelper.RoundPrice(limitPrice, currency);
    }

    private async Task<decimal?> GetMidPriceAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        try
        {
            var book = await _books.GetAsync(stockId, currency, ct).ConfigureAwait(false);
            if (book is null) return null;
            var bid = book.PeekBestBuy()?.Price;
            var ask = book.PeekBestSell()?.Price;
            // One-sided books would just reintroduce the ratchet; require both.
            return (bid > 0m && ask > 0m) ? (bid.Value + ask.Value) / 2m : (decimal?)null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Midprice fetch failed for stock {Stock}/{Currency}; falling back to last-trade.",
                stockId, currency);
            return null;
        }
    }

    private async Task<int> ComputeOrderQuantityAsync(AiBotContext ctx, AIUser user, OrderType type,
        int stockId, CurrencyType currency, CommittedTotals committed, decimal notionalCapOverride, CancellationToken ct)
    {
        var portfolio = ctx.PortfolioValueByCurrency(user.UserId, currency);
        if (portfolio <= 0m) return 0;

        // §direct-flow chaser: when sizing is overridden (the chase notional cap, already sized off a SEED-price
        // portfolio base), take NO random size draws — a chase tick must consume 0 seeded draws. The override is
        // treated exactly like a normal rawTrade so every downstream clamp (cash, position room, AvailableQuantity,
        // short-cover, depth cap) still applies — i.e. no naked flow. Otherwise the normal fat-tailed draws run.
        decimal rawTrade;
        if (notionalCapOverride > 0m)
        {
            rawTrade = notionalCapOverride;
        }
        else
        {
            // Fat tails: skew the uniform draw so typical orders sit near Min with a heavy
            // right tail to Max. tailShape 0 (or feature off) = uniform, as before.
            var u = ctx.Decimal01(user.AiUserId);
            if (_fatTails && _tradeSizeTailShape > 0m)
                u = (decimal)Math.Pow((double)u, 1.0 + (double)_tradeSizeTailShape * TailExponentScale);
            var tradePrc = Lerp(user.MinTradeAmountPrc, user.MaxTradeAmountPrc, u);
            var jitter   = ctx.Decimal01(user.AiUserId) * user.AggressivenessPrc;
            tradePrc     = Math.Min(tradePrc * (1m + jitter), user.MaxTradeAmountPrc);
            // Rare block trade: an occasional outsized order past the per-bot Max. The
            // downstream room/cash/position clamps truncate it to actual capacity.
            if (_fatTails && _blockTradeProb > 0m && ctx.Decimal01(user.AiUserId) < _blockTradeProb)
                tradePrc *= _blockTradeMultiple;
            if (tradePrc <= 0m) return 0;
            rawTrade = tradePrc * portfolio;
        }

        var marketPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (marketPrice <= 0m) return 0;

        decimal estimatePrice = type switch
        {
            OrderType.TrueMarketBuy or OrderType.TrueMarketSell => marketPrice,
            OrderType.SlippageMarketBuy  => CurrencyHelper.RoundMoney(marketPrice * (1m + EffectiveSlippage(user, stockId)), currency),
            OrderType.SlippageMarketSell => CurrencyHelper.RoundMoney(marketPrice * (1m - EffectiveSlippage(user, stockId)), currency),
            _                            => marketPrice // limit
        };
        if (estimatePrice <= 0m) return 0;

        var fund       = ctx.GetFund(user.UserId, currency);
        var pos        = ctx.GetPosition(user.UserId, stockId);
        var capValue   = user.PerPositionMaxPrc * portfolio;
        var currentVal = CurrencyHelper.Notional(marketPrice, pos.Quantity, currency);
        var roomValue  = Math.Max(0m, capValue - currentVal);

        // §chaser-v2: the bot's structural BUY ceiling on the cash side, hoisted out of the buy branch so the
        // symmetric sell gate (C3) can mirror it. Pure relocation of the former buy-branch computation — no
        // behaviour change (the buy branch still reads the same spendableBalance below).
        var committedBuy      = committed.BuyFundsByCurrency.GetValueOrDefault(currency);
        var ctxFreeBalance    = Math.Max(0m, fund.TotalBalance - committedBuy);
        // Plan B: clamp to the engine's AvailableBalance so the bot never generates an order that's doomed at
        // Phase 1.6 — same defence as the sell branch below.
        var engineFreeBalance = _accounts.GetFund(user.UserId, currency)?.AvailableBalance ?? 0m;
        var freeBalance       = Math.Min(ctxFreeBalance, engineFreeBalance);
        // Always leave at least BuySafetyBuffer un-reserved in the user's currency.
        var spendableBalance  = Math.Max(0m, freeBalance - BuySafetyBuffer);

        if (IsBuyOrder(type))
        {
            // §chaser-v2 C5: on a chase tick, relax the position-room clamp toward cash-only by BuyRoomRelaxFrac
            // (0 ⇒ effRoom==roomValue, byte-identical; 1 ⇒ room drops out, buy limited only by cash/notional).
            // Adds buy gross to balance the free sells; still cash-gated ⇒ conservation-safe (no naked flow).
            var effRoom = roomValue;
            if (_chaserBuyRoomRelaxFrac > 0.0 && notionalCapOverride > 0m)
                effRoom = roomValue + (decimal)_chaserBuyRoomRelaxFrac * (Math.Max(roomValue, spendableBalance) - roomValue);
            var allowedBalance    = Math.Min(Math.Min(spendableBalance, rawTrade), effRoom);
            var qty = (int)Math.Floor(allowedBalance / estimatePrice);
            // Floor at 1 share when the intended notional rounds to zero but the bot can
            // still afford a share within its spendable + position room — mirrors the sell
            // branch's max(1, …) so small order fractions don't silently vanish.
            if (qty == 0 && spendableBalance >= estimatePrice && roomValue >= estimatePrice)
                qty = 1;
            // §P6: never buy PAST flat on a stock the bot is short — the buy-side mirror of risk #7
            // (a short→long flip), which P6 forbids. Clamp to the uncovered short (engine-authoritative, like
            // the sell branch); a cover can flatten but never overshoot into a long. Fully covered → qty 0.
            //
            // Round 2 §0007 (Path 2): this clamp is for PLAIN orders only. Bracket-entry qty is
            // computed in BuildBracketAsync, which carves an explicit flip exception when
            // _bracketFlip is on (the inventoryPortion / flipPortion split). Plain orders KEEP this
            // clamp — the cover-clamp invariant is preserved across round 2 per the hard-constraint
            // contract in the round-1 bracket-flip plan §5.
            var enginePos = _accounts.GetPosition(user.UserId, stockId);
            if (enginePos is { Quantity: < 0 })
            {
                int shortMag       = -enginePos.Quantity;
                int committedCover = committed.CoverSharesByStock.GetValueOrDefault(stockId);
                int coverable      = Math.Max(0, shortMag - committedCover);
                qty = Math.Min(qty, coverable);
            }
            // §P6 anti-sweep: a market buy can't take more than a fraction of resting asks.
            if (IsSlippageOrder(type))
                qty = ApplyDepthCap(qty, isBuy: true, stockId, currency);
            return qty;
        }
        else
        {
            var committedSell = committed.SellSharesByStock.GetValueOrDefault(stockId);
            var ctxAvailable  = Math.Max(0, pos.Quantity - committedSell);
            // Plan B: same clamp as ChooseStockId — engine view is authoritative. If the
            // ctx says we have N free but engine has more reserved, take engine's number.
            var engineAvailable = _accounts.GetPosition(user.UserId, stockId)?.AvailableQuantity ?? 0;
            var availableQty    = Math.Min(ctxAvailable, engineAvailable);
            var desiredQty      = Math.Max(1, (int)Math.Floor(rawTrade / estimatePrice));
            var sellQty         = Math.Min(desiredQty, availableQty);
            // §P6 anti-sweep: a market sell can't take more than a fraction of resting bids.
            if (IsSlippageOrder(type))
                sellQty = ApplyDepthCap(sellQty, isBuy: false, stockId, currency);
            // §chaser-v2 C3: on a chase tick, cap the sell to the SAME structural buy-ceiling this bot's chase-BUY
            // would face (drift-neutral per-bot for a net-long population). Applied LAST so it can only ever
            // tighten the anti-sweep cap, never widen it. roomValue is the buy branch's actual room (correct for
            // shorts too). Off (symFrac=0) ⇒ untouched.
            if (_chaserSellSymFrac > 0.0 && notionalCapOverride > 0m)
                sellQty = ChaseSymmetricSellQty(sellQty, estimatePrice, roomValue, spendableBalance,
                                                capValue, _chaserSellSymFrac, ChaseFloorIntensity);
            return sellQty;
        }
    }

    // §perf C4: the per-decision "already committed" totals, computed in a single walk of the user's open
    // orders instead of one walk per consumer (the sell path called the old per-stock helper once per
    // candidate). OpenOrders is immutable within a decision, so these snapshot totals equal what the old
    // ComputeCommittedBuyFunds / ComputeCommittedSellShares / ComputeCommittedCoverShares returned at each
    // call site. The per-order predicates are unchanged: a buy limit contributes its RemainingAmount to the
    // currency bucket AND its RemainingQuantity to the stock cover bucket; a sell limit contributes its
    // RemainingQuantity to the stock sell bucket.
    internal readonly record struct CommittedTotals(
        IReadOnlyDictionary<CurrencyType, decimal> BuyFundsByCurrency,
        IReadOnlyDictionary<int, int> SellSharesByStock,
        IReadOnlyDictionary<int, int> CoverSharesByStock);

    // §patch 0001 memoize wrapper: per-tick cached version of ComputeCommitted. Called by every
    // ComputeOrderAsync (and prospectively by buy/sell quantity helpers); previously a single bot
    // tick could re-walk OpenOrders many times. Cache lives on AiBotContext, cleared per tick.
    internal CommittedTotals GetCommitted(AiBotContext ctx, int userId)
    {
        if (_memoizeTickValues && ctx.CommittedCache.TryGetValue(userId, out var cached)) return cached;
        var fresh = ComputeCommitted(ctx, userId);
        if (_memoizeTickValues) ctx.CommittedCache[userId] = fresh;
        return fresh;
    }

    internal static CommittedTotals ComputeCommitted(AiBotContext ctx, int userId)
    {
        var buyFunds    = new Dictionary<CurrencyType, decimal>();
        var sellShares  = new Dictionary<int, int>();
        var coverShares = new Dictionary<int, int>();
        if (ctx.OpenOrders.TryGetValue(userId, out var orders))
            foreach (var o in orders.Values)
            {
                if (!o.IsLimitOrder) continue;
                if (o.IsBuyOrder)
                {
                    buyFunds[o.CurrencyType] = (buyFunds.TryGetValue(o.CurrencyType, out var f) ? f : 0m) + o.RemainingAmount;
                    coverShares[o.StockId]   = (coverShares.TryGetValue(o.StockId, out var c) ? c : 0) + o.RemainingQuantity;
                }
                else if (o.IsSellOrder)
                {
                    sellShares[o.StockId]    = (sellShares.TryGetValue(o.StockId, out var s) ? s : 0) + o.RemainingQuantity;
                }
            }
        return new CommittedTotals(buyFunds, sellShares, coverShares);
    }

    // ValueTask: the common path is an in-memory cache hit (no allocation); it's called several times
    // per decision (band veto, price, qty) across collect/arb/advanced, so a Task alloc per call adds
    // real GC pressure across the loop. Only the cold cache-miss path actually awaits the market.
    private ValueTask<decimal> GetStockPriceAsync(AiBotContext ctx, int stockId,
        CurrencyType currency, CancellationToken ct)
    {
        if (ctx.StockPrices.TryGetValue((stockId, currency), out var price) && price > 0m)
            return new ValueTask<decimal>(price);
        return Cold(ctx, stockId, currency, ct);

        async ValueTask<decimal> Cold(AiBotContext c, int sid, CurrencyType cur, CancellationToken token)
        {
            var p = await _market.GetLastPriceAsync(sid, cur, token).ConfigureAwait(false);
            c.StockPrices[(sid, cur)] = p;
            return p;
        }
    }

    // §P6 tiered ladder: roll Close / Mid / Far and return that tier's (min,max) offset band. Mid/Far
    // fall back to the Close band if a bot pre-dates the tier columns (all-zero), so behaviour degrades
    // gracefully on an un-regenerated workbook.
    private (decimal min, decimal max, bool isClose) PickLimitTier(AiBotContext ctx, AIUser user)
    {
        var r = ctx.Decimal01(user.AiUserId);
        // isClose flags the touch-churning tier (the only one the bounce-tighten touches). Mid/Far fall back to
        // the Close band when their columns are unset, so the tier identity is reported here rather than inferred
        // from the returned (min,max) at the call site (which would be ambiguous under that fallback).
        if (r < _tierCloseProb) return (user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc, true);
        if (r < _tierCloseProb + _tierMidProb)
            return user.MidLimitMaxPrc > 0m
                ? (user.MidLimitMinPrc, user.MidLimitMaxPrc, false)
                : (user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc, false);
        return user.FarLimitMaxPrc > 0m
            ? (user.FarLimitMinPrc, user.FarLimitMaxPrc, false)
            : (user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc, false);
    }

    // §P6: per-bot protective-stop distance — drawn from the bot's StopDistance band (config fallback
    // for un-regenerated bots), jittered ±20% to de-cluster trigger levels, and clamped strictly inside
    // the Far walls so a fired (capped) stop runs into a standing wall instead of triggering the next stop.
    private decimal StopOffset(AiBotContext ctx, AIUser user)
    {
        var lo = user.StopDistanceMinPrc > 0m ? user.StopDistanceMinPrc : _stopOffsetMin;
        var hi = user.StopDistanceMaxPrc > 0m ? user.StopDistanceMaxPrc : _stopOffsetMax;
        var off = Lerp(lo, hi, ctx.Decimal01(user.AiUserId));
        var jitter = (ctx.Decimal01(user.AiUserId) * 2m - 1m) * 0.20m;
        off *= (1m + jitter);
        // Clamp strictly inside the Far wall. The far wall and the stop both get the global distance dial
        // on the way out, so the clamp is computed pre-dial — include _limitOffsetMult (the far wall has it)
        // so the "stop fires into a standing wall" invariant survives any limit-ladder scaling.
        var farMin = user.FarLimitMinPrc > 0m ? user.FarLimitMinPrc * _limitOffsetMult : hi;
        off = Math.Min(off, farMin * 0.9m);
        return Math.Max(0.001m, off) * _distanceMult;   // §P6 tightness dial
    }

    // §P6 value-band veto (shared by the plain and advanced order paths): true when an order would chase
    // price past the personality-scaled overheat band — buying a stock already far above fundamental, or
    // selling/shorting one already far below. Brackets and shorts route through this too, so the advanced
    // path can't bypass the anchor and feed a runaway.
    // §patch 0002: rewritten sync (was async Task<bool>) — every caller has ctx.StockPrices available
    // for the market read, so no actual I/O happens on the hot path. Drops the per-call Task alloc.
    // Memoized per-tick via ctx.OverBand{Buy,Sell}Cache (patch 0001 cache fields).
    private bool IsOverBand(AiBotContext ctx, int stockId, CurrencyType currency, bool isBuy)
    {
        if (_overheatCap <= 0m) return false;
        var cache = isBuy ? ctx.OverBandBuyCache : ctx.OverBandSellCache;
        if (_memoizeTickValues && cache.TryGetValue((stockId, currency), out var cached)) return cached;

        // §cap-from-seed: when on, the hard veto measures deviation from seed instead of Fundamental.
        // Decouples the absolute ceiling from any moving target (OU walk or daily TWAP) — without it,
        // a daily anchor that drifts up re-centers the cap window each rotation, producing a
        // multi-day compounding ratchet. Anchor PULL (in MakeBuyDecisionAsync) still uses
        // Fundamental() so it tracks the recent regime — only the hard ceiling is anchored to seed.
        // §adaptive: re-center the cap on the moving anchor (clamped to the seed band) so a genuine
        // move re-rates the level and sticks, instead of snapping back to the fixed seed. Off ⇒ the
        // legacy seed/Fundamental source, byte-identical.
        var anchor = _adaptiveAnchor ? _priceMemory.GetAdaptiveAnchor(stockId, currency)
                   : _capFromSeed     ? SeedPrice(stockId, currency, ctx)
                                      : Fundamental(stockId, currency, ctx);
        if (anchor <= 0m) { if (_memoizeTickValues) cache[(stockId, currency)] = false; return false; }

        // §patch 0002: read the price from ctx.StockPrices directly (sync hit-path). On miss, the next
        // tick will populate it; this tick falls back to no-veto, matching the defensive `anchor <= 0`
        // path. No ConfigureAwait alloc, no async state machine.
        if (!ctx.StockPrices.TryGetValue((stockId, currency), out var mkt) || mkt <= 0m)
        { if (_memoizeTickValues) cache[(stockId, currency)] = false; return false; }

        // §A5 fast-anchor slack: widen the INTRADAY veto so excursions can happen, while the slow
        // value-anchor probability tilt (_valueAnchorStrength) still pulls price back on the multi-hour
        // scale. 0 ⇒ unchanged. Applies uniformly to the plain and advanced paths (both call this).
        var cap = _overheatCap * _profiles.Get(stockId).OverheatCapMult * (1m + _anchorFastSlack);
        // Defensive ceiling: guarantees the absolute-deviation promise even when personality mult + slack stack high.
        if (_absoluteCapMax > 0m && cap > _absoluteCapMax) cap = _absoluteCapMax;
        // §geometric bands: buy>anchor×F / sell<anchor/F (F=1+cap), log-symmetric so a "200%" cap = ×3 up / ÷3 down.
        bool result;
        if (_geometricBand)
            result = PriceBandMath.IsOver(mkt, anchor, PriceBandMath.Factor(cap), isBuy);
        else
        {
            var dev = (mkt - anchor) / anchor;
            result = isBuy ? dev > cap : dev < -cap;
        }
        // §adaptive runaway guard: the moving anchor can re-rate intraday, but the TOTAL excursion
        // from the original seed is hard-bounded — a provably-binding veto so the market can't walk
        // to infinity even as the cap window follows price. Independent of the re-centered cap above.
        if (!result && _adaptiveAnchor)
        {
            var seed = SeedPrice(stockId, currency, ctx);
            if (seed > 0m)
            {
                result = _geometricBand
                    ? PriceBandMath.IsOver(mkt, seed, PriceBandMath.Factor(_maxTotalExcursion), isBuy)
                    : (isBuy ? (mkt - seed) / seed > _maxTotalExcursion : (mkt - seed) / seed < -_maxTotalExcursion);
            }
        }
        if (_memoizeTickValues) cache[(stockId, currency)] = result;
        return result;
    }

    // §cap-from-seed helper: per-(stock, currency) seed price from the StockListings. Returns 0 when
    // the listing is missing (caller handles that as "no veto" — same as the prior fund<=0 path).
    // §patch 0001: per-tick memoize — seed is per-(stock,ccy) constant, but the per-listing scan
    // costs O(#listings) per bot per tick; cache hits are O(1).
    private decimal SeedPrice(int stockId, CurrencyType currency, AiBotContext? ctx = null)
    {
        if (_memoizeTickValues && ctx is not null)
        {
            var key = (stockId, currency);
            if (ctx.SeedPriceCache.TryGetValue(key, out var cached)) return cached;
        }
        decimal value = 0m;
        foreach (var l in _stocks.GetListings(stockId))
            if (l.CurrencyType == currency) { value = l.SeedPrice; break; }
        if (_memoizeTickValues && ctx is not null) ctx.SeedPriceCache[(stockId, currency)] = value;
        return value;
    }

    // §P6 liquidity-aware anti-sweep: cap a bot MARKET order to a fraction of the resting opposite-side
    // depth so no single order can sweep more than that share of the book, regardless of slippage. Pure
    // size reduction — conservation-neutral. Limits are unaffected (they rest, they don't sweep).
    // §patch 0002: rewritten sync via IOrderBookEngine.TryGetLoaded. On miss (book not loaded yet)
    // we fall back to no-cap behaviour — same defensive shape as the prior `book is null` path. No
    // try/catch needed: TryGetLoaded is total; SumQuantity is lock-guarded inside OrderBook and
    // doesn't throw on the hot path.
    private int ApplyDepthCap(int qty, bool isBuy, int stockId, CurrencyType currency)
    {
        if (qty <= 0 || _maxSweepFractionOfDepth <= 0m) return qty;
        if (!_books.TryGetLoaded(stockId, currency, out var book)) return qty;
        long oppQty = book.SumQuantity(buySide: !isBuy);
        if (oppQty <= 0) return qty; // empty opposite side — nothing to sweep
        int cap = (int)Math.Floor((decimal)oppQty * _maxSweepFractionOfDepth);
        return Math.Min(qty, Math.Max(0, cap));
    }
    #endregion

    #region Sentiment Integration
    private decimal AverageWatchlistSentiment(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        if (user.Watchlist == null || user.Watchlist.Count == 0) return 0m;
        decimal sum = 0m;
        int count = 0;
        foreach (var sid in user.Watchlist)
        {
            sum += _sentiment.GetSentiment(sid) + ctx.PersonalSentiment(user, sid, currency);
            count++;
        }
        return count > 0 ? sum / count : 0m;
    }

    // Sentiment-dynamics §: watchlist-averaged SHARED sentiment (no per-bot personal term), so the level `s`
    // is consistent with the per-stock shared slope the phase model also reads.
    private decimal AverageWatchlistSharedSentiment(AIUser user)
    {
        if (user.Watchlist == null || user.Watchlist.Count == 0) return 0m;
        decimal sum = 0m;
        int count = 0;
        foreach (var sid in user.Watchlist) { sum += _sentiment.GetSentiment(sid); count++; }
        return count > 0 ? sum / count : 0m;
    }

    // Sentiment-dynamics §: watchlist-averaged EWMA slope (fast or slow). 0 when the slope feature is off.
    private decimal AverageWatchlistSlope(AIUser user, bool fast)
    {
        if (user.Watchlist == null || user.Watchlist.Count == 0) return 0m;
        decimal sum = 0m;
        int count = 0;
        foreach (var sid in user.Watchlist) { sum += _sentiment.GetSentimentSlope(sid, fast); count++; }
        return count > 0 ? sum / count : 0m;
    }

    // §perceived-price desync: this bot's OWN (fast, slow) slope, averaged over its eligible watchlist — each derived
    // from a per-(bot,stock) perceived-price EWMA whose rate is dispersed by Lateness + a salted id hash, so the
    // cohort fans out. Reads the same SmoothedPrices series the shared slope path perceives (fallback to the raw
    // StockPrices last quote). Pure/RNG-free; advances the bot's perceived-price state for this decision.
    private (decimal dsFast, decimal dsSlow) AveragePerceivedSlope(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        var watch = EligibleWatchlist(ctx, user, currency);
        decimal sumF = 0m, sumS = 0m; int count = 0;
        foreach (var sid in watch)
        {
            var key = (sid, currency);
            decimal live = ctx.SmoothedPrices.TryGetValue(key, out var sp) && sp > 0m ? sp
                         : ctx.StockPrices.TryGetValue(key, out var lp) && lp > 0m ? lp : 0m;
            if (live <= 0m) continue;
            var (dsF, dsS) = ctx.PerceivedSlope(user.UserId, user.AiUserId, sid, currency, live, user.Lateness,
                PerceivedSaltFast, PerceivedSaltSlow, _perceivedMinAlpha, _perceivedMaxAlpha);
            sumF += dsF; sumS += dsS; count++;
        }
        return count > 0 ? (sumF / count, sumS / count) : (0m, 0m);
    }

    /// <summary>§direct-flow chaser: the resolved chase target — the watchlist stock to chase and its signed shock.</summary>
    internal readonly record struct ChasePick(int StockId, double Shock);

    /// <summary>
    /// §direct-flow chaser: pick the watchlist stock this bot chases — the largest |shock| among the bot's
    /// eligible (listed-in-currency) watchlist names that carry a live shock AND for which this bot is in the
    /// salted per-(bot,shock) cohort. Instance wrapper: pulls the per-(bot,tick) eligible watchlist (reusing the
    /// existing memoized view) and delegates the deterministic argmax to the pure core. RNG-free; 0 seeded draws.
    /// </summary>
    private ChasePick? ChaseSelect(AiBotContext ctx, AIUser user, CurrencyType currency)
        => ChaseSelectCore(EligibleWatchlist(ctx, user, currency), _shockOf!, _shockIdOf!,
                           user.AiUserId, _chaserFraction, ChaserSalt, 0.0);

    /// <summary>
    /// §direct-flow chaser: pure deterministic argmax. Returns the candidate with the greatest |shock| (tie-broken
    /// by LOWEST stockId — cap-saturated shocks frequently tie at ±Cap, so the tie-break must be total and stable),
    /// among candidates with |shock| &gt; floor that this bot chases. Independent of input iteration order, so a
    /// HashSet-backed watchlist can never make selection nondeterministic. Pure &amp; RNG-free → unit-testable.
    /// </summary>
    internal static ChasePick? ChaseSelectCore(int[] candidates, Func<int, double> shockOf, Func<int, int> shockIdOf,
        int aiUserId, double fraction, int salt, double floor)
    {
        int bestId = 0; double bestShock = 0.0, bestAbs = -1.0;
        foreach (var sid in candidates)
        {
            double shock = shockOf(sid);
            double abs   = Math.Abs(shock);
            if (abs <= floor) continue;
            if (!IsChaser(aiUserId, shockIdOf(sid), fraction, salt)) continue;
            if (abs > bestAbs || (abs == bestAbs && sid < bestId))
            {
                bestAbs = abs; bestShock = shock; bestId = sid;
            }
        }
        return bestId > 0 ? new ChasePick(bestId, bestShock) : (ChasePick?)null;
    }

    /// <summary>
    /// §direct-flow chaser: per-order chase notional. Sized for PERSISTENCE across the shock's life (a flat
    /// intensity floor, not pure ∝|shock| which front-loads all volume at onset), off a SEED-price portfolio base
    /// (mark-independent, so a chaser buying into a rising shock cannot inflate its own order size — no positive
    /// feedback). Capped to <paramref name="maxFrac"/>·seedPortfolio. Pure &amp; RNG-free → unit-testable.
    /// </summary>
    internal static decimal ChaseNotionalCap(double shock, double cap, double notionalFrac, double maxFrac,
        decimal seedPortfolio)
    {
        if (seedPortfolio <= 0m || notionalFrac <= 0.0) return 0m;
        double intensity = Math.Max(ChaseFloorIntensity, Math.Abs(shock) / Math.Max(1e-6, cap));
        decimal notional = (decimal)(notionalFrac * intensity) * seedPortfolio;
        decimal hardCap  = (decimal)Math.Max(0.0, maxFrac) * seedPortfolio;
        return hardCap > 0m ? Math.Min(notional, hardCap) : notional;
    }

    /// <summary>§direct-flow chaser: flat intensity floor so chase volume persists across the shock's whole life.
    /// Reused by <see cref="ChaseSymmetricSellQty"/> as the floorFrac so an at-cap long is never frozen out.</summary>
    private const double ChaseFloorIntensity = 0.25;

    /// <summary>
    /// §chaser-v2 ratio-fix (C3): cap a chase-SELL's quantity to the SAME structural buy-ceiling the same bot's
    /// chase-BUY would face — symFrac·min(buyRoomValue, spendableBuyValue) — with a floorRoom (floorFrac·capValue)
    /// so a bot at its position cap (roomValue 0) can still shed a small amount into a shock rather than being
    /// frozen. For a net-long population this removes the free-sell lean per-bot. Pure &amp; RNG-free; only ever
    /// REDUCES qty (conservation-safe). symFrac&lt;=0 or non-positive price ⇒ passthrough (off).
    /// </summary>
    internal static int ChaseSymmetricSellQty(int desiredSellQty, decimal estimatePrice,
        decimal buyRoomValue, decimal spendableBuyValue, decimal capValue, double symFrac, double floorFrac)
    {
        if (symFrac <= 0.0 || estimatePrice <= 0m) return desiredSellQty;
        decimal floorRoom = (decimal)Math.Max(0.0, floorFrac) * Math.Max(0m, capValue);
        decimal buyCeil   = Math.Max(floorRoom, Math.Min(buyRoomValue, spendableBuyValue));
        int symQty = (int)Math.Floor((decimal)symFrac * buyCeil / estimatePrice);
        return Math.Min(desiredSellQty, symQty); // only ever reduces
    }

    /// <summary>
    /// §chaser-v2 cadence: whether this bot may chase on this tick — at most once per <paramref name="intervalTicks"/>
    /// window, by a deterministic per-(bot, window) slot. A salted avalanche hash picks the one due slot in each
    /// window, so firing is spread across bots and reproducible across runs (keys on the pure TickId counter, not
    /// wall-clock). <paramref name="intervalTicks"/> &lt;= 1 ⇒ always due (feature off). Pure &amp; RNG-free.
    /// </summary>
    internal static bool ChaseCadenceDue(int aiUserId, long tickId, int intervalTicks, int salt)
    {
        if (intervalTicks <= 1) return true;
        long bucket = tickId / intervalTicks;
        int  slot   = (int)(tickId % intervalTicks);
        return (int)(BotMath.HashUnit01(aiUserId ^ salt, (int)bucket) * intervalTicks) == slot;
    }

    /// <summary>
    /// §exogenous-information: whether this bot chases this shock — a pure, RNG-free, salted, per-(bot,shock)
    /// avalanche hash compared to the cohort fraction. Salt keeps the cohort independent of the RegimeDrift
    /// IsFollower split; keying on shockId reshuffles the cohort per genuine impulse. Deterministic & call-order
    /// independent. <c>(uint)</c>-safe inside <see cref="BotMath.HashUnit01(int,int)"/> for negative ids/salt.
    /// </summary>
    internal static bool IsChaser(int aiUserId, int shockId, double fraction, int salt)
    {
        if (fraction <= 0.0) return false;
        if (fraction >= 1.0) return true;
        return BotMath.HashUnit01(aiUserId ^ salt, shockId) < fraction;
    }

    /// <summary>
    /// §exogenous-information: the per-bot chase tilt for a live shock — <c>strength·tanh(shock/scale)</c>,
    /// odd-symmetric in shock. Scale ≥ Cap keeps it near-linear so the push tracks shock magnitude (preserving
    /// volatility clustering rather than saturating to a constant). Pure &amp; RNG-free → unit-testable.
    /// </summary>
    internal static double ChaserResponse(double shock, double strength, double scale)
        => strength * Math.Tanh(shock / scale);

    /// <summary>
    /// Sentiment-dynamics §: the per-strategy directional bias added to buyProb, a pure deterministic
    /// function of (level s, fast/slow slope ds, per-bot lateness L). Symmetric by construction
    /// (bias(s,ds,…) == −bias(−s,−ds,…)). Replaces the old level-only momentum + sentiment-bias terms.
    ///   • Momentum cohort (Scalper=fast slope, TrendFollower=slow slope): blend follow-slope (early/low L)
    ///     with chase-level (late/high L) so high-L bots buy the top and get left long as s mean-reverts.
    ///   • MeanReversion: fade the level (−s) and fade HARDER as the extreme rolls over (the ReLU term).
    ///   • MarketMaker: gentle lean against the level. Random: 0.
    /// Pure &amp; RNG-free → unit-testable.
    /// </summary>
    internal static decimal DirectionalBias(AiStrategy strategy, decimal s, decimal dsFast, decimal dsSlow,
        decimal lateness, decimal kMomentum, decimal kScalper, decimal kReversion, decimal kReversal,
        decimal kMmLean, decimal scaleFast, decimal scaleSlow)
    {
        var L  = Clamp01(lateness);
        var sC = ClampSigned(s, 1m);
        switch (strategy)
        {
            case AiStrategy.Scalper:
            {
                var follow = Tanh(dsFast / scaleFast);
                return kScalper * ((1m - L) * follow + L * sC);
            }
            case AiStrategy.TrendFollower:
            {
                var follow = Tanh(dsSlow / scaleSlow);
                return kMomentum * ((1m - L) * follow + L * sC);
            }
            case AiStrategy.MeanReversion:
            {
                // ReLU is positive only when the level is extreme AND the slope is turning against it
                // (s>0 & ds<0  or  s<0 & ds>0) — i.e. a topping/bottoming market the contrarian fades harder.
                var roll = Relu(-Sign(sC) * Tanh(dsSlow / scaleSlow));
                return -kReversion * sC - kReversal * sC * roll;
            }
            case AiStrategy.MarketMaker:
                return -kMmLean * sC;
            default:
                return 0m; // Random, Arbitrage: no directional bias
        }
    }

    private static decimal Tanh(decimal x) => (decimal)Math.Tanh((double)x);
    private static decimal Relu(decimal x) => x > 0m ? x : 0m;
    private static decimal Sign(decimal x) => x > 0m ? 1m : x < 0m ? -1m : 0m;

    /// <summary>
    /// Hybrid pressure formula §: composes the homeostatic personality baseline with directional /
    /// herd / anchor terms into a final buyProb ∈ [0,1].
    /// <list type="bullet">
    /// <item><b>multiplicative=false</b> (default) ⇒ the original additive form
    ///   <c>Clamp01(homeostatic + directional·noiseFactor + herd + anchor)</c>, byte-for-byte
    ///   identical to today's expression.</item>
    /// <item><b>multiplicative=true</b> ⇒ magnitude/direction split.
    ///   <c>mag = |directional·noiseFactor + herd|·gain</c> amplifies the per-bot personality
    ///   <c>(homeostatic − 0.5)</c> by a factor <c>f = 1 + mag ≥ 1</c> (never inverts).
    ///   Direction comes from a separate additive shift <c>(directional·noiseFactor + herd)</c>.
    ///   Final form: <c>Clamp01(0.5 + (homeostatic − 0.5)·f + shift + anchor)</c>. Preserves cohort
    ///   spread symmetrically under sign of directional: <c>|spread(+d)| == |spread(−d)|</c> by
    ///   construction. Contrarian counter-pressure survives at extremes on BOTH sides; the
    ///   sign-inversion bug of <c>f = 1 + d·gain</c> (sell-biased bot becoming bullish under strong
    ///   sell signal) cannot occur.</item>
    /// </list>
    /// Anchors are additive in BOTH branches — structural override of personality. Pure,
    /// RNG-free, unit-testable.
    /// </summary>
    internal static decimal BuyProbHybrid(decimal homeostatic, decimal directional, decimal noiseFactor,
        decimal herdTilt, decimal anchorTilt, bool multiplicative, decimal diversityGain)
    {
        if (multiplicative)
        {
            var directionalShift = directional * noiseFactor + herdTilt;
            var mag = Math.Abs(directionalShift) * diversityGain;
            var f = 1m + mag;
            return Clamp01(0.5m + (homeostatic - 0.5m) * f + directionalShift + anchorTilt);
        }
        return Clamp01(homeostatic + directional * noiseFactor + herdTilt + anchorTilt);
    }

    // R5 §C: apply this bot's configured dead-band to a raw watchlist gap.
    private decimal ApplyAnchorDeadband(decimal gap) => AnchorDeadband(gap, _anchorDeadbandPrc);

    // R5 §C: signed Bollinger-style dead-band — zero pull within ±band, pass only the excess beyond it.
    // band is in deviation units (fraction of price), matching the raw gap from the watchlist aggregators.
    internal static decimal AnchorDeadband(decimal gap, decimal band)
    {
        if (band <= 0m) return gap;
        var mag = Math.Abs(gap) - band;
        if (mag <= 0m) return 0m;
        return gap < 0m ? -mag : mag;
    }

    // Average signed gap to fundamental across the watchlist: (seed − price)/seed. Positive = broadly
    // below fundamental (cheap → bias to buy); negative = above (rich → bias to sell).
    private decimal AverageWatchlistValueGap(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        if (user.Watchlist == null || user.Watchlist.Count == 0) return 0m;
        decimal sum = 0m;
        int count = 0;
        foreach (var sid in user.Watchlist)
        {
            var f = Fundamental(sid, currency, ctx);
            if (f <= 0m) continue;
            if (!ctx.SmoothedPrices.TryGetValue((sid, currency), out var p) || p <= 0m) continue;
            // §impact-decouple A: measure the value gap against the >1-min reference so the anchor stops fading
            // the cohort's own 1-min push (the live self-impact channel under SentimentDynamics). The slow
            // fundamental target f is unchanged ⇒ the multi-minute restoring force (runaway guard) survives.
            // Off ⇒ ReactionRefOr returns p ⇒ byte-identical.
            p = ctx.ReactionRefOr((sid, currency), p);
            sum += (f - p) / f;
            count++;
        }
        return count > 0 ? sum / count : 0m;
    }

    // Price-memory anchors §: signed gap to the per-stock RECENT-EWMA price, watchlist-averaged.
    // (recent − price)/recent. Positive ⇒ market below its recent average (bias to buy back);
    // negative ⇒ stretched above (bias to sell). Always reads _priceMemory.GetRecentEwma —
    // independent of the daily-anchor switch (the two anchors have distinct targets by design).
    private decimal AverageWatchlistRecentGap(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        if (user.Watchlist == null || user.Watchlist.Count == 0) return 0m;
        decimal sum = 0m;
        int count = 0;
        foreach (var sid in user.Watchlist)
        {
            var r = _priceMemory.GetRecentEwma(sid, currency);
            if (r <= 0m) continue;
            if (!ctx.SmoothedPrices.TryGetValue((sid, currency), out var p) || p <= 0m) continue;
            // §impact-decouple A: gap against the >1-min reference, not the 1s-tracking smoothed price (the
            // recent-EWMA target r is unchanged, so medium-term mean-reversion survives). Off ⇒ p unchanged.
            p = ctx.ReactionRefOr((sid, currency), p);
            sum += (r - p) / r;
            count++;
        }
        return count > 0 ? sum / count : 0m;
    }

    // A market order's slippage, capped low so no single market order sweeps a thin book far.
    // §C1 activity-scaled impact: when enabled, RELAX the cap on hot names (scaled by S) up to an absolute
    // Range ceiling so sweeps print wicks/spikes — calm names (S≤1) stay at the tight default. Activity-gated
    // (S≡1 when the activity field is off, so C1 then no-ops). The structural anti-sweep DEPTH cap is left
    // untouched as the hard ceiling. Off ⇒ today's value exactly.
    private decimal EffectiveSlippage(AIUser user, int stockId)
    {
        var cap = _marketSlippagePrc;
        if (_rangeActivityImpact)
        {
            decimal s = _activity.S(stockId);
            cap = Math.Min(_rangeMaxSlippage, Math.Max(_marketSlippagePrc, _marketSlippagePrc * s));
        }
        return Math.Min(user.SlippageTolerancePrc, cap);
    }

    // Per-(stock,currency) fundamental — the long-term value-anchor target. By default the
    // FundamentalService OU walk (fixed seed when OU is disabled). When UseDailyAnchor=true,
    // routes through BotPriceMemoryService.GetPreviousDayAverage instead — the previous day's
    // TWAP, hard-clamped to seed × [1±MaxDailyDrift], so the OverheatCap veto, the value-anchor
    // pull, PickStock's value-target selection, and bracket SL refs all read from one source.
    // The OU walk keeps Ticking either way (deterministic-RNG-safe; output simply unused under
    // daily-anchor) so rollback is a single config flip.
    // §patch 0001: per-tick memoize when ctx is supplied. Same input → same output, so the cache
    // is pure-function safe; cleared at the top of every tick by AiTradeService.
    private decimal Fundamental(int stockId, CurrencyType currency, AiBotContext? ctx = null)
    {
        if (_memoizeTickValues && ctx is not null)
        {
            var key = (stockId, currency);
            if (ctx.FundamentalCache.TryGetValue(key, out var cached)) return cached;
            var value = _useDailyAnchor ? _priceMemory.GetPreviousDayAverage(stockId, currency)
                                        : _funds.Get(stockId, currency);
            ctx.FundamentalCache[key] = value;
            return value;
        }
        return _useDailyAnchor ? _priceMemory.GetPreviousDayAverage(stockId, currency)
                               : _funds.Get(stockId, currency);
    }

    private OrderType ApplyExtremeReaction(AiBotContext ctx, AIUser user,
        int stockId, CurrencyType currency, OrderType currentType)
    {
        var raw = _sentiment.GetSentiment(stockId) + ctx.PersonalSentiment(user, stockId, currency);
        var absRaw = Math.Abs(raw);
        if (absRaw <= 1m) return currentType;

        var overflow   = absRaw - 1m;
        // §Pillar B: hot names react harder — scale the overflow gain by S (≡1 when activity is off, so the
        // forced-order probability is unchanged and no new RNG draw is introduced).
        var gain       = _activityEnabled ? OverflowGain * _activity.S(stockId) : OverflowGain;
        var forcedProb = Math.Min(1m, overflow * gain);
        if (ctx.Decimal01(user.AiUserId) >= forcedProb) return currentType;

        var style = PickExtremeReactionStyle(ctx, user);
        var dir   = (raw > 0m) ? BullDirection(style) : BearDirection(style);

        // Sell override into a stock the bot doesn't hold would just fail
        // Phase 1.5 — fall back to the original type so the order still
        // has a chance of being placed.
        if (dir == ExtremeDirection.Sell)
        {
            var pos = ctx.GetPosition(user.UserId, stockId);
            if (pos.Quantity <= 0) return currentType;
        }

        return dir switch
        {
            ExtremeDirection.Buy  => OrderType.SlippageMarketBuy,
            ExtremeDirection.Sell => OrderType.SlippageMarketSell,
            _                     => currentType,
        };
    }

    private ExtremeReactionStyle PickExtremeReactionStyle(AiBotContext ctx, AIUser user)
    {
        var defaultStyle = DefaultExtremeStyle(user.Strategy, user.AiUserId, _greedStyle, _greedSplit);

        // Out-of-character branch: pick a random style uniformly among the pool (one of them is None, so a
        // slice of out-of-character rolls land on "no extreme reaction" — same as Random-strategy bots).
        // With greed on the pool gains Greed (buy-both), so it mirrors Panic (sell-both) and nets to zero;
        // with greed off the pool is the original Next(4) → the RNG stream is byte-identical.
        if (ctx.Decimal01(user.AiUserId) < user.ExtremeReactionRandomnessPrc)
        {
            var pick = ctx.GetRandom(user.AiUserId).Next(_greedStyle ? 5 : 4);
            return pick switch
            {
                0 => ExtremeReactionStyle.FOMO,
                1 => ExtremeReactionStyle.Contrarian,
                2 => ExtremeReactionStyle.Panic,
                3 when _greedStyle => ExtremeReactionStyle.Greed,
                _ => ExtremeReactionStyle.None,
            };
        }
        return defaultStyle;
    }

    internal static ExtremeDirection BullDirection(ExtremeReactionStyle style) => style switch
    {
        ExtremeReactionStyle.FOMO       => ExtremeDirection.Buy,   // chase the top
        ExtremeReactionStyle.Contrarian => ExtremeDirection.Sell,  // fade the top
        ExtremeReactionStyle.Panic      => ExtremeDirection.Sell,  // take profit
        ExtremeReactionStyle.Greed      => ExtremeDirection.Buy,   // chase the rally (buy-both mirror of Panic)
        _                               => ExtremeDirection.None,
    };

    internal static ExtremeDirection BearDirection(ExtremeReactionStyle style) => style switch
    {
        ExtremeReactionStyle.FOMO       => ExtremeDirection.Sell,  // panic the bottom
        ExtremeReactionStyle.Contrarian => ExtremeDirection.Buy,   // buy the dip
        ExtremeReactionStyle.Panic      => ExtremeDirection.Sell,  // capitulate
        ExtremeReactionStyle.Greed      => ExtremeDirection.Buy,   // buy the dip / accumulate the crash
        _                               => ExtremeDirection.None,
    };

    // Strategy-default extreme-reaction style. Pure & RNG-free so it is reproducible and unit-testable.
    // When greed is on, half the Scalpers (a stable hash split at `split`) flip Panic→Greed so the
    // sell-both Panic lean is mirrored by a buy-both cohort; off ⇒ every Scalper is Panic (byte-identical).
    internal static ExtremeReactionStyle DefaultExtremeStyle(
        AiStrategy strategy, int aiUserId, bool greed, decimal split) => strategy switch
    {
        AiStrategy.TrendFollower => ExtremeReactionStyle.FOMO,
        AiStrategy.MeanReversion => ExtremeReactionStyle.Contrarian,
        AiStrategy.MarketMaker   => ExtremeReactionStyle.Contrarian,
        AiStrategy.Scalper       => (greed && BotRegimeService.StableUnit(aiUserId) < split)
                                        ? ExtremeReactionStyle.Greed : ExtremeReactionStyle.Panic,
        _                        => ExtremeReactionStyle.None,
    };

    internal enum ExtremeReactionStyle { FOMO, Contrarian, Panic, Greed, None }
    internal enum ExtremeDirection { Buy, Sell, None }
    #endregion

    #region OrderType Enum and Helpers
    private enum OrderType
    {
        TrueMarketBuy, TrueMarketSell,
        SlippageMarketBuy, SlippageMarketSell,
        LimitBuy, LimitSell
    }

    private static bool IsBuyOrder(OrderType t) =>
        t is OrderType.TrueMarketBuy or OrderType.SlippageMarketBuy or OrderType.LimitBuy;

    private static bool IsSellOrder(OrderType t) =>
        t is OrderType.TrueMarketSell or OrderType.SlippageMarketSell or OrderType.LimitSell;

    private static bool IsSlippageOrder(OrderType t) =>
        t is OrderType.SlippageMarketBuy or OrderType.SlippageMarketSell;

    private static bool IsTrueMarketOrder(OrderType t) =>
        t is OrderType.TrueMarketBuy or OrderType.TrueMarketSell;

    private static string ToOrderTypeString(OrderType t) => t switch
    {
        OrderType.TrueMarketBuy      => Order.Types.TrueMarketBuy,
        OrderType.TrueMarketSell     => Order.Types.TrueMarketSell,
        OrderType.SlippageMarketBuy  => Order.Types.SlippageMarketBuy,
        OrderType.SlippageMarketSell => Order.Types.SlippageMarketSell,
        OrderType.LimitBuy           => Order.Types.LimitBuy,
        OrderType.LimitSell          => Order.Types.LimitSell,
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };
    #endregion

    #region Math Helpers
    private static decimal Lerp(decimal a, decimal b, decimal t) => a + (b - a) * t;

    private static decimal Clamp01(decimal x) => x < 0m ? 0m : x > 1m ? 1m : x;

    private static decimal ClampSigned(decimal x, decimal magnitude) =>
        x < -magnitude ? -magnitude : x > magnitude ? magnitude : x;

    /// <summary>
    /// Cash-reserve restoring shift on the buy bias. With <paramref name="continuous"/> off this is the
    /// original edge-only controller verbatim (flat inside [min,max], a 0.40 distance-normalized push at the
    /// walls) so the RNG-free result is byte-identical. With it on, a gentle linear restoring force pulls cash
    /// toward the band midpoint (== seed cash% after the §8 recenter, so the rest-point is the seed) and the
    /// hard walls still force buy/sell at the edges. Pure &amp; RNG-free → unit-testable.
    /// </summary>
    internal static decimal CashHomeostasis(decimal buyBias, decimal cashPrc,
        decimal minReserve, decimal maxReserve,
        bool continuous, decimal maxShift, decimal edgeBuy, decimal edgeSell)
    {
        var homeostatic = buyBias;
        if (continuous)
        {
            var mid  = (minReserve + maxReserve) / 2m;
            var half = (maxReserve - minReserve) / 2m;
            if (half > 0m)
                homeostatic += maxShift * ClampSigned((cashPrc - mid) / half, 1m); // above mid → buy, below → sell
            if (cashPrc >= maxReserve) homeostatic = Math.Max(homeostatic, edgeBuy);  // excess cash → force buy
            if (cashPrc <= minReserve) homeostatic = Math.Min(homeostatic, edgeSell); // starved → force no-buy
        }
        else
        {
            const decimal oldMaxShift = 0.40m;
            if (cashPrc < minReserve)
            {
                var distance = minReserve <= 0m ? 1m : (minReserve - cashPrc) / minReserve;
                homeostatic -= oldMaxShift * Clamp01(distance);
            }
            else if (cashPrc > maxReserve)
            {
                var distance = 1m - maxReserve <= 0m ? 1m : (cashPrc - maxReserve) / (1m - maxReserve);
                homeostatic += oldMaxShift * Clamp01(distance);
            }
        }
        return homeostatic;
    }

    // Microstructure bid-ask bounce: scale a close-tier offset toward mid by (1-touchTightenPrc) so the touch
    // tightens and each spread-crossing print zig-zags less. Applies only to the close tier (the touch-churning
    // orders); Mid/Far standing walls pass through untouched. touchTightenPrc=0 (or non-close) ⇒ no-op,
    // byte-identical. Scaling the band before the Lerp is identical to scaling the final clamped offset (the
    // Lerp and its clamp bounds scale by the same factor), so the clamp stays valid and dispersion is preserved.
    internal static decimal TightenOffset(decimal offset, bool isCloseTier, decimal touchTightenPrc)
        => (touchTightenPrc > 0m && isCloseTier) ? offset * (1m - touchTightenPrc) : offset;

    // Snap a price toward the nearest psychologically-significant level. spread>0 disperses the result
    // within ±spread·unit of that level (jitter01 ∈ [0,1] is the dispersion draw) so a cohort of snapped
    // orders forms a soft cluster across nearby ticks instead of one monolithic wall. spread=0 ⇒ exact.
    internal static decimal SnapToRoundNumber(decimal price, decimal spread = 0m, decimal jitter01 = 0m)
    {
        decimal unit = price switch
        {
            >= 500m => 5m,
            >= 100m => 1m,
            >= 20m  => 0.50m,
            _       => 0.10m
        };
        var rounded = Math.Max(0.01m, Math.Round(price / unit) * unit);
        if (spread <= 0m) return rounded;
        var disperse = (jitter01 * 2m - 1m) * spread * unit;   // ±spread·unit around the level
        return Math.Max(0.01m, rounded + disperse);
    }
    #endregion
}
