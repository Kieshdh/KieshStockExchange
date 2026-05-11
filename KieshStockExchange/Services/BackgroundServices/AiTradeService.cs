using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices;

public class AiTradeService : IAiTradeService, IAsyncDisposable
{
    // Diagnostic switches mirroring MatchingEngine / SettlementEngine. With 20k+ bots,
    // batch-order warnings flood the log; filter to a single user (admin) so only the
    // active user's bot activity (rare in practice — admin is human) is visible. Set
    // DebugUserId to null to log every user's warnings; set DebugMode to false to
    // suppress entirely.
    private readonly bool DebugMode = true;
    private readonly int? DebugUserId = 20001;

    #region Public Properties
    public TimeSpan TradeInterval        { get; private set; } = TimeSpan.FromSeconds(1);
    public TimeSpan DailyCheckInterval   { get; private set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ReloadAssetsInterval { get; private set; } = TimeSpan.FromMinutes(1);
    public TimeSpan PruneInterval        { get; private set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<CurrencyType> CurrenciesToTrade { get; private set; } =
        new[] { CurrencyType.USD };

    public int LoadedBotCount => _ctx.AiUsersByAiUserId.Count;

    public int OnlineBotCount
    {
        get
        {
            int n = 0;
            foreach (var u in _ctx.AiUsersByAiUserId.Values)
                if (u.IsEnabled) n++;
            return n;
        }
    }

    public int? ActiveBotCap { get; private set; } = 50;

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

    public IReadOnlyList<string> RecentFailures
    {
        get { lock (_recentFailures) return _recentFailures.ToArray(); }
    }

    public event EventHandler? StatsChanged;
    #endregion

    #region Private Fields
    private DateTime _nextDailyCheck   = DateTime.MinValue;
    private DateTime _nextAssetReload  = DateTime.MinValue;
    private DateTime _nextPruneTime    = DateTime.MinValue;
    private DateTime _nextStatsLogTime = DateTime.MinValue;

    private const int    PruneOrdersPerBot   = 2;
    private const int    RecentFailuresMax   = 10;
    private static readonly TimeSpan PruneStaleAge = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StatsLogInterval = TimeSpan.FromSeconds(30);
    private const decimal PruneDistanceFactor = 2.0m;

    private CancellationTokenSource? _cts;
    private Task?  _runner;
    private long   _tickCount = 0;
    private long   _tradesPlacedThisSession = 0;
    private long   _failuresThisSession = 0;
    private readonly Queue<string> _recentFailures = new();

    // Cumulative counters used to emit a 30s window log (snapshot-and-diff).
    private long _buyTotal       = 0;
    private long _sellTotal      = 0;
    private long _limitTotal     = 0;
    private long _slipMarketTotal = 0;
    private long _trueMarketTotal = 0;
    private long _cancelledTotal  = 0;
    private decimal _volumeTotal = 0m;
    private readonly object _volumeLock = new();

    // Last values snapshotted at the previous stats log; deltas = current − snapshot.
    private long _buySnapshot, _sellSnapshot, _limitSnapshot, _slipSnapshot, _trueSnapshot, _cancelledSnapshot;
    private decimal _volumeSnapshot = 0m;

    // Tick-latency tracking for the dynamic bot scaler.
    // EWMA reacts in ~5 ticks at α=0.2; raw last sample is published for the dashboard.
    private const double EwmaAlpha = 0.2;
    private double _tickWorkMsEwma = 0.0;
    private long _lastTickWorkMicros = 0;
    #endregion

    #region Internal Helpers
    private readonly AiBotContext         _ctx;
    private readonly AiBotStateService    _state;
    private readonly AiBotDecisionService _decisions;
    private readonly BotScalerService     _scaler;
    #endregion

    #region Services and Constructor
    private readonly IOrderExecutionService _marketOrders;
    private readonly IMarketDataService     _market;
    private readonly IStockService          _stocks;
    private readonly IAccountsCache         _accounts;
    private readonly ILogger<AiTradeService> _logger;

    public AiTradeService(
        IOrderExecutionService marketOrders,
        IMarketDataService market,
        IStockService stocks,
        IDataBaseService db,
        IAccountsCache accounts,
        ILogger<AiTradeService> logger,
        ILoggerFactory loggerFactory)
    {
        _marketOrders = marketOrders  ?? throw new ArgumentNullException(nameof(marketOrders));
        _market       = market        ?? throw new ArgumentNullException(nameof(market));
        _stocks       = stocks        ?? throw new ArgumentNullException(nameof(stocks));
        _accounts     = accounts      ?? throw new ArgumentNullException(nameof(accounts));
        _logger       = logger        ?? throw new ArgumentNullException(nameof(logger));

        _ctx       = new AiBotContext();
        _state     = new AiBotStateService(db, loggerFactory.CreateLogger<AiBotStateService>());
        _decisions = new AiBotDecisionService(market, loggerFactory.CreateLogger<AiBotDecisionService>());
        _scaler    = new BotScalerService(loggerFactory.CreateLogger<BotScalerService>());

        _market.QuoteUpdated += OnQuoteUpdated;
    }
    #endregion

    #region Configure and Lifecycle
    public void Configure(TimeSpan? tradeInterval = null,
        TimeSpan? dailyCheckInterval = null, TimeSpan? reloadAssetsInterval = null,
        IEnumerable<CurrencyType>? currencies = null, TimeSpan? pruneInterval = null)
    {
        if (tradeInterval       is { } ti)  TradeInterval        = ti;
        if (dailyCheckInterval  is { } di)  DailyCheckInterval   = di;
        if (reloadAssetsInterval is { } rai) ReloadAssetsInterval = rai;
        if (currencies != null)             CurrenciesToTrade    = currencies.ToList();
        if (pruneInterval       is { } pi)  PruneInterval        = pi;
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

        // If the new ceiling is below the current online cap, lower ActiveBotCap to match.
        // Reuses the existing apply path (and emits StatsChanged once).
        if (cap.HasValue && (!ActiveBotCap.HasValue || ActiveBotCap.Value > cap.Value))
            SetActiveBotCap(cap);
        else
            StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyCollection<int> GetAiUserIds() => _ctx.AiUsersByUserId.Keys.ToArray();

    public async Task StartBotAsync(CancellationToken ct = default)
    {
        if (_runner != null && !_runner.IsCompleted) return;

        await _stocks.EnsureLoadedAsync(ct).ConfigureAwait(false);

        // Bots are non-UI subscribers — keep _quotes populated and ref-counted, but tell
        // MarketDataService not to dispatch tick work to the UI thread for these books.
        foreach (var currency in CurrenciesToTrade)
            await _market.SubscribeAllAsync(currency, forUi: false, ct).ConfigureAwait(false);

        // Reset session counters every time the loop starts.
        Interlocked.Exchange(ref _tickCount, 0);
        Interlocked.Exchange(ref _tradesPlacedThisSession, 0);
        Interlocked.Exchange(ref _failuresThisSession, 0);
        Interlocked.Exchange(ref _buyTotal, 0);
        Interlocked.Exchange(ref _sellTotal, 0);
        Interlocked.Exchange(ref _limitTotal, 0);
        Interlocked.Exchange(ref _slipMarketTotal, 0);
        Interlocked.Exchange(ref _trueMarketTotal, 0);
        Interlocked.Exchange(ref _cancelledTotal, 0);
        lock (_volumeLock) _volumeTotal = 0m;
        _buySnapshot = _sellSnapshot = _limitSnapshot = _slipSnapshot = _trueSnapshot = _cancelledSnapshot = 0;
        _volumeSnapshot = 0m;
        Volatile.Write(ref _tickWorkMsEwma, 0.0);
        Interlocked.Exchange(ref _lastTickWorkMicros, 0);
        _nextStatsLogTime = TimeHelper.NowUtc() + StatsLogInterval;
        lock (_recentFailures) _recentFailures.Clear();
        LastTradeAtUtc = null;
        LoopStartedAtUtc = TimeHelper.NowUtc();

        _cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runner = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public async Task StopBotAsync()
    {
        if (_runner == null) return;
        try
        {
            _cts?.Cancel();
            await _runner.ConfigureAwait(false);

            foreach (var currency in CurrenciesToTrade)
                await _market.UnsubscribeAllAsync(currency, forUi: false).ConfigureAwait(false);
        }
        finally
        {
            _runner = null;
            _cts?.Dispose();
            _cts = null;
            LoopStartedAtUtc = null;
            StatsChanged?.Invoke(this, EventArgs.Empty);
        }
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

            // Phase 1: collect orders — pure CPU, no DB reads
            var pending = new List<(AIUser user, Order order)>();
            foreach (var user in _ctx.AiUsersByAiUserId.Values)
            {
                if (!user.IsEnabled || !_decisions.CanPlaceMoreOrder(_ctx, user)) continue;

                // Spontaneous burst: rare chance (~0.2%/tick) of entering a focused session
                var burstActive = _ctx.BurstEndTimes.TryGetValue(user.AiUserId, out var burstEnd) && now < burstEnd;
                if (!burstActive && _ctx.Decimal01(user.AiUserId) < 0.002m)
                {
                    var secs = 120 + (int)(_ctx.Decimal01(user.AiUserId) * 360); // 2–8 min
                    _ctx.BurstEndTimes[user.AiUserId] = now + TimeSpan.FromSeconds(secs);
                    burstActive = true;
                }

                // Post-trade quiet period: more conservative bots wait longer between trades
                var quietSecs = 60.0 - 50.0 * (double)user.AggressivenessPrc; // 10–60 s
                if (user.LastTradeTime > DateTime.MinValue &&
                    (now - user.LastTradeTime).TotalSeconds < quietSecs) continue;

                // Burst: halve decision interval and boost trade probability
                var effectiveInterval = burstActive
                    ? TimeSpan.FromSeconds(Math.Max(1.0, user.DecisionInterval.TotalSeconds * 0.5))
                    : user.DecisionInterval;
                var effectiveTradeProb = burstActive
                    ? Math.Min(1m, user.TradeProb * 1.5m)
                    : user.TradeProb;

                if (now - user.LastDecisionTime < effectiveInterval) continue;

                user.RecordDecision(now);
                if (_ctx.Decimal01(user.AiUserId) > effectiveTradeProb) continue;

                foreach (var currency in CurrenciesToTrade)
                {
                    var order = await _decisions.ComputeOrderAsync(_ctx, user, currency, ct)
                        .ConfigureAwait(false);
                    if (order is not null) pending.Add((user, order));
                }
            }

            // Phase 2: submit all orders in one batch (4 DB ops regardless of N)
            if (pending.Count > 0)
            {
                var orderList = pending.Select(x => x.order).ToList();
                IReadOnlyList<OrderResult> results;
                try
                {
                    results = await _marketOrders.PlaceAndMatchBatchAsync(orderList, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PlaceAndMatchBatchAsync failed on tick {Tick}", _tickCount);
                    results = orderList
                        .Select(_ => new OrderResult { Status = OrderStatus.OperationFailed, ErrorMessage = ex.Message })
                        .ToList();
                }

                // Phase 3: update in-memory caches from fills — no DB reads
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
                        RecordFailure($"AIUser {user.AiUserId} stock {order.StockId}: {result.Status} — {result.ErrorMessage}");
                        continue;
                    }
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Batch order AIUser {Id} stock {Stock}: {Status}",
                            user.AiUserId, order.StockId, result.Status);

                    Interlocked.Increment(ref _tradesPlacedThisSession);

                    if (order.IsBuyOrder)  Interlocked.Increment(ref _buyTotal);
                    else                   Interlocked.Increment(ref _sellTotal);

                    if      (order.IsLimitOrder)      Interlocked.Increment(ref _limitTotal);
                    else if (order.IsSlippageOrder)   Interlocked.Increment(ref _slipMarketTotal);
                    else if (order.IsTrueMarketOrder) Interlocked.Increment(ref _trueMarketTotal);

                    if (result.FillTransactions.Count > 0)
                    {
                        LastTradeAtUtc = TimeHelper.NowUtc();
                        decimal fillVol = 0m;
                        for (int f = 0; f < result.FillTransactions.Count; f++)
                            fillVol += result.FillTransactions[f].TotalAmount;
                        if (fillVol > 0m) lock (_volumeLock) _volumeTotal += fillVol;
                    }

                    _state.ApplyResultToCache(_ctx, result);
                }
            }

            RecordTickLatency(Stopwatch.GetElapsedTime(tickStart));
            Interlocked.Increment(ref _tickCount);

            // Let the scaler decide whether to adjust the cap based on the fresh EWMA.
            // It returns null when no change is warranted; SetActiveBotCap fires StatsChanged
            // itself, so we only emit the unchanged-stats event when the scaler stays put.
            var scalerTarget = _scaler.OnTick(this);
            if (scalerTarget.HasValue) SetActiveBotCap(scalerTarget.Value);
            else                       StatsChanged?.Invoke(this, EventArgs.Empty);

            try { await Task.Delay(TradeInterval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { /* breaking loop */ }
        }
    }

    private void RecordTickLatency(TimeSpan elapsed)
    {
        var ms = elapsed.TotalMilliseconds;
        Interlocked.Exchange(ref _lastTickWorkMicros, (long)(ms * 1000.0));

        // Loop runs single-threaded, so a non-interlocked read-modify-write is safe.
        // Volatile.Write publishes the new value to the dashboard's reader thread.
        var prev = _tickWorkMsEwma;
        var next = prev <= 0.0 ? ms : EwmaAlpha * ms + (1.0 - EwmaAlpha) * prev;
        Volatile.Write(ref _tickWorkMsEwma, next);
    }

    private void RecordFailure(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var stamped = $"{TimeHelper.NowUtc().ToLocalTime():HH:mm:ss}  {message}";
        lock (_recentFailures)
        {
            _recentFailures.Enqueue(stamped);
            while (_recentFailures.Count > RecentFailuresMax) _recentFailures.Dequeue();
        }
    }
    #endregion

    #region Timers and Pruning
    private async Task CheckTimers(DateTime now, CancellationToken ct)
    {
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
            await PruneWorstOrdersAsync(ct).ConfigureAwait(false);
            _nextPruneTime = now + PruneInterval;
        }
        if (now >= _nextStatsLogTime)
        {
            LogStatsWindow();
            _nextStatsLogTime = now + StatsLogInterval;
        }
    }

    private void LogStatsWindow()
    {
        long buy    = Interlocked.Read(ref _buyTotal);
        long sell   = Interlocked.Read(ref _sellTotal);
        long lim    = Interlocked.Read(ref _limitTotal);
        long slip   = Interlocked.Read(ref _slipMarketTotal);
        long trueM  = Interlocked.Read(ref _trueMarketTotal);
        long cancel = Interlocked.Read(ref _cancelledTotal);
        decimal vol;
        lock (_volumeLock) vol = _volumeTotal;

        long dBuy    = buy    - _buySnapshot;
        long dSell   = sell   - _sellSnapshot;
        long dLim    = lim    - _limitSnapshot;
        long dSlip   = slip   - _slipSnapshot;
        long dTrue   = trueM  - _trueSnapshot;
        long dCancel = cancel - _cancelledSnapshot;
        decimal dVol = vol - _volumeSnapshot;

        _buySnapshot       = buy;
        _sellSnapshot      = sell;
        _limitSnapshot     = lim;
        _slipSnapshot      = slip;
        _trueSnapshot      = trueM;
        _cancelledSnapshot = cancel;
        _volumeSnapshot    = vol;

        _logger.LogInformation(
            "BotStats[30s] @ {Time}: bots {Online}/{Loaded}, trades {Total} (buy {Buy}/sell {Sell}), " +
            "type (Limit {Limit}/SlipMarket {Slip}/TrueMarket {True}), cancelled {Cancelled}, volume {Vol}",
            TimeHelper.NowUtc().ToLocalTime().ToString("HH:mm:ss"), OnlineBotCount, LoadedBotCount,
            dBuy + dSell, dBuy, dSell, dLim, dSlip, dTrue, dCancel,
            CurrencyHelper.Format(dVol, CurrencyType.USD));
    }

    private async Task PruneWorstOrdersAsync(CancellationToken ct)
    {
        var toCancel = new List<(int userId, Order order)>();
        bool logging = false;

        foreach (var user in _ctx.AiUsersByAiUserId.Values)
        {
            if (!_ctx.OpenOrders.TryGetValue(user.UserId, out var userOrders) || userOrders.Count == 0)
                continue;

            var limitOrders = userOrders.Values.Where(o => o.IsOpenLimitOrder).ToList();
            if (limitOrders.Count == 0) continue;

            // Criterion 1: stale age — cancel regardless of capacity.
            // Anchor age to max(CreatedAt, LoopStartedAtUtc) so orders that already
            // existed when the bot loop started get a fresh grace window — otherwise
            // every order from the previous session would be wiped on first prune.
            var sessionStart = LoopStartedAtUtc ?? DateTime.MinValue;
            foreach (var o in limitOrders)
            {
                var effectiveCreated = o.CreatedAt > sessionStart ? o.CreatedAt : sessionStart;
                if (TimeHelper.NowUtc() - effectiveCreated >= PruneStaleAge)
                    toCancel.Add((user.UserId, o));
            }

            // Criterion 2: capacity — only when at ≥80% of MaxOpenOrders
            if (userOrders.Count < (int)Math.Ceiling(user.MaxOpenOrders * 0.8)) continue;

            var alreadyQueued     = new HashSet<int>(toCancel.Select(x => x.order.OrderId));
            var distanceThreshold = PruneDistanceFactor * user.MaxLimitOffsetPrc;

            var scored = new List<(Order order, decimal distance)>();
            foreach (var o in limitOrders)
            {
                if (alreadyQueued.Contains(o.OrderId)) continue;
                if (!_ctx.StockPrices.TryGetValue((o.StockId, o.CurrencyType), out var m) || m <= 0m) continue;
                var dist = o.IsBuyOrder ? (m - o.Price) / m : (o.Price - m) / m;
                if (dist > distanceThreshold) scored.Add((o, dist));
            }

            foreach (var (o, _) in scored.OrderByDescending(x => x.distance).Take(PruneOrdersPerBot))
                toCancel.Add((user.UserId, o));
        }

        if (toCancel.Count == 0) return;

        // Filter out anything no longer tracked in-memory; one CancelOrdersBatchAsync call.
        var ids = new List<int>(toCancel.Count);
        for (int i = 0; i < toCancel.Count; i++)
        {
            var (userId, order) = toCancel[i];
            if (!_ctx.OpenOrders.TryGetValue(userId, out var userOrders)) continue;
            if (!userOrders.ContainsKey(order.OrderId)) continue;
            ids.Add(order.OrderId);
        }
        if (ids.Count == 0) return;

        IReadOnlyList<OrderResult> results;
        try
        {
            results = await _marketOrders.CancelOrdersBatchAsync(ids, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PruneWorstOrders: CancelOrdersBatchAsync failed for {Count} orders", ids.Count);
            return;
        }

        int pruned = 0;
        for (int i = 0; i < results.Count; i++)
        {
            var orderId = ids[i];
            var result = results[i];
            if (result.PlacedSuccessfully || result.Status == OrderStatus.AlreadyClosed)
            {
                // Find the userId for this orderId from the original toCancel entries.
                for (int j = 0; j < toCancel.Count; j++)
                {
                    if (toCancel[j].order.OrderId != orderId) continue;
                    if (_ctx.OpenOrders.TryGetValue(toCancel[j].userId, out var userOrders))
                        userOrders.Remove(orderId);
                    break;
                }
                pruned++;
            }
            else
            {
                _logger.LogWarning("PruneWorstOrders: cancel of {OrderId} returned {Status}",
                    orderId, result.Status);
            }
        }

        if (pruned > 0 && logging)
        {
            Interlocked.Add(ref _cancelledTotal, pruned);
            _logger.LogInformation("PruneWorstOrders: cancelled {Count} orders at {Time}",
                pruned, TimeHelper.NowUtc().ToLocalTime().ToString("HH:mm:ss"));
        }
    }
    #endregion

    #region Quote Handler
    private void OnQuoteUpdated(object? sender, LiveQuote quote)
    {
        if (quote == null || quote.LastPrice <= 0m) return;
        var key = (quote.StockId, quote.Currency);

        // Snapshot old raw price for tick-to-tick delta
        if (_ctx.StockPrices.TryGetValue(key, out var oldPrice) && oldPrice > 0m)
            _ctx.PreviousPrices[key] = oldPrice;

        _ctx.StockPrices[key] = quote.LastPrice;

        // EWMA smoothing (α=0.15): reacts over ~6 ticks, dampens spike noise
        var smoothed = _ctx.SmoothedPrices.TryGetValue(key, out var s) ? s : quote.LastPrice;
        _ctx.SmoothedPrices[key] = 0.85m * smoothed + 0.15m * quote.LastPrice;
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
