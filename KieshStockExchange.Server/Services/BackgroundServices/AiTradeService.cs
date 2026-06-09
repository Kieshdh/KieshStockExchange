using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
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
    // Economy snapshot: 60s is frequent enough to spot drift trends without the
    // per-call walk (bots × positions) crowding the log.
    private static readonly TimeSpan EconomyLogInterval = TimeSpan.FromSeconds(60);
    // Sentiment snapshot cadence (config Bots:SentimentLogIntervalSeconds, default 60). Lower it (e.g.
    // 15s) for a denser sentiment/price correlation export; higher to thin production telemetry.
    private readonly TimeSpan _sentimentLogInterval;
    // Cash injection: 1-hour nominal-growth driver; per-bot frequency knob
    // gates each bot's actual deposit within the cycle.
    private static readonly TimeSpan CashInjectionInterval = TimeSpan.FromHours(1);

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
    private long _lastTickWorkMicros = 0;

    // Per-phase tick profiling (opt-in via Bots:PhaseTimingSeconds > 0). Accumulates µs per phase
    // across the window, logs the average breakdown so "what takes the most time" is observable
    // without an external profiler. Single-threaded loop → plain fields are safe.
    private TimeSpan _phaseTimingInterval = TimeSpan.Zero;
    private DateTime _nextPhaseLogTime = DateTime.MaxValue;
    private long _phCheckUs, _phCollectUs, _phBatchUs, _phAdvUs, _phArbUs, _phReconUs;
    private long _phPending, _phAdvCount, _phTicks;

    private readonly AiBotContext         _ctx;
    private readonly AiBotStateService    _state;
    private readonly AiBotDecisionService _decisions;
    private readonly BotScalerService     _scaler;
    private readonly BotFailureTracker    _failures;
    private readonly BotStatsLogger       _stats;
    private readonly ReservationAuditor   _auditor;
    private readonly BotEconomyTelemetry  _economy;
    private readonly BotSentimentService  _sentiment;
    private readonly FundamentalService   _funds;     // §P6 slowly-drifting fundamentals
    private readonly StockProfileService  _profiles;  // §P6 per-stock personality
    private readonly BotCashInjector      _injector;
    private readonly ArbitrageDecisionService _arbitrage; // §3.7 cohort runs OUT of the normal path
    private readonly bool                 _arbitrageEnabled; // §3.7 Bots:Arbitrage:Enabled kill-switch
    private readonly FxDeskTelemetry      _fxDesk;        // §3.7 session conversion data (reset on Start)
    private readonly int                  _houseUserId;   // §3.7 platform FX-desk account (warm-loaded)
    #endregion

    #region Services and Constructor
    private readonly IOrderExecutionService _marketOrders;
    private readonly IOrderEntryService     _entry;   // §P6: stop/trailing/bracket entry route (not the batch matcher)
    private readonly IMarketDataService     _market;
    private readonly IStockService          _stocks;
    private readonly IAccountsCache         _accounts;
    private readonly IFxRateService         _fxRates;
    private readonly ILogger<AiTradeService> _logger;
    private readonly IConfiguration         _configuration;
    private readonly int  _maxAdvancedPerTick;   // §P6a cap on entry-route submissions per tick
    private readonly bool _advancedEnabled;       // §P6a master switch (default off)

    public AiTradeService(
        IOrderExecutionService marketOrders,
        IOrderEntryService entry,
        IMarketDataService market,
        IStockService stocks,
        IDataBaseService db,
        IAccountsCache accounts,
        IReservationLedger ledger,
        IOrderBookEngine books,
        IUserPortfolioService portfolio,
        IFxRateService fxRates,
        FxDeskTelemetry fxDesk,
        ILogger<AiTradeService> logger,
        ILoggerFactory loggerFactory,
        IOptions<SeparatorLoggerOptions> loggerOptions,
        IConfiguration configuration)
    {
        _marketOrders = marketOrders ?? throw new ArgumentNullException(nameof(marketOrders));
        _entry        = entry        ?? throw new ArgumentNullException(nameof(entry));
        _market       = market       ?? throw new ArgumentNullException(nameof(market));
        _stocks       = stocks       ?? throw new ArgumentNullException(nameof(stocks));
        _accounts     = accounts     ?? throw new ArgumentNullException(nameof(accounts));
        _fxRates      = fxRates      ?? throw new ArgumentNullException(nameof(fxRates));
        _fxDesk       = fxDesk       ?? throw new ArgumentNullException(nameof(fxDesk));
        _logger       = logger       ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (db          is null) throw new ArgumentNullException(nameof(db));
        if (ledger      is null) throw new ArgumentNullException(nameof(ledger));
        if (books       is null) throw new ArgumentNullException(nameof(books));
        if (portfolio   is null) throw new ArgumentNullException(nameof(portfolio));
        if (loggerFactory  is null) throw new ArgumentNullException(nameof(loggerFactory));
        if (loggerOptions  is null) throw new ArgumentNullException(nameof(loggerOptions));

        _sentimentLogInterval = TimeSpan.FromSeconds(
                        Math.Max(1.0, _configuration.GetValue("Bots:SentimentLogIntervalSeconds", 60.0)));
        _phaseTimingInterval = TimeSpan.FromSeconds(
                        Math.Max(0.0, _configuration.GetValue("Bots:PhaseTimingSeconds", 0.0)));
        _ctx       = new AiBotContext(accounts,
                        personalSentiment: _configuration.GetValue("Bots:PersonalSentiment", true));
        _stats     = new BotStatsLogger(new SeparatorLogger<BotStatsLogger>(loggerFactory, loggerOptions));
        _failures  = new BotFailureTracker(stocks, new SeparatorLogger<BotFailureTracker>(loggerFactory, loggerOptions));
        _auditor   = new ReservationAuditor(accounts, ledger, new SeparatorLogger<ReservationAuditor>(loggerFactory, loggerOptions),
                        phantomWarnThreshold: _configuration.GetValue("Bots:ReservationPhantomWarnThreshold", 5.0m));
        _houseUserId = _configuration.GetValue("Platform:HouseUserId", 20002);
        _economy   = new BotEconomyTelemetry(_ctx, accounts, stocks, fxRates,
                        new SeparatorLogger<BotEconomyTelemetry>(loggerFactory, loggerOptions),
                        houseUserId:     _houseUserId,
                        drainCeilingPct: _configuration.GetValue("Bots:Arbitrage:ValueDrainCeilingPct", 5.0m));
        // §P6 liveliness: per-stock personality + slowly-drifting fundamentals. Built before the
        // sentiment + decision services because both consume them.
        _profiles  = new StockProfileService(
                        enabled: _configuration.GetValue("Bots:Personality:Enabled", true));
        _funds     = new FundamentalService(stocks, _profiles,
                        new SeparatorLogger<FundamentalService>(loggerFactory, loggerOptions),
                        enabled:          _configuration.GetValue("Bots:Fundamental:Enabled", true),
                        band:             _configuration.GetValue("Bots:Fundamental:Band", 0.12m),
                        theta:            _configuration.GetValue("Bots:Fundamental:Theta", 0.02),
                        sigma:            _configuration.GetValue("Bots:Fundamental:Sigma", 0.004),
                        driftIntervalSec: _configuration.GetValue("Bots:Fundamental:DriftIntervalSeconds", 60.0));
        _sentiment = new BotSentimentService(stocks, _profiles, new SeparatorLogger<BotSentimentService>(loggerFactory, loggerOptions),
                        newsEvents:              _configuration.GetValue("Bots:NewsEvents", true),
                        shockMeanIntervalHours:  _configuration.GetValue("Bots:ShockMeanIntervalHours", 6.0),
                        shockMinMagnitude:       _configuration.GetValue("Bots:ShockMinMagnitude", 0.3m),
                        shockMaxMagnitude:       _configuration.GetValue("Bots:ShockMaxMagnitude", 1.5m),
                        shockMagnitudeExponent:  _configuration.GetValue("Bots:ShockMagnitudeExponent", 3.0),
                        shockDecayPerTick:       _configuration.GetValue("Bots:ShockDecayPerTick", 0.999m));
        _injector  = new BotCashInjector(_ctx, portfolio, _economy,
                        new SeparatorLogger<BotCashInjector>(loggerFactory, loggerOptions));
        // §3.7 arbitrage cohort: dedicated decision path, fully outside the sentiment/anchor/veto/
        // injection flow. Reuses the engine market-order route + the platform FX desk.
        _arbitrageEnabled = _configuration.GetValue("Bots:Arbitrage:Enabled", true);
        _arbitrage = new ArbitrageDecisionService(entry, books, accounts, fxRates, portfolio, stocks, _economy,
                        new SeparatorLogger<ArbitrageDecisionService>(loggerFactory, loggerOptions),
                        conversionSkewBand: _configuration.GetValue("Bots:Arbitrage:ConversionSkewBand", 0.15m));
        _state     = new AiBotStateService(db, accounts, marketOrders, _stats,
                        new SeparatorLogger<AiBotStateService>(loggerFactory, loggerOptions),
                        distanceMult: _configuration.GetValue("Bots:DecisionDistanceMult", 1m));
        _decisions = new AiBotDecisionService(market, accounts, books, stocks, _sentiment, _funds, _profiles,
                        new SeparatorLogger<AiBotDecisionService>(loggerFactory, loggerOptions),
                        fatTails:           _configuration.GetValue("Bots:FatTails", true),
                        tradeSizeTailShape: _configuration.GetValue("Bots:TradeSizeTailShape", 0.5m),
                        blockTradeProb:     _configuration.GetValue("Bots:BlockTradeProb", 0.01m),
                        blockTradeMultiple: _configuration.GetValue("Bots:BlockTradeMultiple", 4m),
                        mmQuoting:          _configuration.GetValue("Bots:MarketMakerQuoting", true),
                        quoteHalfSpreadPrc: _configuration.GetValue("Bots:QuoteHalfSpreadPrc", 0.003m),
                        limitOffsetMult:    _configuration.GetValue("Bots:Liquidity:OffsetMult", 1m),
                        distanceMult:       _configuration.GetValue("Bots:DecisionDistanceMult", 1m),
                        maxOpenOrdersMult:  _configuration.GetValue("Bots:Liquidity:MaxOpenMult", 1m),
                        valueAnchorStrength: _configuration.GetValue("Bots:ValueAnchor:Strength", 0m),
                        valueAnchorScale:    _configuration.GetValue("Bots:ValueAnchor:Scale", 0.15m),
                        valueTargetSelection: _configuration.GetValue("Bots:ValueAnchor:TargetSelection", false),
                        overheatCap:         _configuration.GetValue("Bots:ValueAnchor:OverheatCap", 0m),
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
                        advancedMaxQty:     _configuration.GetValue("Bots:Advanced:MaxQty", 50));
        _maxAdvancedPerTick = _configuration.GetValue("Bots:Advanced:MaxPerTick", 50);
        _advancedEnabled    = _configuration.GetValue("Bots:Advanced:Enabled", false);
        _scaler    = new BotScalerService(new SeparatorLogger<BotScalerService>(loggerFactory, loggerOptions));

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

    public IReadOnlyList<BotActivitySample> GetActivitySamples()
    {
        lock (_activitySamplesLock) return _activitySamples.ToArray();
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
        _funds.Reset();   // §P6: re-seed fundamentals at the listing seed prices for this session
        _fxDesk.Reset();  // §3.7: fresh per-session FX-desk conversion tallies

        _nextStatsLogTime      = TimeHelper.NowUtc() + StatsLogInterval;
        _nextPhaseLogTime      = _phaseTimingInterval > TimeSpan.Zero ? TimeHelper.NowUtc() + _phaseTimingInterval : DateTime.MaxValue;
        _phCheckUs = _phCollectUs = _phBatchUs = _phAdvUs = _phArbUs = _phReconUs = 0;
        _phPending = _phAdvCount = _phTicks = 0;
        _nextReconcileTime     = TimeHelper.NowUtc() + ReconcileFirstDelay;
        _nextEconomyLogTime    = TimeHelper.NowUtc() + EconomyLogInterval;
        _nextSentimentLogTime  = TimeHelper.NowUtc() + _sentimentLogInterval;
        _nextCashInjectionTime = TimeHelper.NowUtc() + CashInjectionInterval;
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
        if (botUserIds.Count > 0)
            await _accounts.EnsureLoadedAsync(botUserIds, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
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

                RecordTickLatency(Stopwatch.GetElapsedTime(tickStart));
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
                    var clamp = _configuration.GetValue("Bots:ReconcileClamp", true);
                    var tReconStart = Stopwatch.GetTimestamp();
                    try { await _auditor.AuditAsync(clamp, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex) { _logger.LogError(ex, "Reservation reconcile pass failed."); }
                    reconUs = (long)Stopwatch.GetElapsedTime(tReconStart).TotalMicroseconds;
                }

                // Per-phase profiling (opt-in): accumulate this tick, log the windowed average breakdown.
                if (_phaseTimingInterval > TimeSpan.Zero)
                {
                    _phCheckUs   += (long)Stopwatch.GetElapsedTime(tickStart, tCheck).TotalMicroseconds;
                    _phCollectUs += (long)Stopwatch.GetElapsedTime(tCheck, tCollect).TotalMicroseconds;
                    _phBatchUs   += (long)Stopwatch.GetElapsedTime(tCollect, tBatch).TotalMicroseconds;
                    _phAdvUs     += (long)Stopwatch.GetElapsedTime(tBatch, tAdv).TotalMicroseconds;
                    _phArbUs     += (long)Stopwatch.GetElapsedTime(tAdv, tArb).TotalMicroseconds;
                    _phReconUs   += reconUs;
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

            try { await Task.Delay(TradeInterval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { /* breaking loop */ }
        }
    }

    // §P6a: submit protective stop/trailing orders through the entry/arm route — sequentially, in ascending
    // aiUserId order (part of the seed-determinism contract), each call owning its own book→fund→position
    // gates while the loop holds none. Runs after the plain batch, outside the matcher's locked region.
    private async Task SubmitAdvancedAsync(List<(AIUser user, BotAdvancedDecision dec)> advanced, CancellationToken ct)
    {
        advanced.Sort((a, b) => a.user.AiUserId.CompareTo(b.user.AiUserId));
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
                            ct, OrderSide.Buy).ConfigureAwait(false),
                    // §P6c short bracket (flat market sell + slippage-capped buy-stop SL above + buy-limit TPs below).
                    BotAdvancedKind.ShortBracket =>
                        await _entry.PlaceBracketAsync(
                            user.UserId, d.StockId, d.Quantity, EntryType.Market, d.Currency,
                            limitPrice: null, buyBudget: null, stopPrice: d.StopPrice,
                            stopLimitPrice: null, stopSlippagePct: d.StopSlippagePct, takeProfits: d.TakeProfits!,
                            ct, OrderSide.Sell).ConfigureAwait(false),
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

    private async Task<(List<(AIUser user, Order order)> Plain, List<(AIUser user, BotAdvancedDecision dec)> Advanced)>
        CollectPendingOrdersAsync(DateTime now, CancellationToken ct)
    {
        var pending = new List<(AIUser user, Order order)>();
        var advanced = new List<(AIUser user, BotAdvancedDecision dec)>();
        foreach (var user in _ctx.AiUsersByAiUserId.Values)
        {
            if (!user.IsEnabled || !_decisions.CanPlaceMoreOrder(_ctx, user)) continue;
            // §3.7 arbitrage bots never enter the normal decision flow (sentiment / anchor / veto /
            // advanced / injection). They run in their own pass via _arbitrage.RunAsync.
            if (user.Strategy == AiStrategy.Arbitrage) continue;

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

            if (now - user.LastDecisionTime < effectiveInterval) continue;

            user.RecordDecision(now);
            if (_ctx.Decimal01(user.AiUserId) > effectiveTradeProb) continue;

            // §P6a: try a protective advanced order first (entry/arm route). Disabled → returns null at the
            // top with NO seeded RNG consumed, so the plain-order stream stays byte-identical vs pre-P6.
            if (_advancedEnabled && advanced.Count < _maxAdvancedPerTick)
            {
                var adv = await _decisions.ComputeAdvancedDecisionAsync(_ctx, user, user.HomeCurrencyType, ct).ConfigureAwait(false);
                if (adv is not null) { advanced.Add((user, adv)); continue; }
            }

            // Bot decides in its home currency only.
            var order = await _decisions.ComputeOrderAsync(_ctx, user, user.HomeCurrencyType, ct).ConfigureAwait(false);
            if (order is not null) pending.Add((user, order));
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

    // Windowed per-phase breakdown (opt-in via Bots:PhaseTimingSeconds). Shows where tick time goes
    // so the scaler's active-bot ceiling can be traced to the dominant phase. Reconcile is a periodic
    // pass, so its per-tick average is diluted across the window (a spike in the window it fires).
    private void LogPhaseTiming()
    {
        if (_phTicks == 0) return;
        double n = _phTicks, k = 1000.0;
        double tot = (_phCheckUs + _phCollectUs + _phBatchUs + _phAdvUs + _phArbUs + _phReconUs) / n / k;
        _logger.LogInformation(
            "BotPhase [{Ticks} ticks, cap {Cap}]: {Tot:F1}ms/tick = check {Chk:F2} + collect {Col:F2} + batch {Bat:F2} + adv {Adv:F2} + arb {Arb:F2} + recon {Rec:F2} (ms); {Pend:F0} orders + {AdvN:F1} adv/tick",
            _phTicks, ActiveBotCap?.ToString() ?? "all", tot,
            _phCheckUs / n / k, _phCollectUs / n / k, _phBatchUs / n / k,
            _phAdvUs / n / k, _phArbUs / n / k, _phReconUs / n / k,
            _phPending / n, _phAdvCount / n);
        _phCheckUs = _phCollectUs = _phBatchUs = _phAdvUs = _phArbUs = _phReconUs = 0;
        _phPending = _phAdvCount = _phTicks = 0;
    }

    private void OnQuoteUpdated(object? sender, LiveQuote quote)
    {
        if (quote == null || quote.LastPrice <= 0m) return;
        var key = (quote.StockId, quote.Currency);

        // Snapshot old raw price for tick-to-tick delta.
        if (_ctx.StockPrices.TryGetValue(key, out var oldPrice) && oldPrice > 0m)
            _ctx.PreviousPrices[key] = oldPrice;

        _ctx.StockPrices[key] = quote.LastPrice;

        // EWMA smoothing (α=0.15): reacts over ~6 ticks, dampens spike noise.
        var smoothed = _ctx.SmoothedPrices.TryGetValue(key, out var s) ? s : quote.LastPrice;
        _ctx.SmoothedPrices[key] = 0.85m * smoothed + 0.15m * quote.LastPrice;
    }
    #endregion

    #region Timers
    private async Task CheckTimers(DateTime now, CancellationToken ct)
    {
        // FX before sentiment: nothing reads FX inside Tick today, but keeping
        // it first matches the "advance external state before consumers" rule.
        _fxRates.Tick(now);
        _sentiment.Tick(now);
        _funds.Tick(now);   // §P6: advance the slowly-drifting fundamentals (internally gated to its interval)
        if (now >= _nextDailyCheck)
        {
            _state.CheckDailyRefresh(_ctx);
            _nextDailyCheck = now + DailyCheckInterval;
        }
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
            _nextEconomyLogTime = now + EconomyLogInterval;
        }
        if (now >= _nextSentimentLogTime)
        {
            _sentiment.LogSnapshot();
            _nextSentimentLogTime = now + _sentimentLogInterval;
        }
        if (now >= _nextCashInjectionTime)
        {
            await _injector.RunAsync(ct).ConfigureAwait(false);
            _nextCashInjectionTime = now + CashInjectionInterval;
        }
    }

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
