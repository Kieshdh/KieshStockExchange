using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Server.Services.HostedServices;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed class OrderEntryService : IOrderEntryService
{
    private readonly bool DebugMode = false;

    #region Services and Constructor
    private readonly IOrderExecutionService _engine;
    private readonly IMarketDataService _data;
    private readonly ILogger<OrderEntryService> _logger;
    private readonly IOrderValidator _validator;
    private readonly IDataBaseService _db;
    private readonly IStopWatcher _stopWatcher;
    private readonly IOrderCacheService _orderCache;

    public OrderEntryService(IOrderExecutionService engine, ILogger<OrderEntryService> logger,
        IOrderValidator validator, IMarketDataService data, IDataBaseService db, IStopWatcher stopWatcher,
        IOrderCacheService orderCache)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _stopWatcher = stopWatcher ?? throw new ArgumentNullException(nameof(stopWatcher));
        _orderCache = orderCache ?? throw new ArgumentNullException(nameof(orderCache));
    }
    #endregion

    #region Order Operations
    public async Task<OrderResult> CancelOrderAsync(int userId, int orderId, CancellationToken ct = default)
    {
        var denied = await VerifyOwnershipAsync(userId, orderId, ct).ConfigureAwait(false);
        if (denied != null) return denied;
        var result = await _engine.CancelOrderAsync(orderId, ct).ConfigureAwait(false);
        // Drop it from the armed index too (no-op when it isn't an armed stop).
        _stopWatcher.Disarm(orderId);
        return result;
    }

    public async Task<OrderResult> ModifyOrderAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default)
    {
        var denied = await VerifyOwnershipAsync(userId, orderId, ct).ConfigureAwait(false);
        return denied ?? await _engine.ModifyOrderAsync(orderId, newQuantity, newPrice, ct).ConfigureAwait(false);
    }

    // §3.6 P3: modify an armed stop's trigger / stop-limit price / quantity. Gate on ownership,
    // enforce the same direction sanity as arm-time for a new StopPrice (the engine validator stays
    // structural), then re-index the trigger watcher so it fires at the new level.
    public async Task<OrderResult> ModifyStopAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newStopPrice = null, decimal? newLimitPrice = null, CancellationToken ct = default)
    {
        var order = await _db.GetOrderById(orderId, ct).ConfigureAwait(false);
        if (order is null || order.UserId != userId)
            return OrderResultFactory.InvalidParams("Order not found.");

        // A trigger modified onto/across the market is intentionally allowed: it's already met, so the
        // watcher promotes it on the next tick and it fills like a market order. The client shows a
        // non-blocking warning before Confirm (mirrors the marketable-limit hint) — no rejection here.
        var result = await _engine.ModifyStopAsync(orderId, newQuantity, newStopPrice, newLimitPrice, ct).ConfigureAwait(false);

        // Re-index the watcher (disarm old snapshot + arm the updated trigger) so it fires at the
        // new StopPrice. Re-read the persisted order so Arm caches the fresh StopPrice/IsBuy.
        if (result.PlacedSuccessfully)
        {
            var updated = await _db.GetOrderById(orderId, ct).ConfigureAwait(false);
            _stopWatcher.Disarm(orderId);
            if (updated is { IsArmed: true }) _stopWatcher.Arm(updated);
        }
        return result;
    }

    // §F5: modify one bracket leg (the SL or a TP), dormant or live. Ownership-gated. Dispatches by
    // the leg's status: a live armed SL / resting TP delegates to the proven modify paths (which handle
    // book + reservation + notify); a dormant (Attached) leg — which reserves nothing and isn't on the
    // book — is edited in place, re-validated against the shared bracket geometry, and only its row is
    // written. newPrice is the leg's stop price (SL) or limit price (TP).
    public async Task<OrderResult> ModifyBracketLegAsync(int userId, int legId, decimal newPrice,
        int newQuantity, CancellationToken ct = default)
    {
        var leg = await _db.GetOrderById(legId, ct).ConfigureAwait(false);
        if (leg is null || leg.UserId != userId || leg.ParentOrderId is not int parentId)
            return OrderResultFactory.InvalidParams("Order not found.");

        // Live legs: reuse the proven paths (book + reservation + notify already handled).
        if (leg.IsArmed && leg.IsStopOrder)   // parent filled, SL armed
            return await ModifyStopAsync(userId, legId, newQuantity, newStopPrice: newPrice, ct: ct).ConfigureAwait(false);
        if (leg.IsOpen && leg.IsLimitOrder)   // parent filled, TP resting on the book
            return await ModifyOrderAsync(userId, legId, newQuantity, newPrice, ct).ConfigureAwait(false);
        if (!leg.IsAttached)
            return OrderResultFactory.InvalidParams("This order can't be modified.");
        if (newQuantity <= 0) return OrderResultFactory.InvalidParams("Quantity must be positive.");

        // Dormant (Attached) leg: a dormant bracket is a resting LIMIT parent, so the entry reference is
        // the parent's limit price. Build the post-edit leg set and re-check the shared geometry before
        // persisting only this leg (siblings untouched — F12).
        var parent = await _db.GetOrderById(parentId, ct).ConfigureAwait(false);
        if (parent is null) return OrderResultFactory.InvalidParams("Bracket parent not found.");

        var siblings = await _db.GetBracketChildrenAsync(parentId, ct).ConfigureAwait(false);
        decimal entryRef = parent.Price;
        decimal? slStop = null;
        var tps = new List<(decimal Price, int Quantity)>();
        foreach (var s in siblings)
        {
            if (s.IsCancelled) continue;
            bool isThis = s.OrderId == legId;
            if (s.IsStopOrder)
                slStop = isThis ? newPrice : s.StopPrice;
            else if (s.IsLimitOrder)
                tps.Add(isThis ? (newPrice, newQuantity) : (s.Price, s.Quantity));
        }
        tps.Sort((a, b) => a.Price.CompareTo(b.Price));

        var geometryErr = BracketGeometryValidator.Validate(entryRef, slStop, tps, parent.Quantity, leg.CurrencyType);
        if (geometryErr != null) return geometryErr;

        if (leg.IsStopOrder) leg.StopPrice = CurrencyHelper.RoundMoney(newPrice, leg.CurrencyType);
        else                 leg.Price     = CurrencyHelper.RoundMoney(newPrice, leg.CurrencyType);
        leg.Quantity = newQuantity;
        leg.UpdatedAt = TimeHelper.NowUtc();
        await _db.UpdateOrder(leg, ct).ConfigureAwait(false);

        _orderCache.NotifyOrdersMutated(new[] { userId });
        return OrderResultFactory.BracketLegModified(leg);
    }

    // The engine cancels/modifies purely by orderId and is shared with system callers,
    // so it can't tell whose order it is. This is the user-facing entry, so gate here:
    // reject anything the caller doesn't own as a uniform "not found" — never reveal
    // that someone else's order exists. Returns null when the caller owns the order.
    private async Task<OrderResult?> VerifyOwnershipAsync(int userId, int orderId, CancellationToken ct)
    {
        var order = await _db.GetOrderById(orderId, ct).ConfigureAwait(false);
        return order is null || order.UserId != userId
            ? OrderResultFactory.InvalidParams("Order not found.")
            : null;
    }
    #endregion

    #region Place Orders
    public Task<OrderResult> PlaceLimitBuyOrderAsync(int userId, int stockId, int quantity, decimal limitPrice,
        CurrencyType currency, CancellationToken ct = default)
        => PlaceOrderAsync(userId, stockId, quantity, limitPrice, currency, buyBudget: null,
            buyOrder: true, limitOrder: true, slippagePercent: null, ct);

    public Task<OrderResult> PlaceLimitSellOrderAsync(int userId, int stockId, int quantity, decimal limitPrice,
        CurrencyType currency, CancellationToken ct = default)
        => PlaceOrderAsync(userId, stockId, quantity, limitPrice, currency, buyBudget: null,
            buyOrder: false, limitOrder: true, slippagePercent: null, ct);

    public Task<OrderResult> PlaceSlippageMarketBuyOrderAsync(int userId, int stockId, int quantity, decimal slippagePct,
        CurrencyType currency, CancellationToken ct = default)
        => PlaceOrderAsync(userId, stockId, quantity, 0m, currency, buyBudget: null,
            buyOrder: true, limitOrder: false, slippagePercent: slippagePct, ct);

    public Task<OrderResult> PlaceSlippageMarketSellOrderAsync(int userId, int stockId, int quantity, decimal slippagePct,
        CurrencyType currency, CancellationToken ct = default)
        => PlaceOrderAsync(userId, stockId, quantity, 0m, currency, buyBudget: null,
            buyOrder: false, limitOrder: false, slippagePercent: slippagePct, ct);

    public Task<OrderResult> PlaceTrueMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal buyBudget, CurrencyType currency, CancellationToken ct = default)
        => PlaceOrderAsync(userId, stockId, quantity, 0m, currency, buyBudget,
            buyOrder: true, limitOrder: false, slippagePercent: null, ct);

    public Task<OrderResult> PlaceTrueMarketSellOrderAsync(int userId, int stockId, int quantity,
        CurrencyType currency, CancellationToken ct = default)
        => PlaceOrderAsync(userId, stockId, quantity, 0m, currency, buyBudget: null,
            buyOrder: false, limitOrder: false, slippagePercent: null, ct);
    #endregion

    #region Place Stop Orders (§3.6 P2)
    public Task<OrderResult> PlaceStopMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal buyBudget, CurrencyType currency, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice: null, buyBudget: buyBudget,
            slippagePct: null, currency, buyOrder: true, limitStop: false, ct);

    public Task<OrderResult> PlaceStopMarketSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, CurrencyType currency, decimal? slippagePct = null, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice: null, buyBudget: null,
            slippagePct: slippagePct, currency, buyOrder: false, limitStop: false, ct);

    public Task<OrderResult> PlaceStopLimitBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice, buyBudget: null,
            slippagePct: null, currency, buyOrder: true, limitStop: true, ct);

    public Task<OrderResult> PlaceStopLimitSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice, buyBudget: null,
            slippagePct: null, currency, buyOrder: false, limitStop: true, ct);

    // Build a stop order, enforce direction sanity against the live price (the engine validator
    // stays structural), arm it via the engine (reserve + persist Pending), and register it with
    // the trigger watcher on success. A sell-stop may carry a slippage cap (fires as a capped
    // market sell); the cap anchor is re-set to the trigger price at promotion time.
    private async Task<OrderResult> ArmStopOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal? limitPrice, decimal? buyBudget, decimal? slippagePct,
        CurrencyType currency, bool buyOrder, bool limitStop, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (stopPrice <= 0m) return OrderResultFactory.InvalidParams("Stop price must be positive.");

        // Direction sanity: a sell-stop sits at/below market, a buy-stop at/above. Skipped only
        // when no live price is available yet (the watcher would still fire on the next cross).
        decimal market = _data.Quotes.TryGetValue((stockId, currency), out var q) && q.LastPrice > 0m
            ? q.LastPrice
            : await _data.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
        if (market > 0m)
        {
            if (buyOrder && stopPrice < market)
                return OrderResultFactory.InvalidParams(
                    $"Buy-stop must be at or above the market price ({CurrencyHelper.Format(market, currency)}).");
            if (!buyOrder && stopPrice > market)
                return OrderResultFactory.InvalidParams(
                    $"Sell-stop must be at or below the market price ({CurrencyHelper.Format(market, currency)}).");
        }

        // A capped market sell-stop carries a slippage cap + an anchor Price (the arm-time market,
        // re-anchored to the trigger price at promotion). Limit stops carry the limit price; an
        // uncapped market stop has Price 0 (and a budget for an uncapped buy).
        bool capped = !limitStop && slippagePct.HasValue && market > 0m;
        decimal price = limitStop ? CurrencyHelper.RoundMoney(limitPrice ?? 0m, currency)
                       : capped   ? CurrencyHelper.RoundMoney(market, currency)
                       : 0m;
        var order = new Order
        {
            UserId = userId,
            StockId = stockId,
            Quantity = quantity,
            Price = price,
            SlippagePercent = capped ? slippagePct : null,
            StopPrice = CurrencyHelper.RoundMoney(stopPrice, currency),
            BuyBudget = (!limitStop && buyOrder && !capped) ? CurrencyHelper.RoundMoney(buyBudget ?? 0m, currency) : null,
            CurrencyType = currency,
            Side = buyOrder ? OrderSide.Buy : OrderSide.Sell,
            Entry = limitStop ? EntryType.Limit : EntryType.Market,
            Stop = StopKind.Stop,
        };

        var result = await _engine.ArmStopAsync(order, ct).ConfigureAwait(false);
        if (result.PlacedSuccessfully) _stopWatcher.Arm(order);
        return result;
    }

    // §3.6 P5 trailing stops (market-only). A trailing stop arms exactly like a static stop — same
    // reserve (shares for a sell, budget for a buy) and the same watcher — but its trigger trails a
    // monotonic watermark seeded at the arm-time market. The watcher recomputes the effective stop each
    // tick; here we just seed watermark + initial StopPrice and reuse the static arm path.
    public Task<OrderResult> PlaceTrailingStopBuyOrderAsync(int userId, int stockId, int quantity,
        decimal trailOffset, bool isPercent, decimal buyBudget, CurrencyType currency, CancellationToken ct = default)
        => ArmTrailingStopAsync(userId, stockId, quantity, trailOffset, isPercent, buyBudget, buyOrder: true, currency, ct);

    public Task<OrderResult> PlaceTrailingStopSellOrderAsync(int userId, int stockId, int quantity,
        decimal trailOffset, bool isPercent, CurrencyType currency, CancellationToken ct = default)
        => ArmTrailingStopAsync(userId, stockId, quantity, trailOffset, isPercent, buyBudget: null, buyOrder: false, currency, ct);

    private async Task<OrderResult> ArmTrailingStopAsync(int userId, int stockId, int quantity,
        decimal trailOffset, bool isPercent, decimal? buyBudget, bool buyOrder, CurrencyType currency, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (trailOffset <= 0m) return OrderResultFactory.InvalidParams("Trail offset must be positive.");
        if (isPercent && trailOffset > 100m)
            return OrderResultFactory.InvalidParams("Trailing percent offset must be between 0 and 100%.");

        // A trailing stop needs a live reference price to seed the watermark + initial trigger.
        decimal market = _data.Quotes.TryGetValue((stockId, currency), out var q) && q.LastPrice > 0m
            ? q.LastPrice
            : await _data.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
        if (market <= 0m)
            return OrderResultFactory.InvalidParams("No reference price available to arm a trailing stop.");

        decimal watermark = market;
        decimal effStop = CurrencyHelper.RoundMoney(
            TrailMath.EffectiveStop(watermark, trailOffset, isPercent, buyOrder), currency);
        if (effStop <= 0m)
            return OrderResultFactory.InvalidParams("Trail offset is too large for the current price.");

        var order = new Order
        {
            UserId = userId,
            StockId = stockId,
            Quantity = quantity,
            Price = 0m,                                   // fires as a market order
            StopPrice = effStop,                          // arm-time effective trigger (watcher updates it)
            TrailOffset = trailOffset,
            TrailIsPercent = isPercent,
            TrailWatermark = watermark,
            BuyBudget = buyOrder ? CurrencyHelper.RoundMoney(buyBudget ?? 0m, currency) : null,
            CurrencyType = currency,
            Side = buyOrder ? OrderSide.Buy : OrderSide.Sell,
            Entry = EntryType.Market,
            Stop = StopKind.Trailing,
        };

        var result = await _engine.ArmStopAsync(order, ct).ConfigureAwait(false);
        if (result.PlacedSuccessfully) _stopWatcher.Arm(order);
        return result;
    }
    #endregion

    #region Place Bracket (§3.6 P4)
    public async Task<OrderResult> PlaceBracketAsync(int userId, int stockId, int quantity, EntryType entry,
        CurrencyType currency, decimal? limitPrice, decimal? buyBudget, decimal? stopPrice,
        decimal? stopLimitPrice, decimal? stopSlippagePct,
        IReadOnlyList<(decimal Price, int Quantity)> takeProfits, CancellationToken ct = default,
        OrderSide side = OrderSide.Buy)
    {
        ct.ThrowIfCancellationRequested();
        if (quantity <= 0) return OrderResultFactory.InvalidParams("Quantity must be positive.");
        bool isShort = side == OrderSide.Sell;
        takeProfits ??= Array.Empty<(decimal, int)>();
        if (takeProfits.Count > 3) return OrderResultFactory.InvalidParams("A bracket supports at most 3 take-profits.");
        // stopPrice null ⇒ a take-profit-only bracket (no protective stop); then ≥1 TP is required.
        bool hasStop = stopPrice.HasValue;
        if (hasStop && stopPrice!.Value <= 0m)
            return OrderResultFactory.InvalidParams("Stop price must be positive.");
        if (!hasStop && takeProfits.Count == 0)
            return OrderResultFactory.InvalidParams("A bracket needs a stop-loss or at least one take-profit.");

        // §P5b: a short bracket SL is a BUY-to-close whose cash pool must be sizeable, so it must be a
        // stop-limit or a slippage-capped market buy-stop — an uncapped market buy-stop has unbounded
        // buyback cost. (Long sell-stop SLs reserve shares and are unaffected.)
        if (isShort && hasStop && stopLimitPrice is null && !stopSlippagePct.HasValue)
            return OrderResultFactory.InvalidParams(
                "A short bracket's stop-loss must be a stop-limit or slippage-capped (uncapped is unsizable).");

        // Entry reference: the limit price for a limit parent, else the live market price. Long: SL below /
        // TPs above. Short: SL above / TPs below. A long market entry funds from buyBudget; a short market
        // entry just sells `quantity` short (collateral handled by the H/P1 settle path), no budget.
        decimal entryRef;
        if (entry == EntryType.Limit)
        {
            if (!(limitPrice > 0m)) return OrderResultFactory.InvalidParams("Limit price must be positive.");
            entryRef = limitPrice.Value;
        }
        else
        {
            if (!isShort && !(buyBudget > 0m))
                return OrderResultFactory.InvalidParams("Buy budget must be positive for a market entry.");
            entryRef = _data.Quotes.TryGetValue((stockId, currency), out var q) && q.LastPrice > 0m
                ? q.LastPrice
                : await _data.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
            if (entryRef <= 0m) return OrderResultFactory.InvalidParams("No live market price to anchor the bracket.");
        }

        // Shared geometry rules (side-aware), also used by ModifyBracketLegAsync so an edit checks identically.
        var geometryErr = BracketGeometryValidator.Validate(
            entryRef, hasStop ? stopPrice : null, takeProfits, quantity, currency, isShort);
        if (geometryErr != null) return geometryErr;

        var legSide = isShort ? OrderSide.Buy : OrderSide.Sell;   // protective legs close the position

        // Build the parent entry (long buy / short sell).
        var parent = new Order
        {
            UserId = userId, StockId = stockId, Quantity = quantity, CurrencyType = currency,
            Side = side, Entry = entry, Stop = StopKind.None,
            Price = entry == EntryType.Limit ? CurrencyHelper.RoundMoney(limitPrice!.Value, currency) : 0m,
            BuyBudget = (!isShort && entry == EntryType.Market) ? CurrencyHelper.RoundMoney(buyBudget!.Value, currency) : null,
        };

        // Build the protective stop-loss (opposite side), if any. Stop-limit when a limit price is given;
        // otherwise a slippage-capped stop-market. For a short SL (buy-stop) the slippage anchor is the
        // trigger (StopPrice) — buy slippage isn't re-anchored at promotion; for a long SL (sell-stop) it's
        // the entry ref (re-anchored to the trigger at promotion, as today). Null ⇒ take-profit-only bracket.
        Order? sl = null;
        if (hasStop)
        {
            bool slCapped = stopLimitPrice is null && stopSlippagePct.HasValue;
            sl = new Order
            {
                UserId = userId, StockId = stockId, Quantity = quantity, CurrencyType = currency,
                Side = legSide, Stop = StopKind.Stop,
                Entry = stopLimitPrice is not null ? EntryType.Limit : EntryType.Market,
                StopPrice = CurrencyHelper.RoundMoney(stopPrice!.Value, currency),
                Price = stopLimitPrice is not null ? CurrencyHelper.RoundMoney(stopLimitPrice.Value, currency)
                      : slCapped ? CurrencyHelper.RoundMoney(isShort ? stopPrice!.Value : entryRef, currency) : 0m,
                SlippagePercent = slCapped ? stopSlippagePct : null,
            };
        }

        // Build the take-profit legs (long sell-limits / short buy-limits).
        var tps = new List<Order>(takeProfits.Count);
        for (int i = 0; i < takeProfits.Count; i++)
        {
            tps.Add(new Order
            {
                UserId = userId, StockId = stockId, Quantity = takeProfits[i].Quantity, CurrencyType = currency,
                Side = legSide, Entry = EntryType.Limit, Stop = StopKind.None,
                Price = CurrencyHelper.RoundMoney(takeProfits[i].Price, currency),
            });
        }

        // Structural validation of every leg before the engine reserves/inserts anything.
        var parentErr = _validator.ValidateNew(parent);
        if (parentErr != null) return parentErr;
        if (sl is not null)
        {
            var slErr = _validator.ValidateNew(sl);
            if (slErr != null) return slErr;
        }
        for (int i = 0; i < tps.Count; i++)
        {
            var tpErr = _validator.ValidateNew(tps[i]);
            if (tpErr != null) return tpErr;
        }

        return await _engine.PlaceBracketAsync(parent, sl, tps, ct).ConfigureAwait(false);
    }
    #endregion

    #region Private methods
    private async Task<OrderResult> PlaceOrderAsync(int userId, int stockId, int quantity, decimal price, CurrencyType currency,  
        decimal? buyBudget, bool buyOrder, bool limitOrder, decimal? slippagePercent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // For SlippageMarket orders, populate the anchor price first so that the
        // validator can verify it. Fast path: cached LiveQuote; fallback: async lookup.
        if (!limitOrder && slippagePercent.HasValue)
        {
            if (_data.Quotes.TryGetValue((stockId, currency), out var q) && q.LastPrice > 0m)
                price = q.LastPrice;
            else
                price = await _data.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
        }

        // Check input parameters
        var inputValidation = _validator.ValidateInput(userId, stockId, quantity, price,
            currency, buyOrder, limitOrder, slippagePercent, buyBudget);
        if (inputValidation != null) return inputValidation;

        // Create order object
        var order = CreateOrder(userId, stockId, quantity, price, buyBudget, 
            currency, buyOrder, limitOrder, slippagePercent); 

        // Validate order object
        var validationResult = _validator.ValidateNew(order);
        if (validationResult != null) return validationResult;

        if (DebugMode) _logger.LogInformation("Placing order: {@Order}", order);

        // Place and match order
        return await _engine.PlaceAndMatchAsync(order, ct).ConfigureAwait(false);
    }

    private Order CreateOrder(int userId, int stockId, int quantity, decimal price, decimal? buyBudget,
        CurrencyType currency, bool buyOrder, bool limitOrder, decimal? slippagePercent)
    {
        // §3.6 decomposition: set the three dimensions. Budget applies only to an uncapped market
        // buy (true market); a limit or slippage-capped market carries none.
        var entry = limitOrder ? EntryType.Limit : EntryType.Market;
        decimal? budget = (entry == EntryType.Market && buyOrder && !slippagePercent.HasValue)
            ? CurrencyHelper.RoundMoney(buyBudget!.Value, currency)
            : null;

        return new Order
        {
            UserId = userId,
            StockId = stockId,
            Quantity = quantity,
            Price = CurrencyHelper.RoundMoney(price, currency),
            SlippagePercent = slippagePercent,
            BuyBudget = budget,
            CurrencyType = currency,
            Side = buyOrder ? OrderSide.Buy : OrderSide.Sell,
            Entry = entry,
            Stop = StopKind.None,
        };
    }
    #endregion
}
