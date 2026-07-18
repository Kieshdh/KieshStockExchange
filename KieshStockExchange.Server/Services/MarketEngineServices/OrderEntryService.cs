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
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Server.Services.HostedServices;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed partial class OrderEntryService : IOrderEntryService
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
    private readonly IOrderRegistry _registry;

    public OrderEntryService(IOrderExecutionService engine, ILogger<OrderEntryService> logger,
        IOrderValidator validator, IMarketDataService data, IDataBaseService db, IStopWatcher stopWatcher,
        IOrderCacheService orderCache, IOrderRegistry registry)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _stopWatcher = stopWatcher ?? throw new ArgumentNullException(nameof(stopWatcher));
        _orderCache = orderCache ?? throw new ArgumentNullException(nameof(orderCache));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }
    #endregion

    #region Private methods
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
            // §bounce lever (b): the order's quoted price normalizes on the finer price-quote grid
            // (RoundPrice; dial 0 ⇒ RoundMoney). BuyBudget above stays RoundMoney (it is cash). The
            // reservation hold is RoundMoney(price*qty), so finer price decimals don't leak cash.
            Price = CurrencyHelper.RoundPrice(price, currency),
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
