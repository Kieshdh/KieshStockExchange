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

    public OrderEntryService(IOrderExecutionService engine, ILogger<OrderEntryService> logger,
        IOrderValidator validator, IMarketDataService data, IDataBaseService db, IStopWatcher stopWatcher)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _stopWatcher = stopWatcher ?? throw new ArgumentNullException(nameof(stopWatcher));
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
            currency, buyOrder: true, limitStop: false, ct);

    public Task<OrderResult> PlaceStopMarketSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, CurrencyType currency, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice: null, buyBudget: null,
            currency, buyOrder: false, limitStop: false, ct);

    public Task<OrderResult> PlaceStopLimitBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice, buyBudget: null,
            currency, buyOrder: true, limitStop: true, ct);

    public Task<OrderResult> PlaceStopLimitSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice, buyBudget: null,
            currency, buyOrder: false, limitStop: true, ct);

    // Build a stop order, enforce direction sanity against the live price (the engine validator
    // stays structural), arm it via the engine (reserve + persist Pending), and register it with
    // the trigger watcher on success.
    private async Task<OrderResult> ArmStopOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal? limitPrice, decimal? buyBudget, CurrencyType currency,
        bool buyOrder, bool limitStop, CancellationToken ct)
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

        string orderType = limitStop
            ? (buyOrder ? Order.Types.StopLimitBuy : Order.Types.StopLimitSell)
            : (buyOrder ? Order.Types.StopMarketBuy : Order.Types.StopMarketSell);

        var order = new Order
        {
            UserId = userId,
            StockId = stockId,
            Quantity = quantity,
            Price = limitStop ? CurrencyHelper.RoundMoney(limitPrice ?? 0m, currency) : 0m,
            StopPrice = CurrencyHelper.RoundMoney(stopPrice, currency),
            BuyBudget = (!limitStop && buyOrder) ? CurrencyHelper.RoundMoney(buyBudget ?? 0m, currency) : null,
            CurrencyType = currency,
            OrderType = orderType,
        };

        var result = await _engine.ArmStopAsync(order, ct).ConfigureAwait(false);
        if (result.PlacedSuccessfully) _stopWatcher.Arm(order);
        return result;
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
        // Order type determination
        string orderType;
        if (limitOrder)
            orderType = buyOrder ? Order.Types.LimitBuy : Order.Types.LimitSell;
        else if (slippagePercent.HasValue)
            orderType = buyOrder ? Order.Types.SlippageMarketBuy : Order.Types.SlippageMarketSell;
        else
            orderType = buyOrder ? Order.Types.TrueMarketBuy : Order.Types.TrueMarketSell;

        // Round buy budget for TrueMarketBuy orders
        if (orderType == Order.Types.TrueMarketBuy)
            buyBudget = CurrencyHelper.RoundMoney(buyBudget!.Value, currency);
        else buyBudget = null; // For safety

        return new Order
        {
            UserId = userId,
            StockId = stockId,
            Quantity = quantity,
            Price = CurrencyHelper.RoundMoney(price, currency),
            SlippagePercent = slippagePercent,
            BuyBudget = buyBudget,
            CurrencyType = currency,
            OrderType = orderType,
        };
    }
    #endregion
}
