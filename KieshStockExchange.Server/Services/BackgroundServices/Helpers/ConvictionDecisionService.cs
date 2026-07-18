using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §conviction: dedicated decision path for <see cref="AiStrategy.Conviction"/> bots — a SEPARATE cohort from the
/// mechanical <see cref="RotatorDecisionService"/> (which stays a ~100%-win estimate-gap rebalancer). These are
/// REALISTIC cash-heavy discretionary traders who rotate into "good plays" OCCASIONALLY on sentiment + sector-
/// momentum, carry per-bot PERSONALITY (chaser vs fader, risk appetite, patience) and take REAL directional risk
/// (win-rate ≪ 100% by design). The signal is sentiment/sector-momentum LED; the price-vs-estimate gap is only a
/// GUARDRAIL that vetoes chasing a name already far above the bank estimate (it never pushes a buy).
///
/// CRITICAL: every acted order is an AGGRESSIVE DIRECTIONAL TAKER (true-market) order — a passive limit would just
/// be absorbed by the value-anchor (the known failure), producing no price impact. A fire buys ONE max-conviction
/// name; exits are MEMORYLESS (no entry-price memory) — a held name is sold when its live conviction decays below
/// ExitBar, its momentum flips negative, or it prints overvalued past StopOvervaluation.
///
/// §P3 shorting (default OFF): when ShortingEnabled, a fire may ALSO short the single most-OVERVALUED name it is
/// FLAT in (flat-only, mirrors the existing short cohort) via the batched market-short route, and COVER a held short
/// once it reverts (hysteresis at 0.5·ShortBar) or momentum turns up. Kept SEPARATE from the long Hot pick (the full
/// signed two-way Hot is the later CRUX phase); this only exercises the short-open + cover plumbing CK-safely. Shorts
/// are SMALL so the cover buyback is always affordable from the cash pile. Off ⇒ byte-identical long-only behaviour.
///
/// Stable by construction:
///  • RiskAppetite ≤ 0.25 HARD-CLAMP + ONE bet per fire ⇒ no all-in cash-bomb (the past failure that froze the loop).
///  • CASH FLOOR — a bot keeps CashFloorPct·seedNotional in cash; a buy deploys only the headroom above it.
///  • SCALER coupling — the per-tick fire probability is scaled by (1−load) so the cohort throttles under load.
///
/// CK-safe: per currency book, two sequential BATCH passes — SELLS first (proceeds settle, cash returns), THEN BUYS
/// sized from the FRESH post-sell <see cref="Fund.AvailableBalance"/> minus the cash floor ⇒ Σ buys ≤ available cash.
/// Both legs are ordinary engine market orders, so ConservationProbe / ReservationAuditor apply unchanged.
/// Deterministic: per-bot dials + the occasional-cadence fire gate are pure HASHES of the aiUserId (no RNG, no
/// wall-clock beyond the sim clock <paramref name="now"/>) ⇒ runs replay. Loop-thread only.
/// </summary>
internal sealed partial class ConvictionDecisionService
{
    #region Services and Constructor
    private readonly IOrderEntryService _entry;
    private readonly IAccountsCache _accounts;
    private readonly IStockService _stocks;
    private readonly ISectorMap _sectorMap;
    private readonly BankEstimateService _bank;
    private readonly BotSentimentService _sentiment;
    private readonly BotEconomyTelemetry _economy;
    private readonly BotScalerService _scaler;
    private readonly MarketMoodService? _mood;   // §mood fear-bid (Feature 3): lagged global-mood source (null in tests)
    private readonly ILogger<ConvictionDecisionService> _logger;

    // Both books the sim runs; a Conviction bot trades each book independently so cash/price/holdings stay
    // currency-consistent (buys are funded by same-book sell proceeds — no implicit FX).
    private static readonly CurrencyType[] Books = { CurrencyType.USD, CurrencyType.EUR };

    private const double MinDtSec = 0.05;
    private const double MaxDtSec = 60.0;

    // Per-(bot,stock) idiosyncratic term (a small personal view → heterogeneity, no lockstep), gap-comparable scale.
    private const double IdioScale = 0.05;

