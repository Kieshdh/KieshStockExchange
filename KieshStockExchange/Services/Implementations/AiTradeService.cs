using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace KieshStockExchange.Services.Implementations;

public class AiTradeService : IAiTradeService, IDisposable
{
    #region Properties
    // In-memory state of AI users by AiUserId
    private Dictionary<int, AIUser> AiUsers { get; set; } = new();
    private Dictionary<int, Random> AiUserRngs { get; set; } = new();

    // In-memory state of positions, funds and open orders for AI users identified by AiUserId
    private Dictionary<int, Dictionary<int, Position>> Positions { get; set; } = new();
    private Dictionary<int, Dictionary<CurrencyType, Fund>> Funds { get; set; } = new();
    private Dictionary<int, List<Order>> OpenOrders { get; set; } = new();

    // In-memory cache of stock prices at last refresh to avoid repeated market data calls
    private Dictionary<(int, CurrencyType), decimal> StockPrices { get; set; } = new();
    #endregion

    #region Services and Constructor
    private readonly IUserPortfolioService _portfolio;
    private readonly IUserOrderService _orders;
    private readonly IMarketDataService _market;
    private readonly IStockService _stocks;
    private readonly ILogger<AiTradeService> _logger;

    public AiTradeService(IUserPortfolioService portfolio, IUserOrderService orders,
        IMarketDataService market, IStockService stocks, ILogger<AiTradeService> logger)
    {
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region AIUser state

    #endregion

    #region AIUser order computation
    public async Task<Order?> ComputeOrderAsync(AIUser user, CurrencyType currency, CancellationToken ct = default)
    {
        // Check if able to place more orders
        if (!CanPlaceMoreOrder(user)) return null;

        // Get the type of order
        var type = ChooseOrderType(user, currency);

        // Choose stock
        var stockId = ChooseStockId(user, type);
        if (stockId <= 0) return null;

        // Get the order price
        var price = await ComputeOrderPriceAsync(user, type, stockId, currency, ct);

        // Get the order quantity
        var quantity = await ComputeOrderQuantityAsync(user, type, stockId, currency, ct);
        if (quantity <= 0) return null;

        return new Order
        {
            UserId = user.UserId, StockId = stockId, CurrencyType = currency,
            Quantity = quantity, Price = price, 
            SlippagePercent = IsSlippageOrder(type) ? user.SlippageTolerancePrc * 100m : null,
            OrderType = ToOrderTypeString(type)
        };
    }

    private bool CanPlaceMoreOrder(AIUser user)
    {
        // Check if enabled
        if (!user.IsEnabled) return false;

        // Make sure we have the open orders list
        if (!OpenOrders.ContainsKey(user.AiUserId))
            OpenOrders[user.AiUserId] = new List<Order>();
        // Check max open orders
        if (OpenOrders[user.AiUserId].Count >= user.MaxOpenOrders) return false;

        // Check if within daily trade limit
        if (user.TradesToday >= user.MaxDailyTrades) return false;

        return true; // Can place more orders
    }

    private OrderType ChooseOrderType(AIUser user, CurrencyType currency)
    {
        // Determine BUY or SELL
        bool isBuy; 
        // Check if something exceeds limits and forced to go one side
        var cashPrc = FundsPercentagePortfolio(user, currency);
        if (cashPrc < user.MinCashReservePrc) // Not enough cash
            isBuy = false; // Force SELL
        else if (cashPrc > user.MaxCashReservePrc) // Too much cash
            isBuy = true; // Force BUY
        else
            isBuy = Decimal01(user) < 0.5m; // Choose randomly

        // Choose market or limit
        var isMarket = Decimal01(user) < user.UseMarketProb;
        var isSlippage = Decimal01(user) < user.UseSlippageMarketProb;

        // Return appropriate order type
        return isBuy 
            ? (isMarket ? (isSlippage ? OrderType.SlippageMarketBuy : OrderType.TrueMarketBuy) : OrderType.LimitBuy)
            : (isMarket ? (isSlippage ? OrderType.SlippageMarketSell : OrderType.TrueMarketSell) : OrderType.LimitSell);
    }

    private int ChooseStockId(AIUser user, OrderType type)
    {
        var rng = GetRandom(user);

        // Get the user's watchlist
        var watch = user.Watchlist?.ToList();
        if (watch == null || watch.Count == 0) return 0;

        // For sell orders, prefer stocks already held
        if (IsSellOrder(type))
        {
            var candidates = new List<int>();
            foreach (var id in watch)
            {
                var pos = GetPosition(user, id);
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
        var marketPrice = await GetStockPrice(stockId, currency, ct);
        if (marketPrice <= 0m) return 0m;

        // Market price for slippage orders (used as anchor)
        if (IsSlippageOrder(type)) return RoundToCurrency(marketPrice, currency);

        // Compute offset based on min/max limit offset
        var offset = Lerp(user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc, Decimal01(user));

        // Add some jitter based on aggressiveness
        var jitter = Decimal01(user) * user.AggressivenessPrc; // [0, AggressivenessPrc)
        offset *= (1m + jitter); // Increase offset by up to AggressivenessPrc
        offset = Math.Min(offset, user.MaxLimitOffsetPrc); // Clamp to max limit offset

        // Compute limit price based on offset
        var limitPrice = IsBuyOrder(type) ? marketPrice * (1m - offset) : marketPrice * (1m + offset);
        return RoundToCurrency(limitPrice, currency);
    }

    private async Task<int> ComputeOrderQuantityAsync(AIUser user, OrderType type, int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Portfolio numbers
        var portfolio = PortfolioValueByCurrency(user, currency);
        if (portfolio <= 0m) return 0;
        
        // Get trade amount as percentage of portfolio
        var tradePrc = Lerp(user.MinTradeAmountPrc, user.MaxTradeAmountPrc, Decimal01(user));

        // Add some jitter based on aggressiveness
        var jitter = Decimal01(user) * user.AggressivenessPrc; // [0, AggressivenessPrc)
        tradePrc *= (1m + jitter); // Increase trade percentage by up to AggressivenessPrc
        tradePrc = Math.Min(tradePrc, user.MaxTradeAmountPrc); // Clamp to max trade amount percentage
        if (tradePrc <= 0m) return 0; // No trade

        // Get market price
        var marketPrice = await GetStockPrice(stockId, currency, ct);
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

        // Position & cash context
        var fund = GetFund(user, currency);
        var pos = GetPosition(user, stockId);

        // Per-position cap: current value + new should be ≤ cap * portfolio
        var capValue = user.PerPositionMaxPrc * portfolio;
        var currentVal = pos.Quantity > 0 ? pos.Quantity * marketPrice : 0m;
        var roomValue = Math.Max(0m, capValue - currentVal);

        // Target trade value (raw)
        var rawTradeValue = tradePrc * portfolio;

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

    #region AIUser order execution
    public async Task<OrderResult?> ExecuteOrderAsync(AIUser aiUser, Order order, CancellationToken ct = default)
    {
        return null;
    }
    #endregion

    #region IDisposable implementation
    public void Dispose()
    {

    }
    #endregion

    #region Static Helpers
    private static decimal RoundToCurrency(decimal price, CurrencyType currency) =>
        CurrencyHelper.RoundMoney(price, currency);

    private static decimal Lerp(decimal left, decimal right, decimal t) => Clamp01(left + (right - left) * t);

    private static decimal Clamp01(decimal x) => x < 0m ? 0m : (x > 1m ? 1m : x);

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

    #region OrderType
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

    #region AiUser fetching 
    private Fund GetFund(AIUser user, CurrencyType currency)
    {
        // Create user funds  dictionary if not available
        if (!Funds.ContainsKey(user.UserId))
            Funds[user.UserId] = new Dictionary<CurrencyType, Fund>();

        // Create fund if not available
        if (!Funds[user.UserId].ContainsKey(currency))
            Funds[user.UserId][currency] = new Fund { UserId = user.UserId };

        return Funds[user.UserId][currency];

    }

    private Position GetPosition(AIUser user, int stockId)
    {
        // Create user postions dictionary if not available
        if (!Positions.ContainsKey(user.UserId))
            Positions[user.UserId] = new Dictionary<int, Position>();

        // Create position if not available
        if (!Positions[user.UserId].ContainsKey(stockId))
            Positions[user.UserId][stockId] = new Position { UserId = user.UserId };

        return Positions[user.UserId][stockId];
    }

    private Random GetRandom(AIUser user)
    {
        // Create Random object using the daily seed if does not exist
        if (!AiUserRngs.ContainsKey(user.UserId))
            AiUserRngs[user.UserId] = new Random(DailySeed(user.Seed, user.UserId, TimeHelper.Today()));

        return AiUserRngs[user.UserId];
    }

    private decimal Decimal01(AIUser user) => (decimal)GetRandom(user).NextDouble();

    private async Task<decimal> GetStockPrice(int stockId, CurrencyType currency, CancellationToken ct)
    {
        if (!StockPrices.TryGetValue((stockId, currency), out var marketPrice) || marketPrice <= 0m)
        {
            marketPrice = await _market.GetLastPriceAsync(stockId, currency, ct);
            StockPrices[(stockId, currency)] = marketPrice;
        }
        return marketPrice;
    }
    #endregion

    #region Financial computations
    private decimal PortfolioValueByCurrency(AIUser user, CurrencyType currency)
    {
        decimal total = 0m;
        // Add fund balance
        var fund = GetFund(user, currency);
        total += fund.TotalBalance;

        // Sum positions
        if (!Positions.ContainsKey(user.UserId)) return total;
        foreach (var position in Positions[user.UserId].Values)
        {
            if (position.Quantity <= 0) continue;
            if (StockPrices.TryGetValue((position.StockId, currency), out var price))
                total += position.Quantity * price;  // Multiply quantity by price 
        }
        return total;
    }

    private decimal FundsPercentagePortfolio(AIUser user, CurrencyType currency)
    {
        var cash = GetFund(user, currency).TotalBalance;
        decimal total = PortfolioValueByCurrency(user, currency);
        return total > 0m ? cash / total : 0m;
    }
    #endregion
}
