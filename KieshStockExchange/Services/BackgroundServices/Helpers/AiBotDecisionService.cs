using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Stateless order computation — given a context and a user, produces an Order or null.
/// </summary>
internal sealed class AiBotDecisionService
{
    #region Services and Constructor
    private readonly IMarketDataService _market;
    private readonly IAccountsCache _accounts;
    private readonly ILogger<AiBotDecisionService> _logger;

    internal AiBotDecisionService(IMarketDataService market, IAccountsCache accounts,
        ILogger<AiBotDecisionService> logger)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Public Interface
    internal bool CanPlaceMoreOrder(AiBotContext ctx, AIUser user)
    {
        // A bot with persistent errors goes quiet for the day to avoid log spam
        if (user.ErrorsToday >= 10) return false;

        if (ctx.OpenOrders.TryGetValue(user.UserId, out var orders) && orders.Count >= user.MaxOpenOrders)
            return false;

        if (user.TradesToday >= user.MaxDailyTrades) return false;

        return true;
    }

    internal async Task<Order?> ComputeOrderAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, CancellationToken ct = default)
    {
        var type    = ChooseOrderType(ctx, user, currency);
        var stockId = ChooseStockId(ctx, user, type);
        if (stockId <= 0) return null;

        var price    = await ComputeOrderPriceAsync(ctx, user, type, stockId, currency, ct).ConfigureAwait(false);
        var quantity = await ComputeOrderQuantityAsync(ctx, user, type, stockId, currency, ct).ConfigureAwait(false);
        if (quantity <= 0) return null;

        decimal? buyBudget = null;
        if (type == OrderType.TrueMarketBuy)
        {
            var mktPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
            buyBudget = mktPrice > 0m ? CurrencyHelper.RoundMoney(quantity * mktPrice, currency) : null;
            if (buyBudget is null or <= 0m) return null;
        }

        return new Order
        {
            UserId = user.UserId, StockId = stockId, CurrencyType = currency,
            Quantity = quantity, Price = price,
            SlippagePercent = IsSlippageOrder(type) ? user.SlippageTolerancePrc * 100m : null,
            BuyBudget = buyBudget,
            OrderType = ToOrderTypeString(type)
        };
    }
    #endregion

    #region Order Decision Logic
    private OrderType ChooseOrderType(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        // 1. Base buy probability adjusted by cash reserve position
        var cashPrc  = ctx.FundsPercentagePortfolio(user.UserId, currency);
        var buyProb  = user.BuyBiasPrc;
        var maxShift = 0.40m;

        if (cashPrc < user.MinCashReservePrc)
        {
            var distance = user.MinCashReservePrc <= 0m ? 1m
                : (user.MinCashReservePrc - cashPrc) / user.MinCashReservePrc;
            buyProb -= maxShift * Clamp01(distance);
        }
        else if (cashPrc > user.MaxCashReservePrc)
        {
            var distance = 1m - user.MaxCashReservePrc <= 0m ? 1m
                : (cashPrc - user.MaxCashReservePrc) / (1m - user.MaxCashReservePrc);
            buyProb += maxShift * Clamp01(distance);
        }

        // 2. Strategy-aware momentum bias (uses EWMA smoothed prices)
        var momentum       = ctx.ComputeWatchlistMomentum(user, currency);
        var momentumSignal = ClampSigned(momentum * 20m, 1m); // ±5% move → ±1

        switch (user.Strategy)
        {
            case AiStrategy.TrendFollower:
                buyProb += 0.20m * momentumSignal; // Chase the move
                break;
            case AiStrategy.MeanReversion:
                buyProb -= 0.15m * momentumSignal; // Fade the move
                break;
            // MarketMaker, Scalper, Random: no directional bias
        }
        buyProb = Clamp01(buyProb);

        // 3. Strategy-aware market-order probability
        var effectiveUseMarket = user.UseMarketProb;
        switch (user.Strategy)
        {
            case AiStrategy.Scalper:
                effectiveUseMarket = Math.Min(1m, effectiveUseMarket + 0.15m * Math.Abs(momentumSignal));
                break;
            case AiStrategy.MarketMaker:
                effectiveUseMarket = Math.Max(0m, effectiveUseMarket - 0.15m);
                break;
        }

        // 4. Resolve to concrete order type
        var isBuy      = ctx.Decimal01(user.AiUserId) < buyProb;
        var isMarket   = ctx.Decimal01(user.AiUserId) < effectiveUseMarket;
        var isSlippage = ctx.Decimal01(user.AiUserId) < user.UseSlippageMarketProb;

        return isBuy
            ? isMarket
                ? isSlippage ? OrderType.SlippageMarketBuy : OrderType.TrueMarketBuy
                : OrderType.LimitBuy
            : isMarket
                ? isSlippage ? OrderType.SlippageMarketSell : OrderType.TrueMarketSell
                : OrderType.LimitSell;
    }

    private int ChooseStockId(AiBotContext ctx, AIUser user, OrderType type)
    {
        var rng   = ctx.GetRandom(user.AiUserId);
        var watch = user.Watchlist?.ToList();
        if (watch == null || watch.Count == 0) return 0;

        if (IsSellOrder(type))
        {
            var candidates = new List<int>();
            foreach (var id in watch)
            {
                var pos       = ctx.GetPosition(user.UserId, id);
                var committed = ComputeCommittedSellShares(ctx, user.UserId, id);
                var ctxAvail  = pos.Quantity - committed;
                // Plan B: cross-check against the engine's authoritative AvailableQuantity
                // (= Quantity − ReservedQuantity). If the engine has reservations the bot's
                // ctx doesn't know about (a leak elsewhere, a refresh race, etc.) the bot
                // would otherwise generate orders that fail Phase 1.5 with InsufficientShares.
                // Take the minimum so the candidate set never includes a stock the engine
                // would reject.
                var enginePos   = _accounts.GetPosition(user.UserId, id);
                var engineAvail = enginePos?.AvailableQuantity ?? 0;
                if (Math.Min(ctxAvail, engineAvail) > 0) candidates.Add(id);
            }
            // Return 0 when bot has nothing to sell — avoids a wasted price lookup and DB call
            return candidates.Count > 0 ? candidates[rng.Next(candidates.Count)] : 0;
        }

        return watch[rng.Next(watch.Count)];
    }
    #endregion

    #region Price and Quantity Computation
    private async Task<decimal> ComputeOrderPriceAsync(AiBotContext ctx, AIUser user, OrderType type,
        int stockId, CurrencyType currency, CancellationToken ct)
    {
        if (IsTrueMarketOrder(type)) return 0m;

        var marketPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (marketPrice <= 0m) return 0m;

        if (IsSlippageOrder(type)) return RoundToCurrency(marketPrice, currency);

        // Limit order: compute offset with bidirectional jitter so some orders land closer to market
        var offset = Clamp01(Lerp(user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc, ctx.Decimal01(user.AiUserId)));
        var jitter = (ctx.Decimal01(user.AiUserId) * 2m - 1m) * user.AggressivenessPrc;
        offset = Math.Max(user.MinLimitOffsetPrc, Math.Min(user.MaxLimitOffsetPrc, offset * (1m + jitter)));

        var limitPrice = IsBuyOrder(type) ? marketPrice * (1m - offset) : marketPrice * (1m + offset);

        // ~30% chance: snap toward a psychologically significant round level
        if (ctx.Decimal01(user.AiUserId) < 0.30m)
            limitPrice = SnapToRoundNumber(limitPrice);

        return RoundToCurrency(limitPrice, currency);
    }

    private async Task<int> ComputeOrderQuantityAsync(AiBotContext ctx, AIUser user, OrderType type,
        int stockId, CurrencyType currency, CancellationToken ct)
    {
        var portfolio = ctx.PortfolioValueByCurrency(user.UserId, currency);
        if (portfolio <= 0m) return 0;

        var tradePrc = Lerp(user.MinTradeAmountPrc, user.MaxTradeAmountPrc, ctx.Decimal01(user.AiUserId));
        var jitter   = ctx.Decimal01(user.AiUserId) * user.AggressivenessPrc;
        tradePrc     = Math.Min(tradePrc * (1m + jitter), user.MaxTradeAmountPrc);
        if (tradePrc <= 0m) return 0;

        var marketPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (marketPrice <= 0m) return 0;

        decimal estimatePrice = type switch
        {
            OrderType.TrueMarketBuy or OrderType.TrueMarketSell => marketPrice,
            OrderType.SlippageMarketBuy  => RoundToCurrency(marketPrice * (1m + user.SlippageTolerancePrc), currency),
            OrderType.SlippageMarketSell => RoundToCurrency(marketPrice * (1m - user.SlippageTolerancePrc), currency),
            _                            => marketPrice // limit
        };
        if (estimatePrice <= 0m) return 0;

        var fund       = ctx.GetFund(user.UserId, currency);
        var pos        = ctx.GetPosition(user.UserId, stockId);
        var capValue   = user.PerPositionMaxPrc * portfolio;
        var currentVal = pos.Quantity > 0 ? pos.Quantity * marketPrice : 0m;
        var roomValue  = Math.Max(0m, capValue - currentVal);
        var rawTrade   = tradePrc * portfolio;

        if (IsBuyOrder(type))
        {
            var committed       = ComputeCommittedBuyFunds(ctx, user.UserId, currency);
            var ctxFreeBalance  = Math.Max(0m, fund.TotalBalance - committed);
            // Plan B: clamp to the engine's AvailableBalance so the bot never generates
            // an order that's doomed at Phase 1.6 — same defence as the sell branch below.
            var engineFreeBalance = _accounts.GetFund(user.UserId, currency)?.AvailableBalance ?? 0m;
            var freeBalance       = Math.Min(ctxFreeBalance, engineFreeBalance);
            var allowedBalance    = Math.Min(Math.Min(freeBalance, rawTrade), roomValue);
            var qty = (int)Math.Floor(allowedBalance / estimatePrice);
            return qty > 0 ? qty : 0;
        }
        else
        {
            var committed     = ComputeCommittedSellShares(ctx, user.UserId, stockId);
            var ctxAvailable  = Math.Max(0, pos.Quantity - committed);
            // Plan B: same clamp as ChooseStockId — engine view is authoritative. If the
            // ctx says we have N free but engine has more reserved, take engine's number.
            var engineAvailable = _accounts.GetPosition(user.UserId, stockId)?.AvailableQuantity ?? 0;
            var availableQty    = Math.Min(ctxAvailable, engineAvailable);
            var desiredQty      = Math.Max(1, (int)Math.Floor(rawTrade / estimatePrice));
            return Math.Min(desiredQty, availableQty);
        }
    }

    private static decimal ComputeCommittedBuyFunds(AiBotContext ctx, int userId, CurrencyType currency)
    {
        if (!ctx.OpenOrders.TryGetValue(userId, out var orders)) return 0m;
        decimal committed = 0m;
        foreach (var o in orders.Values)
            if (o.IsBuyOrder && o.IsLimitOrder && o.CurrencyType == currency)
                committed += CurrencyHelper.RoundMoney(o.RemainingAmount, currency);
        return committed;
    }

    private static int ComputeCommittedSellShares(AiBotContext ctx, int userId, int stockId)
    {
        if (!ctx.OpenOrders.TryGetValue(userId, out var orders)) return 0;
        int committed = 0;
        foreach (var o in orders.Values)
            if (o.IsSellOrder && o.IsLimitOrder && o.StockId == stockId)
                committed += o.RemainingQuantity;
        return committed;
    }

    private async Task<decimal> GetStockPriceAsync(AiBotContext ctx, int stockId,
        CurrencyType currency, CancellationToken ct)
    {
        if (!ctx.StockPrices.TryGetValue((stockId, currency), out var price) || price <= 0m)
        {
            price = await _market.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
            ctx.StockPrices[(stockId, currency)] = price;
        }
        return price;
    }
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
    private static decimal RoundToCurrency(decimal price, CurrencyType currency) =>
        CurrencyHelper.RoundMoney(price, currency);

    private static decimal Lerp(decimal a, decimal b, decimal t) => a + (b - a) * t;

    private static decimal Clamp01(decimal x) => x < 0m ? 0m : x > 1m ? 1m : x;

    private static decimal ClampSigned(decimal x, decimal magnitude) =>
        x < -magnitude ? -magnitude : x > magnitude ? magnitude : x;

    private static decimal SnapToRoundNumber(decimal price)
    {
        decimal unit = price switch
        {
            >= 500m => 5m,
            >= 100m => 1m,
            >= 20m  => 0.50m,
            _       => 0.10m
        };
        return Math.Max(0.01m, Math.Round(price / unit) * unit);
    }
    #endregion
}