    // Per-bot hashed-dial ranges. The config *Base value is the LOW bound; a fixed const span sets the width, so the
    // defaults reproduce the council ranges and each Base knob shifts its band live. RiskAppetite is HARD-CLAMPED.
    private const double CashFloorSpan       = 0.30;   // Base 0.55 ⇒ [0.55, 0.85]
    private const double RiskAppetiteSpan    = 0.20;   // Base 0.05 ⇒ [0.05, 0.25]
    private const double RiskAppetiteHardCap = 0.25;   // ★ CK-safety: no bet ever exceeds ¼ of seed notional
    private const double ConvictionBarSpan   = 0.09;   // Base 0.03 ⇒ [0.03, 0.12]
    private const double SentimentSensLo     = 0.30, SentimentSensHi = 1.50;
    private const double CheckInMeanSpan     = 9600.0; // Base 1200 ⇒ [1200, 10800] sec (20 min – 3 h)
    private const double ChaserProb          = 0.65;   // Lean: chaser(+1) 65% / fader(−1) 35%

    // Distinct per-dial + fire-gate salts (mixed with the pass counter where a per-pass reshuffle is wanted).
    private const int CashFloorSalt = 0x0C01, RiskSalt = 0x0C02, BarSalt = 0x0C03, SensSalt = 0x0C04,
                      LeanSalt = 0x0C05, CadenceSalt = 0x0C06, FireSalt = 0x0C07, HoldSalt = 0x0C08,
                      ReviewSalt = 0x0C09, HazardSalt = 0x0C0A, NoiseSalt = 0x0C0B; // §P4 review gate / exit draw / entry noise

