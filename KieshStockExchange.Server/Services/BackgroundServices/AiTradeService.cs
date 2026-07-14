using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace KieshStockExchange.Services.BackgroundServices;

public class AiTradeService : IAiTradeService, IAsyncDisposable
{
    // Targeted debug logging — full failure echo for one bot's order traffic.
    private readonly bool DebugMode = true;
    private readonly int? DebugUserId = 20001;

    #region Public Properties
    public TimeSpan TradeInterval        { get; private set; } = TimeSpan.FromSeconds(1);
    public TimeSpan DailyCheckInterval   { get; private set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ReloadAssetsInterval { get; private set; } = TimeSpan.FromMinutes(1);
    public TimeSpan PruneInterval        { get; private set; } = TimeSpan.FromSeconds(30);

    // Every currency a bot might trade in. Bots now decide in their own
    // HomeCurrency only; this list still drives the engine's book
    // subscriptions and the per-currency walks in BotEconomyTelemetry.
    public IReadOnlyList<CurrencyType> CurrenciesToTrade { get; private set; } =
        new[] { CurrencyType.USD, CurrencyType.EUR };

    public int LoadedBotCount => _ctx.AiUsersByAiUserId.Count;

    public int OnlineBotCount
    {
        get
        {
            // Lock the dict because AiBotStateService.LoadAsync clears + repopulates
            // it during the daily refresh on the bot-loop thread, while admin HTTP
            // requests read it on the request thread. Matching lock is in LoadAsync.
            int n = 0;
            lock (_ctx.AiUsersByAiUserId)
            {
                foreach (var u in _ctx.AiUsersByAiUserId.Values)
                    if (u.IsEnabled) n++;
            }
            return n;
        }
    }

    public int? ActiveBotCap { get; private set; } = 100;

    /// <summary>User-configured hard ceiling on online bots; the scaler may move
    /// <see cref="ActiveBotCap"/> within [MinBotCap, MaxBotCap]. Null means no ceiling.</summary>
    public int? MaxBotCap { get; private set; } = 20000;

    public long TickCount => Interlocked.Read(ref _tickCount);
    public long TradesPlacedThisSession => Interlocked.Read(ref _tradesPlacedThisSession);
    public long FailuresThisSession => Interlocked.Read(ref _failuresThisSession);

    /// <summary>EWMA-smoothed tick-work duration in milliseconds. 0 until first tick.</summary>
    public double TickWorkMsEwma => Volatile.Read(ref _tickWorkMsEwma);

    /// <summary>EWMA-smoothed "actionable" tick-work in ms — the Collect+Batch span the scaler can act
    /// on, excluding the cap-exempt cohorts. Always maintained (telemetry). 0 until first tick.</summary>
    public double TickWorkActionableMsEwma => Volatile.Read(ref _tickWorkActionableMsEwma);

    /// <summary>Raw duration of the most recent tick's work in microseconds.</summary>
    public long LastTickWorkMicros => Interlocked.Read(ref _lastTickWorkMicros);

    /// <summary>When true, the internal scaler adjusts <see cref="ActiveBotCap"/> based on tick-work load.</summary>
    public bool AutoScale
    {
        get => _scaler.Enabled;
        set => _scaler.Enabled = value;
    }

    /// <summary>Floor on the active bot count when the scaler scales down.</summary>
    public int MinBotCap
    {
        get => _scaler.MinBotCap;
        set => _scaler.MinBotCap = Math.Max(0, value);
    }

    /// <summary>Most recent EWMA / TradeInterval ratio observed by the scaler. 0 before any sample.</summary>
    public double LastLoadFraction => _scaler.LastLoadFraction;

    public DateTime? LastTradeAtUtc { get; private set; }
    public DateTime? LoopStartedAtUtc { get; private set; }

    // Failure surface delegates to the tracker so callers can't touch the live state.
    public IReadOnlyList<string> RecentFailures => _failures.RecentFailures;
    public IReadOnlyDictionary<FailureCategory, long> FailuresByCategory => _failures.FailuresByCategory;
    public IReadOnlyDictionary<int, long> FailuresByStockId => _failures.FailuresByStockId;
    public IReadOnlyList<FailureRecord> RecentFailureRecords => _failures.RecentFailureRecords;
    public string SuggestedFailuresExportFileName => _failures.SuggestedExportFileName;
    public Task<string> ExportFailuresCsvAsync(string path, CancellationToken ct = default)
        => _failures.ExportCsvAsync(path, ct);
    public string BuildFailuresCsv(CancellationToken ct = default) => _failures.BuildCsv(ct);
    public void ClearFailures() => _failures.ClearAll();

    // Reservation ledger surface delegates to the auditor.
    public int ReservationLedgerEntryCount => _auditor.LedgerEntryCount;
    public string SuggestedLedgerExportFileName => _auditor.SuggestedLedgerExportFileName;
    public Task<string> ExportReservationLedgerCsvAsync(string path, CancellationToken ct = default)
        => _auditor.ExportLedgerCsvAsync(path, ct);
    public string BuildReservationLedgerCsv(CancellationToken ct = default) => _auditor.BuildLedgerCsv(ct);

    // Economy telemetry surface delegates to the telemetry helper.
    public int EconomySampleCount => _economy.SampleCount;
    public string SuggestedEconomyExportFileName => _economy.SuggestedExportFileName;
    public Task<string> ExportEconomyCsvAsync(string path, CancellationToken ct = default)
        => _economy.ExportCsvAsync(path, ct);
    public string BuildEconomyCsv(CancellationToken ct = default) => _economy.BuildCsv(ct);

    // Sentiment telemetry surface delegates to the sentiment service.
    public int SentimentSampleCount => _sentiment.SampleCount;
    public string SuggestedSentimentExportFileName => _sentiment.SuggestedExportFileName;
    public Task<string> ExportSentimentCsvAsync(string path, CancellationToken ct = default)
        => _sentiment.ExportCsvAsync(path, ct);
    public string BuildSentimentCsv(CancellationToken ct = default) => _sentiment.BuildCsv(ct);

    public event EventHandler? StatsChanged;
    #endregion

    #region Private Fields
    // Timer clocks driving CheckTimers. All scheduling lives here; the work
    // each clock triggers lives in the relevant helper.
    private DateTime _nextDailyCheck    = DateTime.MinValue;
    private DateTime _nextMoodLog       = DateTime.MinValue; // §fear-greed periodic distribution log throttle
    private DateTime _nextAssetReload   = DateTime.MinValue;
    private DateTime _nextPruneTime     = DateTime.MinValue;
    private DateTime _nextStatsLogTime      = DateTime.MinValue;
    private DateTime _nextReconcileTime     = DateTime.MinValue;
    private DateTime _nextEconomyLogTime    = DateTime.MinValue;
    private DateTime _nextSentimentLogTime  = DateTime.MinValue;
    private DateTime _nextCashInjectionTime = DateTime.MinValue;

    private static readonly TimeSpan StatsLogInterval = TimeSpan.FromSeconds(60);
    // Reservation reconcile: 5 minutes is plenty for a passive leak hunter; first
    // run fires 1 minute after start so a cold-load mismatch surfaces early.
    private static readonly TimeSpan ReconcileInterval   = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReconcileFirstDelay = TimeSpan.FromMinutes(1);
    // Economy snapshot cadence (config Bots:EconomyLogIntervalSeconds, default 60 = byte-identical). The
    // per-call walk (bots × positions) is the largest residual maint chunk; raise the interval to sample it
    // down (telemetry-only — zero CK/behaviour effect).
    private readonly TimeSpan _economyLogInterval;
    // Sentiment snapshot cadence (config Bots:SentimentLogIntervalSeconds, default 60). Lower it (e.g.
    // 15s) for a denser sentiment/price correlation export; higher to thin production telemetry.
    private readonly TimeSpan _sentimentLogInterval;
    // Cash injection: 1-hour nominal-growth driver; per-bot frequency knob
    // gates each bot's actual deposit within the cycle.
    private readonly TimeSpan _cashInjectionInterval; // Bots:CashInjection:IntervalMinutes (default 30m)

    // Schedule fires on drain signal; engine only fires if drain times out.
    private CancellationTokenSource? _schedulingCts;
    private CancellationTokenSource? _engineCts;
    private Task? _runner;
    private long _tickCount = 0;
    private long _tradesPlacedThisSession = 0;
    private long _failuresThisSession = 0;

    // EWMA tick-latency for the dashboard + scaler. α=0.2 reacts in ~5 ticks.
    private const double EwmaAlpha = 0.2;
    private double _tickWorkMsEwma = 0.0;
    // §B-P-b: parallel EWMA of the actionable (Collect+Batch) span only — feeds the scaler's §B-2 sizing
    // when enabled; always maintained so the switch is a pure read (no plumbing change on the hot path).
    private double _tickWorkActionableMsEwma = 0.0;
    private long _lastTickWorkMicros = 0;

    // §B-(b) self-correcting delay: when true, the end-of-tick delay subtracts elapsed work so the true
    // period tracks TradeInterval instead of interval + work. Default off ⇒ fixed delay (byte-identical).
    private readonly bool _selfCorrectingDelay;

    // Per-phase tick profiling (opt-in via Bots:PhaseTimingSeconds > 0). Accumulates µs per phase
    // across the window, logs the average breakdown so "what takes the most time" is observable
    // without an external profiler. Single-threaded loop → plain fields are safe.
    private TimeSpan _phaseTimingInterval = TimeSpan.Zero;
    private DateTime _nextPhaseLogTime = DateTime.MaxValue;
    private readonly bool _reconcileClamp; // §perf: cached once at startup (was a config walk per reconcile pass)
    private long _phCheckUs, _phCollectUs, _phBatchUs, _phAdvUs, _phArbUs, _phReconUs, _phMaintUs;
    // §perf-observability: the special cohorts (market-maker, rotator, jump) + the bracket drain run between the
    // arb pass and RecordTickLatency, so their cost feeds the scaler EWMA but was NOT in the BotPhase breakdown.
    // This bucket makes "did enabling the rotator/MM slow the loop?" directly observable (near-zero when all off).
    private long _phCohortsUs;
    private long _phPending, _phAdvCount, _phTicks;
    // §collect-split observability: break the aggregate collect span into the one-time shared-cache prepass,
    // the O(N) full-fleet burst/bookkeeping "pass", and the heavy per-due Compute*Async. pass is DERIVED at
    // log time (= _phCollectUs − pre − compute) so it needs no per-tick subtraction. Eligible/Due counts give
    // per-bot unit costs to project collect to a larger fleet. Timing-only ⇒ byte-identical; gated by the
    // same Bots:PhaseTimingSeconds as the rest of BotPhase.
    private long _phCollectPreUs, _phCollectComputeUs, _phCollectEligibleN, _phCollectDueN;
    // Window snapshot of the process-cumulative root-commit (fsync) counter, so the
    // BotPhase line can report commits/sec + round-trips/order — the commit-bound
    // metrics that transfer to prod (per-tick ms / cap are docker-skew artifacts).
    private long _cmPrevCommits;
    // Companion snapshot for the settled-trade counter → trades/sec on the BotPhase
    // line (the throughput signal; commits/sec falls by design under commit coalescing).
    private long _cmPrevTrades;

    private readonly AiBotContext         _ctx;
    private readonly AiBotStateService    _state;
    private readonly AiBotDecisionService _decisions;
    private readonly BotScalerService     _scaler;
    private readonly BotFailureTracker    _failures;
    private readonly BotStatsLogger       _stats;
    private readonly ReservationAuditor   _auditor;
    private readonly BotEconomyTelemetry  _economy;
    private readonly BotSentimentService  _sentiment;
    private readonly BotPriceMemoryService _priceMemory; // medium-term EWMA + long-term daily-TWAP anchors
    private readonly BotRegimeService     _regime;    // §A2/A3/A4 shared regime (default off)
    private readonly BotActivityService   _activity;  // §Pillar B activity field (default off)
    private readonly MarketMoodService    _mood;      // §fear-greed composite index (default off → v1 fallback)
    private readonly double               _moodGreedScale; // v1 fallback tanh gain (Bots:Mood:GreedScale)
    private readonly FundamentalService   _funds;     // §P6 slowly-drifting fundamentals
    private readonly ExogenousShockService _news;     // §exogenous-information news-shock bus (default off)
    private readonly StockProfileService  _profiles;  // §P6 per-stock personality
    private readonly BotCashInjector      _injector;
    private readonly ArbitrageDecisionService _arbitrage; // §3.7 cohort runs OUT of the normal path
    private readonly bool                 _arbitrageEnabled; // §3.7 Bots:Arbitrage:Enabled kill-switch
    private readonly MarketMakerDecisionService _marketMaker; // §mm-cohort all-weather two-sided maker (OUT of normal path)
    private readonly bool                 _marketMakerEnabled; // §mm-cohort Bots:MarketMaker:Enabled master gate (default off)
    private readonly BankEstimateService  _bank;              // §bank-estimate published fair-value estimate (feeds anchor + rotator)
    private readonly bool                 _bankEstimateEnabled; // §bank-estimate Bots:BankEstimate:Enabled master gate (default off)
    private readonly RotatorDecisionService _rotator;         // §rotator estimate-driven rotational cohort (OUT of normal path)
    private readonly bool                 _rotatorEnabled;    // §rotator Bots:Rotator:Enabled master gate (default off)
    private readonly ConvictionDecisionService _conviction;   // §conviction discretionary sentiment-momentum cohort (OUT of normal path)
    private readonly bool                 _convictionEnabled; // §conviction Bots:Conviction:Enabled master gate (default off)
    private readonly JumpService          _jump;               // §fat-tail jumps rare realized price-jump lever (OUT of normal path)
    private readonly bool                 _jumpEnabled;        // §fat-tail jumps Bots:Jumps:Enabled master gate (default off)
    private readonly int                  _jumpAggressorUserId; // §fat-tail jumps dedicated house aggressor account (warm-loaded)
    private readonly FxDeskTelemetry      _fxDesk;        // §3.7 session conversion data (reset on Start)
    private readonly int                  _houseUserId;   // §3.7 platform FX-desk account (warm-loaded)
    #endregion

    #region Services and Constructor
    private readonly IOrderExecutionService _marketOrders;
    private readonly IOrderEntryService     _entry;   // §P6: stop/trailing/bracket entry route (not the batch matcher)
    private readonly IMarketDataService     _market;
    private readonly IStockService          _stocks;
    private readonly ISectorMap             _sectorMap; // §sector: real stock→sector map for BankEstimate re-rating
    private readonly IAccountsCache         _accounts;
    private readonly IFxRateService         _fxRates;
    private readonly IBracketCoordinator    _bracket;   // Round 2 §0006c: end-of-tick queue drain
    private readonly ILogger<AiTradeService> _logger;
    private readonly IConfiguration         _configuration;
    private readonly int  _maxAdvancedPerTick;   // §P6a cap on entry-route submissions per tick
    private readonly bool _advancedEnabled;       // §P6a master switch (default off)
    private readonly bool _batchArms;             // §A1a batch the stop/trailing arm route (default off)
    private readonly bool _bracketBatch;          // Round 2 §0005 batch the bracket route (default off)
    private readonly bool _batchBuyStops;         // §A1b batch the buy-stop fund-reserve arm route (default off)
    private readonly bool _batchShortOpens;       // Slice 2 batch the flat market-short-open match+settle route (default off)
    private readonly bool _stopReplaceOld;        // §replace-old: cancel a bot's prior (stock,side) standalone armed stop before arming a new one (default off)
    private readonly double _smoothedPriceHalfLifeSec; // 0 ⇒ legacy fixed α=0.15 EWMA; >0 ⇒ time-based half-life
    // §impact-decouple A: the >1-min reaction reference EWMA (maintained in OnQuoteUpdated). Default off.
    private readonly bool   _reactionRef;
    private readonly double _reactionRefHalfLifeSec;
    // §stagger: phase-offset each bot's act cadence across ticks. Default off ⇒ byte-identical.
    private readonly bool _staggerEnabled;        // master switch (default off)
    private readonly int  _staggerSlots;          // tick-phase buckets = the per-tick load-cut factor N

    public AiTradeService(
        IOrderExecutionService marketOrders,
        IOrderEntryService entry,
        IMarketDataService market,
        IStockService stocks,
        ISectorMap sectorMap,
        IDataBaseService db,
        IBotMaintenanceQueries botMaint,
        IAccountsCache accounts,
        IReservationLedger ledger,
        IOrderBookEngine books,
        IUserPortfolioService portfolio,
        IFxRateService fxRates,
        FxDeskTelemetry fxDesk,
        IBracketCoordinator bracket,
        ILogger<AiTradeService> logger,
        ILoggerFactory loggerFactory,
        IOptions<SeparatorLoggerOptions> loggerOptions,
        IConfiguration configuration)
    {
        _marketOrders = marketOrders ?? throw new ArgumentNullException(nameof(marketOrders));
        _entry        = entry        ?? throw new ArgumentNullException(nameof(entry));
        _market       = market       ?? throw new ArgumentNullException(nameof(market));
        _stocks       = stocks       ?? throw new ArgumentNullException(nameof(stocks));
        _sectorMap    = sectorMap    ?? throw new ArgumentNullException(nameof(sectorMap));
        _accounts     = accounts     ?? throw new ArgumentNullException(nameof(accounts));
        _fxRates      = fxRates      ?? throw new ArgumentNullException(nameof(fxRates));
        _fxDesk       = fxDesk       ?? throw new ArgumentNullException(nameof(fxDesk));
        _bracket      = bracket      ?? throw new ArgumentNullException(nameof(bracket));
        _logger       = logger       ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        // Cash-injection cadence: was a hard-coded 1h const; now config (default 30m) so "more money" is a live dial.
        _cashInjectionInterval = TimeSpan.FromMinutes(Math.Max(1.0, _configuration.GetValue("Bots:CashInjection:IntervalMinutes", 30.0)));
        // §smoothed-price half-life: decouple bots from their OWN ~1-min price impact by perceiving a lagged
        // price. 0 ⇒ legacy fixed per-quote α=0.15 (byte-identical rollback). Targets the ret_acf_lag1 ceiling.
        _smoothedPriceHalfLifeSec = Math.Max(0.0, _configuration.GetValue("Bots:SmoothedPriceHalfLifeSec", 0.0));
        // §impact-decouple A/B: structural break of the 1-min self-impact reaction loop (the ret_acf_lag1
        // ceiling). Both default OFF ⇒ byte-identical. ImpactHoldProbe is a default-off liveliness counter for B.
        _reactionRef            = _configuration.GetValue("Bots:ImpactDecoupleReference", false);
        _reactionRefHalfLifeSec = Math.Max(0.0, _configuration.GetValue("Bots:ImpactDecoupleReferenceHalfLifeSec", 240.0));
        ImpactHoldProbe.Configure(_configuration.GetValue("Bots:ImpactHoldProbe", false));
        ChaserProbe.Configure(_configuration.GetValue("Bots:ExogShock:ChaserProbe", false));
        ArmedStopCapProbe.Configure(_configuration.GetValue("Bots:ArmedStopCapProbe", false));
        if (db          is null) throw new ArgumentNullException(nameof(db));
        if (ledger      is null) throw new ArgumentNullException(nameof(ledger));
        if (books       is null) throw new ArgumentNullException(nameof(books));
        if (portfolio   is null) throw new ArgumentNullException(nameof(portfolio));
        if (loggerFactory  is null) throw new ArgumentNullException(nameof(loggerFactory));
        if (loggerOptions  is null) throw new ArgumentNullException(nameof(loggerOptions));

        _sentimentLogInterval = TimeSpan.FromSeconds(
                        Math.Max(1.0, _configuration.GetValue("Bots:SentimentLogIntervalSeconds", 60.0)));
        _economyLogInterval = TimeSpan.FromSeconds(
                        Math.Max(1.0, _configuration.GetValue("Bots:EconomyLogIntervalSeconds", 60.0)));
        _phaseTimingInterval = TimeSpan.FromSeconds(
                        Math.Max(0.0, _configuration.GetValue("Bots:PhaseTimingSeconds", 0.0)));
        // §perf: cache the reconcile-clamp flag once (startup) instead of a config walk on every reconcile pass —
        // consistent with every other Bots flag (all startup-cached); loses only mid-run live reconfig of it.
        _reconcileClamp = _configuration.GetValue("Bots:ReconcileClamp", true);
        // §tick: config-settable loop period (default 0 ⇒ keep the 1s default ⇒ byte-identical). Prod uses a
        // shorter tick (e.g. 250ms) so staggered fills land per render frame; see docs/COUNCIL_DECISION_shorter_tick_fleet_split.md.
        var tradeIntervalMs = _configuration.GetValue("Bots:TradeIntervalMs", 0.0);
        if (tradeIntervalMs > 0.0) TradeInterval = TimeSpan.FromMilliseconds(tradeIntervalMs);
        // Engine commit counting rides the same opt-in switch: on only when phase timing
        // is enabled, so the default path stays byte-identical (no counting, no log line).
        EngineCommitMetrics.Configure(_phaseTimingInterval > TimeSpan.Zero);
        _ctx       = new AiBotContext(accounts,
                        personalSentiment: _configuration.GetValue("Bots:PersonalSentiment", true),
                        reactionRef:       _reactionRef);
        // §refill-throttle (Bots:RefillThrottle): the mover-response refill lever. Build the gate ONLY when
        // enabled, so the context holds a null gate by default and every call site is byte-identical and
        // draw-free when off. Bot-decision layer only (no engine change); CK-safe by construction.
        RefillThrottleProbe.Configure(_configuration.GetValue("Bots:RefillThrottle:Probe", false));
        // §composition taker override telemetry: auto-armed whenever the lever is on (the liveliness read is
        // the soak's "coupling actually fired" kill-check — an inert flag must be visible in the first minutes).
        ActivityCompositionProbe.Configure(
            _configuration.GetValue("Bots:Activity:Composition:TakerExp", 0.0) > 0.0);
        if (_configuration.GetValue("Bots:RefillThrottle:Enabled", false))
        {
            var rtSrcName = _configuration.GetValue("Bots:RefillThrottle:Signal:Source", "RealizedReturnFast");
            if (!Enum.TryParse<RefillThrottleGate.SignalSource>(rtSrcName, ignoreCase: true, out var rtSrc))
            {
                rtSrc = RefillThrottleGate.SignalSource.RealizedReturnFast;
                _logger.LogWarning("Bots:RefillThrottle:Signal:Source '{Src}' unrecognized — using RealizedReturnFast.", rtSrcName);
            }
            _ctx.RefillGate = new RefillThrottleGate(new RefillThrottleGate.Settings
            {
                Enabled            = true,
                Source             = rtSrc,
                ThresholdArm       = _configuration.GetValue("Bots:RefillThrottle:Signal:ThresholdArm", 0m),
                ThresholdDisarm    = _configuration.GetValue("Bots:RefillThrottle:Signal:ThresholdDisarm", 0m),
                MaxEventMovePct    = _configuration.GetValue("Bots:RefillThrottle:Control:MaxEventMovePct", 0m),
                RearmCooldownTicks = _configuration.GetValue("Bots:RefillThrottle:Control:RearmCooldownTicks", 0L),
                OffsetWidenMult    = _configuration.GetValue("Bots:RefillThrottle:OffsetWiden:Mult", 0m),
                SkipRepostProb     = _configuration.GetValue("Bots:RefillThrottle:SkipRepost:Prob", 0m),
            });
        }
        _stats     = new BotStatsLogger(new SeparatorLogger<BotStatsLogger>(loggerFactory, loggerOptions));
        _failures  = new BotFailureTracker(stocks, new SeparatorLogger<BotFailureTracker>(loggerFactory, loggerOptions));
        _auditor   = new ReservationAuditor(accounts, ledger, new SeparatorLogger<ReservationAuditor>(loggerFactory, loggerOptions),
                        phantomWarnThreshold: _configuration.GetValue("Bots:ReservationPhantomWarnThreshold", 5.0m));
        _houseUserId = _configuration.GetValue("Platform:HouseUserId", 20002);
        _economy   = new BotEconomyTelemetry(_ctx, accounts, fxRates,
                        new SeparatorLogger<BotEconomyTelemetry>(loggerFactory, loggerOptions),
                        houseUserId:     _houseUserId,
                        drainCeilingPct: _configuration.GetValue("Bots:Arbitrage:ValueDrainCeilingPct", 5.0m),
                        // §per-strategy telemetry: emit the BotStratPerf line each economy snapshot (default on).
                        strategyTelemetry: _configuration.GetValue("Bots:StrategyTelemetry:Enabled", true));
        // §P6 liveliness: per-stock personality + slowly-drifting fundamentals. Built before the
        // sentiment + decision services because both consume them.
        _profiles  = new StockProfileService(
                        enabled: _configuration.GetValue("Bots:Personality:Enabled", true));
        // §exogenous-information: the news-shock bus + its random source. Built before _funds and _decisions
        // because both consume it. Default-OFF ⇒ GetShock≡0 and Tick is a no-op ⇒ byte-identical.
        var exogEnabled        = _configuration.GetValue("Bots:ExogShock:Enabled", false);
        var exogCap            = _configuration.GetValue("Bots:ExogShock:Cap", 0.06);
        var exogChaserStrength = _configuration.GetValue("Bots:ExogShock:ChaserStrength", 0.0); // retired tilt — logged for back-compat audit only
        var exogChaserScale    = _configuration.GetValue("Bots:ExogShock:ChaserScale", 0.08);   // retired tilt — logged for back-compat audit only
        var exogChaserFraction = _configuration.GetValue("Bots:ExogShock:ChaserFraction", 0.0);
        // §direct-flow chaser dials: NotionalFrac is the primary ACF lever (0 ⇒ off, byte-identical),
        // MaxNotionalFrac is the per-order cap as a fraction of seed-price portfolio value.
        var exogChaserNotionalFrac    = _configuration.GetValue("Bots:ExogShock:ChaserNotionalFrac", 0.0);
        var exogChaserMaxNotionalFrac = _configuration.GetValue("Bots:ExogShock:ChaserMaxNotionalFrac", 0.02);
        // §chaser-v2 ratio-fix co-dials (default 0 ⇒ byte-identical) + per-bot chase cadence. SellSymFrac caps a
        // chase-SELL to the bot's own buy-ceiling (drift↓); BuyRoomRelaxFrac relaxes the chase-BUY room toward
        // cash-only (gross↑). MinIntervalSec → tick count HERE (using TradeInterval) so the decision path stays
        // wall-clock-free; 0 ⇒ off.
        var exogChaserSellSymFrac      = _configuration.GetValue("Bots:ExogShock:ChaserSellSymFrac", 0.0);
        var exogChaserBuyRoomRelaxFrac = _configuration.GetValue("Bots:ExogShock:ChaserBuyRoomRelaxFrac", 0.0);
        var exogChaserMinIntervalSec   = _configuration.GetValue("Bots:ExogShock:ChaserMinIntervalSec", 0.0);
        var exogChaserIntervalTicks    = exogChaserMinIntervalSec <= 0.0 ? 0
            : (int)Math.Ceiling(exogChaserMinIntervalSec / Math.Max(0.001, TradeInterval.TotalSeconds));
        // §global co-fire: on a MARKET-WIDE pulse, a cohort fires a SIMULTANEOUS same-sign taker burst spread across
        // all stocks (correlated flow the slow sentiment ring can't make). Needs GlobalFraction>0 for pulses to exist.
        // Default off ⇒ byte-identical.
        var exogGlobalCoFire             = _configuration.GetValue("Bots:ExogShock:GlobalCoFire", false);
        var exogGlobalCoFireFraction     = _configuration.GetValue("Bots:ExogShock:GlobalCoFireFraction", 0.0);
        var exogGlobalCoFireNotionalFrac = _configuration.GetValue("Bots:ExogShock:GlobalCoFireNotionalFrac", 0.0);
        // §sector pulse: a fraction of global pulses scope to ONE sector (stockId % SectorCount) ⇒ intra-sector corr.
        // SectorCount 1 OR SectorFraction 0 ⇒ every pulse market-wide ⇒ byte-identical.
        var exogSectorCount    = _configuration.GetValue("Bots:ExogShock:SectorCount", 1);
        var exogSectorFraction = _configuration.GetValue("Bots:ExogShock:SectorFraction", 0.0);
        var exogSource = new RandomShockSource(stocks,
                        meanIntervalMinutes: _configuration.GetValue("Bots:ExogShock:MeanIntervalMinutes", 3.0),
                        minMagnitude:        _configuration.GetValue("Bots:ExogShock:MinMagnitude", 0.01),
                        maxMagnitude:        _configuration.GetValue("Bots:ExogShock:MaxMagnitude", 0.06),
                        magnitudeExponent:   _configuration.GetValue("Bots:ExogShock:MagnitudeExponent", 1.8),
                        // §global-exog: fraction of shock arrivals that fire MARKET-WIDE (shared → cross-stock corr); 0 ⇒ per-stock-only.
                        globalFraction:      _configuration.GetValue("Bots:ExogShock:GlobalFraction", 0.0),
                        sectorCount:         exogSectorCount,
                        sectorFraction:      exogEnabled ? exogSectorFraction : 0.0);
        _news      = new ExogenousShockService(stocks, _profiles,
                        new SeparatorLogger<ExogenousShockService>(loggerFactory, loggerOptions), exogSource,
                        enabled:          exogEnabled,
                        decayHalfLifeSec: _configuration.GetValue("Bots:ExogShock:DecayHalfLifeSec", 300.0),
                        cap:              exogCap,
                        softWallK:        _configuration.GetValue("Bots:ExogShock:SoftWallK", 0.1),
                        difficultyMult:   _configuration.GetValue("Bots:ExogShock:DifficultyMult", 1.0));

        // Anti-runaway validation for AnchorTracksShock: the moving target must stay provably INTERIOR to the
        // hard overheat veto. Require Enabled, CapFromSeed=true (veto pinned to SEED while the soft target
        // moves), AbsoluteCapMax>0, and Band+Cap < AbsoluteCapMax. Unsafe ⇒ log error and refuse (don't enable).
        var exogAnchorTracks = _configuration.GetValue("Bots:ExogShock:AnchorTracksShock", false);
        var fundBand   = _configuration.GetValue("Bots:Fundamental:Band", 0.12m);
        var absCapMax  = _configuration.GetValue("Bots:ValueAnchor:AbsoluteCapMax", 0m);
        var capFromSeed = _configuration.GetValue("Bots:ValueAnchor:CapFromSeed", false);
        if (exogAnchorTracks)
        {
            bool safe = exogEnabled && capFromSeed && absCapMax > 0m && (fundBand + (decimal)exogCap) < absCapMax;
            if (!safe)
            {
                _logger.LogError("CONFIGCHECK ExogShock REFUSING AnchorTracksShock: needs Enabled && CapFromSeed " +
                    "&& AbsoluteCapMax>0 && Band+Cap<AbsoluteCapMax (Enabled={En} CapFromSeed={Cfs} Band={Band} " +
                    "Cap={Cap} AbsoluteCapMax={Abs}).", exogEnabled, capFromSeed, fundBand, exogCap, absCapMax);
                exogAnchorTracks = false;
            }
        }

        // §bank-estimate: the "house analyst" published fair-value estimate. Built AFTER _sentiment (below), but
        // the master gate is read here so the anchor pivot can be wired via a lazy field reference — null when off
        // ⇒ FundamentalService's reversion target stays the seed ⇒ byte-identical.
        _bankEstimateEnabled = _configuration.GetValue("Bots:BankEstimate:Enabled", false);
        _funds     = new FundamentalService(stocks, _profiles,
                        new SeparatorLogger<FundamentalService>(loggerFactory, loggerOptions),
                        enabled:          _configuration.GetValue("Bots:Fundamental:Enabled", true),
                        band:             fundBand,
                        theta:            _configuration.GetValue("Bots:Fundamental:Theta", 0.02),
                        sigma:            _configuration.GetValue("Bots:Fundamental:Sigma", 0.004),
                        driftIntervalSec: _configuration.GetValue("Bots:Fundamental:DriftIntervalSeconds", 60.0),
                        // §exogenous-information: anchor target tracks the news shock when validated-safe.
                        exogShock:        exogAnchorTracks ? (Func<int, double>)_news.GetShock : null,
                        anyShockActive:   exogAnchorTracks ? (Func<bool>)(() => _news.AnyActive) : null,
                        shockCap:         (decimal)exogCap,
                        // §co-movement: a SHARED market-factor shift composed onto each stock's anchor target
                        // (read-time) so all fundamentals co-move ⇒ the value-anchor pulls stocks together
                        // (cross-stock correlation). Lazily reads _sentiment (assigned just below) ⇒ null-safe;
                        // returns 0 when CoMovement is disabled ⇒ byte-identical.
                        coMoveShift:      sid => _sentiment?.CoMoveShift(sid) ?? 0.0,
                        coMoveShiftCap:   _configuration.GetValue("Bots:Sentiment:CoMovement:ShiftCap", 0.08m),
                        // §bank-estimate: pivot the OU reversion target to the bank estimate (lazy field ref, _bank
                        // assigned below). null when off ⇒ target stays the seed ⇒ byte-identical.
                        bankTarget:       _bankEstimateEnabled ? (Func<int, double>)(sid => _bank?.BankTarget(sid) ?? 0.0) : null);
        // Sentiment-dynamics §: the master flag gates BOTH the EWMA slope (here) and the directional phase
        // model (in AiBotDecisionService). Off ⇒ no slope compute and byte-identical decisions.
        var sentimentDynamics = _configuration.GetValue("Bots:SentimentDynamics:Enabled", false);
        // Price-memory anchors + hybrid pressure §: three independent flags, default OFF. The
        // first two gate the BotPriceMemoryService Tick body via anyConsumer — when both are off,
        // Tick short-circuits at the top and the service is observationally inert.
        var useDailyAnchor          = _configuration.GetValue("Bots:ValueAnchor:UsePreviousDayAverage", false);
        var recentAnchorEnabled     = _configuration.GetValue("Bots:RecentAnchor:Enabled", false);
        var adaptiveAnchorEnabled   = _configuration.GetValue("Bots:ValueAnchor:Adaptive:Enabled", false);
        var multiplicativeDirection = _configuration.GetValue("Bots:DirectionalPressure:Multiplicative", false);
        _priceMemory = new BotPriceMemoryService(stocks,
                        new SeparatorLogger<BotPriceMemoryService>(loggerFactory, loggerOptions),
                        priceLookup:    key => _ctx.SmoothedPrices.TryGetValue(key, out var p) ? p : 0m,
                        anyConsumer:    useDailyAnchor || recentAnchorEnabled || adaptiveAnchorEnabled,
                        halfLifeSec:    _configuration.GetValue("Bots:RecentAnchor:HalfLifeSec", 1800.0),
                        dayLengthHours: _configuration.GetValue("Bots:ValueAnchor:DayLengthHours", 24.0),
                        boundary:       ParseDayBoundary(_configuration.GetValue("Bots:ValueAnchor:DayBoundaryMode", "ServiceStart")),
                        maxDailyDrift:  _configuration.GetValue("Bots:ValueAnchor:MaxDailyDrift", 0.50m),
                        windowDays:     _configuration.GetValue("Bots:ValueAnchor:WindowDays", 1),
                        adaptiveEnabled:     adaptiveAnchorEnabled,
                        fastHalfLifeSec:     _configuration.GetValue("Bots:ValueAnchor:Adaptive:FastHalfLifeSec", 900.0),
                        adaptiveBlendWeight: _configuration.GetValue("Bots:ValueAnchor:Adaptive:BlendWeight", 0.5m),
                        maxTotalExcursion:   _configuration.GetValue("Bots:ValueAnchor:Adaptive:MaxTotalExcursion", 0.35m));
        _sentiment = new BotSentimentService(stocks, _profiles, new SeparatorLogger<BotSentimentService>(loggerFactory, loggerOptions),
                        newsEvents:              _configuration.GetValue("Bots:NewsEvents", true),
                        shockMeanIntervalHours:  _configuration.GetValue("Bots:ShockMeanIntervalHours", 6.0),
                        shockMinMagnitude:       _configuration.GetValue("Bots:ShockMinMagnitude", 0.3m),
                        shockMaxMagnitude:       _configuration.GetValue("Bots:ShockMaxMagnitude", 1.5m),
                        shockMagnitudeExponent:  _configuration.GetValue("Bots:ShockMagnitudeExponent", 3.0),
                        shockDecayPerTick:       _configuration.GetValue("Bots:ShockDecayPerTick", 0.999m),
                        slopeEnabled:            sentimentDynamics,
                        slopeTauFastSec:         _configuration.GetValue("Bots:SentimentDynamics:SlopeTauFastSec", 45.0),
                        slopeTauSlowSec:         _configuration.GetValue("Bots:SentimentDynamics:SlopeTauSlowSec", 180.0),
                        // §price-reaction (#2): contrarian sentiment feedback on sustained moves. Default-off.
                        recentReturn:            RecentReturnForActivity,
                        priceReaction:           _configuration.GetValue("Bots:Sentiment:PriceReaction", false),
                        reactStrength:           _configuration.GetValue("Bots:Sentiment:ReactStrength", 6.0),
                        reactTauSec:             _configuration.GetValue("Bots:Sentiment:ReactTauSec", 300.0),
                        reactDeadband:           _configuration.GetValue("Bots:Sentiment:ReactDeadband", 0.01),
                        reactCap:                _configuration.GetValue("Bots:Sentiment:ReactCap", 0.40),
                        // #3 waves: fast positive-feedback (momentum) term. MomStrength 0 ⇒ off.
                        momStrength:             _configuration.GetValue("Bots:Sentiment:MomStrength", 0.0),
                        momTauSec:               _configuration.GetValue("Bots:Sentiment:MomTauSec", 60.0),
                        momCap:                  _configuration.GetValue("Bots:Sentiment:MomCap", 0.25),
                        // §slow-ring damp: scale the slow per-stock OU rings to attack linear drift. 1.0 ⇒ off.
                        slowRingDamp:            _configuration.GetValue("Bots:Sentiment:SlowRingDamp", 1.0),
                        // §System A: persistent common-mode bounded random-walk regime driver. Enabled=false ⇒ off.
                        regimeEnabled:           _configuration.GetValue("Bots:Sentiment:RegimeDrift:Enabled", false),
                        regimeStepSigma:         _configuration.GetValue("Bots:Sentiment:RegimeDrift:StepSigma", 0.03),
                        regimeCap:               _configuration.GetValue("Bots:Sentiment:RegimeDrift:Cap", 0.5),
                        regimeSoftWallK:         _configuration.GetValue("Bots:Sentiment:RegimeDrift:SoftWallK", 0.1),
                        regimeStrength:          _configuration.GetValue("Bots:Sentiment:RegimeDrift:Strength", 1.0),
                        // §co-movement: one SHARED market-factor walk + per-stock beta dispersion ⇒ cross-stock
                        // co-movement (corr ~0 today). Default off ⇒ byte-identical. Sibling of RegimeDrift.
                        coMoveEnabled:           _configuration.GetValue("Bots:Sentiment:CoMovement:Enabled", false),
                        coMoveStepSigma:         _configuration.GetValue("Bots:Sentiment:CoMovement:StepSigma", 0.03),
                        coMoveCap:               _configuration.GetValue("Bots:Sentiment:CoMovement:Cap", 0.4),
                        coMoveSoftWallK:         _configuration.GetValue("Bots:Sentiment:CoMovement:SoftWallK", 0.1),
                        coMoveStrength:          _configuration.GetValue("Bots:Sentiment:CoMovement:Strength", 0.5),
                        coMoveBetaSpread:        _configuration.GetValue("Bots:Sentiment:CoMovement:BetaSpread", 0.4),
                        // §impact-decouple A: wire the >1-min-decoupled return ONLY when the flag is on; null ⇒
                        // the price-reaction term uses the legacy ~1s return ⇒ byte-identical.
                        reactionReturn:          _reactionRef ? ReactionReturnForSentiment : (Func<int, double>?)null,
                        // §global-shock: discrete market-wide BEARISH sentiment event ⇒ correlated crash (elevator-down)
                        // that turns the whole fleet bearish at once (⇒ fleet-wide bear-short). Enabled=false ⇒
                        // byte-identical (dedicated RNG, folds an exact 0.0 into every stock).
                        globalShockEnabled:           _configuration.GetValue("Bots:Sentiment:GlobalShock:Enabled", false),
                        globalShockMeanIntervalHours: _configuration.GetValue("Bots:Sentiment:GlobalShock:MeanIntervalHours", 3.0),
                        globalShockMinMagnitude:      _configuration.GetValue("Bots:Sentiment:GlobalShock:MinMagnitude", 0.3m),
                        globalShockMaxMagnitude:      _configuration.GetValue("Bots:Sentiment:GlobalShock:MaxMagnitude", 1.5m),
                        globalShockMagnitudeExponent: _configuration.GetValue("Bots:Sentiment:GlobalShock:MagnitudeExponent", 3.0),
                        globalShockDecayPerTick:      _configuration.GetValue("Bots:Sentiment:GlobalShock:DecayPerTick", 0.999m),
                        globalShockDownBias:          _configuration.GetValue("Bots:Sentiment:GlobalShock:DownBias", 0.85),
                        // §correlation lever: scale per-stock (down) + global (up) ring σ ⇒ shared common-mode dominates.
                        perStockSigmaMult:            _configuration.GetValue("Bots:Sentiment:PerStockSigmaMult", 1.0),
                        globalSigmaMult:              _configuration.GetValue("Bots:Sentiment:GlobalSigmaMult", 1.0));
        // §bank-estimate: build the published-estimate state machine now that _sentiment exists (it zero-means the
        // per-stock sentiment). Folds live news via the existing shock delegate. Default OFF ⇒ Tick no-op / target 0.
        _bank      = new BankEstimateService(stocks, _profiles, _sentiment,
                        new SeparatorLogger<BankEstimateService>(loggerFactory, loggerOptions),
                        enabled:                _bankEstimateEnabled,
                        alpha:                  _configuration.GetValue("Bots:BankEstimate:Alpha", 0.3),
                        poissonMeanIntervalSec: _configuration.GetValue("Bots:BankEstimate:PoissonMeanIntervalSec", 30.0),
                        wrongnessFraction:      _configuration.GetValue("Bots:BankEstimate:WrongnessFraction", 0.15),
                        // §sector: real map supersedes the modulo count when sectors are seeded; the config value is
                        // kept as the fallback modulo count (byte-identical when HasRealSectors is false).
                        sectorMap:              _sectorMap,
                        sectorCount:            _configuration.GetValue("Bots:BankEstimate:SectorCount", exogSectorCount),
                        exogShock:              exogEnabled ? (Func<int, double>)_news.GetShock : null,
                        // §sector A/B rollback: false forces the config-modulo path even with sectors seeded.
                        useRealSectors:         _configuration.GetValue("Bots:BankEstimate:UseRealSectors", true),
                        // §soak: publish an estimate for EVERY stock on the first tick (default off = prod unchanged).
                        seedAllOnStart:         _configuration.GetValue("Bots:BankEstimate:SeedAllOnStart", false),
                        sectorDriftCap:         _configuration.GetValue("Bots:BankEstimate:SectorDriftCap", 0.03),
                        sectorStepScale:        _configuration.GetValue("Bots:BankEstimate:SectorStepScale", 1.0),
                        sectorEventProb:        _configuration.GetValue("Bots:BankEstimate:SectorEventProb", 0.0),
                        sectorEventMult:        _configuration.GetValue("Bots:BankEstimate:SectorEventMult", 10.0),
                        sectorEventDownBias:    _configuration.GetValue("Bots:BankEstimate:SectorEventDownBias", 0.7));
        // §v2 emergent-correlation pillars (all default off / inert). The regime ticks only when at least one
        // of its consumers is enabled; the activity field is inert (every factor ≡ 1) until Bots:Activity:Enabled.
        _regime    = new BotRegimeService(new SeparatorLogger<BotRegimeService>(loggerFactory, loggerOptions),
                        enabled: _configuration.GetValue("Bots:Imbalance:Herding", false)
                              || _configuration.GetValue("Bots:Imbalance:MomentumDominance", false)
                              || _configuration.GetValue("Bots:Imbalance:RoleSplit", false),
                        regimeMeanSec: _configuration.GetValue("Bots:Imbalance:Herding:RegimeMeanSec", 960.0));
        _activity  = new BotActivityService(stocks, _sentiment,
                        new SeparatorLogger<BotActivityService>(loggerFactory, loggerOptions),
                        RecentReturnForActivity,
                        enabled:          _configuration.GetValue("Bots:Activity:Enabled", false),
                        activityBaseline: _configuration.GetValue("Bots:Activity:Baseline", 0.6),
                        globalTauSec:     _configuration.GetValue("Bots:Activity:GlobalTauSec", 3600.0),
                        globalSigma:      _configuration.GetValue("Bots:Activity:GlobalSigma", 0.20),
                        perStockTauSec:   _configuration.GetValue("Bots:Activity:PerStockTauSec", 600.0),
                        perStockSigma:    _configuration.GetValue("Bots:Activity:PerStockSigma", 0.30),
                        floor:            _configuration.GetValue("Bots:Activity:Floor", 0.2),
                        sMax:             _configuration.GetValue("Bots:Activity:SMax", 6.0),
                        wNews:            _configuration.GetValue("Bots:Activity:WNews", 0.6),
                        wMoveUp:          _configuration.GetValue("Bots:Activity:WMoveUp", 1.0),
                        wMoveDown:        _configuration.GetValue("Bots:Activity:WMoveDown", 2.0),
                        wSent:            _configuration.GetValue("Bots:Activity:WSent", 0.3),
                        theta:            _configuration.GetValue("Bots:Activity:Theta", 0.3),
                        wSelf:            _configuration.GetValue("Bots:Activity:WSelf", 0.009),
                        decay:            _configuration.GetValue("Bots:Activity:Decay", 0.99),
                        bDriftAmp:        _configuration.GetValue("Bots:Activity:BDriftAmp", 0.15),
                        compGExp:         _configuration.GetValue("Bots:Activity:Composition:GExp", 0.5),
                        compFloor:        _configuration.GetValue("Bots:Activity:Composition:Floor", 0.4),
                        compCap:          _configuration.GetValue("Bots:Activity:Composition:Cap", 3.0));
        // §fear-greed: composite Fear/Greed index (fast layer of the one-axis-three-timescales model). Read-only
        // projection; default-off so the live gauge keeps the v1 sentiment×activity fallback until it is wired +
        // soak-validated. Momentum-dominant weights; sentiment demoted to a small slow anchor.
        _moodGreedScale = _configuration.GetValue("Bots:Mood:GreedScale", 1.2);
        _mood      = new MarketMoodService(stocks.ById.Keys,
                        enabled:           _configuration.GetValue("Bots:Mood:Enabled", false),
                        weights:           new MoodWeights(
                            Mom:     _configuration.GetValue("Bots:Mood:WMom", 0.9),
                            Breadth: _configuration.GetValue("Bots:Mood:WBreadth", 0.35),
                            Vol:     _configuration.GetValue("Bots:Mood:WVol", 0.2),
                            Flow:    _configuration.GetValue("Bots:Mood:WFlow", 0.15),
                            Sent:    _configuration.GetValue("Bots:Mood:WSent", 0.2)),
                        anchorTauSec:      _configuration.GetValue("Bots:Mood:AnchorTauSec", 600.0),
                        volTauSec:         _configuration.GetValue("Bots:Mood:VolTauSec", 60.0),
                        volBaselineTauSec: _configuration.GetValue("Bots:Mood:VolBaselineTauSec", 900.0),
                        flowTauSec:        _configuration.GetValue("Bots:Mood:FlowTauSec", 300.0),
                        smoothTauSec:      _configuration.GetValue("Bots:Mood:SmoothTauSec", 60.0));
        _injector  = new BotCashInjector(_ctx, portfolio, _economy,
                        new SeparatorLogger<BotCashInjector>(loggerFactory, loggerOptions));
        // §3.7 arbitrage cohort: dedicated decision path, fully outside the sentiment/anchor/veto/
        // injection flow. Reuses the engine market-order route + the platform FX desk.
        _arbitrageEnabled = _configuration.GetValue("Bots:Arbitrage:Enabled", true);
        _arbitrage = new ArbitrageDecisionService(entry, books, accounts, fxRates, portfolio, stocks, _economy,
                        new SeparatorLogger<ArbitrageDecisionService>(loggerFactory, loggerOptions),
                        conversionSkewBand: _configuration.GetValue("Bots:Arbitrage:ConversionSkewBand", 0.15m),
                        // STRETCH (unbaked): batch the arb cohort's round-trip legs into 2 passes/tick.
                        batchLegs: _configuration.GetValue("Bots:Arbitrage:BatchLegs", false),
                        // §arb-scan Phase 2a (unbaked): share the cross-listed gap scan across the cohort.
                        sharedScan: _configuration.GetValue("Bots:Arbitrage:SharedScan", false));
        // §mm-cohort: all-weather two-sided resting-liquidity cohort (AiStrategy.MarketMakerHouse). Dedicated
        // decision path, fully outside the normal sentiment/anchor/veto/injection flow. Default OFF + (with no
        // strategy-6 bots seeded) byte-identical. Posts limit quotes around a one-sided-book-surviving reference.
        _marketMakerEnabled = _configuration.GetValue("Bots:MarketMaker:Enabled", false);
        MarketMakerProbe.Configure(_configuration.GetValue("Bots:MarketMaker:Probe", false));
        var mmCfg = new MmConfig(
            Enabled:             _marketMakerEnabled,
            HalfSpreadBps:       _configuration.GetValue("Bots:MarketMaker:HalfSpreadBps", 15m),
            QuoteSize:           _configuration.GetValue("Bots:MarketMaker:QuoteSize", 5),
            SkewBps:             _configuration.GetValue("Bots:MarketMaker:SkewBps", 20m),
            RequoteThresholdBps: _configuration.GetValue("Bots:MarketMaker:RequoteThresholdBps", 5m),
            MaxCashFrac:         _configuration.GetValue("Bots:MarketMaker:MaxCashFrac", 0.5m),
            PriceJitterBps:      _configuration.GetValue("Bots:MarketMaker:PriceJitterBps", 2m),
            OneSidedWidenMult:   _configuration.GetValue("Bots:MarketMaker:OneSidedWidenMult", 2.0m),
            UseMicro:            _configuration.GetValue("Bots:MarketMaker:UseMicro", false));
        _marketMaker = new MarketMakerDecisionService(entry, books, accounts, stocks,
                        new SeparatorLogger<MarketMakerDecisionService>(loggerFactory, loggerOptions), mmCfg);
        // §rotator: the estimate-driven rotational cohort (AiStrategy.Rotator). Dedicated decision path OUT of the
        // normal flow; reads the bank estimate + rotates capital via batched market orders. Default OFF + (with no
        // strategy-7 bots seeded) byte-identical. ParticipationFraction is the runtime correlation/flow valve.
        _rotatorEnabled = _configuration.GetValue("Bots:Rotator:Enabled", false);
        // §rotator: the scaler must exist before the rotator (which reads its load for scaler-coupled participation).
        _scaler    = new BotScalerService(new SeparatorLogger<BotScalerService>(loggerFactory, loggerOptions));
        // §B scaler control-loop levers (Bots:Scaler:*). All default-off ⇒ loadFrac == ewma/interval,
        // byte-identical. The tick-≤-interval guard defaults to 1.0 (inert) but auto-lowers to 0.95 when
        // the denominator correction is on, so enabling §B-1a can never crank the cap up unguarded.
        var correctDenom = _configuration.GetValue("Bots:Scaler:DutyCycleDenominator", false);
        _scaler.CorrectDutyCycleDenominator = correctDenom;
        _scaler.SizeFromActionableSpan      = _configuration.GetValue("Bots:Scaler:ActionableSpanSizing", false);
        _scaler.TickGuardFraction           = Math.Clamp(
                        _configuration.GetValue("Bots:Scaler:TickGuardFraction", correctDenom ? 0.95 : 1.0), 0.5, 1.0);
        // §R2-1: MaxTickMultiple is the operator-facing tick-cadence ceiling for the corrected path. It engages
        // ONLY with DutyCycleDenominator on (off ⇒ ignored ⇒ byte-identical). When on and the operator hasn't
        // set it, default k=1.0 (work ≤ interval, guard 0.5) — this supersedes the inert 0.95 auto-guard above
        // and honours the tick-≤-interval gate out of the box; raise k for a coarser tick / higher throughput.
        // ApplyMaxTickMultiple recenters the whole band on k, so it overrides the TickGuardFraction set above.
        if (correctDenom)
            _scaler.ApplyMaxTickMultiple(_configuration.GetValue("Bots:Scaler:MaxTickMultiple", 1.0));
        _selfCorrectingDelay = _configuration.GetValue("Bots:Scaler:SelfCorrectingDelay", false);
        _rotator = new RotatorDecisionService(entry, accounts, stocks, _bank, _sentiment, _economy, _scaler,
                        new SeparatorLogger<RotatorDecisionService>(loggerFactory, loggerOptions),
                        participationFraction: _configuration.GetValue("Bots:Rotator:ParticipationFraction", 0.10),
                        participationFloor:    _configuration.GetValue("Bots:Rotator:ParticipationFloor", 0.02),
                        turnoverFraction:      _configuration.GetValue("Bots:Rotator:TurnoverFraction", 0.10),
                        seedBalanceUsd:        _configuration.GetValue("Bots:Rotator:SeedBalanceUsd", 1_000_000m),
                        seedBalanceEur:        _configuration.GetValue("Bots:Rotator:SeedBalanceEur", 900_000m),
                        useLoadEwma:           _configuration.GetValue("Bots:Rotator:UseLoadEwma", false));
        // §conviction: the discretionary sentiment/sector-momentum cohort (AiStrategy.Conviction). Dedicated decision
        // path OUT of the normal flow; places AGGRESSIVE TAKER orders so the value-anchor can't absorb them. Default
        // OFF + (with no strategy-8 bots seeded) byte-identical. Reads the bank estimate only as a guardrail veto.
        _convictionEnabled = _configuration.GetValue("Bots:Conviction:Enabled", false);
        _conviction = new ConvictionDecisionService(entry, accounts, stocks, sectorMap, _bank, _sentiment, _economy, _scaler,
                        new SeparatorLogger<ConvictionDecisionService>(loggerFactory, loggerOptions),
                        wSec:               _configuration.GetValue("Bots:Conviction:Wsec", 1.0),
                        wMom:               _configuration.GetValue("Bots:Conviction:Wmom", 0.5),
                        wGlobal:            _configuration.GetValue("Bots:Conviction:Wglobal", 0.3),
                        wIdio:              _configuration.GetValue("Bots:Conviction:Widio", 0.2),
                        wOver:              _configuration.GetValue("Bots:Conviction:Wover", 0.5),
                        convictionBarBase:  _configuration.GetValue("Bots:Conviction:ConvictionBarBase", 0.03),
                        exitBar:            _configuration.GetValue("Bots:Conviction:ExitBar", 0.0),
                        stopOvervaluation:  _configuration.GetValue("Bots:Conviction:StopOvervaluation", 0.10),
                        cashFloorBase:      _configuration.GetValue("Bots:Conviction:CashFloorBase", 0.55),
                        riskAppetiteBase:   _configuration.GetValue("Bots:Conviction:RiskAppetiteBase", 0.05),
                        checkInMeanSecBase: _configuration.GetValue("Bots:Conviction:CheckInMeanSecBase", 1200.0),
                        seedBalanceUsd:     _configuration.GetValue("Bots:Conviction:SeedBalanceUsd", 200_000m),
                        seedBalanceEur:     _configuration.GetValue("Bots:Conviction:SeedBalanceEur", 180_000m),
                        useLoadEwma:        _configuration.GetValue("Bots:Conviction:UseLoadEwma", false),
                        holdHorizonEnabled: _configuration.GetValue("Bots:Conviction:HoldHorizonEnabled", false),
                        holdMinSec:         _configuration.GetValue("Bots:Conviction:HoldMinSec", 1800.0),
                        holdMaxSec:         _configuration.GetValue("Bots:Conviction:HoldMaxSec", 172_800.0),
                        convictionSizingEnabled: _configuration.GetValue("Bots:Conviction:ConvictionSizingEnabled", false),
                        convScale:          _configuration.GetValue("Bots:Conviction:ConvScale", 0.12),
                        maxDeploy:          _configuration.GetValue("Bots:Conviction:MaxDeploy", 0.90),
                        sizingGamma:        _configuration.GetValue("Bots:Conviction:SizingGamma", 3.0),
                        shortingEnabled:    _configuration.GetValue("Bots:Conviction:ShortingEnabled", false),
                        shortBar:           _configuration.GetValue("Bots:Conviction:ShortBar", 0.06),
                        shortRiskFraction:  _configuration.GetValue("Bots:Conviction:ShortRiskFraction", 0.15),
                        signedHotEnabled:   _configuration.GetValue("Bots:Conviction:SignedHotEnabled", false),
                        wGap:               _configuration.GetValue("Bots:Conviction:Wgap", 1.0),
                        wOwn:               _configuration.GetValue("Bots:Conviction:Wown", 0.1),
                        wNoise:             _configuration.GetValue("Bots:Conviction:Wnoise", 0.2),
                        reviewMeanSec:      _configuration.GetValue("Bots:Conviction:ReviewMeanSec", 300.0),
                        exitBaseHazard:     _configuration.GetValue("Bots:Conviction:ExitBaseHazard", 0.02),
                        exitFlipGain:       _configuration.GetValue("Bots:Conviction:ExitFlipGain", 2.0),
                        exitSatisfyGain:    _configuration.GetValue("Bots:Conviction:ExitSatisfyGain", 0.15),
                        exitTimeExp:        _configuration.GetValue("Bots:Conviction:ExitTimeExp", 2.5),
                        satisfiedBand:      _configuration.GetValue("Bots:Conviction:SatisfiedBand", 0.02),
                        minHoldSec:         _configuration.GetValue("Bots:Conviction:MinHoldSec", 120.0),
                        maxExitFractionPerPass: _configuration.GetValue("Bots:Conviction:MaxExitFractionPerPass", 0.10),
                        shortBarMult:       _configuration.GetValue("Bots:Conviction:ShortBarMult", 1.2),
                        maxEntriesPerFire:  _configuration.GetValue("Bots:Conviction:MaxEntriesPerFire", 1));
        // §fat-tail jumps: a RARE per-stock Poisson price JUMP realized via REAL marketable orders from a
        // dedicated house aggressor (CK=0), self-bounded per event so it momentarily exceeds the per-tick band,
        // then mean-reverts against the un-moved anchor + AbsoluteCapMax. Runs OUT of the normal sentiment/
        // anchor/veto/injection flow (like the MM/arbitrage cohorts). Default OFF ⇒ no RNG drawn, byte-identical.
        _jumpEnabled         = _configuration.GetValue("Bots:Jumps:Enabled", false);
        JumpsProbe.Configure(_configuration.GetValue("Bots:Jumps:Probe", false));
        _jumpAggressorUserId = _configuration.GetValue("Bots:Jumps:AggressorUserId", 20008);
        var jumpSource = new RandomJumpSource(stocks,
                        meanIntervalHours: _configuration.GetValue("Bots:Jumps:MeanIntervalHours", 2.0),
                        minPct:            _configuration.GetValue("Bots:Jumps:MinPct", 0.02),
                        maxPct:            _configuration.GetValue("Bots:Jumps:MaxPct", 0.05),
                        magnitudeExponent: _configuration.GetValue("Bots:Jumps:MagnitudeExponent", 1.5));
        _jump = new JumpService(entry, books, accounts, stocks,
                        new SeparatorLogger<JumpService>(loggerFactory, loggerOptions), jumpSource,
                        enabled:           _jumpEnabled,
                        aggressorUserId:   _jumpAggressorUserId,
                        maxSlices:         _configuration.GetValue("Bots:Jumps:MaxSlices", 6),
                        slippagePct:       _configuration.GetValue("Bots:Jumps:SlippagePct", 12.0m),
                        aftershockBuckets: _configuration.GetValue("Bots:Jumps:AftershockBuckets", 4),
                        aftershockDecay:   _configuration.GetValue("Bots:Jumps:AftershockDecay", 0.5),
                        driftGuardPct:     _configuration.GetValue("Bots:Jumps:DriftGuardPct", 0.10));
        // §source-cap (Bots:MaxArmedStopsPerBot): bound the per-bot armed-stop pool at placement. Shared by the
        // state service (the +1 increment) and the decision service (the reject gate). Requires LeanReload for
        // exact counting, and StopMaxAgeSec=0 (the aged cull removes armed stops without decrementing the count).
        var leanReload          = _configuration.GetValue("Bots:LeanReload", false);
        var maxArmedStopsPerBot = _configuration.GetValue("Bots:MaxArmedStopsPerBot", 0);
        if (maxArmedStopsPerBot > 0 && !leanReload)
            _logger.LogWarning("§source-cap: Bots:MaxArmedStopsPerBot={Cap} requires Bots:LeanReload=true for an " +
                "exact per-bot count; degrading to a reload-granular OpenOrders scan.", maxArmedStopsPerBot);
        if (maxArmedStopsPerBot > 0 && _configuration.GetValue("Bots:StopMaxAgeSec", 0) > 0)
            _logger.LogWarning("§source-cap: Bots:MaxArmedStopsPerBot={Cap} with Bots:StopMaxAgeSec>0 — the aged-stop " +
                "cull removes armed stops without decrementing the count, biasing the cap high. Set StopMaxAgeSec=0.",
                maxArmedStopsPerBot);
        _state     = new AiBotStateService(db, accounts, marketOrders, _stats,
                        new SeparatorLogger<AiBotStateService>(loggerFactory, loggerOptions),
                        botMaint,
                        distanceMult: _configuration.GetValue("Bots:DecisionDistanceMult", 1m),
                        // Realism §: age-based resting-limit-order expiry. 0 = off (default, byte-identical).
                        // When > 0, the prune sweep cancels open limit orders older than a per-order
                        // randomized [0.5x, 1.5x] of this lifetime, so the book churns like a real
                        // market instead of accumulating resting orders without bound.
                        orderMaxAgeSec: _configuration.GetValue("Bots:OrderMaxAgeSec", 0),
                        // §stop-ttl (INTERIM): standalone armed stops have no TTL and pile up unbounded, bloating
                        // the O(book) prune scan (the maint-phase blowup). >0 gives them a per-order jittered
                        // lifetime, cancelled via the safe per-order path; StopCullMaxPerSweep caps the per-sweep
                        // drain so a large backlog can't mass-cancel in one tick. 0 = off.
                        stopMaxAgeSec: _configuration.GetValue("Bots:StopMaxAgeSec", 0),
                        stopCullMaxPerSweep: _configuration.GetValue("Bots:StopCullMaxPerSweep", 500),
                        // §B2 (Workstream 1): make PruneWorstOrdersAsync iterate a limit-only index so the
                        // ~30s maint scan is O(limits), independent of the armed-stop pool. Default off ⇒
                        // prune reads full OpenOrders, byte-identical. Supersedes the StopMaxAgeSec interim.
                        pruneLimitOnly: _configuration.GetValue("Bots:PruneLimitOnly", false),
                        // §B3 (Workstream 1b): lean reload — RefreshAssetsAsync fetches only open limits + a
                        // per-bot armed-stop COUNT (not the ~1.18M armed-stop Orders), so the ~60s reload is
                        // O(limits) not O(pool). Default off ⇒ full hydration, byte-identical. Assumes
                        // Bots:StopMaxAgeSec=0 (stop-ttl retired).
                        leanReload: leanReload,
                        // §source-cap: the +1 increment (NoteArmedStopPlaced) is gated on this being > 0.
                        maxArmedStopsPerBot: maxArmedStopsPerBot);
        _decisions = new AiBotDecisionService(market, accounts, books, stocks, _sentiment, _funds, _profiles,
                        _regime, _activity, _priceMemory,
                        new SeparatorLogger<AiBotDecisionService>(loggerFactory, loggerOptions),
                        fatTails:           _configuration.GetValue("Bots:FatTails", true),
                        tradeSizeTailShape: _configuration.GetValue("Bots:TradeSizeTailShape", 0.5m),
                        blockTradeProb:     _configuration.GetValue("Bots:BlockTradeProb", 0.01m),
                        blockTradeMultiple: _configuration.GetValue("Bots:BlockTradeMultiple", 4m),
                        mmQuoting:          _configuration.GetValue("Bots:MarketMakerQuoting", true),
                        quoteHalfSpreadPrc: _configuration.GetValue("Bots:QuoteHalfSpreadPrc", 0.003m),
                        limitOffsetMult:    _configuration.GetValue("Bots:Liquidity:OffsetMult", 1m),
                        distanceMult:       _configuration.GetValue("Bots:DecisionDistanceMult", 1m),
                        marketProbMult:     _configuration.GetValue("Bots:MarketProbMult", 1m),
                        maxOpenOrdersMult:  _configuration.GetValue("Bots:Liquidity:MaxOpenMult", 1m),
                        valueAnchorStrength: _configuration.GetValue("Bots:ValueAnchor:Strength", 0m),
                        valueAnchorScale:    _configuration.GetValue("Bots:ValueAnchor:Scale", 0.15m),
                        anchorElastic:         _configuration.GetValue("Bots:ValueAnchor:Elastic", false),
                        anchorElasticDeadband: _configuration.GetValue("Bots:ValueAnchor:ElasticDeadbandPrc", 0.20m),
                        anchorElasticPower:    _configuration.GetValue("Bots:ValueAnchor:ElasticPower", 3.0m),
                        // §trend-follower (chartist) cohort. Enabled=false / fraction 0 ⇒ byte-identical.
                        trendFollowerEnabled:     _configuration.GetValue("Bots:TrendFollower:Enabled", false),
                        trendCohortFraction:      _configuration.GetValue("Bots:TrendFollower:CohortFraction", 0m),
                        trendStrength:            _configuration.GetValue("Bots:TrendFollower:Strength", 0m),
                        trendContrarianFraction:  _configuration.GetValue("Bots:TrendFollower:ContrarianFraction", 0.2m),
                        trendTakerCoupling:       _configuration.GetValue("Bots:TrendFollower:TakerCoupling", false),
                        trendTakerThreshold:      _configuration.GetValue("Bots:TrendFollower:TakerThreshold", 0.05m),
                        trendSharedChaseWeight:   _configuration.GetValue("Bots:TrendFollower:SharedChaseWeight", 0m),
                        dipBuyStrength:      _configuration.GetValue("Bots:DipBuyStrength", 0m),
                        valueTargetSelection: _configuration.GetValue("Bots:ValueAnchor:TargetSelection", false),
                        overheatCap:         _configuration.GetValue("Bots:ValueAnchor:OverheatCap", 0m),
                        absoluteCapMax:      _configuration.GetValue("Bots:ValueAnchor:AbsoluteCapMax", 0m),
                        geometricBand:       _configuration.GetValue("Bots:GeometricBand", false),
                        marketSlippagePrc:   _configuration.GetValue("Bots:MarketSlippagePrc", 0.003m),
                        // §P6 balancing: tier-selection probs, stop-fire slippage cap, anti-sweep depth fraction.
                        tierCloseProb:       _configuration.GetValue("Bots:Tiers:CloseProb", 0.6m),
                        tierMidProb:         _configuration.GetValue("Bots:Tiers:MidProb", 0.3m),
                        stopSlippagePct:     _configuration.GetValue("Bots:Advanced:StopSlippagePct", 0.3m),
                        maxSweepFractionOfDepth: _configuration.GetValue("Bots:Liquidity:MaxSweepFractionOfDepth", 0.25m),
                        // §P6: advanced-order generation. Master on/off is config; the per-kind probabilities
                        // are PER-BOT (AIUser.*Prob, seeded by strategy in Tools/Person.py). Offsets + caps
                        // remain global config. When disabled, the seeded plain-order stream is byte-identical.
                        advancedEnabled:    _configuration.GetValue("Bots:Advanced:Enabled", false),
                        stopOffsetMin:      _configuration.GetValue("Bots:Advanced:StopOffsetPrcMin", 0.02m),
                        stopOffsetMax:      _configuration.GetValue("Bots:Advanced:StopOffsetPrcMax", 0.05m),
                        tpOffsetMin:        _configuration.GetValue("Bots:Advanced:TpOffsetPrcMin", 0.03m),
                        tpOffsetMax:        _configuration.GetValue("Bots:Advanced:TpOffsetPrcMax", 0.08m),
                        bracketSlippagePct: _configuration.GetValue("Bots:Advanced:BracketSlippagePct", 5m),
                        advancedMaxQty:     _configuration.GetValue("Bots:Advanced:MaxQty", 50),
                        // §v2 imbalance/activity/range — all default off / inert (registry in plan §7b).
                        sentimentMaxBias:    _configuration.GetValue("Bots:SentimentMaxBias", 0.20m),
                        inertia:             _configuration.GetValue("Bots:Imbalance:Inertia", false),
                        inertiaMinSec:       _configuration.GetValue("Bots:Imbalance:Inertia:MinSec", 30.0),
                        inertiaMaxSec:       _configuration.GetValue("Bots:Imbalance:Inertia:MaxSec", 600.0),
                        inertiaLeak:         _configuration.GetValue("Bots:Imbalance:Inertia:Leak", 0.10m),
                        inertiaSentimentModulated: _configuration.GetValue("Bots:Imbalance:Inertia:SentimentModulated", false),
                        herding:             _configuration.GetValue("Bots:Imbalance:Herding", false),
                        followerFraction:    _configuration.GetValue("Bots:Imbalance:Herding:FollowerFraction", 0.25m),
                        herdTilt:            _configuration.GetValue("Bots:Imbalance:Herding:Tilt", 0.10m),
                        momentumDominance:   _configuration.GetValue("Bots:Imbalance:MomentumDominance", false),
                        momentumStrength:    _configuration.GetValue("Bots:Imbalance:MomentumDominance:Strength", 0m),
                        roleSplit:           _configuration.GetValue("Bots:Imbalance:RoleSplit", false),
                        noiseDamp:           _configuration.GetValue("Bots:Imbalance:RoleSplit:NoiseDamp", 1.0m),
                        anchorFastSlack:     _configuration.GetValue("Bots:Anchor:FastSlack", 0m),
                        activityEnabled:     _configuration.GetValue("Bots:Activity:Enabled", false),
                        activityGamma:       _configuration.GetValue("Bots:Activity:Gamma", 1.0),
                        compTakerExp:        _configuration.GetValue("Bots:Activity:Composition:TakerExp", 0.0),
                        compDistExpClose:    _configuration.GetValue("Bots:Activity:Composition:DistExpClose", 0.0),
                        compDistExpMid:      _configuration.GetValue("Bots:Activity:Composition:DistExpMid", 0.0),
                        compDistExpFar:      _configuration.GetValue("Bots:Activity:Composition:DistExpFar", 0.0),
                        compSizeExp:         _configuration.GetValue("Bots:Activity:Composition:SizeExp", 0.0),
                        compSizeCap:         _configuration.GetValue("Bots:Activity:Composition:SizeCap", 3.0),
                        openRampMin:         _configuration.GetValue("Bots:Activity:Composition:OpenRampMin", 0.0),
                        openRampStaggerMin:  _configuration.GetValue("Bots:Activity:Composition:OpenRampStaggerMin", 0.0),
                        rangeActivityImpact: _configuration.GetValue("Bots:Range:ActivityImpact", false),
                        rangeMaxSlippage:    _configuration.GetValue("Bots:Range:MaxSlippage", 0.02m),
                        fatImpactProb:       _configuration.GetValue("Bots:Range:FatImpactProb", 0m),
                        // Down-drift fix — Greed extreme-reaction style + optional continuous cash homeostasis
                        // (all default off / inert; flag-off reproduces today's logic byte-for-byte).
                        greedStyle:                _configuration.GetValue("Bots:ExtremeReaction:GreedStyle", false),
                        greedSplit:                _configuration.GetValue("Bots:ExtremeReaction:GreedSplit", 0.5m),
                        cashHomeostasisContinuous: _configuration.GetValue("Bots:CashHomeostasis:Continuous", false),
                        cashMaxShift:              _configuration.GetValue("Bots:CashHomeostasis:MaxShift", 0.15m),
                        cashEdgeBuy:               _configuration.GetValue("Bots:CashHomeostasis:EdgeForceBuy", 0.95m),
                        cashEdgeSell:              _configuration.GetValue("Bots:CashHomeostasis:EdgeForceSell", 0.05m),
                        // Sentiment-dynamics §: the slope-aware phase model (default off ⇒ byte-identical).
                        sentimentDynamics:    sentimentDynamics,
                        slopeScaleFast:       _configuration.GetValue("Bots:SentimentDynamics:SlopeScaleFast", 0.01m),
                        slopeScaleSlow:       _configuration.GetValue("Bots:SentimentDynamics:SlopeScaleSlow", 0.005m),
                        momentumConviction:   _configuration.GetValue("Bots:SentimentDynamics:MomentumConviction", 0.15m),
                        scalperConviction:    _configuration.GetValue("Bots:SentimentDynamics:ScalperConviction", 0.20m),
                        reversionConviction:  _configuration.GetValue("Bots:SentimentDynamics:ReversionConviction", 0.15m),
                        reversalConviction:   _configuration.GetValue("Bots:SentimentDynamics:ReversalConviction", 0.10m),
                        marketMakerLean:      _configuration.GetValue("Bots:SentimentDynamics:MarketMakerLean", 0.05m),
                        aggressionBoost:      _configuration.GetValue("Bots:SentimentDynamics:AggressionBoost", 0.20m),
                        // Price-memory anchors + hybrid pressure § (defaults preserve today's behaviour).
                        useDailyAnchor:            useDailyAnchor,
                        recentAnchorEnabled:       recentAnchorEnabled,
                        recentAnchorStrength:      _configuration.GetValue("Bots:RecentAnchor:Strength", 0.35m),
                        recentAnchorScale:         _configuration.GetValue("Bots:RecentAnchor:Scale", 0.04m),
                        multiplicativeDirectional: multiplicativeDirection,
                        diversityGain:             _configuration.GetValue("Bots:DirectionalPressure:DiversityGain", 1.5m),
                        // §cap-from-seed: hard veto measures vs seed instead of Fundamental() when on.
                        capFromSeed:               _configuration.GetValue("Bots:ValueAnchor:CapFromSeed", false),
                        // §adaptive (path-dependent) anchor: cap re-centers on the moving anchor; a
                        // separate total-excursion-from-seed veto is the runaway guard. Default off.
                        adaptiveAnchor:            adaptiveAnchorEnabled,
                        maxTotalExcursion:         _configuration.GetValue("Bots:ValueAnchor:Adaptive:MaxTotalExcursion", 0.35m),
                        // Round 2 §0007 (Path 2): bracket-flip eligibility. R3 §0006 retired the
                        // intermediate Path-1 `bracketRoundTrip` flag (legacy-config warning below).
                        bracketFlip:               _configuration.GetValue("Bots:Advanced:BracketFlip", false),
                        // Round 2 §0011 (E1): inventory-aware kind biasing.
                        inventoryBias:             _configuration.GetValue("Bots:Advanced:InventoryBias", false),
                        inventoryBiasThresholdPrc: _configuration.GetValue("Bots:Advanced:InventoryBiasThresholdPrc", 0.05m),
                        // Round 2 Q1 follow-up: short-side multiplier for asymmetric bear-tail compensation.
                        inventoryBiasShortMult:    _configuration.GetValue("Bots:Advanced:InventoryBiasShortMult", 1m),
                        // §bear-short: sentiment-responsive short participation (symmetric sell-side ammo, fixes up-skew).
                        bearShortStrength:         _configuration.GetValue("Bots:BearShortStrength", 0m),
                        // R4 §0009 Stage 4 — Option D: liquidity-aware limit-offset asymmetry.
                        liquidityAwarePlacement:   _configuration.GetValue("Bots:LiquidityAwarePlacement", false),
                        liquidityAwareGain:        _configuration.GetValue("Bots:LiquidityAwareGain", 0m),
                        // R5 §B+C anchor-timing fix (breaks the −0.43 1-min return autocorrelation). Default-off.
                        anchorReactionLag:         _configuration.GetValue("Bots:AnchorReactionLag", false),
                        anchorLagMinAlpha:         _configuration.GetValue("Bots:AnchorLagMinAlpha", 0.05m),
                        anchorLagMaxAlpha:         _configuration.GetValue("Bots:AnchorLagMaxAlpha", 0.30m),
                        anchorDeadbandPrc:         _configuration.GetValue("Bots:AnchorDeadbandPrc", 0m),
                        // Order-wall declumping: soft round-number snap. Defaults = prior exact 30% snap.
                        roundSnapProb:             _configuration.GetValue("Bots:RoundSnapProb", 0.30m),
                        roundSnapSpread:           _configuration.GetValue("Bots:RoundSnapSpread", 0m),
                        // Microstructure bid-ask bounce: tighten the touch toward mid. Default 0 ⇒ byte-identical.
                        touchTightenPrc:           _configuration.GetValue("Bots:TouchTightenPrc", 0m),
                        // #1: Lateness-staggered lag on the directional/sentiment loop. Default-off.
                        buyStopFraction:           _configuration.GetValue("Bots:Advanced:BuyStopFraction", 0m),
                        directionalReactionLag:    _configuration.GetValue("Bots:DirectionalReactionLag", false),
                        dirLagMinAlpha:            _configuration.GetValue("Bots:DirLagMinAlpha", 0.05m),
                        dirLagMaxAlpha:            _configuration.GetValue("Bots:DirLagMaxAlpha", 0.30m),
                        // §perceived-price desync: per-bot perceived-price slope (supersedes DirectionalReactionLag).
                        perceivedPriceDesync:      _configuration.GetValue("Bots:PerceivedPriceDesync", false),
                        perceivedMinAlpha:         _configuration.GetValue("Bots:PerceivedPriceMinAlpha", 0.05m),
                        perceivedMaxAlpha:         _configuration.GetValue("Bots:PerceivedPriceMaxAlpha", 0.45m),
                        perceivedSlopeScaleFast:   _configuration.GetValue("Bots:PerceivedSlopeScaleFast", 0.01m),
                        perceivedSlopeScaleSlow:   _configuration.GetValue("Bots:PerceivedSlopeScaleSlow", 0.02m),
                        // §impact-decouple B: hard per-bot refractory on the directional stance. Default off.
                        reactionHold:              _configuration.GetValue("Bots:ImpactDecoupleHold", false),
                        reactionHoldWindowSec:     _configuration.GetValue("Bots:ImpactDecoupleHoldWindowSec", 90.0),
                        // §direct-flow chaser cohort. NotionalFrac default 0 ⇒ no chase order ⇒ byte-identical.
                        chaserFraction:            exogEnabled ? exogChaserFraction : 0.0,
                        chaserNotionalFrac:        exogEnabled ? exogChaserNotionalFrac : 0.0,
                        chaserMaxNotionalFrac:     exogEnabled ? exogChaserMaxNotionalFrac : 0.0,
                        // §chaser-v2 ratio-fix co-dials + cadence (gated by exogEnabled ⇒ 0 when the arm is off).
                        chaserSellSymFrac:         exogEnabled ? exogChaserSellSymFrac : 0.0,
                        chaserBuyRoomRelaxFrac:    exogEnabled ? exogChaserBuyRoomRelaxFrac : 0.0,
                        chaserIntervalTicks:       exogEnabled ? exogChaserIntervalTicks : 0,
                        exogCap:                   exogCap,
                        shockOf:                   _news.GetShock,
                        shockIdOf:                 _news.GetShockId,
                        anyShockActive:            () => _news.AnyActive,
                        // §global co-fire: same-tick, same-sign taker burst across all stocks on a market-wide pulse.
                        globalCoFire:              exogEnabled && exogGlobalCoFire,
                        globalCoFireFraction:      exogEnabled ? exogGlobalCoFireFraction : 0.0,
                        globalCoFireNotionalFrac:  exogEnabled ? exogGlobalCoFireNotionalFrac : 0.0,
                        globalCoFireSignOf:        () => _news.GlobalCoFireSign,
                        globalPulseIdOf:           () => _news.GlobalPulseId,
                        // §sector pulse: restrict a sector-scoped pulse's co-fire cohort to that sector (−1 ⇒ market-wide).
                        globalCoFireSectorOf:      () => _news.GlobalCoFireSector,
                        sectorCount:               exogSectorCount,
                        // §reaction-persistence split: two-clock reaction/persistence + taker coupling. Default off ⇒
                        // byte-identical (ReactionTauSec is read + logged below but RESERVED — not wired in v1).
                        reactionPersistence:       _configuration.GetValue("Bots:Imbalance:ReactionPersistence", false),
                        persistMinSec:             _configuration.GetValue("Bots:Imbalance:ReactionPersistence:PersistMinSec", 300.0),
                        persistMaxSec:             _configuration.GetValue("Bots:Imbalance:ReactionPersistence:PersistMaxSec", 1200.0),
                        reactionWLocal:            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:WLocal", 1.0m),
                        reactionWShared:           _configuration.GetValue("Bots:Imbalance:ReactionPersistence:WShared", 0.7m),
                        reactionLeak:              _configuration.GetValue("Bots:Imbalance:ReactionPersistence:Leak", 0.10m),
                        reactionTakerCoupling:     _configuration.GetValue("Bots:Imbalance:ReactionPersistence:TakerCoupling", false),
                        reactionTakerThreshold:    _configuration.GetValue("Bots:Imbalance:ReactionPersistence:TakerThreshold", 0.15m),
                        reactionTakerGain:         _configuration.GetValue("Bots:Imbalance:ReactionPersistence:TakerGain", 1.0m),
                        reactionTakerGovScale:     _configuration.GetValue("Bots:Imbalance:ReactionPersistence:TakerGovScale", 1000000000m),
                        // §source-cap: the reject gate in BuildProtectiveStopAsync + the count source selector.
                        maxArmedStopsPerBot:       maxArmedStopsPerBot,
                        leanReload:                leanReload);
        _maxAdvancedPerTick = _configuration.GetValue("Bots:Advanced:MaxPerTick", 50);
        _advancedEnabled    = _configuration.GetValue("Bots:Advanced:Enabled", false);
        _logger.LogInformation(
            "CONFIGCHECK ImpactDecouple ref={Ref} refHL={Hl} hold={Hold} holdWin={Win} probe={Probe} (absent ⇒ false/-1)",
            _configuration.GetValue("Bots:ImpactDecoupleReference", false),
            _configuration.GetValue("Bots:ImpactDecoupleReferenceHalfLifeSec", -1.0),
            _configuration.GetValue("Bots:ImpactDecoupleHold", false),
            _configuration.GetValue("Bots:ImpactDecoupleHoldWindowSec", -1.0),
            _configuration.GetValue("Bots:ImpactHoldProbe", false));
        // §exogenous-information arm marker: full knob set + resolved invariants so an A/B arm is auditable from
        // the log alone (anchorTracksResolved reflects the anti-runaway validation, not just the requested flag).
        _logger.LogInformation(
            "CONFIGCHECK ExogShock enabled={En} anchorTracks={Anchor} cap={Cap} chaserFraction={Cf} " +
            "chaserNotionalFrac={Cnf} chaserMaxNotionalFrac={Cmf} " +
            "chaserSellSymFrac={Css} chaserBuyRoomRelaxFrac={Cbr} chaserMinIntervalSec={Cmi} chaserIntervalTicks={Cit} " +
            "(retired: chaserStrength={Cs} chaserScale={Csc}) " +
            "meanIntervalMin={Mi} halfLifeSec={Hl} mag=[{MinM},{MaxM}] exp={Exp} (off ⇒ byte-identical)",
            exogEnabled, exogAnchorTracks, exogCap, exogChaserFraction,
            exogChaserNotionalFrac, exogChaserMaxNotionalFrac,
            exogChaserSellSymFrac, exogChaserBuyRoomRelaxFrac, exogChaserMinIntervalSec, exogChaserIntervalTicks,
            exogChaserStrength, exogChaserScale,
            _configuration.GetValue("Bots:ExogShock:MeanIntervalMinutes", 3.0),
            _configuration.GetValue("Bots:ExogShock:DecayHalfLifeSec", 300.0),
            _configuration.GetValue("Bots:ExogShock:MinMagnitude", 0.01),
            _configuration.GetValue("Bots:ExogShock:MaxMagnitude", 0.06),
            _configuration.GetValue("Bots:ExogShock:MagnitudeExponent", 1.8));
        _logger.LogInformation(
            "CONFIGCHECK ExogShock GlobalCoFire={Cf} coFireFraction={Cff} coFireNotionalFrac={Cfn} globalFraction={Gf} " +
            "sectorCount={Sc} sectorFraction={Sf} " +
            "(needs Enabled + GlobalFraction>0 to fire; SectorCount>1 & SectorFraction>0 ⇒ sector-scoped; off ⇒ byte-identical)",
            exogEnabled && exogGlobalCoFire, exogGlobalCoFireFraction, exogGlobalCoFireNotionalFrac,
            _configuration.GetValue("Bots:ExogShock:GlobalFraction", 0.0), exogSectorCount, exogSectorFraction);
        // Microstructure bounce arm marker: lets an A/B soak operator confirm OFF (0) vs ON from the log.
        _logger.LogInformation("CONFIGCHECK TouchTighten touchTighten={TouchTighten} (absent ⇒ 0 ⇒ byte-identical)",
            _configuration.GetValue("Bots:TouchTightenPrc", 0m));
        // §perceived-price desync arm marker: confirm OFF vs ON + the resolved alphas/scales from the log alone.
        _logger.LogInformation(
            "CONFIGCHECK PerceivedPriceDesync on={On} minAlpha={Min} maxAlpha={Max} scaleFast={Sf} scaleSlow={Ss} " +
            "(off ⇒ byte-identical; supersedes DirectionalReactionLag — do not co-enable)",
            _configuration.GetValue("Bots:PerceivedPriceDesync", false),
            _configuration.GetValue("Bots:PerceivedPriceMinAlpha", 0.05m),
            _configuration.GetValue("Bots:PerceivedPriceMaxAlpha", 0.45m),
            _configuration.GetValue("Bots:PerceivedSlopeScaleFast", 0.01m),
            _configuration.GetValue("Bots:PerceivedSlopeScaleSlow", 0.02m));
        // §reaction-persistence arm marker: confirm OFF vs ON + all resolved knobs from the log alone. Supersedes
        // the §A1 inertia clamp and the reaction/hold levers when on (do not co-enable). reactionTauSec is RESERVED
        // (default 0 ⇒ inert) — logged so a future sweep can see it, not wired in v1.
        _logger.LogInformation(
            "CONFIGCHECK ReactionPersistence on={On} reactionTauSec={Tau} persistSec=[{Pmin},{Pmax}] " +
            "wLocal={Wl} wShared={Ws} leak={Leak} takerCoupling={Tc} takerThreshold={Tt} takerGain={Tg} takerGovScale={Tgs} " +
            "(off ⇒ byte-identical; supersedes Inertia + DirectionalReactionLag + PerceivedPriceDesync + " +
            "ImpactDecoupleHold + TrendFollower taker/chase — do not co-enable)",
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence", false),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:ReactionTauSec", 0.0),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:PersistMinSec", 300.0),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:PersistMaxSec", 1200.0),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:WLocal", 1.0m),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:WShared", 0.7m),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:Leak", 0.10m),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:TakerCoupling", false),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:TakerThreshold", 0.15m),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:TakerGain", 1.0m),
            _configuration.GetValue("Bots:Imbalance:ReactionPersistence:TakerGovScale", 1000000000m));
        _batchArms          = _configuration.GetValue("Bots:Advanced:BatchArms", false);
        // §replace-old (Workstream 1): before arming a new standalone protective stop, cancel the bot's prior
        // (stock,side) standalone armed stop via the SAFE per-order path — cures the additive stop firehose.
        // Default off ⇒ byte-identical. See AiBotStateService.CancelPriorStandaloneStopsAsync.
        _stopReplaceOld     = _configuration.GetValue("Bots:StopReplaceOld", false);
        _bracketBatch       = _configuration.GetValue("Bots:Advanced:BracketBatch", false);
        _batchBuyStops      = _configuration.GetValue("Bots:Advanced:BatchBuyStops", false);
        _batchShortOpens    = _configuration.GetValue("Bots:Advanced:BatchShortOpens", false);
        // §stagger: deterministic per-bot tick-phase scheduling. Default off ⇒ byte-identical;
        // Slots is the per-tick load-cut factor N (only ~1/N of bots are due to act per tick).
        _staggerEnabled     = _configuration.GetValue("Bots:Staggering:Enabled", false);
        _staggerSlots       = Math.Max(1, _configuration.GetValue("Bots:Staggering:Slots", 4));

        // R3 §0006: legacy-config warning. The Bots:Advanced:BracketRoundTrip key was a
        // Path-1-minimal flag (qty-clamped ShortBracket on flat-or-long), strict subset of
        // BracketFlip — round-2 baked BracketFlip = true in production so this path is
        // unreachable in any shipped configuration. Operators that explicitly set the legacy
        // key get a one-shot warning so they remove it instead of silently getting different
        // behaviour.
        if (_configuration.GetSection("Bots:Advanced:BracketRoundTrip").Exists())
        {
            _logger.LogWarning(
                "Bots:Advanced:BracketRoundTrip is set but the flag is retired in R3 §0006. " +
                "The Path-1 minimal qty-clamp has been removed; BracketFlip is the only flag. " +
                "Remove the setting from appsettings to silence this warning.");
        }
        // _scaler is constructed earlier (before the rotator, which reads its load) — see the §rotator block above.

        _market.QuoteUpdated += OnQuoteUpdated;
    }
    #endregion

    #region Configure and Lifecycle
    public void Configure(TimeSpan? tradeInterval = null,
        TimeSpan? dailyCheckInterval = null, TimeSpan? reloadAssetsInterval = null,
        IEnumerable<CurrencyType>? currencies = null, TimeSpan? pruneInterval = null)
    {
        if (tradeInterval        is { } ti)  TradeInterval        = ti;
        if (dailyCheckInterval   is { } di)  DailyCheckInterval   = di;
        if (reloadAssetsInterval is { } rai) ReloadAssetsInterval = rai;
        if (currencies != null)              CurrenciesToTrade    = currencies.ToList();
        if (pruneInterval        is { } pi)  PruneInterval        = pi;
    }

    public void SetActiveBotCap(int? cap)
    {
        if (cap.HasValue && cap.Value < 0) cap = 0;

        // Clamp to MaxBotCap so manual edits and scaler decisions can't exceed the ceiling.
        if (MaxBotCap.HasValue)
            cap = cap.HasValue ? Math.Min(cap.Value, MaxBotCap.Value) : MaxBotCap.Value;

        ActiveBotCap = cap;

        // Apply the cap immediately and force an asset reload on the next loop tick so
        // bots that just became active have their funds/positions/orders ready.
        _state.ApplyActiveBotCap(_ctx, cap);
        _nextAssetReload = DateTime.MinValue;
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMaxBotCap(int? cap)
    {
        if (cap.HasValue && cap.Value < 0) cap = 0;
        MaxBotCap = cap;

        // If the new ceiling is below the current active cap, lower it to match;
        // SetActiveBotCap already fires StatsChanged.
        if (cap.HasValue && (!ActiveBotCap.HasValue || ActiveBotCap.Value > cap.Value))
            SetActiveBotCap(cap);
        else
            StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyCollection<int> GetAiUserIds() => _ctx.AiUsersByUserId.Keys.ToArray();

    // §dashboard: UserId -> strategy for per-strategy transaction aggregation. Best-effort snapshot (matches
    // GetAiUserIds's lock-free read); the bot maps only change on load/reset, not per-tick.
    public IReadOnlyDictionary<int, AiStrategy> GetBotStrategies()
    {
        var map = new Dictionary<int, AiStrategy>(_ctx.AiUsersByUserId.Count);
        foreach (var kv in _ctx.AiUsersByUserId.ToArray()) map[kv.Key] = kv.Value.Strategy;
        return map;
    }

    // §dashboard: the economy telemetry's latest per-strategy snapshot (loop-thread published, lock-free read).
    public IReadOnlyList<StrategySnapshotRow> GetStrategySnapshot() => _economy.LatestStrategySnapshot;

    public IReadOnlyList<BotActivitySample> GetActivitySamples()
    {
        lock (_activitySamplesLock) return _activitySamples.ToArray();
    }

    // §market-mood: the bots' ground-truth mood surfaced as a 0..100 Fear/Greed gauge. Loop-thread writes the
    // composite; the HTTP thread reads the cached value here — the per-stock dicts only gain keys at reset then
    // update values, so a best-effort read is safe without a lock. When the composite is enabled we return its
    // cached score; otherwise the v1 sentiment×activity fallback (truthful, byte-identical to the old endpoint).
    public double MoodForStock(int stockId)
        => _mood.Enabled
            ? _mood.MoodFor(stockId)
            : MarketMoodService.LegacyMoodScore(
                (double)_sentiment.GetSentiment(stockId), _activity.CompositionActivity(stockId), _moodGreedScale);

    public (double Global, IReadOnlyDictionary<int, double> Stocks) GetMarketMood()
    {
        var stocks = new Dictionary<int, double>(_stocks.ById.Count);
        double sum = 0; int n = 0;
        foreach (var sid in _stocks.ById.Keys)
        {
            double mood = MoodForStock(sid);
            stocks[sid] = mood; sum += mood; n++;
        }
        // Global mood = the market-wide mean; if half the names are greedy and half fearful the market is
        // neutral, which is the honest aggregate (it already folds in the shared common-mode sentiment).
        return (n > 0 ? sum / n : 50.0, stocks);
    }

    // 10s cadence × 8640 = 24h history (matches longest dashboard range).
    private const int MaxActivitySamples = 8640;
    private static readonly TimeSpan ActivitySampleInterval = TimeSpan.FromSeconds(10);
    private DateTime _lastActivitySampleUtc = DateTime.MinValue;
    private readonly object _activitySamplesLock = new();
    private readonly Queue<BotActivitySample> _activitySamples = new();

    private void RecordActivitySample()
    {
        var now = TimeHelper.NowUtc();
        if (now - _lastActivitySampleUtc < ActivitySampleInterval) return;
        _lastActivitySampleUtc = now;

        var sample = new BotActivitySample(
            TimestampUtc: now,
            OnlineBots:   OnlineBotCount,
            ActiveBotCap: ActiveBotCap ?? OnlineBotCount,
            LoadedBots:   LoadedBotCount);
        lock (_activitySamplesLock)
        {
            _activitySamples.Enqueue(sample);
            while (_activitySamples.Count > MaxActivitySamples) _activitySamples.Dequeue();
        }
    }

    public async Task StartBotAsync(CancellationToken ct = default)
    {
        if (_runner != null && !_runner.IsCompleted) return;

        await _stocks.EnsureLoadedAsync(ct).ConfigureAwait(false);

        // Bots are non-UI subscribers — keep _quotes populated and ref-counted, but tell
        // MarketDataService not to dispatch tick work to the UI thread for these books.
        foreach (var currency in CurrenciesToTrade)
            await _market.SubscribeAllAsync(currency, forUi: false, ct).ConfigureAwait(false);

        ResetSessionState();
        _schedulingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _engineCts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runner = Task.Run(() => RunLoopAsync(_schedulingCts.Token));
    }

    public async Task StopBotAsync()
    {
        if (_runner == null) return;
        try
        {
            _schedulingCts?.Cancel();

            var graceMs = _configuration.GetValue("Bots:GracefulStopMs", 8000);
            var sw = Stopwatch.StartNew();
            var winner = await Task.WhenAny(_runner, Task.Delay(Math.Max(0, graceMs))).ConfigureAwait(false);
            sw.Stop();

            if (winner == _runner)
            {
                _logger.LogInformation("Bot loop drained cleanly in {Ms}ms.", sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogWarning(
                    "Bot loop drain timeout after {Ms}ms; hard-canceling in-flight engine work.",
                    graceMs);
                _engineCts?.Cancel();
                await _runner.ConfigureAwait(false);
            }

            foreach (var currency in CurrenciesToTrade)
                await _market.UnsubscribeAllAsync(currency, forUi: false).ConfigureAwait(false);

            // §3.7 final session FX-desk line so a soak log captures the complete conversion tally.
            _fxDesk.LogSummary();
        }
        finally
        {
            _runner = null;
            _schedulingCts?.Dispose();
            _schedulingCts = null;
            _engineCts?.Dispose();
            _engineCts = null;
            LoopStartedAtUtc = null;
            StatsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetSessionState()
    {
        // Per-session run counters reset every Start.
        Interlocked.Exchange(ref _tickCount, 0);
        Interlocked.Exchange(ref _tradesPlacedThisSession, 0);
        Interlocked.Exchange(ref _failuresThisSession, 0);
        Volatile.Write(ref _tickWorkMsEwma, 0.0);
        Interlocked.Exchange(ref _lastTickWorkMicros, 0);
        _stats.Reset();
        _fxRates.Reset();

        // _failures, _economy, _sentiment ringbuffers are intentionally NOT
        // cleared. They accumulate across Stop/Start cycles so the dashboard
        // shows the full session history, and they survive server restarts
        // once disk persistence lands. Sentiment still re-rolls its starting
        // factors though (different semantics from the others).
        _sentiment.Reset(TimeHelper.NowUtc());
        _regime.Reset(TimeHelper.NowUtc());    // §A2/A3/A4: open from a deterministic +1 regime
        _activity.Reset(TimeHelper.NowUtc());  // §Pillar B: open neutral (field ≡ baseline/1)
        _decisions.LoopStartUtc = TimeHelper.NowUtc();  // §open taker ramp: arm the uptime clock
        _bank.Reset(TimeHelper.NowUtc()); // §bank-estimate: clear estimates + reseed RNG (before _funds reads them)
        _funds.Reset();   // §P6: re-seed fundamentals at the listing seed prices for this session
        _news.Reset(TimeHelper.NowUtc()); // §exogenous-information: clear shocks + reseed source (inert when off)
        _rotator.Reset(); // §rotator: reset the participation-shuffle pass counter (deterministic)
        _conviction.Reset(); // §conviction: reset the fire-cadence pass counter + arm the clock (deterministic)
        _jump.Reset(TimeHelper.NowUtc()); // §fat-tail jumps: clear aftershocks + reseed source (inert when off)
        _priceMemory.Reset(TimeHelper.NowUtc()); // re-seed EWMA + day window; inert until Tick if anyConsumer=false
        _fxDesk.Reset();  // §3.7: fresh per-session FX-desk conversion tallies

        _nextStatsLogTime      = TimeHelper.NowUtc() + StatsLogInterval;
        _nextPhaseLogTime      = _phaseTimingInterval > TimeSpan.Zero ? TimeHelper.NowUtc() + _phaseTimingInterval : DateTime.MaxValue;
        _phCheckUs = _phCollectUs = _phBatchUs = _phAdvUs = _phArbUs = _phReconUs = 0;
        _phCollectPreUs = _phCollectComputeUs = _phCollectEligibleN = _phCollectDueN = 0;
        _phPending = _phAdvCount = _phTicks = 0;
        _cmPrevCommits = EngineCommitMetrics.ReadCommits(); // first window measures from loop start
        _cmPrevTrades = EngineCommitMetrics.ReadTrades();
        _nextReconcileTime     = TimeHelper.NowUtc() + ReconcileFirstDelay;
        _nextEconomyLogTime    = TimeHelper.NowUtc() + _economyLogInterval;
        _nextSentimentLogTime  = TimeHelper.NowUtc() + _sentimentLogInterval;
        _nextCashInjectionTime = TimeHelper.NowUtc() + _cashInjectionInterval;
        LastTradeAtUtc   = null;
        LoopStartedAtUtc = TimeHelper.NowUtc();
    }
    #endregion

    #region Main Loop
    private async Task RunLoopAsync(CancellationToken ct)
    {
        await _state.LoadAsync(_ctx, ct).ConfigureAwait(false);
        // Bots load with IsEnabled=true; apply the current cap (if any) before the first tick.
        _state.ApplyActiveBotCap(_ctx, ActiveBotCap);

        // Warm the engine-wide accounts cache for every bot user up front so the first
        // batch of orders doesn't pay the cold-load cost inside the per-book lock.
        var botUserIds = new List<int>(_ctx.AiUsersByAiUserId.Count + 1);
        foreach (var u in _ctx.AiUsersByAiUserId.Values) botUserIds.Add(u.UserId);
        // §3.7 warm the platform house account too so convert-spread crediting and the value-drain
        // telemetry read its USD/EUR funds from the cache without a cold DB hit.
        botUserIds.Add(_houseUserId);
        // §fat-tail jumps: warm the dedicated aggressor account too (not in the fleet, like the house) so its
        // cash/inventory reads from the cache without a cold DB hit. Only when the lever is enabled.
        if (_jumpEnabled && _jumpAggressorUserId > 0) botUserIds.Add(_jumpAggressorUserId);
        if (botUserIds.Count > 0)
            await _accounts.EnsureLoadedAsync(botUserIds, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            // §B-(b): start-of-iteration stamp for the optional self-correcting delay below. Reading a
            // timestamp has no observable effect ⇒ byte-identical when the flag is off.
            var iterStart = Stopwatch.GetTimestamp();
            // Whole-tick guard: a transient failure (e.g. a DB command timeout) must not end
            // the loop — log and continue after the interval so the fleet keeps trading.
            try
            {
                var tickStart = Stopwatch.GetTimestamp();
                var now = TimeHelper.NowUtc();
                await CheckTimers(now, ct).ConfigureAwait(false);
                var tCheck = Stopwatch.GetTimestamp();

                var (pending, advanced) = await CollectPendingOrdersAsync(now, ct).ConfigureAwait(false);
                var tCollect = Stopwatch.GetTimestamp();
                if (pending.Count > 0)
                    await SubmitAndApplyBatchAsync(pending, _engineCts?.Token ?? ct).ConfigureAwait(false);
                var tBatch = Stopwatch.GetTimestamp();
                // §P6a: advanced (stop/trailing) decisions go through the entry/arm route, sequentially and
                // in aiUserId order, AFTER and OUTSIDE the batch matcher's locked region (each call owns its
                // own gates; the loop holds none). Off the matching hot path; reconcile below is the gate.
                if (advanced.Count > 0)
                    await SubmitAdvancedAsync(advanced, _engineCts?.Token ?? ct).ConfigureAwait(false);
                var tAdv = Stopwatch.GetTimestamp();

                // §3.7 arbitrage cohort: dedicated pass, after the normal batch + advanced routes and
                // outside the matcher's locked region (each leg owns its own gates). Its market legs
                // settle through the same engine, so ConservationProbe/ReservationAuditor cover them.
                // Gated by Bots:Arbitrage:Enabled so the whole feature can be killed from config.
                if (_arbitrageEnabled)
                    await _arbitrage.RunAsync(_ctx, now, _engineCts?.Token ?? ct).ConfigureAwait(false);
                var tArb = Stopwatch.GetTimestamp();

                // §mm-cohort: the market-maker cohort's quoting pass, after the arbitrage pass and outside the
                // matcher's locked region (its limit orders own their own gates and settle through the same
                // engine, so ConservationProbe/ReservationAuditor cover them). Gated by Bots:MarketMaker:Enabled.
                if (_marketMakerEnabled)
                    await _marketMaker.RunAsync(_ctx, now, _engineCts?.Token ?? ct).ConfigureAwait(false);

                // §rotator: the estimate-driven rotational cohort's pass, AFTER the MM pass (so it rotates against
                // the freshest two-sided book) and outside the matcher's locked region — its market legs settle
                // through the same engine, so ConservationProbe/ReservationAuditor cover them. Gated by
                // Bots:Rotator:Enabled ⇒ a single bool check when off (byte-identical).
                if (_rotatorEnabled)
                    await _rotator.RunAsync(_ctx, now, _engineCts?.Token ?? ct).ConfigureAwait(false);

                // §conviction: the discretionary sentiment/sector-momentum cohort's pass, AFTER the rotator (so it
                // acts on the freshest book) and outside the matcher's locked region — its taker orders own their own
                // gates and settle through the same engine, so ConservationProbe/ReservationAuditor cover them.
                // Gated by Bots:Conviction:Enabled ⇒ a single bool check when off (byte-identical).
                if (_convictionEnabled)
                    await _conviction.RunAsync(_ctx, now, _engineCts?.Token ?? ct).ConfigureAwait(false);

                // §fat-tail jumps: rare realized price-jump pass, AFTER the MM pass (so it walks the freshest
                // two-sided book) and outside the matcher's locked region (its marketable orders own their own
                // gates and settle through the same engine, so ConservationProbe/ReservationAuditor cover them).
                // Gated by Bots:Jumps:Enabled ⇒ a single bool check when off (byte-identical).
                if (_jumpEnabled)
                    await _jump.RunAsync(_ctx, now, _engineCts?.Token ?? ct).ConfigureAwait(false);

                // Round 2 §0006c: drain the end-of-tick coordinator queue when
                // Bots:Advanced:BatchCoordinator is on. No-op when off — the per-event On*Async
                // already ran synchronously inline. Failure is logged + recovered per-event so
                // a single bad bracket doesn't stop the tick.
                try { await _bracket.DrainAsync(_engineCts?.Token ?? ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when ((_engineCts?.Token ?? ct).IsCancellationRequested) { }
                catch (Exception ex) { _logger.LogError(ex, "Bracket coordinator drain failed on tick {Tick}", _tickCount); }
                var tCohorts = Stopwatch.GetTimestamp(); // end of the special-cohort span (mm + rotator + jump + drain)

                RecordTickLatency(Stopwatch.GetElapsedTime(tickStart));
                // §B-P-b: the actionable (Collect+Batch) span — the fleet load the scaler can act on,
                // excluding the cap-exempt cohorts (arb/mm/rotator/jump/drain) between tBatch and here.
                RecordActionableLatency(Stopwatch.GetElapsedTime(tCheck, tBatch));
                Interlocked.Increment(ref _tickCount);
                RecordActivitySample();

                // The scaler may move the cap based on the fresh EWMA. SetActiveBotCap
                // fires StatsChanged itself, so only emit the unchanged event when the
                // scaler decides to stay put.
                var scalerTarget = _scaler.OnTick(this);
                if (scalerTarget.HasValue) SetActiveBotCap(scalerTarget.Value);
                else                       StatsChanged?.Invoke(this, EventArgs.Empty);

                // Reconcile at the post-batch quiescent frame: this tick's market orders are
                // terminal, so only resting limit reservations remain and the clamp is safe.
                // After RecordTickLatency so this maintenance pass doesn't skew the scaler EWMA.
                long reconUs = 0;
                if (now >= _nextReconcileTime)
                {
                    _nextReconcileTime = now + ReconcileInterval;
                    var tReconStart = Stopwatch.GetTimestamp();
                    try { await _auditor.AuditAsync(_reconcileClamp, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex) { _logger.LogError(ex, "Reservation reconcile pass failed."); }
                    reconUs = (long)Stopwatch.GetElapsedTime(tReconStart).TotalMicroseconds;
                }

                // Periodic maintenance at the post-batch quiescent frame, AFTER RecordTickLatency so these
                // amortized spikes (LogSnapshot O(bots×stocks), prune, asset reload) never skew the scaler
                // EWMA — same placement rationale as the reconcile pass above. Still on the loop thread
                // (single-threaded, no new races).
                var tMaintStart = Stopwatch.GetTimestamp();
                await RunPeriodicMaintenanceAsync(now, ct).ConfigureAwait(false);
                long maintUs = (long)Stopwatch.GetElapsedTime(tMaintStart).TotalMicroseconds;

                // Per-phase profiling (opt-in): accumulate this tick, log the windowed average breakdown.
                if (_phaseTimingInterval > TimeSpan.Zero)
                {
                    _phCheckUs   += (long)Stopwatch.GetElapsedTime(tickStart, tCheck).TotalMicroseconds;
                    _phCollectUs += (long)Stopwatch.GetElapsedTime(tCheck, tCollect).TotalMicroseconds;
                    _phBatchUs   += (long)Stopwatch.GetElapsedTime(tCollect, tBatch).TotalMicroseconds;
                    _phAdvUs     += (long)Stopwatch.GetElapsedTime(tBatch, tAdv).TotalMicroseconds;
                    _phArbUs     += (long)Stopwatch.GetElapsedTime(tAdv, tArb).TotalMicroseconds;
                    _phCohortsUs += (long)Stopwatch.GetElapsedTime(tArb, tCohorts).TotalMicroseconds;
                    _phReconUs   += reconUs;
                    _phMaintUs   += maintUs;
                    _phPending   += pending.Count;
                    _phAdvCount  += advanced.Count;
                    _phTicks++;
                    if (now >= _nextPhaseLogTime) { LogPhaseTiming(); _nextPhaseLogTime = now + _phaseTimingInterval; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bot tick failed; loop continuing after the interval delay.");
            }

            // §B-(b) self-correcting delay: when on, subtract this iteration's elapsed work so the true
            // period tracks TradeInterval (not interval + work). NOT byte-identical — it changes `now`
            // spacing ⇒ which bots are due ⇒ own flag, default off. Off ⇒ the fixed delay below verbatim.
            var delay = TradeInterval;
            if (_selfCorrectingDelay)
            {
                var remainMs = TradeInterval.TotalMilliseconds - Stopwatch.GetElapsedTime(iterStart).TotalMilliseconds;
                delay = remainMs > 0.0 ? TimeSpan.FromMilliseconds(remainMs) : TimeSpan.Zero;
            }
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { /* breaking loop */ }
        }
    }

    // §P6a: submit protective stop/trailing orders through the entry/arm route — sequentially, in ascending
    // aiUserId order (part of the seed-determinism contract), each call owning its own book→fund→position
    // gates while the loop holds none. Runs after the plain batch, outside the matcher's locked region.
    // §A1a (flag Bots:Advanced:BatchArms): the pure-arm kinds (StopMarketSell/TrailingStopSell) route
    // through one batched entry call instead — share pre-reserve + a single bulk-insert tx — while
    // short-opens/brackets stay per-order. The stable partition keeps ascending-aiUserId order within
    // each half; arms never match, so arming order is externally immaterial.
    private async Task SubmitAdvancedAsync(List<(AIUser user, BotAdvancedDecision dec)> advanced, CancellationToken ct)
    {
        advanced.Sort((a, b) => a.user.AiUserId.CompareTo(b.user.AiUserId));

        // §replace-old (Bots:StopReplaceOld): BEFORE arming this tick's new standalone protective stops,
        // cancel each bot's prior (stock,side) standalone armed stop via the SAFE per-order path — so a bot
        // MOVES its stop instead of STACKING a new one on every StopProb/TrailingProb draw (the additive
        // firehose). Runs on the sorted list (deterministic). Only protective-stop kinds match; bracket
        // children never reach here. Off ⇒ skipped entirely ⇒ byte-identical.
        if (_stopReplaceOld)
        {
            foreach (var (user, dec) in advanced)
            {
                OrderSide? side = dec.Kind switch
                {
                    BotAdvancedKind.StopMarketSell or BotAdvancedKind.TrailingStopSell => OrderSide.Sell,
                    BotAdvancedKind.StopMarketBuy                                       => OrderSide.Buy,
                    _                                                                   => null,
                };
                if (side is { } s)
                    await _state.CancelPriorStandaloneStopsAsync(_ctx, user.UserId, dec.StockId, s, ct)
                        .ConfigureAwait(false);
            }
        }

        if (_batchArms || _bracketBatch || _batchBuyStops || _batchShortOpens)
        {
            var armSells = new List<(AIUser user, BotAdvancedDecision dec)>();
            var brackets = new List<(AIUser user, BotAdvancedDecision dec)>();
            var shorts   = new List<(AIUser user, BotAdvancedDecision dec)>();
            var buyStops = new List<(AIUser user, BotAdvancedDecision dec)>();
            var rest     = new List<(AIUser user, BotAdvancedDecision dec)>();
            foreach (var item in advanced)
            {
                switch (item.dec.Kind)
                {
                    case BotAdvancedKind.StopMarketSell:
                    case BotAdvancedKind.TrailingStopSell:
                        if (_batchArms) armSells.Add(item); else rest.Add(item);
                        break;
                    case BotAdvancedKind.LongBracket:
                    case BotAdvancedKind.ShortBracket:
                        if (_bracketBatch) brackets.Add(item); else rest.Add(item);
                        break;
                    case BotAdvancedKind.ShortOpen:
                        // Slice 2: flat market-short opens batch under their own dedicated flag,
                        // decoupled from BracketBatch (which now covers brackets only).
                        if (_batchShortOpens) shorts.Add(item); else rest.Add(item);
                        break;
                    case BotAdvancedKind.StopMarketBuy:
                        if (_batchBuyStops) buyStops.Add(item); else rest.Add(item);
                        break;
                    default:
                        rest.Add(item);
                        break;
                }
            }
            if (armSells.Count > 0 && !await SubmitArmBatchAsync(armSells, ct).ConfigureAwait(false))
                return; // shutdown requested mid-batch
            if (brackets.Count > 0 && !await SubmitBracketBatchAsync(brackets, ct).ConfigureAwait(false))
                return;
            if (shorts.Count > 0 && !await SubmitMarketShortBatchAsync(shorts, ct).ConfigureAwait(false))
                return;
            if (buyStops.Count > 0 && !await SubmitBuyStopBatchAsync(buyStops, ct).ConfigureAwait(false))
                return;
            await SubmitAdvancedPerOrderAsync(rest, ct).ConfigureAwait(false);
            return;
        }

        await SubmitAdvancedPerOrderAsync(advanced, ct).ConfigureAwait(false);
    }

    // Round 2 §0005: submit the tick's bracket cohort through OrderEntryService.PlaceBracketBatchAsync.
    // Per-decision bookkeeping (RecordError, _tradesPlacedThisSession) matches the per-order path.
    private async Task<bool> SubmitBracketBatchAsync(
        List<(AIUser user, BotAdvancedDecision dec)> brackets, CancellationToken ct)
    {
        var requests = new List<BracketBatchRequest>(brackets.Count);
        foreach (var (user, d) in brackets)
        {
            bool isShort = d.Kind == BotAdvancedKind.ShortBracket;
            requests.Add(new BracketBatchRequest(
                user.UserId, d.StockId, d.Quantity, EntryType.Market, d.Currency,
                Price: null,
                BuyBudget: isShort ? null : d.BuyBudget,
                StopPrice: d.StopPrice == 0m ? null : d.StopPrice,
                StopLimitPrice: null,
                StopSlippagePct: d.StopSlippagePct,
                TakeProfits: BuildTpLegs(d.TakeProfits),
                Side: isShort ? OrderSide.Sell : OrderSide.Buy,
                FlipQuantity: d.FlipQuantity));
        }

        IReadOnlyList<OrderResult> results;
        try
        {
            results = await _entry.PlaceBracketBatchAsync(requests, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Bot loop stop requested mid-advanced on tick {Tick}; skipping remaining.", _tickCount);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batched bracket submit failed for {Count} decision(s) on tick {Tick}",
                brackets.Count, _tickCount);
            foreach (var (user, _) in brackets)
            {
                user.RecordError();
                Interlocked.Increment(ref _failuresThisSession);
            }
            return true;
        }

        for (int i = 0; i < brackets.Count; i++)
            ApplyAdvancedResult(brackets[i].user, brackets[i].dec, results[i]);
        return true;
    }

    private async Task<bool> SubmitMarketShortBatchAsync(
        List<(AIUser user, BotAdvancedDecision dec)> shorts, CancellationToken ct)
    {
        var requests = new List<MarketShortBatchRequest>(shorts.Count);
        foreach (var (user, d) in shorts)
            requests.Add(new MarketShortBatchRequest(user.UserId, d.StockId, d.Quantity, d.Currency));

        IReadOnlyList<OrderResult> results;
        try
        {
            results = await _entry.PlaceMarketShortBatchAsync(requests, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batched market-short submit failed for {Count} decision(s) on tick {Tick}",
                shorts.Count, _tickCount);
            foreach (var (user, _) in shorts)
            {
                user.RecordError();
                Interlocked.Increment(ref _failuresThisSession);
            }
            return true;
        }

        for (int i = 0; i < shorts.Count; i++)
            ApplyAdvancedResult(shorts[i].user, shorts[i].dec, results[i]);
        return true;
    }

    // Round 2 §0005: shared per-decision result handling — same shape as SubmitArmBatchAsync's
    // post-loop block, factored out so both the bracket and short batch paths use it.
    private void ApplyAdvancedResult(AIUser user, BotAdvancedDecision dec, OrderResult result)
    {
        if (result.PlacedSuccessfully)
        {
            Interlocked.Increment(ref _tradesPlacedThisSession);
            _state.NoteArmedStopPlaced(_ctx, user, dec.Kind, result);   // §source-cap: +1 on a standalone arm
        }
        else
        {
            if (DebugMode && (!DebugUserId.HasValue || user.UserId == DebugUserId.Value))
                _logger.LogWarning("Advanced order AIUser {Id} stock {Stock}: {Status} — {Error}",
                    user.AiUserId, dec.StockId, result.Status, result.ErrorMessage);
            user.RecordError();
            Interlocked.Increment(ref _failuresThisSession);
        }
    }

    private static List<BracketLeg> BuildTpLegs(IReadOnlyList<(decimal Price, int Quantity)>? takeProfits)
    {
        if (takeProfits is null || takeProfits.Count == 0) return new List<BracketLeg>(0);
        var legs = new List<BracketLeg>(takeProfits.Count);
        for (int i = 0; i < takeProfits.Count; i++)
            legs.Add(new BracketLeg(takeProfits[i].Price, takeProfits[i].Quantity));
        return legs;
    }

    // §A1a: submit the tick's protective arms in one batched entry call. Per-decision bookkeeping
    // matches the per-order loop's exactly. Returns false when shutdown was requested mid-call.
    private async Task<bool> SubmitArmBatchAsync(
        List<(AIUser user, BotAdvancedDecision dec)> armSells, CancellationToken ct)
    {
        var requests = new List<StopArmRequest>(armSells.Count);
        foreach (var (user, d) in armSells)
            requests.Add(new StopArmRequest(
                user.UserId, d.StockId, d.Quantity, d.Currency,
                d.Kind == BotAdvancedKind.TrailingStopSell
                    ? StopArmKind.TrailingStopSell : StopArmKind.StopMarketSell,
                d.StopPrice, d.StopSlippagePct, d.TrailOffset, d.TrailIsPercent));

        IReadOnlyList<OrderResult> results;
        try
        {
            results = await _entry.ArmStopSellBatchAsync(requests, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Bot loop stop requested mid-advanced on tick {Tick}; skipping remaining.", _tickCount);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batched arm submit failed for {Count} decision(s) on tick {Tick}",
                armSells.Count, _tickCount);
            foreach (var (user, _) in armSells)
            {
                user.RecordError();
                Interlocked.Increment(ref _failuresThisSession);
            }
            return true;
        }

        for (int i = 0; i < armSells.Count; i++)
        {
            var (user, d) = armSells[i];
            var result = results[i];
            if (result.PlacedSuccessfully)
            {
                Interlocked.Increment(ref _tradesPlacedThisSession);
                _state.NoteArmedStopPlaced(_ctx, user, d.Kind, result);   // §source-cap: +1 on a standalone arm
            }
            else
            {
                if (DebugMode && (!DebugUserId.HasValue || user.UserId == DebugUserId.Value))
                    _logger.LogWarning("Advanced order AIUser {Id} stock {Stock}: {Status} — {Error}",
                        user.AiUserId, d.StockId, result.Status, result.ErrorMessage);
                user.RecordError();
                Interlocked.Increment(ref _failuresThisSession);
            }
        }
        return true;
    }

    // §A1b: submit the tick's buy-stop cohort in one batched entry call. Mirrors SubmitArmBatchAsync
    // but routes StopMarketBuy (cash-reserve) through ArmStopBuyBatchAsync. Uses the shared
    // ApplyAdvancedResult bookkeeping. Returns false when shutdown was requested mid-call.
    private async Task<bool> SubmitBuyStopBatchAsync(
        List<(AIUser user, BotAdvancedDecision dec)> buyStops, CancellationToken ct)
    {
        var requests = new List<StopArmRequest>(buyStops.Count);
        foreach (var (user, d) in buyStops)
            requests.Add(new StopArmRequest(
                user.UserId, d.StockId, d.Quantity, d.Currency,
                StopArmKind.StopMarketBuy,
                d.StopPrice, StopSlippagePct: null, TrailOffset: 0m, TrailIsPercent: false));

        IReadOnlyList<OrderResult> results;
        try
        {
            results = await _entry.ArmStopBuyBatchAsync(requests, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Bot loop stop requested mid-advanced on tick {Tick}; skipping remaining.", _tickCount);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batched buy-stop submit failed for {Count} decision(s) on tick {Tick}",
                buyStops.Count, _tickCount);
            foreach (var (user, _) in buyStops)
            {
                user.RecordError();
                Interlocked.Increment(ref _failuresThisSession);
            }
            return true;
        }

        for (int i = 0; i < buyStops.Count; i++)
            ApplyAdvancedResult(buyStops[i].user, buyStops[i].dec, results[i]);
        return true;
    }

    private async Task SubmitAdvancedPerOrderAsync(List<(AIUser user, BotAdvancedDecision dec)> advanced, CancellationToken ct)
    {
        foreach (var (user, d) in advanced)
        {
            OrderResult result;
            try
            {
                result = d.Kind switch
                {
                    BotAdvancedKind.StopMarketSell =>
                        await _entry.PlaceStopMarketSellOrderAsync(
                            user.UserId, d.StockId, d.Quantity, d.StopPrice, d.Currency, d.StopSlippagePct, ct).ConfigureAwait(false),
                    // Taker-symmetry: up-trigger BUY routed as a stop-LIMIT (limit = trigger × 1.005). The
                    // limit path reserves cash correctly (qty × Price) — a capped market buy-stop reserves via
                    // BuyBudget, which is $0 here → rejects. The limit ≥ ask at trigger, so it TAKES on the
                    // breakout (adds buy taker pressure) yet is BOUNDED per fire (council's no-up-runaway rule).
                    BotAdvancedKind.StopMarketBuy =>
                        await _entry.PlaceStopLimitBuyOrderAsync(
                            user.UserId, d.StockId, d.Quantity, d.StopPrice,
                            CurrencyHelper.RoundMoney(d.StopPrice * 1.005m, d.Currency), d.Currency, ct).ConfigureAwait(false),
                    BotAdvancedKind.TrailingStopSell =>
                        await _entry.PlaceTrailingStopSellOrderAsync(
                            user.UserId, d.StockId, d.Quantity, d.TrailOffset, d.TrailIsPercent, d.Currency, ct).ConfigureAwait(false),
                    // §P6b flat-only market short (opens a cash-collateralized short via the P1 path).
                    BotAdvancedKind.ShortOpen =>
                        await _entry.PlaceTrueMarketSellOrderAsync(
                            user.UserId, d.StockId, d.Quantity, d.Currency, ct).ConfigureAwait(false),
                    // §P6b long bracket (buy entry + sell-stop SL + sell-limit TPs).
                    BotAdvancedKind.LongBracket =>
                        await _entry.PlaceBracketAsync(
                            user.UserId, d.StockId, d.Quantity, EntryType.Market, d.Currency,
                            limitPrice: null, buyBudget: d.BuyBudget, stopPrice: d.StopPrice,
                            stopLimitPrice: null, stopSlippagePct: d.StopSlippagePct, takeProfits: d.TakeProfits!,
                            ct, OrderSide.Buy, flipQuantity: d.FlipQuantity).ConfigureAwait(false),
                    // §P6c short bracket (flat market sell + slippage-capped buy-stop SL above + buy-limit TPs below).
                    BotAdvancedKind.ShortBracket =>
                        await _entry.PlaceBracketAsync(
                            user.UserId, d.StockId, d.Quantity, EntryType.Market, d.Currency,
                            limitPrice: null, buyBudget: null, stopPrice: d.StopPrice,
                            stopLimitPrice: null, stopSlippagePct: d.StopSlippagePct, takeProfits: d.TakeProfits!,
                            ct, OrderSide.Sell, flipQuantity: d.FlipQuantity).ConfigureAwait(false),
                    _ => new OrderResult { Status = OrderStatus.OperationFailed, ErrorMessage = "unknown advanced kind" },
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Bot loop stop requested mid-advanced on tick {Tick}; skipping remaining.", _tickCount);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Advanced order ({Kind}) failed for AIUser {Id} stock {Stock}",
                    d.Kind, user.AiUserId, d.StockId);
                user.RecordError();
                Interlocked.Increment(ref _failuresThisSession);
                continue;
            }

            if (result.PlacedSuccessfully)
            {
                Interlocked.Increment(ref _tradesPlacedThisSession);
                _state.NoteArmedStopPlaced(_ctx, user, d.Kind, result);   // §source-cap: +1 on a standalone arm
            }
            else
            {
                if (DebugMode && (!DebugUserId.HasValue || user.UserId == DebugUserId.Value))
                    _logger.LogWarning("Advanced order AIUser {Id} stock {Stock}: {Status} — {Error}",
                        user.AiUserId, d.StockId, result.Status, result.ErrorMessage);
                user.RecordError();
                Interlocked.Increment(ref _failuresThisSession);
            }
        }
    }

    /// <summary>
    /// §stagger: deterministic tick-phase gate. Returns true when a bot is due to act on the given
    /// tick. A bot belongs to slot <c>aiUserId % slots</c> and acts only on ticks whose
    /// <c>tickId % slots</c> matches its slot, so each tick sees ~1/slots of the cohort. Pure
    /// function of (id, tick) — no RNG, no wall-clock — so it is reproducible and unit-testable.
    /// <paramref name="slots"/> &lt;= 1 ⇒ always due (the staggering-off / disabled behaviour).
    /// </summary>
    internal static bool StaggerDue(int aiUserId, long tickId, int slots)
    {
        if (slots <= 1) return true;
        // aiUserId is a non-negative identity column; guard anyway so the modulo never goes negative.
        int slot = (int)(((long)aiUserId % slots + slots) % slots);
        return (int)((tickId % slots + slots) % slots) == slot;
    }

    private async Task<(List<(AIUser user, Order order)> Plain, List<(AIUser user, BotAdvancedDecision dec)> Advanced)>
        CollectPendingOrdersAsync(DateTime now, CancellationToken ct)
    {
        // §patch 0001: stamp the tick id and clear per-tick memoization caches before the
        // foreach. Cache entries from the previous tick would be stale (stock prices, OpenOrders
        // composition, committed totals, OverBand verdicts all change tick-to-tick).
        _ctx.TickId = Interlocked.Read(ref _tickCount);
        _ctx.TickNowTicks = now.Ticks;   // §impact-decouple B: one deterministic clock for all bots this tick
        _ctx.ClearTickCaches();
        // §collect-split: only when BotPhase timing is on. Times the prepass + heavy compute so the aggregate
        // collect span can be broken into pre / pass / compute (see LogPhaseTiming). Zero syscalls when off.
        bool phase = _phaseTimingInterval > TimeSpan.Zero;
        // §bot-parallelism Phase 0: warm the shared per-stock caches once, deterministically, before the
        // sweep. Byte-identical to the prior lazy populate-on-first-read (values are frozen during collect);
        // makes the future parallel-collect region pure-read on these caches. Serial today.
        long tPre0 = phase ? Stopwatch.GetTimestamp() : 0L;
        _decisions.PrecomputeSharedTickCaches(_ctx);
        if (phase) _phCollectPreUs += (long)Stopwatch.GetElapsedTime(tPre0).TotalMicroseconds;

        var pending = new List<(AIUser user, Order order)>();
        var advanced = new List<(AIUser user, BotAdvancedDecision dec)>();
        int collectEligibleN = 0, collectDueN = 0;   // §collect-split per-bot unit-cost denominators
        long collectComputeUs = 0;                    // §collect-split heavy Compute*Async accumulator (this tick)
        foreach (var user in _ctx.AiUsersByAiUserId.Values)
        {
            if (!user.IsEnabled || !_decisions.CanPlaceMoreOrder(_ctx, user)) continue;
            // §3.7 arbitrage bots never enter the normal decision flow (sentiment / anchor / veto /
            // advanced / injection). They run in their own pass via _arbitrage.RunAsync.
            if (user.Strategy == AiStrategy.Arbitrage) continue;
            // §mm-cohort: the house market-maker cohort (strategy 6) likewise runs only via _marketMaker.RunAsync.
            // Dead branch until strategy-6 bots are seeded, so this is byte-identical when the cohort is absent.
            if (user.Strategy == AiStrategy.MarketMakerHouse) continue;
            // §rotator: the rotational cohort (strategy 7) runs only via _rotator.RunAsync. Dead branch until
            // strategy-7 bots are seeded ⇒ byte-identical when the cohort is absent.
            if (user.Strategy == AiStrategy.Rotator) continue;
            // §conviction: the conviction cohort (strategy 8) runs only via _conviction.RunAsync. Dead branch until
            // strategy-8 bots are seeded ⇒ byte-identical when the cohort is absent.
            if (user.Strategy == AiStrategy.Conviction) continue;
            collectEligibleN++;   // §collect-split: reached the O(N) full-fleet burst/bookkeeping pass

            // Spontaneous burst: rare chance (~0.2%/tick) of entering a focused session.
            var burstActive = _ctx.BurstEndTimes.TryGetValue(user.AiUserId, out var burstEnd) && now < burstEnd;
            if (!burstActive && _ctx.Decimal01(user.AiUserId) < 0.002m)
            {
                var secs = 120 + (int)(_ctx.Decimal01(user.AiUserId) * 360); // 2–8 min
                _ctx.BurstEndTimes[user.AiUserId] = now + TimeSpan.FromSeconds(secs);
                burstActive = true;
            }

            // Post-trade quiet period: more conservative bots wait longer between trades.
            var quietSecs = 60.0 - 50.0 * (double)user.AggressivenessPrc; // 10–60 s
            if (user.LastTradeTime > DateTime.MinValue &&
                (now - user.LastTradeTime).TotalSeconds < quietSecs) continue;

            // Burst halves the decision interval and boosts trade probability.
            var effectiveInterval = burstActive
                ? TimeSpan.FromSeconds(Math.Max(1.0, user.DecisionInterval.TotalSeconds * 0.5))
                : user.DecisionInterval;
            var effectiveTradeProb = burstActive
                ? Math.Min(1m, user.TradeProb * 1.5m)
                : user.TradeProb;

            // §Pillar B (Seam 1): scale how OFTEN this bot trades by the activity field G·B — hot watchlist
            // → more frequent trading, quiet lull → less. Pure multiply, NO new RNG draw, so OFF (mult ≡ 1)
            // is byte-identical and ON does not perturb the per-bot RNG order. The interval divisor is
            // clamped so a hot watchlist can't drive a bot to trade every tick (runaway/perf guard).
            var actMult = _activity.G * _activity.B(user.AiUserId, user.Strategy, user.Watchlist);
            if (actMult != 1m)
            {
                effectiveTradeProb = Math.Clamp(effectiveTradeProb * actMult, 0m, 1m);
                var div = Math.Clamp(actMult, 0.25m, 4m);
                effectiveInterval = TimeSpan.FromSeconds(Math.Max(1.0, effectiveInterval.TotalSeconds / (double)div));
            }

            // §stagger: deterministic per-bot tick-phase. A bot in slot (AiUserId % Slots) is only
            // due to act on ticks where (TickId % Slots) == its slot, cutting per-tick decision +
            // order-placement load ~Slots-fold. Pure function of (id, tick) — NO RNG, so runs stay
            // reproducible. Off (or Slots<=1) ⇒ StaggerDue is always true ⇒ byte-identical. Burst /
            // quiet / activity bookkeeping above still runs every tick, so their RNG streams and the
            // interval floor below are untouched when off.
            if (_staggerEnabled && !StaggerDue(user.AiUserId, _ctx.TickId, _staggerSlots)) continue;
            collectDueN++;   // §collect-split: past the stagger gate — the O(cap/Slots) due population

            if (now - user.LastDecisionTime < effectiveInterval) continue;

            user.RecordDecision(now);
            if (_ctx.Decimal01(user.AiUserId) > effectiveTradeProb) continue;

            // §P6a: try a protective advanced order first (entry/arm route). Disabled → returns null at the
            // top with NO seeded RNG consumed, so the plain-order stream stays byte-identical vs pre-P6.
            if (_advancedEnabled && advanced.Count < _maxAdvancedPerTick)
            {
                long ca0 = phase ? Stopwatch.GetTimestamp() : 0L;
                var adv = await _decisions.ComputeAdvancedDecisionAsync(_ctx, user, user.HomeCurrencyType, ct).ConfigureAwait(false);
                if (phase) collectComputeUs += (long)Stopwatch.GetElapsedTime(ca0).TotalMicroseconds;
                if (adv is not null) { advanced.Add((user, adv)); continue; }
            }

            // Bot decides in its home currency only.
            long co0 = phase ? Stopwatch.GetTimestamp() : 0L;
            var order = await _decisions.ComputeOrderAsync(_ctx, user, user.HomeCurrencyType, ct).ConfigureAwait(false);
            if (phase) collectComputeUs += (long)Stopwatch.GetElapsedTime(co0).TotalMicroseconds;
            if (order is not null) pending.Add((user, order));
        }
        if (phase)
        {
            _phCollectComputeUs += collectComputeUs;
            _phCollectEligibleN += collectEligibleN;
            _phCollectDueN      += collectDueN;
        }
        return (pending, advanced);
    }

    private async Task SubmitAndApplyBatchAsync(List<(AIUser user, Order order)> pending, CancellationToken ct)
    {
        var orderList = pending.Select(x => x.order).ToList();
        IReadOnlyList<OrderResult> results;
        try
        {
            results = await _marketOrders.PlaceAndMatchBatchAsync(orderList, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Bot loop is shutting down (Stop button or server exit). Don't log
            // as an error or fabricate fake failures — the orders simply weren't
            // attempted. Return so the caller's tick loop exits cleanly.
            _logger.LogInformation("Bot loop stop requested mid-batch on tick {Tick}; skipping {Count} pending order(s) cleanly.",
                _tickCount, orderList.Count);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlaceAndMatchBatchAsync failed on tick {Tick}", _tickCount);
            results = orderList
                .Select(_ => new OrderResult { Status = OrderStatus.OperationFailed, ErrorMessage = ex.Message })
                .ToList();
        }

        for (int i = 0; i < pending.Count; i++)
        {
            var (user, order) = pending[i];
            var result = results[i];

            if (!result.PlacedSuccessfully)
            {
                if (DebugMode && (!DebugUserId.HasValue || user.UserId == DebugUserId.Value))
                    _logger.LogWarning("Batch order AIUser {Id} stock {Stock}: {Status} — {Error}",
                        user.AiUserId, order.StockId, result.Status, result.ErrorMessage);
                user.RecordError();
                Interlocked.Increment(ref _failuresThisSession);
                _failures.Record(new FailureRecord(
                    TimestampUtc: TimeHelper.NowUtc(),
                    AiUserId:     user.AiUserId,
                    UserId:       user.UserId,
                    StockId:      order.StockId,
                    Side:         order.IsBuyOrder ? "Buy" : "Sell",
                    OrderType:    order.OrderType ?? string.Empty,
                    Quantity:     order.Quantity,
                    Price:        order.Price,
                    Status:       result.Status,
                    Category:     result.Status.ToCategory(),
                    ErrorMessage: result.ErrorMessage ?? string.Empty));
                continue;
            }
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Batch order AIUser {Id} stock {Stock}: {Status}",
                    user.AiUserId, order.StockId, result.Status);

            Interlocked.Increment(ref _tradesPlacedThisSession);
            _stats.RecordPlacement(order);

            if (result.FillTransactions.Count > 0)
            {
                LastTradeAtUtc = TimeHelper.NowUtc();
                decimal fillVol = 0m;
                for (int f = 0; f < result.FillTransactions.Count; f++)
                    fillVol += result.FillTransactions[f].TotalAmount;
                _stats.AddVolume(fillVol);
                // §Pillar B self-excitation: fills on this name beget more (trade clustering). No-op when the
                // activity field is disabled; accumulated and drained once per tick on the loop thread.
                _activity.RecordFill(order.StockId, result.FillTransactions.Count);
                // §fear-greed: the aggressing (batched) order is the taker; buffer its signed filled notional
                // for the flow-imbalance term. No-op when the composite is disabled.
                if (_mood.Enabled)
                    _mood.RecordTakerFlow(order.StockId, order.IsBuyOrder, fillVol);
            }

            _state.ApplyResultToCache(_ctx, result);
        }
    }

    private void RecordTickLatency(TimeSpan elapsed)
    {
        var ms = elapsed.TotalMilliseconds;
        Interlocked.Exchange(ref _lastTickWorkMicros, (long)(ms * 1000.0));

        // Loop runs single-threaded, so non-interlocked RMW is safe; Volatile.Write
        // publishes to the dashboard's reader thread.
        var prev = _tickWorkMsEwma;
        var next = prev <= 0.0 ? ms : EwmaAlpha * ms + (1.0 - EwmaAlpha) * prev;
        Volatile.Write(ref _tickWorkMsEwma, next);
    }

    // §B-P-b: EWMA of the actionable (Collect+Batch) span the scaler can act on. Same α as the full-span
    // EWMA. Telemetry only — read by the scaler solely when Bots:Scaler:ActionableSpanSizing is on.
    private void RecordActionableLatency(TimeSpan elapsed)
    {
        var ms = elapsed.TotalMilliseconds;
        var prev = _tickWorkActionableMsEwma;
        var next = prev <= 0.0 ? ms : EwmaAlpha * ms + (1.0 - EwmaAlpha) * prev;
        Volatile.Write(ref _tickWorkActionableMsEwma, next);
    }

    // Windowed per-phase breakdown (opt-in via Bots:PhaseTimingSeconds). Shows where tick time goes
    // so the scaler's active-bot ceiling can be traced to the dominant phase. Reconcile is a periodic
    // pass, so its per-tick average is diluted across the window (a spike in the window it fires).
    private void LogPhaseTiming()
    {
        if (_phTicks == 0) return;
        double n = _phTicks, k = 1000.0;
        double tot = (_phCheckUs + _phCollectUs + _phBatchUs + _phAdvUs + _phArbUs + _phCohortsUs + _phReconUs + _phMaintUs) / n / k;
        // Commit telemetry (the prod-transferable lever metric): root commits == fsync
        // round-trips this window. commits/sec is the headline; round-trips/order is
        // normalized to plain orders (the dominant commit driver — advanced arms collapse
        // into one insert via BatchArms, arb is a tiny cohort), matching the {Pend} field.
        long commitsNow  = EngineCommitMetrics.ReadCommits();
        long dCommits    = commitsNow - _cmPrevCommits;
        long tradesNow   = EngineCommitMetrics.ReadTrades();
        long dTrades     = tradesNow - _cmPrevTrades;
        double winSec    = _phaseTimingInterval > TimeSpan.Zero ? _phaseTimingInterval.TotalSeconds : n;
        double commitsPerSec   = winSec > 0 ? dCommits / winSec : 0.0;
        double tradesPerSec    = winSec > 0 ? dTrades / winSec : 0.0;
        double roundTripsPerOrder = _phPending > 0 ? (double)dCommits / _phPending : 0.0;
        // §A measurement gate: the high-water mark of concurrent root committers — if this is well above 1
        // under load the default path already amortizes fsync across committers, discounting Workstream A.
        int maxCommitters = EngineCommitMetrics.ReadMaxConcurrentCommitters();
        // §collect-split: pass is the residual O(N) full-fleet burst/bookkeeping cost (= collect − pre − compute),
        // clamped ≥0 against measurement skew. Unit costs (µs/eligible-bot for pass, µs/due-bot for compute) let
        // the collect cost be PROJECTED to a larger fleet: pass ∝ eligible N, compute ∝ due = N/Slots.
        long collectPassUsWin = Math.Max(0L, _phCollectUs - _phCollectPreUs - _phCollectComputeUs);
        double colPassBotUs = _phCollectEligibleN > 0 ? (double)collectPassUsWin / _phCollectEligibleN : 0.0;
        double colCmpDueUs  = _phCollectDueN > 0 ? (double)_phCollectComputeUs / _phCollectDueN : 0.0;
        _logger.LogInformation(
            "BotPhase [{Ticks} ticks, cap {Cap}]: {Tot:F1}ms/tick = check {Chk:F2} + collect {Col:F2} + batch {Bat:F2} + adv {Adv:F2} + arb {Arb:F2} + cohorts {Coh:F2} + recon {Rec:F2} + maint {Mnt:F2} (ms); collect-split[pre {ColPre:F2} / pass {ColPass:F2} / compute {ColCmp:F2} ms; {ColElig:F0} eligible, {ColDue:F0} due/tick; {ColPassBot:F1}µs/bot, {ColCmpDue:F1}µs/due]; {Pend:F0} orders + {AdvN:F1} adv/tick; {Commits:F0} commits ({Cps:F1}/sec, {Rto:F3} round-trips/order, {MaxC} max concurrent committers); {Trds:F0} trades ({Tps:F1}/sec)",
            _phTicks, ActiveBotCap?.ToString() ?? "all", tot,
            _phCheckUs / n / k, _phCollectUs / n / k, _phBatchUs / n / k,
            _phAdvUs / n / k, _phArbUs / n / k, _phCohortsUs / n / k, _phReconUs / n / k, _phMaintUs / n / k,
            _phCollectPreUs / n / k, collectPassUsWin / n / k, _phCollectComputeUs / n / k,
            _phCollectEligibleN / n, _phCollectDueN / n, colPassBotUs, colCmpDueUs,
            _phPending / n, _phAdvCount / n,
            (double)dCommits, commitsPerSec, roundTripsPerOrder, maxCommitters,
            (double)dTrades, tradesPerSec);
        _phCheckUs = _phCollectUs = _phBatchUs = _phAdvUs = _phArbUs = _phCohortsUs = _phReconUs = _phMaintUs = 0;
        _phCollectPreUs = _phCollectComputeUs = _phCollectEligibleN = _phCollectDueN = 0;
        _phPending = _phAdvCount = _phTicks = 0;
        _cmPrevCommits = commitsNow;
        _cmPrevTrades = tradesNow;
    }

    private void OnQuoteUpdated(object? sender, LiveQuote quote)
    {
        if (quote == null || quote.LastPrice <= 0m) return;
        var key = (quote.StockId, quote.Currency);

        // Snapshot old raw price for tick-to-tick delta.
        if (_ctx.StockPrices.TryGetValue(key, out var oldPrice) && oldPrice > 0m)
            _ctx.PreviousPrices[key] = oldPrice;

        _ctx.StockPrices[key] = quote.LastPrice;

        // EWMA smoothing. Legacy: fixed α=0.15 PER QUOTE (reacts over ~6 quotes — effectively seconds, so the
        // perceived price tracks the instantaneous one and bots counter-trade their OWN 1-min impact). When
        // SmoothedPriceHalfLifeSec > 0, use a TIME-based half-life so the perceived price lags by ~τ regardless
        // of quote rate — decoupling bot reaction from same-minute impact (the ret_acf_lag1 ceiling).
        var smoothed = _ctx.SmoothedPrices.TryGetValue(key, out var s) ? s : quote.LastPrice;
        if (_smoothedPriceHalfLifeSec <= 0.0)
        {
            _ctx.SmoothedPrices[key] = 0.85m * smoothed + 0.15m * quote.LastPrice;
        }
        else
        {
            var now = TimeHelper.NowUtc();
            double dt = _ctx.SmoothedPriceUpdatedUtc.TryGetValue(key, out var last)
                ? (now - last).TotalSeconds : 0.0;
            _ctx.SmoothedPriceUpdatedUtc[key] = now;
            decimal keep = (decimal)TimeEwmaKeep(dt, _smoothedPriceHalfLifeSec);
            _ctx.SmoothedPrices[key] = keep * smoothed + (1m - keep) * quote.LastPrice;
        }

        // §impact-decouple A: maintain the >1-min reaction reference as its OWN time-based EWMA with a
        // DEDICATED timestamp dict (never SmoothedPriceUpdatedUtc, which is empty when the smoothed half-life
        // is 0 — the prod/bake default — and would freeze dt=0 ⇒ keep=1 ⇒ ref stuck). Gated ⇒ zero cost and
        // byte-identical when off. First quote seeds ref = LastPrice ((cur-ref)=0), then converges to a
        // ~halflife-lagged price. RNG-free; per-quote-key, on the quote-drain thread (ConcurrentDictionary).
        if (_reactionRef)
        {
            var rnow = TimeHelper.NowUtc();
            var rprev = _ctx.ReactionRefPrices.TryGetValue(key, out var rp) ? rp : quote.LastPrice;
            double rdt = _ctx.ReactionRefUpdatedUtc.TryGetValue(key, out var rlast)
                ? (rnow - rlast).TotalSeconds : 0.0;
            _ctx.ReactionRefUpdatedUtc[key] = rnow;
            decimal rkeep = (decimal)TimeEwmaKeep(rdt, _reactionRefHalfLifeSec);
            _ctx.ReactionRefPrices[key] = rkeep * rprev + (1m - rkeep) * quote.LastPrice;
        }
    }

    // Time-based EWMA keep weight (weight retained on the OLD value) for elapsed dt at a given half-life:
    // 0.5^(dt/halfLife). dt≤0 or halfLife≤0 ⇒ keep 1 (no update). Pure ⇒ unit-testable.
    internal static double TimeEwmaKeep(double dtSec, double halfLifeSec)
        => (halfLifeSec <= 0.0 || dtSec <= 0.0) ? 1.0 : Math.Exp(-0.6931471805599453 * dtSec / halfLifeSec);
    #endregion

    #region Timers
    // §Pillar B: signed recent (EWMA) fractional return for a stock in the reference currency (USD, then
    // EUR), read from the price caches the loop already maintains. Drives the activity field's leverage
    // term (down>up). Pure read, no RNG; 0 when no price is known. Passed as a delegate to BotActivityService.
    private double RecentReturnForActivity(int stockId)
    {
        foreach (var ccy in CurrenciesToTrade)
        {
            if (_ctx.SmoothedPrices.TryGetValue((stockId, ccy), out var cur) && cur > 0m &&
                _ctx.PreviousPrices.TryGetValue((stockId, ccy), out var prev) && prev > 0m)
                return (double)((cur - prev) / prev);
        }
        return 0.0;
    }

    // §fear-greed: the current smoothed price (reference currency USD, then EUR) from the loop's price cache,
    // fed to the mood service's trend anchor. 0 when no price is known (mood skips the stock this tick).
    private double SmoothedPriceForMood(int stockId)
    {
        foreach (var ccy in CurrenciesToTrade)
            if (_ctx.SmoothedPrices.TryGetValue((stockId, ccy), out var cur) && cur > 0m)
                return (double)cur;
        return 0.0;
    }

    // §impact-decouple A: the sentiment price-reaction's return measured against the >1-min reference instead
    // of the ~1s prior price. Mirrors RecentReturnForActivity's first-match currency selection EXACTLY, then
    // uses the reference for THAT same currency, falling back to the legacy (cur-prev)/prev when its ref is
    // unseeded (never crosses to a different currency's ref). Decimal-divide-then-(double)-cast, as the
    // original — so with ref==prev it is bit-for-bit RecentReturnForActivity. Wired only when the flag is on.
    private double ReactionReturnForSentiment(int stockId)
    {
        foreach (var ccy in CurrenciesToTrade)
        {
            if (_ctx.SmoothedPrices.TryGetValue((stockId, ccy), out var cur) && cur > 0m &&
                _ctx.PreviousPrices.TryGetValue((stockId, ccy), out var prev) && prev > 0m)
            {
                var baseline = _ctx.ReactionRefPrices.TryGetValue((stockId, ccy), out var rr) && rr > 0m ? rr : prev;
                return (double)((cur - baseline) / baseline);
            }
        }
        return 0.0;
    }

    // §impact-decouple A liveliness: prove the reference is genuinely decoupled (NOT tracking cur). Logs the
    // mean |return vs the >1-min reference| against the mean |return vs the ~1s prior price| over seeded keys.
    // ratio≈1 ⇒ A is inert (e.g. the dt=0/keep=1 failure). Loop-thread read, RNG-free, self-contained (does
    // not touch the memoized decision paths).
    private void LogReactionRefDivergence()
    {
        double sumRef = 0.0, sumPrev = 0.0;
        int n = 0;
        foreach (var kv in _ctx.ReactionRefPrices)
        {
            var key = kv.Key;
            var refp = kv.Value;
            if (refp <= 0m) continue;
            if (!_ctx.SmoothedPrices.TryGetValue(key, out var cur) || cur <= 0m) continue;
            sumRef += Math.Abs((double)((cur - refp) / refp));
            if (_ctx.PreviousPrices.TryGetValue(key, out var prev) && prev > 0m)
                sumPrev += Math.Abs((double)((cur - prev) / prev));
            n++;
        }
        if (n == 0) return;
        double mref = sumRef / n, mprev = sumPrev / n;
        _logger.LogInformation(
            "REACTIONREF n={N} meanAbsRefRet={A:0.00000} meanAbsPrevRet={B:0.00000} ratio={R:0.000}",
            n, mref, mprev, mprev > 0 ? mref / mprev : 0.0);
    }

    private async Task CheckTimers(DateTime now, CancellationToken ct)
    {
        // FX before sentiment: nothing reads FX inside Tick today, but keeping
        // it first matches the "advance external state before consumers" rule.
        _fxRates.Tick(now);
        _sentiment.Tick(now);
        _regime.Tick(now);    // §A2/A3/A4 regime (no-op when all consumers disabled)
        _activity.Tick(now);  // §Pillar B activity field (no-op when disabled); reads sentiment shock above
        _news.Tick(now);    // §exogenous-information: decay + arrive news shocks before consumers read them
        _bank.Tick(now);    // §bank-estimate: republish estimates AFTER sentiment/news, BEFORE _funds reads them
        _funds.Tick(now);   // §P6: advance the slowly-drifting fundamentals (internally gated to its interval)
        _priceMemory.Tick(now); // EWMA + day-TWAP; short-circuits at top when anyConsumer=false
        // §fear-greed: fold this tick's per-stock returns + (prior-tick) taker flow into the composite, then
        // rescore. Depends on _sentiment/_activity having advanced above; gated so it's byte-identical when off.
        // Two passes: Observe all (momentum EWMA) → breadth scan → Score all. Taker flow is buffered during the
        // batch phase (RecordTakerFlow) and drained here on the next tick (1-tick lag, negligible).
        if (_mood.Enabled)
        {
            _mood.Tick(now);
            foreach (var sid in _stocks.ById.Keys) _mood.Observe(sid, SmoothedPriceForMood(sid));
            double breadth = _mood.ComputeBreadth();
            double pooledSigma = _mood.ComputePooledSigma();
            foreach (var sid in _stocks.ById.Keys) _mood.Score(sid, breadth, pooledSigma, (double)_sentiment.GetSentiment(sid));
            if (now >= _nextMoodLog)
            {
                var (mean, mn, mx, hist) = _mood.Distribution();
                _logger.LogInformation("MOOD mean={Mean:0.0} min={Min:0.0} max={Max:0.0} breadth={Breadth:0.00} hist=[{H0},{H1},{H2},{H3},{H4}]",
                    mean, mn, mx, breadth, hist[0], hist[1], hist[2], hist[3], hist[4]);
                _nextMoodLog = now + TimeSpan.FromSeconds(60);
            }
        }
        if (now >= _nextDailyCheck)
        {
            _state.CheckDailyRefresh(_ctx);
            _nextDailyCheck = now + DailyCheckInterval;
        }
    }

    // Periodic, amortized maintenance — heavy, gated, and deliberately kept OUT of CheckTimers so it can
    // run AFTER RecordTickLatency (see RunLoopAsync) and never feed the scaler EWMA. These walks
    // (RefreshAssetsAsync, PruneWorstOrdersAsync, LogSnapshot O(bots×stocks)) used to spike a single tick
    // to ~100% load and crater the active-bot cap. Still runs on the single-threaded loop thread, so the
    // lock-free AiBotContext / AccountsCache mutation discipline is unchanged (no new races).
    private async Task RunPeriodicMaintenanceAsync(DateTime now, CancellationToken ct)
    {
        if (now >= _nextAssetReload)
        {
            await _state.RefreshAssetsAsync(_ctx, ct).ConfigureAwait(false);
            _nextAssetReload = now + ReloadAssetsInterval;
        }
        if (now >= _nextPruneTime)
        {
            await _state.PruneWorstOrdersAsync(_ctx, ct).ConfigureAwait(false);
            _nextPruneTime = now + PruneInterval;
        }
        if (now >= _nextStatsLogTime)
        {
            _stats.LogWindow(OnlineBotCount, LoadedBotCount);
            _nextStatsLogTime = now + StatsLogInterval;
        }
        if (now >= _nextEconomyLogTime)
        {
            _economy.LogSnapshot(CurrenciesToTrade);
            _nextEconomyLogTime = now + _economyLogInterval;
        }
        if (now >= _nextSentimentLogTime)
        {
            _sentiment.LogSnapshot();
            _news.LogSnapshot(); // §exogenous-information: durable per-stock shock series for the soak harvester
            // §impact-decouple liveliness (so an inert flag is caught in ~1 min, not after a 150-min soak).
            if (_reactionRef) LogReactionRefDivergence();
            if (ImpactHoldProbe.Enabled)
            {
                var (held, recomputed, heldFrac) = ImpactHoldProbe.Drain();
                _logger.LogInformation("IMPACTHOLD held={Held} recomputed={Recomp} heldFrac={Frac:0.000}",
                    held, recomputed, heldFrac);
            }
            if (ArmedStopCapProbe.Enabled)
            {
                // §source-cap liveliness: blocked>0 once the pool reaches the cap confirms the gate fires;
                // poolTotal/maxPerBot show the pool bounded at ≈ cap×fleet. blocked==0 with a large pool = the
                // inert-flag trap (increment wired to the wrong path). Pool read from the loop-owned dict.
                var (blocked, armed) = ArmedStopCapProbe.Drain();
                long poolTotal = 0; int maxPerBot = 0;
                foreach (var c in _ctx.ArmedStopCount.Values) { poolTotal += c; if (c > maxPerBot) maxPerBot = c; }
                _logger.LogInformation("ARMEDCAP blocked={Blocked} armed={Armed} poolTotal={Pool} maxPerBot={Max}",
                    blocked, armed, poolTotal, maxPerBot);
            }
            if (ChaserProbe.Enabled)
            {
                var (buyOrders, sellOrders, buySupp, sellSupp, buyNotional, sellNotional, netNotional, grossNotional)
                    = ChaserProbe.Drain();
                // §chaser-v2 separability readout: WIN ⇒ netNotional → 0 (drift removed) while grossNotional stays
                // ~flat vs OFF (acf-driving volume retained). Per-side counts make a one-sided lean obvious.
                _logger.LogInformation(
                    "CHASER buyOrders={Bo} sellOrders={So} buySuppressed={Bs} sellSuppressed={Ss} " +
                    "buyNotional={Bn:0.00} sellNotional={Sn:0.00} netNotional={Net:0.00} grossNotional={Gross:0.00}",
                    buyOrders, sellOrders, buySupp, sellSupp, buyNotional, sellNotional, netNotional, grossNotional);
            }
            if (MarketMakerProbe.Enabled)
            {
                var (bidOrders, askOrders, bidShares, askShares, bidNotional, askNotional) = MarketMakerProbe.Drain();
                // §mm-cohort smoking-gun: net bot inventory should DRAIN (net selling) on a one-sided book and
                // FLATTEN once the MM cohort absorbs/supplies. mmNet is the cohort's own (skew should keep it ~0).
                var (mmNet, netBot) = SumBotInventory();
                _logger.LogInformation(
                    "MM bidOrders={Bo} askOrders={Ao} bidShares={Bsh} askShares={Ash} " +
                    "bidNotional={Bn:0.00} askNotional={An:0.00} mmNetInventory={MmNet} netBotInventory={NetBot}",
                    bidOrders, askOrders, bidShares, askShares, bidNotional, askNotional, mmNet, netBot);
            }
            if (JumpsProbe.Enabled)
            {
                // §fat-tail jumps liveliness: fired>0 in the first ~1 min catches the inert-flag trap; meanPct
                // confirms events reach target magnitude; net≈0 confirms the per-event-random sign stays drift-neutral.
                var (fired, suppressed, meanPct, buyEv, sellEv, net, gross, aftershocks) = JumpsProbe.Drain();
                _logger.LogInformation(
                    "JUMP fired={F} suppressed={S} meanPct={M:0.000} buy={B} sell={Se} " +
                    "net={N:0.00} gross={G:0.00} aftershocks={A}",
                    fired, suppressed, meanPct, buyEv, sellEv, net, gross, aftershocks);
            }
            if (RefillThrottleProbe.Enabled)
            {
                // §refill-throttle liveliness: widen/skips>0 in the first ~1 min confirms the gate ARMED on a
                // mover (the inert-flag trap that sank a prior lever); resistBuy/resistSell should stay balanced
                // (a one-sided lean would inject drift, the BuyStopFraction symptom).
                var (widen, skips, skipDraws, buySkips, sellSkips) = RefillThrottleProbe.Drain();
                _logger.LogInformation(
                    "REFILL widen={W} skips={Sk} skipDraws={Sd} resistBuy={Rb} resistSell={Rs}",
                    widen, skips, skipDraws, buySkips, sellSkips);
            }
            if (ActivityCompositionProbe.Enabled)
            {
                // §composition liveliness: eligible>0 + up/down>0 in the first minutes confirms the taker
                // override reaches the hot path (the inert-flag trap); a sustained one-sided buy/sell upgrade
                // split is the drift-lean tell to watch alongside the drift gate.
                var (eligible, ups, downs, buyUps, sellUps) = ActivityCompositionProbe.Drain();
                _logger.LogInformation(
                    "ACTCOMP eligible={E} up={U} down={D} buyUp={Bu} sellUp={Su}",
                    eligible, ups, downs, buyUps, sellUps);
            }
            _nextSentimentLogTime = now + _sentimentLogInterval;
        }
        if (now >= _nextCashInjectionTime)
        {
            await _injector.RunAsync(ct).ConfigureAwait(false);
            _nextCashInjectionTime = now + _cashInjectionInterval;
        }
    }

    // §mm-cohort: point-in-time signed-share sums for the MarketMakerProbe log — (cohort MM net, fleet-wide net).
    // Walks the per-user stock index against the in-memory accounts cache; called only at the sentiment-log
    // interval, so the O(bots×stocks) walk is off the per-tick hot path.
    private (long MmNet, long NetBot) SumBotInventory()
    {
        long mmNet = 0, netBot = 0;
        foreach (var user in _ctx.AiUsersByAiUserId.Values)
        {
            if (!_ctx.StocksByUser.TryGetValue(user.UserId, out var stocks)) continue;
            long userNet = 0;
            foreach (var sid in stocks)
                userNet += _accounts.GetPosition(user.UserId, sid)?.Quantity ?? 0;
            netBot += userNet;
            if (user.Strategy == AiStrategy.MarketMakerHouse) mmNet += userNet;
        }
        return (mmNet, netBot);
    }

    // Price-memory anchors §: case-insensitive enum parse for the appsettings key.
    // Unknown values fall back to ServiceStart so a typo doesn't break the run.
    private static DayBoundaryMode ParseDayBoundary(string s)
        => string.Equals(s, "UtcMidnight", StringComparison.OrdinalIgnoreCase)
            ? DayBoundaryMode.UtcMidnight
            : DayBoundaryMode.ServiceStart;

    #endregion

    #region IAsyncDisposable
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing AiTradeService...");
        _market.QuoteUpdated -= OnQuoteUpdated;
        await StopBotAsync().ConfigureAwait(false);
    }
    #endregion
}
