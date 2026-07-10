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
internal sealed class ConvictionDecisionService
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
                      LeanSalt = 0x0C05, CadenceSalt = 0x0C06, FireSalt = 0x0C07, HoldSalt = 0x0C08;

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
        bool shortingEnabled = false, double shortBar = 0.06, double shortRiskFraction = 0.15)
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
    }

    internal void Reset() { _passCount = 0; _lastPassUtc = DateTime.MaxValue; }
    #endregion

    #region Pure decision math (unit-testable, RNG-free)
    /// <summary>A per-bot hashed dial in [lo, hi): lo + (hi−lo)·HashUnit01(id, salt). Deterministic, RNG-free.</summary>
    internal static double Dial(int aiUserId, int salt, double lo, double hi)
        => lo + (hi - lo) * BotMath.HashUnit01(aiUserId, salt);

    /// <summary>Per-bot lean: chaser(+1) with probability <paramref name="chaserProb"/>, else fader(−1).</summary>
    internal static int Lean(int aiUserId, double chaserProb)
        => BotMath.HashUnit01(aiUserId, LeanSalt) < chaserProb ? 1 : -1;

    /// <summary>The conviction score. Sentiment + sector-momentum LED (a fader NEGATES those two terms); the shared
    /// global signal and the personal idio term add heterogeneity; the −Wover overvaluation term is a one-way VETO
    /// (only subtracts, never pushes a buy) against chasing a name already above the bank estimate. Pure ⇒ testable.</summary>
    internal static double Hot(double sectorSent, double mom, double global, double idio, double gap,
        int lean, double wSec, double wMom, double wGlobal, double wIdio, double wOver)
    {
        double leanF = lean >= 0 ? 1.0 : -1.0;
        double over  = Math.Max(0.0, -gap);      // overvaluation = (price − est)/est when price is above the estimate
        return wSec * leanF * sectorSent
             + wMom * leanF * mom
             + wGlobal * global
             + wIdio * idio
             - wOver * over;
    }

    /// <summary>Entry gate: conviction clears the sensitivity-scaled bar. Higher Sensitivity ⇒ lower effective bar
    /// ⇒ the bot acts on weaker signals. Pure ⇒ testable.</summary>
    internal static bool PassesBar(double hot, double bar, double sens)
        => sens > 0.0 && hot >= bar / sens;

    /// <summary>Memoryless exit test for a HELD name: its thesis has decayed (Hot below ExitBar), momentum has
    /// flipped negative, or it prints overvalued past StopOvervaluation. Pure ⇒ testable.</summary>
    internal static bool ShouldExit(double hot, double mom, double overvaluation, double exitBar, double stopOvervaluation)
        => hot < exitBar || mom < 0.0 || overvaluation > stopOvervaluation;

    /// <summary>§P1 hold-horizon exit: a HARD exit (overvalued past the stop) fires immediately; the SOFT thesis-
    /// decay exit (Hot below ExitBar) only fires once the intended holding period has elapsed (heldSec ≥ holdSec).
    /// Unlike <see cref="ShouldExit"/> there is NO momentum knee-jerk — the bot HOLDS THROUGH DRAWDOWNS, so it can
    /// end underwater (real directional risk = win-rate ≪ 100%). Pure ⇒ testable.</summary>
    internal static bool ShouldExitHeld(double hot, double overvaluation, double exitBar, double stopOvervaluation,
        double heldSec, double holdSec)
        => overvaluation > stopOvervaluation || (hot < exitBar && heldSec >= holdSec);

    /// <summary>CK-safe bet size: min(RiskAppetite·seed, availCash − cashFloor), floored at 0 so a buy can never
    /// exceed available cash nor dip below the reserved floor. Pure ⇒ testable.</summary>
    internal static decimal DeployNotional(decimal riskNotional, decimal availCash, decimal cashFloorAmount)
    {
        decimal headroom = availCash - cashFloorAmount;
        if (headroom <= 0m) return 0m;
        decimal d = Math.Min(riskNotional, headroom);
        return d < 0m ? 0m : d;
    }

    /// <summary>§P2 conviction-scaled deploy fraction: a CONVEX power curve of conviction strength above the (already
    /// sensitivity-scaled) bar ⇒ MOST fires deploy a tiny fraction of the cash headroom, RARE exceptional convictions
    /// approach MaxDeploy. z = clamp(strength/convScale, 0, 1); frac = MaxDeploy·z^gamma. Pure ⇒ testable.</summary>
    internal static double ConvictionDeployFraction(double strength, double convScale, double maxDeploy, double gamma)
    {
        double z = Math.Clamp(strength / Math.Max(1e-9, convScale), 0.0, 1.0);
        return maxDeploy * Math.Pow(z, gamma);
    }

    /// <summary>§P3 open a short: the name prints OVERVALUED past ShortBar (price well above the bank estimate) and its
    /// momentum is NOT rising. Flat-only is enforced at the call site (Position.Quantity==0). Pure ⇒ testable.</summary>
    internal static bool ShouldOpenShort(double overvaluation, double mom, double shortBar)
        => overvaluation >= shortBar && mom <= 0.0;

    /// <summary>§P3 cover a held short: HYSTERESIS — the overvaluation has reverted below half the open bar (back toward
    /// fair value) OR momentum has turned up against the short. The 0.5·ShortBar band prevents open/cover thrash.
    /// Pure ⇒ testable.</summary>
    internal static bool ShouldCoverShort(double overvaluation, double mom, double shortBar)
        => overvaluation <= 0.5 * shortBar || mom > 0.0;

    /// <summary>§P3 short size (SHARES): a SMALL exposure = ShortRiskFraction·RiskAppetite·seedNotional / price, floored
    /// at 0. Shorts reserve collateral at fill (not cash at placement); this bounds EXPOSURE so the later cover buyback
    /// stays affordable from the cash pile. Pure ⇒ testable.</summary>
    internal static int ShortQty(decimal seedNotional, double riskAppetite, double shortRiskFraction, double price)
    {
        if (price <= 0.0) return 0;
        decimal notional = (decimal)(riskAppetite * shortRiskFraction) * seedNotional;
        if (notional <= 0m) return 0;
        int q = (int)Math.Floor((double)notional / price);
        return q > 0 ? q : 0;
    }

    private double CashFloorPctOf(int id)   => Dial(id, CashFloorSalt, _cashFloorBase, _cashFloorBase + CashFloorSpan);
    private double RiskAppetiteOf(int id)   => Math.Min(RiskAppetiteHardCap,
                                                   Dial(id, RiskSalt, _riskAppetiteBase, _riskAppetiteBase + RiskAppetiteSpan));
    private double ConvictionBarOf(int id)  => Dial(id, BarSalt, _convictionBarBase, _convictionBarBase + ConvictionBarSpan);
    private double SentimentSensOf(int id)  => Dial(id, SensSalt, SentimentSensLo, SentimentSensHi);
    private double CheckInMeanSecOf(int id) => Dial(id, CadenceSalt, _checkInMeanSecBase, _checkInMeanSecBase + CheckInMeanSpan);
    private double HoldSecOf(int id)        => Dial(id, HoldSalt, _holdMinSec, _holdMaxSec);   // §P1 per-bot hold horizon
    #endregion

    #region Run
    internal async Task RunAsync(AiBotContext ctx, DateTime now, CancellationToken ct)
    {
        _passCount++;
        // First pass just arms the clock (inert), like BankEstimateService/BotSentimentService.
        if (_lastPassUtc == DateTime.MaxValue) { _lastPassUtc = now; return; }
        double dt = Math.Clamp((now - _lastPassUtc).TotalSeconds, MinDtSec, MaxDtSec);
        _lastPassUtc = now;

        // Eligible cohort = enabled Conviction bots. Filter the small (~300) cohort — not all ~20k users.
        var eligible = new List<AIUser>();
        foreach (var user in ctx.AiUsersByAiUserId.Values)
        {
            if (!user.IsEnabled || user.Strategy != AiStrategy.Conviction) continue;
            eligible.Add(user);
        }
        if (eligible.Count == 0) return;

        // OCCASIONAL, STATELESS cadence: per-tick fire probability = clamp(dt / CheckInMeanSec), SCALER-COUPLED by
        // (1−load) so the cohort backs off under load. A deterministic per-(bot,pass) hash decides who fires, so
        // each bot acts irregularly ~every CheckInMeanSec with no per-bot timer state (replay-stable).
        double load = Math.Clamp(_useLoadEwma ? _scaler.LoadFractionEwma : _scaler.LastLoadFraction, 0.0, 1.0);
        double loadScale = 1.0 - load;
        var firing = new List<AIUser>();
        foreach (var user in eligible)
        {
            double fireProb = Math.Clamp(dt / CheckInMeanSecOf(user.AiUserId), 0.0, 1.0) * loadScale;
            if (BotMath.HashUnit01(user.AiUserId, FireSalt ^ unchecked((int)_passCount)) < fireProb)
            { firing.Add(user); user.RecordDecision(now); }
        }
        if (firing.Count == 0) return;
        firing.Sort((a, b) => a.AiUserId.CompareTo(b.AiUserId)); // deterministic execution order

        foreach (var ccy in Books)
            await TradeBookAsync(ctx, firing, ccy, now, ct).ConfigureAwait(false);
    }

    private async Task TradeBookAsync(AiBotContext ctx, List<AIUser> firing, CurrencyType ccy, DateTime now, CancellationToken ct)
    {
        // Board universe for this book: every listed stock with a live quote + seed (authoritative = IStockService).
        var board = new List<(int Sid, double Price, decimal Seed)>();
        foreach (var sid in _stocks.ById.Keys)
        {
            if (!_stocks.IsListedIn(sid, ccy)) continue;
            if (!ctx.StockPrices.TryGetValue((sid, ccy), out var price) || price <= 0m) continue;
            decimal seed = SeedPrice(sid, ccy);
            if (seed <= 0m) continue;
            board.Add((sid, (double)price, seed));
        }
        if (board.Count == 0) return;

        double global = (double)_sentiment.GlobalSignal();
        decimal seedNotional = ccy == CurrencyType.USD ? _seedBalanceUsd : _seedBalanceEur;

        // §perf: the per-sid signal (sector sentiment, momentum, gap, overvaluation) is BOT-INDEPENDENT, so resolve
        // it ONCE per book. Sector sentiment = the mean per real sector (falls back to the per-name value when no
        // real sectors are seeded ⇒ sector-of-one), so a whole sector's mood leads its names together.
        bool realSectors = _sectorMap.HasRealSectors;
        Dictionary<int, (double Sum, int N)>? sectorAcc = realSectors ? new() : null;
        if (realSectors)
            foreach (var (sid, _, _) in board)
            {
                int ord = _sectorMap.OrdinalOf(sid);
                if (ord < 0) continue;
                var cur = sectorAcc!.GetValueOrDefault(ord);
                sectorAcc[ord] = (cur.Sum + (double)_sentiment.GetSentiment(sid), cur.N + 1);
            }

        var signal = new List<(int Sid, double Price, double SectorSent, double Mom, double Gap, double Overval)>(board.Count);
        foreach (var (sid, price, seed) in board)
        {
            double dev = _bank.BankTarget(sid);
            double est = (double)seed * (1.0 + dev);
            if (est <= 0.0) continue;
            double gap = (est - price) / est;                    // >0 undervalued, <0 overvalued
            double overval = Math.Max(0.0, -gap);
            double mom = (double)_sentiment.GetSentimentSlope(sid, fast: false);
            double sectorSent;
            if (realSectors)
            {
                int ord = _sectorMap.OrdinalOf(sid);
                var a = ord >= 0 ? sectorAcc!.GetValueOrDefault(ord) : default;
                sectorSent = a.N > 0 ? a.Sum / a.N : (double)_sentiment.GetSentiment(sid);
            }
            else sectorSent = (double)_sentiment.GetSentiment(sid);
            signal.Add((sid, price, sectorSent, mom, gap, overval));
        }
        if (signal.Count == 0) return;

        var sellReqs   = new List<TrueMarketSellBatchRequest>();
        var sellOwners = new List<AIUser>();
        var buyPlans   = new List<(AIUser User, int Sid, double Price, double Strength)>();
        // §P3 short-side (only populated when _shortingEnabled): covers (buy-to-flatten a held short) and opens.
        var coverReqs   = new List<TrueMarketBuyBatchRequest>();
        var coverOwners = new List<AIUser>();
        var shortReqs   = new List<MarketShortBatchRequest>();
        var shortOwners = new List<AIUser>();

        // §perf: one reusable scratch buffer for the per-bot ranking (single loop thread ⇒ no aliasing).
        var scored = new List<(int Sid, double Price, double Hot, double Mom, double Overval)>(signal.Count);
        foreach (var user in firing)
        {
            int lean = Lean(user.AiUserId, ChaserProb);
            double sens = SentimentSensOf(user.AiUserId);
            double bar  = ConvictionBarOf(user.AiUserId);

            scored.Clear();
            int bestIdx = -1; double bestHot = double.NegativeInfinity;
            foreach (var (sid, price, sectorSent, mom, gap, overval) in signal)
            {
                double idio = (BotMath.HashUnit01(user.AiUserId, sid) * 2.0 - 1.0) * IdioScale;
                double hot  = Hot(sectorSent, mom, global, idio, gap, lean, _wSec, _wMom, _wGlobal, _wIdio, _wOver);
                scored.Add((sid, price, hot, mom, overval));
                if (hot > bestHot) { bestHot = hot; bestIdx = scored.Count - 1; }
            }

            // Per-bot scan (turnover-bounded to ONE name each): the worst-Hot HELD LONG that fails its thesis (exit),
            // and — §P3 shorting on — the most-bullish HELD SHORT to cover + the most-overvalued FLAT name to short.
            // §P1: with the hold-horizon ON, a held long is HELD THROUGH DRAWDOWNS — the soft thesis-decay exit waits
            // out the per-bot HoldSec (hard overvaluation bypasses it); OFF ⇒ the original memoryless exit.
            int sellIdx = -1;  double sellHot   = double.PositiveInfinity;
            int coverIdx = -1; double coverHot  = double.NegativeInfinity;  // §P3 most-bullish held short = urgent cover
            int shortIdx = -1; double shortOver = double.NegativeInfinity;  // §P3 most-overvalued flat = short candidate
            for (int i = 0; i < scored.Count; i++)
            {
                var s = scored[i];
                var pos = _accounts.GetPosition(user.UserId, s.Sid);
                int qty = pos?.Quantity ?? 0;
                if ((pos?.AvailableQuantity ?? 0) > 0)
                {
                    bool doExit = _holdHorizonEnabled
                        ? ShouldExitHeld(s.Hot, s.Overval, _exitBar, _stopOvervaluation,
                                         (now - pos!.UpdatedAt).TotalSeconds, HoldSecOf(user.AiUserId))
                        : ShouldExit(s.Hot, s.Mom, s.Overval, _exitBar, _stopOvervaluation);
                    if (doExit && s.Hot < sellHot) { sellHot = s.Hot; sellIdx = i; }
                }
                else if (_shortingEnabled && qty < 0)
                {
                    if (ShouldCoverShort(s.Overval, s.Mom, _shortBar) && s.Hot > coverHot) { coverHot = s.Hot; coverIdx = i; }
                }
                else if (_shortingEnabled && qty == 0)
                {
                    if (ShouldOpenShort(s.Overval, s.Mom, _shortBar) && s.Overval > shortOver) { shortOver = s.Overval; shortIdx = i; }
                }
            }
            if (sellIdx >= 0)
            {
                int held = _accounts.GetPosition(user.UserId, scored[sellIdx].Sid)?.AvailableQuantity ?? 0;
                if (held > 0)
                {
                    sellReqs.Add(new TrueMarketSellBatchRequest(user.UserId, scored[sellIdx].Sid, held, ccy));
                    sellOwners.Add(user);
                }
            }

            // §P3 COVER: buy EXACTLY the short qty to flatten (never flip long); a ×1.5 budget headroom for a price
            // rise (always affordable — P3 shorts are small vs the cash pile). The settler releases the collateral.
            if (_shortingEnabled && coverIdx >= 0)
            {
                int shortQty = -(_accounts.GetPosition(user.UserId, scored[coverIdx].Sid)?.Quantity ?? 0);
                double cprice = scored[coverIdx].Price;
                if (shortQty > 0 && cprice > 0.0)
                {
                    decimal budget = (decimal)(shortQty * cprice * 1.5);
                    coverReqs.Add(new TrueMarketBuyBatchRequest(user.UserId, scored[coverIdx].Sid, shortQty, budget, ccy));
                    coverOwners.Add(user);
                }
            }

            // §P3 SHORT OPEN: a small flat-only market short of the most-overvalued name (collateral reserved at fill).
            if (_shortingEnabled && shortIdx >= 0)
            {
                int qty = ShortQty(seedNotional, RiskAppetiteOf(user.AiUserId), _shortRiskFraction, scored[shortIdx].Price);
                if (qty > 0)
                {
                    shortReqs.Add(new MarketShortBatchRequest(user.UserId, scored[shortIdx].Sid, qty, ccy));
                    shortOwners.Add(user);
                }
            }

            // ENTRY: buy the single max-conviction name when it clears the sensitivity-scaled bar (funded in pass 3).
            // §P2 carries the conviction STRENGTH above the effective bar (bar/sens) so the sizing curve can read it.
            // §P3 flip guard: with shorting on, never long-buy a name the bot is SHORT in (the cover path flattens it).
            if (bestIdx >= 0 && PassesBar(bestHot, bar, sens)
                && (!_shortingEnabled || (_accounts.GetPosition(user.UserId, scored[bestIdx].Sid)?.Quantity ?? 0) >= 0))
                buyPlans.Add((user, scored[bestIdx].Sid, scored[bestIdx].Price, bestHot - bar / Math.Max(1e-9, sens)));
        }

        // CK-safe ordering = sell / cover THEN buy / short: cash-producing legs settle first so the cash-consuming
        // buys are sized off FRESH AvailableBalance; the collateral-neutral short opens go last.
        // Pass 1 — long SELLS (proceeds settle, cash returns to fund the buys).
        if (sellReqs.Count > 0)
        {
            var sellResults = await _entry.PlaceTrueMarketSellBatchAsync(sellReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < sellOwners.Count; i++) RecordFills(sellOwners[i], sellResults[i]);
        }

        // Pass 2 — §P3 COVERS (buy-to-flatten held shorts): releases short collateral + settles the short's P&L
        // before the long buys read available cash. Cover buys ride the plain buy-batch; the settler flattens them.
        if (coverReqs.Count > 0)
        {
            var coverResults = await _entry.PlaceTrueMarketBuyBatchAsync(coverReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < coverOwners.Count; i++) RecordFills(coverOwners[i], coverResults[i]);
        }

        // Pass 3 — one aggressive TAKER BUY per firing bot, from FRESH post-sell/cover AvailableBalance, bounded by
        // RiskAppetite·seed AND the cash floor ⇒ Σ buys ≤ available cash (no all-in cash-bomb, no self-inflation).
        var buyReqs   = new List<TrueMarketBuyBatchRequest>();
        var buyOwners = new List<AIUser>();
        foreach (var (user, sid, price, strength) in buyPlans)
        {
            if (price <= 0.0) continue;
            decimal avail        = _accounts.GetFund(user.UserId, ccy)?.AvailableBalance ?? 0m;
            decimal cashFloorAmt = (decimal)CashFloorPctOf(user.AiUserId) * seedNotional;
            decimal budget;
            if (_convictionSizingEnabled)
            {
                // §P2: convex conviction-scaled fraction of the cash HEADROOM (most small, rare large) — cash floor
                // still applies (removed only in the later cash-to-zero phase). DeployNotional keeps it ≤ headroom ≤ avail.
                double deployFrac = ConvictionDeployFraction(strength, _convScale, _maxDeploy, _sizingGamma);
                budget = DeployNotional((decimal)deployFrac * (avail - cashFloorAmt), avail, cashFloorAmt);
            }
            else
            {
                decimal riskNotional = (decimal)RiskAppetiteOf(user.AiUserId) * seedNotional;
                budget = DeployNotional(riskNotional, avail, cashFloorAmt);   // ★ CK-safe: ≤ available cash
            }
            if (budget <= 0m) continue;
            int qty = (int)Math.Floor((double)budget / price);
            if (qty <= 0) continue;
            buyReqs.Add(new TrueMarketBuyBatchRequest(user.UserId, sid, qty, budget, ccy));
            buyOwners.Add(user);
        }
        if (buyReqs.Count > 0)
        {
            var buyResults = await _entry.PlaceTrueMarketBuyBatchAsync(buyReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < buyOwners.Count; i++) RecordFills(buyOwners[i], buyResults[i]);
        }

        // Pass 4 — §P3 SHORT OPENS (collateral-neutral at open: proceeds are locked as collateral, buying power
        // unchanged) go LAST so they never affect the buys' cash sizing. Flat-only market shorts via the short batch.
        if (shortReqs.Count > 0)
        {
            var shortResults = await _entry.PlaceMarketShortBatchAsync(shortReqs, ct).ConfigureAwait(false);
            for (int i = 0; i < shortOwners.Count; i++) RecordFills(shortOwners[i], shortResults[i]);
        }
    }

    private decimal SeedPrice(int stockId, CurrencyType ccy)
    {
        foreach (var l in _stocks.GetListings(stockId))
            if (l.CurrencyType == ccy) return l.SeedPrice;
        return 0m;
    }

    private void RecordFills(AIUser user, OrderResult result)
    {
        if (result.FillTransactions.Count == 0) return;
        for (int i = 0; i < result.FillTransactions.Count; i++)
            user.RecordTrade(result.FillTransactions[i]);
    }
    #endregion
}