    private readonly double _wSec, _wMom, _wGlobal, _wIdio, _wOver; // Hot-signal weights (Wsec+Wmom ≫ Wover)
    private readonly double _convictionBarBase;                     // entry-bar low bound (dial spreads up)
    private readonly double _exitBar;                               // sell a held name when Hot drops below this
    private readonly double _stopOvervaluation;                     // sell when price prints this far above estimate
    private readonly double _cashFloorBase;                         // CashFloorPct low bound
    private readonly double _riskAppetiteBase;                      // RiskAppetite low bound
    private readonly double _checkInMeanSecBase;                    // CheckInMeanSec low bound
    private readonly decimal _seedBalanceUsd, _seedBalanceEur;      // per-bot seed notional (bet base — no per-tick scan)
    private readonly bool _useLoadEwma;                             // read the scaler's smoothed load instead of the raw sample
    // §P1 hold-horizon: when enabled, a held name is HELD THROUGH DRAWDOWNS — the soft thesis-decay exit only fires
    // after a per-bot intended holding period (hashed HoldSec) has elapsed since entry (Position.UpdatedAt). Hard
    // exits (overvaluation, and later crash) bypass the horizon. Default off ⇒ the original memoryless exit.
    private readonly bool   _holdHorizonEnabled;
    private readonly double _holdMinSec, _holdMaxSec;
    // §P2 conviction-scaled sizing: replace the flat RiskAppetite·seed bet with a CONVEX power curve of conviction
    // strength above the bar ⇒ MOST plays tiny, RARE exceptional convictions approach MaxDeploy of the headroom.
    // Default off ⇒ the original flat sizing. Cash floor still applies (removed only in the later cash-to-zero phase).
    private readonly bool   _convictionSizingEnabled;
    private readonly double _convScale, _maxDeploy, _sizingGamma;
    // §P3 shorting ROUTE (isolation): when enabled, a firing bot may also SHORT the single most-OVERVALUED name it is
    // FLAT in (overvaluation ≥ ShortBar, momentum not rising) via the batched market-short route, and COVER a held
    // short once it reverts toward fair value (hysteresis at 0.5·ShortBar) or momentum turns up. Deliberately kept
    // SEPARATE from the long Hot pick (the full signed two-way Hot is the later CRUX phase) — this phase only proves
    // the short-open + cover plumbing, the collateral/CK invariant, F1 rollback rate, and perf. Shorts are sized
    // SMALL (ShortRiskFraction·RiskAppetite·seed) so the cover buyback is always affordable from the cash pile.
    // Default off ⇒ no short/cover requests are built (byte-identical long-only behaviour).
    private readonly bool   _shortingEnabled;
    private readonly double _shortBar, _shortRiskFraction;
    // §P4 signed two-way Hot + conviction-led probabilistic lifecycle (the CRUX; supersedes the P1 hold-gate, the P3
    // standalone short trigger and the deterministic exit scan when ON). Two clocks on one pass: entry-hunting stays
    // on CheckInMeanSec; a FASTER review clock (ReviewMeanSec) re-evaluates ONLY held positions (cheap — walks the
    // entry-record dict, no second board scan). Exits are hazard-driven (see ExitHazard) with three guardrails:
    // the close probability is LOAD-SCALED, a per-pass cohort exit-rate cap bounds turnover, and a min-hold floor
    // stops thrash. Default off ⇒ the v1/P1–P3 path runs byte-identical.
    private readonly bool   _signedHotEnabled;
    private readonly double _wGap, _wOwn, _wNoise;                  // signed-Hot weights (wSec/wMom/wGlobal shared with v1)
    private readonly double _reviewMeanSec;                          // the fast position-review clock
    private readonly double _exitBaseHazard, _exitFlipGain, _exitSatisfyGain, _exitTimeExp, _satisfiedBand;
    private readonly double _minHoldSec;                             // hazard floor: no soft exit before this
    private readonly double _maxExitFractionPerPass;                 // cohort-wide soft-exit cap per pass
    private readonly double _shortBarMult;                           // short entry bar = long bar × this (slightly wider)
    // §P5 basket entries: a fire may open the top-K names above the bar instead of only the single best; the per-fire
    // deployment is SPLIT across the basket (budget ÷ basket size) so K>1 = many smaller plays, NOT K× the risk.
    // 1 ⇒ the legacy single-best pick, byte-identical (single-candidate tracking, no list/sort).
    private readonly int    _maxEntriesPerFire;
    // §P4 per-position entry record (Fable review): heldSec anchors here (NOT Position.UpdatedAt, which moves on any
    // write) and "satisfied" = the ENTRY gap closed (kills the born-satisfied churn). Keyed WITHOUT currency —
    // Position is currency-agnostic (a cross-listed stock shares ONE Position) — the entry book is stored so the
    // exit routes through the same book (a cover must be same-ccy for the collateral release). Loop-thread only.
    internal readonly record struct EntryRec(DateTime EnteredAt, double EntryGap, int Side, CurrencyType Ccy);
    private readonly Dictionary<(int UserId, int Sid), EntryRec> _entryRecs = new();
    // §mood fear-bid (Feature 3): a BOUNDED, FEAR-ONLY, BUY-ONLY nudge to the conviction score — the cohort BUYS the
    // panic (adds to LONG conviction, never forces a short). Flows through the existing conviction→order path so
    // fund/position reservation + CK gating are INHERITED. Default off ⇒ byte-identical.
    private readonly bool   _moodFearBid;
    private readonly double _moodFearBidGain;
    private long _passCount;                                        // monotonic, reshuffles the fire subset each pass
    private DateTime _lastPassUtc = DateTime.MaxValue;              // inert until the first RunAsync arms the clock

