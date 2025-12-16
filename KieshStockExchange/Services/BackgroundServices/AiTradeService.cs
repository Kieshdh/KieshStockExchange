using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.PortfolioServices;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace KieshStockExchange.Services.BackgroundServices;

/// <summary>
/// Background service that uses configured <see cref="AIUser"/> bots
/// to place orders in the market at a fixed cadence.
/// </summary>
public interface IAiTradeService
{
    /// <summary>Interval between trading ticks for the AI loop.</summary>
    TimeSpan TradeInterval { get; }

    /// <summary>How often to recompute which AI users are online.</summary>
    TimeSpan OnlineCheckInterval { get; }

    /// <summary>How often to run daily housekeeping checks.</summary>
    TimeSpan DailyCheckInterval { get; }

    /// <summary>How often to reload AI users' portfolios and cached prices.</summary>
    TimeSpan ReloadAssetsInterval { get; }

    /// <summary>Currencies that the AI users are allowed to trade.</summary>
    IReadOnlyList<CurrencyType> CurrenciesToTrade { get; }

    /// <summary>
    /// Adjusts the cadence and trading universe of the AI loop at runtime.
    /// Any null argument keeps the current value.
    /// </summary>
    void Configure(TimeSpan? tradeInterval = null, TimeSpan? onlineCheckInterval = null,
        TimeSpan? dailyCheckInterval = null, TimeSpan? reloadAssetsInterval = null,
        IEnumerable<CurrencyType>? currencies = null);

    /// <summary>
    /// Starts the background trading loop if it is not already running.
    /// Safe to call multiple times.
    /// </summary>
    Task StartBotAsync(CancellationToken ct = default);

    /// <summary>
    /// Requests the background trading loop to stop and waits for it to finish.
    /// </summary>
    Task StopBotAsync();
}

