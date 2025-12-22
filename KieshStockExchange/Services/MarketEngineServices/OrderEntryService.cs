using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.PortfolioServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed class OrderEntryService : IOrderEntryService
{
    private readonly bool DebugMode = false;

    #region Services and Constructor
    private readonly IOrderExecutionService _engine;
    private readonly IMarketDataService _data;
    private readonly ILogger<OrderEntryService> _logger;
    private readonly IOrderValidator _validator;

    public OrderEntryService(IOrderExecutionService engine, ILogger<OrderEntryService> logger, 
        IOrderValidator validator, IMarketDataService data)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }
    #endregion

    #region Order Operations
    public Task<OrderResult> CancelOrderAsync(int userId, int orderId, CancellationToken ct = default)
        => _engine.CancelOrderAsync(orderId, ct);

    public Task<OrderResult> ModifyOrderAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default)
        => _engine.ModifyOrderAsync(orderId, newQuantity, newPrice, ct);
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

    #region Private methods
    private async Task<OrderResult> PlaceOrderAsync(int userId, int stockId, int quantity, decimal price, CurrencyType currency,  
        decimal? buyBudget, bool buyOrder, bool limitOrder, decimal? slippagePercent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Check input parameters
        var inputValidation = _validator.ValidateInput(userId, stockId, quantity, price,
            currency, buyOrder, limitOrder, slippagePercent);
        if (inputValidation != null) return inputValidation;

        // Get the price for SlippageMarket orders
        if (!limitOrder && slippagePercent.HasValue)
            price = await _data.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);

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