    internal ConvictionDecisionService(IOrderEntryService entry, IAccountsCache accounts, IStockService stocks,
        ISectorMap sectorMap, BankEstimateService bank, BotSentimentService sentiment, BotEconomyTelemetry economy,
        BotScalerService scaler, ILogger<ConvictionDecisionService> logger,
        double wSec = 1.0, double wMom = 0.5, double wGlobal = 0.3, double wIdio = 0.2, double wOver = 0.5,
        double convictionBarBase = 0.03, double exitBar = 0.0, double stopOvervaluation = 0.10,
        double cashFloorBase = 0.55, double riskAppetiteBase = 0.05, double checkInMeanSecBase = 1200.0,
        decimal seedBalanceUsd = 200_000m, decimal seedBalanceEur = 180_000m, bool useLoadEwma = false,
        bool holdHorizonEnabled = false, double holdMinSec = 1800.0, double holdMaxSec = 172_800.0,
        bool convictionSizingEnabled = false, double convScale = 0.12, double maxDeploy = 0.90, double sizingGamma = 3.0,
        bool shortingEnabled = false, double shortBar = 0.06, double shortRiskFraction = 0.15,
        bool signedHotEnabled = false, double wGap = 1.0, double wOwn = 0.1, double wNoise = 0.2,
        double reviewMeanSec = 300.0, double exitBaseHazard = 0.02, double exitFlipGain = 2.0,
        double exitSatisfyGain = 0.15, double exitTimeExp = 2.5, double satisfiedBand = 0.02,
        double minHoldSec = 120.0, double maxExitFractionPerPass = 0.10, double shortBarMult = 1.2,
        int maxEntriesPerFire = 1,
        // §mood fear-bid (Feature 3): the mood source + fear-bid gain. Trailing/optional so existing callers are
        // unaffected; null mood or flag off ⇒ inert (byte-identical long-only behaviour).
        MarketMoodService? mood = null, bool moodFearBid = false, double moodFearBidGain = 0.10)
    {
        _entry     = entry     ?? throw new ArgumentNullException(nameof(entry));
        _accounts  = accounts  ?? throw new ArgumentNullException(nameof(accounts));
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _sectorMap = sectorMap ?? throw new ArgumentNullException(nameof(sectorMap));
        _bank      = bank      ?? throw new ArgumentNullException(nameof(bank));
        _sentiment = sentiment ?? throw new ArgumentNullException(nameof(sentiment));
        _economy   = economy   ?? throw new ArgumentNullException(nameof(economy));
        _scaler    = scaler    ?? throw new ArgumentNullException(nameof(scaler));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        _wSec = wSec; _wMom = wMom; _wGlobal = wGlobal; _wIdio = wIdio; _wOver = Math.Max(0.0, wOver);
        _convictionBarBase  = Math.Max(0.0, convictionBarBase);
        _exitBar            = exitBar;
        _stopOvervaluation  = Math.Max(0.0, stopOvervaluation);
        _cashFloorBase      = Math.Clamp(cashFloorBase, 0.0, 1.0);
        _riskAppetiteBase   = Math.Clamp(riskAppetiteBase, 0.0, RiskAppetiteHardCap);
        _checkInMeanSecBase = Math.Max(MinDtSec, checkInMeanSecBase);
        _seedBalanceUsd     = seedBalanceUsd;
        _seedBalanceEur     = seedBalanceEur;
        _useLoadEwma        = useLoadEwma;
        _holdHorizonEnabled = holdHorizonEnabled;
        _holdMinSec         = Math.Max(0.0, holdMinSec);
        _holdMaxSec         = Math.Max(_holdMinSec, holdMaxSec);
        _convictionSizingEnabled = convictionSizingEnabled;
        _convScale          = Math.Max(1e-6, convScale);
        _maxDeploy          = Math.Clamp(maxDeploy, 0.0, 1.0);
        _sizingGamma        = Math.Max(0.1, sizingGamma);
        _shortingEnabled    = shortingEnabled;
        _shortBar           = Math.Max(0.0, shortBar);
        _shortRiskFraction  = Math.Clamp(shortRiskFraction, 0.0, 1.0);
        _signedHotEnabled   = signedHotEnabled;
        _wGap = wGap; _wOwn = wOwn; _wNoise = Math.Max(0.0, wNoise);
        _reviewMeanSec      = Math.Max(MinDtSec, reviewMeanSec);
        _exitBaseHazard     = Math.Clamp(exitBaseHazard, 0.0, 1.0);
        _exitFlipGain       = Math.Max(0.0, exitFlipGain);
        _exitSatisfyGain    = Math.Max(0.0, exitSatisfyGain);
        _exitTimeExp        = Math.Max(0.1, exitTimeExp);
        _satisfiedBand      = Math.Max(0.0, satisfiedBand);
        _minHoldSec         = Math.Max(0.0, minHoldSec);
        _maxExitFractionPerPass = Math.Clamp(maxExitFractionPerPass, 0.0, 1.0);
        _shortBarMult       = Math.Max(1.0, shortBarMult);
        _maxEntriesPerFire  = Math.Max(1, maxEntriesPerFire);
        _mood               = mood;
        _moodFearBid        = moodFearBid;
        _moodFearBidGain    = Math.Max(0.0, moodFearBidGain);   // buy-only: a NEGATIVE gain would fade — disallow
    }

    internal void Reset() { _passCount = 0; _lastPassUtc = DateTime.MaxValue; _entryRecs.Clear(); }
    #endregion
}
