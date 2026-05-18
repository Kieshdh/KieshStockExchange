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

/// <summary>
/// Stateless order computation — given a context and a user, produces an Order or null.
/// </summary>
internal sealed class AiBotDecisionService
{
    #region Services and Constructor
    // Max nudge applied to buyProb by the clamped sentiment value.
    private const decimal SentimentMaxBias = 0.10m;
    // Probability of forcing a market order, per unit of |sentiment| > 1.
    private const decimal OverflowGain     = 0.25m;

    private readonly IMarketDataService _market;
    private readonly IAccountsCache _accounts;
    private readonly IOrderBookCache _books;
    private readonly IStockService _stocks;
    private readonly BotSentimentService _sentiment;
    private readonly ILogger<AiBotDecisionService> _logger;

    internal AiBotDecisionService(IMarketDataService market, IAccountsCache accounts,
        IOrderBookCache books, IStockService stocks, BotSentimentService sentiment,
        ILogger<AiBotDecisionService> logger)
    {
        _market    = market    ?? throw new ArgumentNullException(nameof(market));
        _accounts  = accounts  ?? throw new ArgumentNullException(nameof(accounts));
        _books     = books     ?? throw new ArgumentNullException(nameof(books));
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _sentiment = sentiment ?? throw new ArgumentNullException(nameof(sentiment));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
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
        var stockId = ChooseStockId(ctx, user, type, currency);
        if (stockId <= 0) return null;

        // When the chosen stock's raw sentiment crosses ±1, force the order
        // into a TrueMarket{Buy,Sell} in the bot's style-appropriate direction
        // with probability proportional to the overflow. No-op when the
        // override would point at zero shares (sell with no position).
        type = ApplyExtremeReaction(ctx, user, stockId, type);

        var price    = await ComputeOrderPriceAsync(ctx, user, type, stockId, currency, ct).ConfigureAwait(false);
        var quantity = await ComputeOrderQuantityAsync(ctx, user, type, stockId, currency, ct).ConfigureAwait(false);
        if (quantity <= 0) return null;

        decimal? buyBudget = null;
        if (type == OrderType.TrueMarketBuy)
        {
            var mktPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
            buyBudget = mktPrice > 0m ? CurrencyHelper.Notional(mktPrice, quantity, currency) : null;
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
            // Equal magnitude on both sides so the net effect across the
            // 25/25 TF/MR split is zero in expectation.
            case AiStrategy.TrendFollower:
                buyProb += 0.175m * momentumSignal; // Chase the move
                break;
            case AiStrategy.MeanReversion:
                buyProb -= 0.175m * momentumSignal; // Fade the move
                break;
            // MarketMaker, Scalper, Random: no directional bias
        }

        // Linear sentiment bias. Watchlist-averaged so the tilt reflects the
        // broad mood for stocks this bot cares about, not any single name.
        // Clamped to ±1 here — extremes drive the forced market order
        // applied later in ComputeOrderAsync.
        var sentimentClamped = ClampSigned(AverageWatchlistSentiment(user), 1m);
        buyProb += sentimentClamped * SentimentMaxBias;
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

    private int ChooseStockId(AiBotContext ctx, AIUser user, OrderType type, CurrencyType currency)
    {
        var rng   = ctx.GetRandom(user.AiUserId);
        var watch = user.Watchlist?
            .Where(id => _stocks.IsListedIn(id, currency))
            .ToList();
        if (watch == null || watch.Count == 0) return 0;

        if (IsSellOrder(type))
        {
            var candidates = new List<int>();
            foreach (var id in watch)
            {
                var pos       = ctx.GetPosition(user.UserId, id);
                var committed = ComputeCommittedSellShares(ctx, user.UserId, id);
                var ctxAvail  = pos.Quantity - committed;
                // Cross-check against the engine's AvailableQuantity to avoid
                // generating orders that would fail Phase 1.5 on stale ctx.
                var enginePos   = _accounts.GetPosition(user.UserId, id);
                var engineAvail = enginePos?.AvailableQuantity ?? 0;
                if (Math.Min(ctxAvail, engineAvail) > 0) candidates.Add(id);
            }
            return candidates.Count > 0 ? WeightedPick(candidates, rng) : 0;
        }

        return WeightedPick(watch, rng);
    }

    /// <summary> Roulette-wheel pick weighted by 1/StockId^alpha (lower ids = bigger cap = more weight). </summary>
    private static int WeightedPick(IList<int> stockIds, Random rng)
    {
        double total = 0;
        Span<double> cum = stackalloc double[stockIds.Count];
        for (int i = 0; i < stockIds.Count; i++)
        {
            total += 1.0 / Math.Pow(stockIds[i], RuntimeWeightAlpha);
            cum[i] = total;
        }
        double r = rng.NextDouble() * total;
        for (int i = 0; i < stockIds.Count; i++)
            if (r < cum[i]) return stockIds[i];
        return stockIds[^1];
    }

    private const double RuntimeWeightAlpha = 0.7;
    #endregion

    #region Price and Quantity Computation
    private async Task<decimal> ComputeOrderPriceAsync(AiBotContext ctx, AIUser user, OrderType type,
        int stockId, CurrencyType currency, CancellationToken ct)
    {
        if (IsTrueMarketOrder(type)) return 0m;

        var marketPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (marketPrice <= 0m) return 0m;

        if (IsSlippageOrder(type)) return CurrencyHelper.RoundMoney(marketPrice, currency);

        // Limit anchor: midprice when both sides are present, last-trade otherwise.
        // Last-trade ratchets upward whenever buys fill at the ask faster than
        // sells at the bid; midprice stays roughly put under that imbalance.
        var anchor = await GetMidPriceAsync(stockId, currency, ct).ConfigureAwait(false)
                     ?? marketPrice;

        // Limit order: compute offset with bidirectional jitter so some orders land closer to market
        var offset = Clamp01(Lerp(user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc, ctx.Decimal01(user.AiUserId)));
        var jitter = (ctx.Decimal01(user.AiUserId) * 2m - 1m) * user.AggressivenessPrc;
        offset = Math.Max(user.MinLimitOffsetPrc, Math.Min(user.MaxLimitOffsetPrc, offset * (1m + jitter)));

        var limitPrice = IsBuyOrder(type) ? anchor * (1m - offset) : anchor * (1m + offset);

        // ~30% chance: snap toward a psychologically significant round level
        if (ctx.Decimal01(user.AiUserId) < 0.30m)
            limitPrice = SnapToRoundNumber(limitPrice);

        return CurrencyHelper.RoundMoney(limitPrice, currency);
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
            OrderType.SlippageMarketBuy  => CurrencyHelper.RoundMoney(marketPrice * (1m + user.SlippageTolerancePrc), currency),
            OrderType.SlippageMarketSell => CurrencyHelper.RoundMoney(marketPrice * (1m - user.SlippageTolerancePrc), currency),
            _                            => marketPrice // limit
        };
        if (estimatePrice <= 0m) return 0;

        var fund       = ctx.GetFund(user.UserId, currency);
        var pos        = ctx.GetPosition(user.UserId, stockId);
        var capValue   = user.PerPositionMaxPrc * portfolio;
        var currentVal = CurrencyHelper.Notional(marketPrice, pos.Quantity, currency);
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
                committed += o.RemainingAmount;
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

    #region Sentiment Integration
    private decimal AverageWatchlistSentiment(AIUser user)
    {
        if (user.Watchlist == null || user.Watchlist.Count == 0) return 0m;
        decimal sum = 0m;
        int count = 0;
        foreach (var sid in user.Watchlist)
        {
            sum += _sentiment.GetSentiment(sid);
            count++;
        }
        return count > 0 ? sum / count : 0m;
    }

    private OrderType ApplyExtremeReaction(AiBotContext ctx, AIUser user,
        int stockId, OrderType currentType)
    {
        var raw = _sentiment.GetSentiment(stockId);
        var absRaw = Math.Abs(raw);
        if (absRaw <= 1m) return currentType;

        var overflow   = absRaw - 1m;
        var forcedProb = Math.Min(1m, overflow * OverflowGain);
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
            ExtremeDirection.Buy  => OrderType.TrueMarketBuy,
            ExtremeDirection.Sell => OrderType.TrueMarketSell,
            _                     => currentType,
        };
    }