public class AiTradeService : IAiTradeService, IAsyncDisposable
{
    #region Public Properties
    // Intervals
    public TimeSpan TradeInterval { get; private set; } = TimeSpan.FromSeconds(1);
    public TimeSpan OnlineCheckInterval { get; private set; } = TimeSpan.FromHours(1);
    public TimeSpan DailyCheckInterval { get; private set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ReloadAssetsInterval { get; private set; } = TimeSpan.FromMinutes(1);
    public TimeSpan TransactionLoadInterval { get; private set; } = TimeSpan.FromSeconds(30);

    // Trade these currencies (default USD only)
    public IReadOnlyList<CurrencyType> CurrenciesToTrade { get; private set; } =
        new[] { CurrencyType.USD };
    #endregion

    #region Private Fields
    // Next check times
    private DateTime NextOnlineCheck = DateTime.MinValue;
    private DateTime NextDailyCheck = DateTime.MinValue;
    private DateTime NextAssetReload = DateTime.MinValue;
    private DateTime NextTxsLoad = DateTime.MinValue;

    // Last daily refresh date
    private DateOnly LastRefreshDate = DateOnly.MinValue;

    // Hashset to store all transaction IDs processed to avoid double counting
    private readonly HashSet<int> ProcessedTxIds = new(); // Resets per day

    // Internal loop state
    private CancellationTokenSource? _cts;
    private Task? _runner;
    private long _tickCount = 0;
    #endregion

    #region Dictionaries
    // In-memory state of AI users by AiUserId
    private readonly Dictionary<int, AIUser> AiUsersByAiUserId = new();
    private readonly Dictionary<int, AIUser> AiUsersByUserId = new();
    private readonly Dictionary<int, Random> AiUserRngs = new();

    // In-memory state of positions, funds and open orders for AI users identified by UserId
    private readonly Dictionary<int, Dictionary<int, Position>> Positions = new();
    private readonly Dictionary<int, Dictionary<CurrencyType, Fund>> Funds = new();
    private readonly Dictionary<int, Dictionary<int, Order>> OpenOrders = new();

    // In-memory cache of stock prices at last refresh to avoid repeated market data calls
    private readonly ConcurrentDictionary<(int, CurrencyType), decimal> StockPrices = new();
    #endregion

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IUserPortfolioService _portfolio;
    private readonly IUserOrderService _userOrders;
    private readonly IMarketOrderService _marketOrders;
    private readonly IMarketDataService _market;
    private readonly IStockService _stocks;
    private readonly ILogger<AiTradeService> _logger;

    public AiTradeService(IUserPortfolioService portfolio, IUserOrderService userOrders, IMarketOrderService marketOrders,
        IMarketDataService market, IStockService stocks, ILogger<AiTradeService> logger, IDataBaseService db)
    {
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _userOrders = userOrders ?? throw new ArgumentNullException(nameof(userOrders));
        _marketOrders = marketOrders ?? throw new ArgumentNullException(nameof(marketOrders));
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = db ?? throw new ArgumentNullException(nameof(db));

        // Subscribe to market quote updates
        _market.QuoteUpdated += OnQuoteUpdated;
    }
    #endregion

    #region Loading AIUsers and runtime caches
    private async Task LoadAiUsers(CancellationToken ct = default)
    {
        // Clear existing users
        AiUsersByAiUserId.Clear();
        AiUsersByUserId.Clear();
        AiUserRngs.Clear();

        // Load AI users from database
        foreach (var user in await _db.GetAIUsersAsync(ct).ConfigureAwait(false))
        {
            AiUsersByAiUserId[user.AiUserId] = user;
            AiUsersByUserId[user.UserId] = user;
            AiUserRngs[user.AiUserId] = GetRandom(user.AiUserId);
        }

        // Preload portfolios and orders for loaded users
        await RefreshAssetsAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshAssetsAsync(CancellationToken ct = default)
    {
        // Clear existing caches
        Positions.Clear();
        Funds.Clear();
        OpenOrders.Clear();

        // Get all enabled user IDs
        var userIds = AiUsersByAiUserId.Values.Where(u => u.IsEnabled).Select(u => u.UserId).ToList();

        if (userIds.Count == 0) return; // No enabled users

        // Load funds, positions and open orders for all enabled users
        var allFunds = await _db.GetFundsForUsersAsync(userIds, ct).ConfigureAwait(false);
        var allPositions = await _db.GetPositionsForUsersAsync(userIds, ct).ConfigureAwait(false);
        var allOrders = await _db.GetOpenOrdersForUsersAsync(userIds, ct).ConfigureAwait(false);

        // Populate dictionaries
        foreach (var group in allFunds.GroupBy(f => f.UserId))
            Funds[group.Key] = group.ToDictionary(f => f.CurrencyType, f => f);

        foreach (var group in allPositions.GroupBy(p => p.UserId))
            Positions[group.Key] = group.ToDictionary(p => p.StockId, p => p);

        foreach (var group in allOrders.GroupBy(o => o.UserId))
            OpenOrders[group.Key] = group.ToDictionary(o => o.OrderId, o => o);
    }

    private async Task LoadRecentTransactionsAsync(DateTime now, CancellationToken ct = default)
    {
        // Load recent transactions
        var since = now - TransactionLoadInterval * 2; // Load double the interval to avoid missing any
        var transactions = await _db.GetTransactionsSinceTime(since, ct).ConfigureAwait(false);
        foreach (var tx in transactions)
            RecordTx(tx);
    }
    #endregion

    #region AIUser state management
    private void CheckDailyRefresh()
    {
        if (LastRefreshDate == TimeHelper.Today()) return; // Already refreshed today

        LastRefreshDate = TimeHelper.Today(); // Update last refresh date

        // Clear RNGs to force reseeding next time
        AiUserRngs.Clear();
        ProcessedTxIds.Clear();

        // Reset daily state for each AI user
        foreach (var user in AiUsersByAiUserId.Values)
            user.ResetDay();

        _logger.LogInformation("Performed daily refresh for AI users on {date}", 
            TimeHelper.Today().ToString("yyyy-MM-dd"));
    }

    private void RecalculateOnlineUsers(DateTime time)
    {
        foreach (var user in AiUsersByAiUserId.Values)
            user.IsEnabled = Decimal01(user.AiUserId) < user.OnlineProb;

        _logger.LogInformation("Recalculated online AI users at {time}", 
            time.ToLocalTime().ToString("HH:mm:ss"));
    }

    private async Task CheckTimers(DateTime now, CancellationToken ct)
    {
        // Online check
        if (now >= NextOnlineCheck)
        {
            RecalculateOnlineUsers(now);
            NextOnlineCheck = now + OnlineCheckInterval;
        }
        // Daily check
        if (now >= NextDailyCheck)
        {
            CheckDailyRefresh();
            NextDailyCheck = now + DailyCheckInterval;
        }
        // Asset and prices reload
        if (now >= NextAssetReload)
        {
            await RefreshAssetsAsync(ct).ConfigureAwait(false);
            NextAssetReload = now + ReloadAssetsInterval;
        }
        // Transaction load
        if (now >= NextTxsLoad)
        {
            await LoadRecentTransactionsAsync(now, ct).ConfigureAwait(false);
            NextTxsLoad = now +  TransactionLoadInterval;
        }
    }

    private void RecordTx(Transaction tx)
    {
        if (tx == null || tx.IsInvalid) return; // Skip invalid transactions
        if (tx.Timestamp < TimeHelper.UtcStartOfToday()) return; // Too old
        if (!ProcessedTxIds.Add(tx.TransactionId)) return; // Already processed

        // Buyer side
        if (AiUsersByUserId.TryGetValue(tx.BuyerId, out var buyer))
        {
            buyer.RecordTrade(tx); // Let the ai user know
            //await SyncUserStateAsync(buyer.UserId, ct); // Update assets
        }

        // Skip if self-trade
        if (tx.SellerId == tx.BuyerId) return;

        // Seller side
        if (AiUsersByUserId.TryGetValue(tx.SellerId, out var seller))
        {
            seller.RecordTrade(tx); // Let the ai user know
            //await SyncUserStateAsync(seller.UserId, ct); // Update assets
        }
    }
    #endregion

    #region Umbrella trading loop
    public void Configure(TimeSpan? tradeInterval = null, TimeSpan? onlineCheckInterval = null,
        TimeSpan? dailyCheckInterval = null, TimeSpan? reloadAssetsInterval = null, 
        IEnumerable<CurrencyType>? currencies = null)
    {
        if (tradeInterval is { } ti) TradeInterval = ti;
        if (onlineCheckInterval is { } oi) OnlineCheckInterval = oi;
        if (dailyCheckInterval is { } di) DailyCheckInterval = di;
        if (reloadAssetsInterval is { } rai) ReloadAssetsInterval = rai;
        if (currencies != null) CurrenciesToTrade = currencies.ToList();
    }

    public async Task StartBotAsync(CancellationToken ct = default)
    {
        // If already running, no-op
        if (_runner != null && !_runner.IsCompleted) return;

        // Make sure the stock list is loaded
        await _stocks.EnsureLoadedAsync(ct).ConfigureAwait(false);

        // Subscribe to all stocks in each currency we trade
        foreach (var currency in CurrenciesToTrade)
            await _market.SubscribeAllAsync(currency, ct).ConfigureAwait(false);

        // Get cancellation token
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        // Start the main loop
        _runner = Task.Run(() => RunLoopAsync(token));
    }

    public async Task StopBotAsync()
    {
        if (_runner == null) return;
        try
        {
            _cts?.Cancel();
            await _runner.ConfigureAwait(false);

            foreach (var currency in CurrenciesToTrade)
                await _market.UnsubscribeAllAsync(currency).ConfigureAwait(false);
        }
        finally
        {
            _runner = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // First-time load AI users
        await LoadAiUsers(ct).ConfigureAwait(false);

        // Main loop
        while (!ct.IsCancellationRequested)
        {
            // Current time
            var now = TimeHelper.NowUtc();

            // Check timers
            await CheckTimers(now, ct).ConfigureAwait(false);

            // Per-AI decisions
            foreach (var user in AiUsersByAiUserId.Values)
                await AIUserNextDecision(user, now, ct).ConfigureAwait(false);

            // Wait for next tick
            _tickCount++;
            try { await Task.Delay(TradeInterval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { /* breaking loop */ }
        }
    }
    #endregion

    #region AIUser Next Decision
    private async Task AIUserNextDecision(AIUser user, DateTime now, CancellationToken ct = default)
    {
        // Skip if not enabled or not due
        if (!user.IsEnabled || !CanPlaceMoreOrder(user)) return;

        // Check if decision is due
        var elapsed = now - user.LastDecisionTime;
        if (elapsed < user.DecisionInterval) return;

        // Record decision time
        user.RecordDecision(now);
        // Roll the dice to see if we trade this tick
        if (Decimal01(user.AiUserId) > user.TradeProb) return;

        // Make trade for each currency
        foreach (var currency in CurrenciesToTrade)
        {
            // Generate order
            var order = await ComputeOrderAsync(user, currency, ct).ConfigureAwait(false);
            if (order is null) continue; // No order to place

            // Execute the order
            var result = await ExecuteOrderAsync(user, order, ct).ConfigureAwait(false);

            // If not placed successfully, skip
            if (result is null || !result.PlacedSuccessfully) continue;
        }
    }

    private bool CanPlaceMoreOrder(AIUser user)
    {
        // Check max open orders
        if (OpenOrders.TryGetValue(user.UserId, out Dictionary<int, Order>? value))
            if (value.Count >= user.MaxOpenOrders)
                return false;

        // Check if within daily trade limit
        if (user.TradesToday >= user.MaxDailyTrades) return false;

        return true; // Can place more orders
    }

    private async Task<Order?> ComputeOrderAsync(AIUser user, CurrencyType currency, CancellationToken ct = default)
    {
        // Get the type of order
        var type = ChooseOrderType(user, currency);

        // Choose stock
        var stockId = ChooseStockId(user, type);
        if (stockId <= 0) return null;

        // Get the order price
        var price = await ComputeOrderPriceAsync(user, type, stockId, currency, ct).ConfigureAwait(false);

        // Get the order quantity
        var quantity = await ComputeOrderQuantityAsync(user, type, stockId, currency, ct).ConfigureAwait(false);
        if (quantity <= 0) return null;

        return new Order
        {
            UserId = user.UserId, StockId = stockId, CurrencyType = currency,
            Quantity = quantity, Price = price, 
            SlippagePercent = IsSlippageOrder(type) ? user.SlippageTolerancePrc * 100m : null,
            OrderType = ToOrderTypeString(type)
        };
    }

    private async Task<OrderResult?> ExecuteOrderAsync(AIUser user, Order order, CancellationToken ct = default)
    {
        if (user is null || order is null || order.UserId != user.UserId) return null;
        if (order.IsInvalid) throw new ArgumentException("Order is invalid", nameof(order)); // Sanity check

        try
        {
            // Sent to the MarketOrderService for placement and matching
            var result = await _marketOrders.PlaceAndMatchAsync(order, user.UserId, ct).ConfigureAwait(false);

            _logger.LogInformation("ExecuteOrderAsync for AIUser {AiUserId} on stock {StockId} resulted in {Status}",
                user.AiUserId, order.StockId, result.Status);

            // Record fills
            foreach (var fill in result.FillTransactions)
                RecordTx(fill);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecuteOrderAsync failed for AIUser {AiUserId} on stock {StockId}", user.AiUserId, order.StockId);
            user.RecordError();
            return new OrderResult { Status = OrderStatus.OperationFailed, ErrorMessage = ex.Message };
        }
    }
    #endregion

    #region Order computation helpers
    private OrderType ChooseOrderType(AIUser user, CurrencyType currency)
    {
        // Decide buy or sell based on cash percentage
        var cashPrc = FundsPercentagePortfolio(user.UserId, currency);
        var buyProb = user.BuyBiasPrc; // Base buy probability
        var maxShift = 0.40m; // Max shift of 40%

        // Adjust buy probability based on cash reserves
        if (cashPrc < user.MinCashReservePrc) // Not enough cash
        {
            // Below min cash → we want to SELL more often.
            // distance = how far below min in [0,1]
            var distance = user.MinCashReservePrc <= 0m ? 1m
                : (user.MinCashReservePrc - cashPrc) / user.MinCashReservePrc;
            // Reduce buy probability accordingly
            buyProb -= maxShift * Clamp01(distance);
        }
        else if (cashPrc > user.MaxCashReservePrc) // Too much cash
        {
            // Above max cash → we want to BUY more often.
            // distance = how far above max in [0,1]
            var distance = 1m - user.MaxCashReservePrc <= 0m ? 1m
                : (cashPrc - user.MaxCashReservePrc) / (1m - user.MaxCashReservePrc);
            // Increase buy probability accordingly
            buyProb += maxShift * Clamp01(distance); 
        }

        // Decide buy or sell based on adjusted probability
        var isBuy = Decimal01(user.AiUserId) < buyProb; // Choose randomly

        // Choose market or limit
        var isMarket = Decimal01(user.AiUserId) < user.UseMarketProb;
        var isSlippage = Decimal01(user.AiUserId) < user.UseSlippageMarketProb;

        // Return appropriate order type
        return isBuy
            ? isMarket
                ? isSlippage ? OrderType.SlippageMarketBuy : OrderType.TrueMarketBuy
                : OrderType.LimitBuy
            : isMarket
                ? isSlippage ? OrderType.SlippageMarketSell : OrderType.TrueMarketSell
                : OrderType.LimitSell;
    }

    private int ChooseStockId(AIUser user, OrderType type)
    {        
        var rng = GetRandom(user.AiUserId);

        // Get the user's watchlist
        var watch = user.Watchlist?.ToList();
        if (watch == null || watch.Count == 0) return 0;

        if (IsSellOrder(type))
        {
            var candidates = new List<int>();
            foreach (var id in watch)
            {
                var pos = GetPosition(user.UserId, id);
                if (pos.AvailableQuantity > 0) candidates.Add(id);
            }
            if (candidates.Count > 0)
                return candidates[rng.Next(candidates.Count)];
        }

        // Fall back to any watch item
        return watch[rng.Next(watch.Count)];
    }

    private async Task<decimal> ComputeOrderPriceAsync(AIUser user, OrderType type, int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // True market has no price anchor
        if (IsTrueMarketOrder(type)) return 0m;

        // Get marketprice
        var marketPrice = await GetStockPrice(stockId, currency, ct).ConfigureAwait(false);
        if (marketPrice <= 0m) return 0m;

        // Market price for slippage orders (used as anchor)
        if (IsSlippageOrder(type)) return RoundToCurrency(marketPrice, currency);

        // Compute offset based on min/max limit offset and add jitter based on aggressiveness
        var offset = Clamp01(Lerp(user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc, Decimal01(user.AiUserId)));
        var jitter = Decimal01(user.AiUserId) * user.AggressivenessPrc; // [0, AggressivenessPrc)
        offset *= 1m + jitter; // Increase offset by up to AggressivenessPrc
        offset = Math.Min(offset, user.MaxLimitOffsetPrc); // Clamp to max limit offset

        // Compute limit price based on offset
        var limitPrice = IsBuyOrder(type) ? marketPrice * (1m - offset) : marketPrice * (1m + offset);
        return RoundToCurrency(limitPrice, currency);
    }

    private async Task<int> ComputeOrderQuantityAsync(AIUser user, OrderType type, int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Portfolio numbers
        var portfolio = PortfolioValueByCurrency(user.UserId, currency);
        if (portfolio <= 0m) return 0;
        
        // Get trade amount as percentage of portfolio
        var tradePrc = Lerp(user.MinTradeAmountPrc, user.MaxTradeAmountPrc, Decimal01(user.AiUserId));

        // Add some jitter based on aggressiveness
        var jitter = Decimal01(user.AiUserId) * user.AggressivenessPrc; // [0, AggressivenessPrc)
        tradePrc *= 1m + jitter; // Increase trade percentage by up to AggressivenessPrc
        tradePrc = Math.Min(tradePrc, user.MaxTradeAmountPrc); // Clamp to max trade amount percentage
        if (tradePrc <= 0m) return 0; // No trade

        // Get market price
        var marketPrice = await GetStockPrice(stockId, currency, ct).ConfigureAwait(false);
        if (marketPrice <= 0m) return 0;

        // Get the estimated execution price
        decimal estimatePrice = type switch
        {
            OrderType.TrueMarketBuy or OrderType.TrueMarketSell => marketPrice,
            OrderType.SlippageMarketBuy => RoundToCurrency(marketPrice * (1m + user.SlippageTolerancePrc), currency),
            OrderType.SlippageMarketSell => RoundToCurrency(marketPrice * (1m - user.SlippageTolerancePrc), currency),
            _ => marketPrice // limit
        };
        if (estimatePrice <= 0m) return 0;

        // Fetch the user's fund and position
        var fund = GetFund(user.UserId, currency);
        var pos = GetPosition(user.UserId, stockId);

        // Get necessary values
        var capValue = user.PerPositionMaxPrc * portfolio;
        var currentVal = pos.Quantity > 0 ? pos.Quantity * marketPrice : 0m;
        var roomValue = Math.Max(0m, capValue - currentVal);
        var rawTradeValue = tradePrc * portfolio;

        // Get the quantity based on buy or sell order
        if (IsBuyOrder(type))
        {
            // Buy: don’t exceed available funds and roomValue
            var allowedBalance = Math.Min(fund.AvailableBalance, rawTradeValue); // Cash available for trade
            allowedBalance = Math.Min(allowedBalance, roomValue); // Also respect position cap
            var qty = (int)Math.Floor(allowedBalance / estimatePrice); // Desired quantity
            return qty > 0 ? qty : 0;
        }
        else
        {
            // Sell: don’t exceed available shares and roomValue
            var desiredQty = (int)Math.Floor(rawTradeValue / estimatePrice);
            desiredQty = Math.Max(1, desiredQty); // At least 1 share
            return Math.Min(desiredQty, pos.AvailableQuantity); // Can't sell more than available
        }
    }
    #endregion

    #region Static Helpers
    private static decimal RoundToCurrency(decimal price, CurrencyType currency) =>
        CurrencyHelper.RoundMoney(price, currency);

    private static decimal Lerp(decimal left, decimal right, decimal t) => left + (right - left) * t;

    private static decimal Clamp01(decimal x) => x < 0m ? 0m : x > 1m ? 1m : x;

    private static int DailySeed(int baseSeed, int userId, DateOnly date)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + baseSeed;
            h = h * 31 + userId;
            h = h * 31 + date.Year;
            h = h * 31 + date.Month;
            h = h * 31 + date.Day;
            return h & int.MaxValue; // ensure non-negative for Random
        }
    }
    #endregion

    #region OrderType helpers
    private enum OrderType { TrueMarketBuy, TrueMarketSell, SlippageMarketBuy, SlippageMarketSell, LimitBuy, LimitSell }

    private bool IsBuyOrder(OrderType t) => t is OrderType.TrueMarketBuy or OrderType.SlippageMarketBuy or OrderType.LimitBuy;
    private bool IsSellOrder(OrderType t) => t is OrderType.TrueMarketSell or OrderType.SlippageMarketSell or OrderType.LimitSell;

    private bool IsSlippageOrder(OrderType t) => t is OrderType.SlippageMarketBuy or OrderType.SlippageMarketSell;
    private bool IsLimitOrder(OrderType t) => t is OrderType.LimitBuy or OrderType.LimitSell;
    private bool IsTrueMarketOrder(OrderType t) => t is OrderType.TrueMarketBuy or OrderType.TrueMarketSell;

    private static string ToOrderTypeString(OrderType t) => t switch
    {
        OrderType.TrueMarketBuy => Order.Types.TrueMarketBuy,
        OrderType.TrueMarketSell => Order.Types.TrueMarketSell,
        OrderType.SlippageMarketBuy => Order.Types.SlippageMarketBuy,
        OrderType.SlippageMarketSell => Order.Types.SlippageMarketSell,
        OrderType.LimitBuy => Order.Types.LimitBuy,
        OrderType.LimitSell => Order.Types.LimitSell,
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };
    #endregion

    #region Data fetching and caching helpers
    private Fund GetFund(int userId, CurrencyType currency)
    {
        // If no fund dictionary is available create a new one
        if (!Funds.ContainsKey(userId))
            Funds[userId] = new Dictionary<CurrencyType, Fund>();

        // If the fund of the currency is unavailable create a new one
        if (!Funds[userId].ContainsKey(currency))
            Funds[userId][currency] = new Fund { UserId = userId, CurrencyType = currency };

        return Funds[userId][currency];
    }

    private Position GetPosition(int userId, int stockId)
    {
        // If no position dictionary is  available create a new one
        if (!Positions.ContainsKey(userId))
            Positions[userId] = new Dictionary<int, Position>();

        // If no position for that stock is available create a new one
        if (!Positions[userId].ContainsKey(stockId))
            Positions[userId][stockId] = new Position { UserId = userId, StockId = stockId };

        return Positions[userId][stockId];
    }

    private Random GetRandom(int aiUserId)
    {
        // If no user
        if (!AiUserRngs.ContainsKey(aiUserId))
        {
            if (!AiUsersByAiUserId.TryGetValue(aiUserId, out var ai))
                throw new KeyNotFoundException($"AIUser not found for userId {aiUserId}");
            AiUserRngs[aiUserId] = new Random(DailySeed(ai.Seed, ai.AiUserId, TimeHelper.Today()));
        }
        return AiUserRngs[aiUserId];
    }

    private decimal Decimal01(int aiUserId) => (decimal)GetRandom(aiUserId).NextDouble();
    #endregion

    #region Financial methods
    private async Task<decimal> GetStockPrice(int stockId, CurrencyType currency, CancellationToken ct)
    {
        if (!StockPrices.TryGetValue((stockId, currency), out var marketPrice) || marketPrice <= 0m)
        {
            marketPrice = await _market.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
            StockPrices[(stockId, currency)] = marketPrice;
        }
        return marketPrice;
    }

    private void OnQuoteUpdated(object? sender, LiveQuote quote)
    {
        if (quote == null) return;
        if (quote.LastPrice <= 0m) return;

        // Adjust property names if your LiveQuote uses e.g. CurrencyType instead of Currency
        var key = (quote.StockId, quote.Currency);
        StockPrices[key] = quote.LastPrice;
    }

    private decimal PortfolioValueByCurrency(int userId, CurrencyType currency)
    {
        decimal total = 0m;
        var fund = GetFund(userId, currency);
        total += fund.TotalBalance;

        if (!Positions.ContainsKey(userId)) return total;
        foreach (var position in Positions[userId].Values)
        {
            if (position.Quantity <= 0) continue;
            if (StockPrices.TryGetValue((position.StockId, currency), out var price))
                total += position.Quantity * price;  // Multiply quantity by price 
        }
        return total;
    }

    private decimal FundsPercentagePortfolio(int userId, CurrencyType currency)
    {
        var freeCash = GetFund(userId, currency).AvailableBalance;
        var total = PortfolioValueByCurrency(userId, currency);

        // If we have no portfolio value at all, treat as 100% or 0% cash
        if (total <= 0m)
            return freeCash > 0m ? 1m : 0m;
        // Otherwise compute percentage and clamp to [0,1]
        return Clamp01(freeCash / total);
    }

    #endregion

    #region IDisposable implementation
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing AItradeService...");
        
        _market.QuoteUpdated -= OnQuoteUpdated;
        await StopBotAsync().ConfigureAwait(false);
    }
    #endregion
}
