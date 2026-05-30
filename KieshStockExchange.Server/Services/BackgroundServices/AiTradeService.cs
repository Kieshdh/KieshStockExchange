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
    // Sentiment snapshot: matches the economy cadence so the two CSVs can be joined on timestamp.
    private static readonly TimeSpan SentimentLogInterval = TimeSpan.FromSeconds(60);
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

    private readonly AiBotContext         _ctx;
    private readonly AiBotStateService    _state;
    private readonly AiBotDecisionService _decisions;
    private readonly BotScalerService     _scaler;
    private readonly BotFailureTracker    _failures;
    private readonly BotStatsLogger       _stats;
    private readonly ReservationAuditor   _auditor;
    private readonly BotEconomyTelemetry  _economy;
    private readonly BotSentimentService  _sentiment;
    private readonly BotCashInjector      _injector;
    #endregion

    #region Services and Constructor
    private readonly IOrderExecutionService _marketOrders;
    private readonly IMarketDataService     _market;
    private readonly IStockService          _stocks;
    private readonly IAccountsCache         _accounts;
    private readonly IFxRateService         _fxRates;
    private readonly ILogger<AiTradeService> _logger;
    private readonly IConfiguration         _configuration;

    public AiTradeService(
        IOrderExecutionService marketOrders,
        IMarketDataService market,
        IStockService stocks,
        IDataBaseService db,
        IAccountsCache accounts,
        IReservationLedger ledger,
        IOrderBookEngine books,
        IUserPortfolioService portfolio,
        IFxRateService fxRates,
        ILogger<AiTradeService> logger,
        ILoggerFactory loggerFactory,
        IOptions<SeparatorLoggerOptions> loggerOptions,
        IConfiguration configuration)
    {
        _marketOrders = marketOrders ?? throw new ArgumentNullException(nameof(marketOrders));
        _market       = market       ?? throw new ArgumentNullException(nameof(market));
        _stocks       = stocks       ?? throw new ArgumentNullException(nameof(stocks));
        _accounts     = accounts     ?? throw new ArgumentNullException(nameof(accounts));
        _fxRates      = fxRates      ?? throw new ArgumentNullException(nameof(fxRates));
        _logger       = logger       ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (db          is null) throw new ArgumentNullException(nameof(db));
        if (ledger      is null) throw new ArgumentNullException(nameof(ledger));
        if (books       is null) throw new ArgumentNullException(nameof(books));
        if (portfolio   is null) throw new ArgumentNullException(nameof(portfolio));
        if (loggerFactory  is null) throw new ArgumentNullException(nameof(loggerFactory));
        if (loggerOptions  is null) throw new ArgumentNullException(nameof(loggerOptions));

        _ctx       = new AiBotContext(accounts,
                        personalSentiment: _configuration.GetValue("Bots:PersonalSentiment", true));
        _stats     = new BotStatsLogger(new SeparatorLogger<BotStatsLogger>(loggerFactory, loggerOptions));
        _failures  = new BotFailureTracker(stocks, new SeparatorLogger<BotFailureTracker>(loggerFactory, loggerOptions));
        _auditor   = new ReservationAuditor(accounts, ledger, new SeparatorLogger<ReservationAuditor>(loggerFactory, loggerOptions));
        _economy   = new BotEconomyTelemetry(_ctx, accounts, stocks, fxRates, new SeparatorLogger<BotEconomyTelemetry>(loggerFactory, loggerOptions));
        _sentiment = new BotSentimentService(stocks, new SeparatorLogger<BotSentimentService>(loggerFactory, loggerOptions),
                        newsEvents:              _configuration.GetValue("Bots:NewsEvents", true),
                        shockMeanIntervalHours:  _configuration.GetValue("Bots:ShockMeanIntervalHours", 6.0),
                        shockMinMagnitude:       _configuration.GetValue("Bots:ShockMinMagnitude", 0.3m),
                        shockMaxMagnitude:       _configuration.GetValue("Bots:ShockMaxMagnitude", 1.5m),
                        shockMagnitudeExponent:  _configuration.GetValue("Bots:ShockMagnitudeExponent", 3.0),
                        shockDecayPerTick:       _configuration.GetValue("Bots:ShockDecayPerTick", 0.999m));
        _injector  = new BotCashInjector(_ctx, portfolio, _economy,
                        new SeparatorLogger<BotCashInjector>(loggerFactory, loggerOptions));
        _state     = new AiBotStateService(db, accounts, marketOrders, _stats,
                        new SeparatorLogger<AiBotStateService>(loggerFactory, loggerOptions));
        _decisions = new AiBotDecisionService(market, accounts, books, stocks, _sentiment,
                        new SeparatorLogger<AiBotDecisionService>(loggerFactory, loggerOptions),
                        fatTails:           _configuration.GetValue("Bots:FatTails", true),
                        tradeSizeTailShape: _configuration.GetValue("Bots:TradeSizeTailShape", 0.5m),
                        blockTradeProb:     _configuration.GetValue("Bots:BlockTradeProb", 0.01m),
                        blockTradeMultiple: _configuration.GetValue("Bots:BlockTradeMultiple", 4m),
                        mmQuoting:          _configuration.GetValue("Bots:MarketMakerQuoting", true),
                        quoteHalfSpreadPrc: _configuration.GetValue("Bots:QuoteHalfSpreadPrc", 0.003m));
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

        _nextStatsLogTime      = TimeHelper.NowUtc() + StatsLogInterval;
        _nextReconcileTime     = TimeHelper.NowUtc() + ReconcileFirstDelay;
        _nextEconomyLogTime    = TimeHelper.NowUtc() + EconomyLogInterval;
        _nextSentimentLogTime  = TimeHelper.NowUtc() + SentimentLogInterval;
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
        var botUserIds = new List<int>(_ctx.AiUsersByAiUserId.Count);
        foreach (var u in _ctx.AiUsersByAiUserId.Values) botUserIds.Add(u.UserId);
        if (botUserIds.Count > 0)
            await _accounts.EnsureLoadedAsync(botUserIds, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var tickStart = Stopwatch.GetTimestamp();
            var now = TimeHelper.NowUtc();
            await CheckTimers(now, ct).ConfigureAwait(false);

            var pending = await CollectPendingOrdersAsync(now, ct).ConfigureAwait(false);
            if (pending.Count > 0)
                await SubmitAndApplyBatchAsync(pending, _engineCts?.Token ?? ct).ConfigureAwait(false);

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
            if (now >= _nextReconcileTime)
            {
                _nextReconcileTime = now + ReconcileInterval;
                var clamp = _configuration.GetValue("Bots:ReconcileClamp", true);
                try { await _auditor.AuditAsync(clamp, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { _logger.LogError(ex, "Reservation reconcile pass failed."); }
            }

            try { await Task.Delay(TradeInterval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { /* breaking loop */ }
        }
    }

    private async Task<List<(AIUser user, Order order)>> CollectPendingOrdersAsync(DateTime now, CancellationToken ct)
    {
        var pending = new List<(AIUser user, Order order)>();
        foreach (var user in _ctx.AiUsersByAiUserId.Values)
        {
            if (!user.IsEnabled || !_decisions.CanPlaceMoreOrder(_ctx, user)) continue;

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

            // Bot decides in its home currency only.
            var order = await _decisions.ComputeOrderAsync(_ctx, user, user.HomeCurrencyType, ct).ConfigureAwait(false);
            if (order is not null) pending.Add((user, order));
        }
        return pending;
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
            await _state.PruneWorstOrdersAsync(_ctx, LoopStartedAtUtc, ct).ConfigureAwait(false);
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
            _nextSentimentLogTime = now + SentimentLogInterval;
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