    private static ExtremeReactionStyle PickExtremeReactionStyle(AiBotContext ctx, AIUser user)
    {
        var defaultStyle = user.Strategy switch
        {
            AiStrategy.TrendFollower => ExtremeReactionStyle.FOMO,
            AiStrategy.MeanReversion => ExtremeReactionStyle.Contrarian,
            AiStrategy.MarketMaker   => ExtremeReactionStyle.Contrarian,
            AiStrategy.Scalper       => ExtremeReactionStyle.Panic,
            _                        => ExtremeReactionStyle.None,
        };

        // Out-of-character branch: pick a random style uniformly among the
        // four (one of them is None, so ~25% of out-of-character rolls land
        // on "no extreme reaction" — same as Random-strategy bots).
        if (ctx.Decimal01(user.AiUserId) < user.ExtremeReactionRandomnessPrc)
        {
            var pick = ctx.GetRandom(user.AiUserId).Next(4);
            return pick switch
            {
                0 => ExtremeReactionStyle.FOMO,
                1 => ExtremeReactionStyle.Contrarian,
                2 => ExtremeReactionStyle.Panic,
                _ => ExtremeReactionStyle.None,
            };
        }
        return defaultStyle;
    }

    private static ExtremeDirection BullDirection(ExtremeReactionStyle style) => style switch
    {
        ExtremeReactionStyle.FOMO       => ExtremeDirection.Buy,   // chase the top
        ExtremeReactionStyle.Contrarian => ExtremeDirection.Sell,  // fade the top
        ExtremeReactionStyle.Panic      => ExtremeDirection.Sell,  // take profit
        _                               => ExtremeDirection.None,
    };

    private static ExtremeDirection BearDirection(ExtremeReactionStyle style) => style switch
    {
        ExtremeReactionStyle.FOMO       => ExtremeDirection.Sell,  // panic the bottom
        ExtremeReactionStyle.Contrarian => ExtremeDirection.Buy,   // buy the dip
        ExtremeReactionStyle.Panic      => ExtremeDirection.Sell,  // capitulate
        _                               => ExtremeDirection.None,
    };

    private enum ExtremeReactionStyle { FOMO, Contrarian, Panic, None }
    private enum ExtremeDirection { Buy, Sell, None }
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
