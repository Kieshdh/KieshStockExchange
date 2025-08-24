using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace KieshStockExchange.Services.Implementations;

public class MarketOrderService : IMarketOrderService
{
    #region Fields and Properties
    // Dependencies
    private readonly IDataBaseService _dbService;
    private readonly IAuthService _authService;
    private readonly ILogger<MarketOrderService> _logger;

    // User information
    private User CurrentUser => _authService.CurrentUser;
    private int UserId => CurrentUser?.UserId ?? 0;
    private bool IsAuthenticated => _authService.IsLoggedIn && UserId > 0;

    // Order book for each stock (keyed by stock ID)
    // In-memory order books: price‐time priority
    // Buy: highest price first; Sell: lowest price first
    private readonly ConcurrentDictionary<int, OrderBook> _orderBooks
        = new ConcurrentDictionary<int, OrderBook>();
    #endregion 

    public MarketOrderService(
        IDataBaseService dbService,
        IAuthService authService,
        ILogger<MarketOrderService> logger)
    {
        _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region public methods
    public async Task<decimal> GetMarketPriceAsync(int stockId)
    {
        var stockPrice = await _dbService.GetLatestStockPriceByStockId(stockId);
        if (stockPrice == null)
        {
            _logger.LogWarning("No stock price found for stock ID {StockId}", stockId);
            return 0m;
        }
        return stockPrice.Price;

    }

    public async Task<OrderResult> MatchOrderAsync(Order incoming)
    {
        var vr = ValidateOrder(incoming);
        if (vr != null) return vr;

        var book = GetOrderBook(incoming.StockId);
        var fills = new List<Transaction>();

        // Market = always match, Limit = match only if crossed
        bool isMarket = incoming.IsMarketOrder();

        // pop best opposite once per iteration
        while (incoming.RemainingQuantity() > 0)
        {
            var opposite = TryGetBestOpposite(book, incoming); // REMOVE from book
            if (opposite == null) break;

            // if limit order and not crossed, put the opposite back and stop
            if (!isMarket && !IsPriceCrossed(incoming, opposite))
            {
                book.AddLimitOrder(opposite); // put it back unchanged
                break;
            }

            int qty = Math.Min(incoming.RemainingQuantity(), opposite.RemainingQuantity());
            decimal tradePrice = opposite.Price; // price-time: take maker’s price

            var tx = await ExecuteFillAsync(incoming, opposite, qty, tradePrice);
            fills.Add(tx);

            // if opposite still has remainder, re-add it
            if (opposite.RemainingQuantity() > 0)
                book.AddLimitOrder(opposite);
        }

        // if it’s a limit order and still has remainder, place it on the book
        if (!isMarket && incoming.RemainingQuantity() > 0)
            book.AddLimitOrder(incoming);

        return new OrderResult
        {
            PlacedOrder = incoming,
            Status = (fills.Count == 0 && isMarket) ?
                OrderStatus.NoMarketPrice : OrderStatus.Success,
            FillTransactions = fills,
            ErrorMessage = fills.Count == 0 && isMarket ? "No market price available." : null
        };
    }

    public Task<OrderResult> CancelOrderAsync(int orderId)
    {
        throw new NotImplementedException("This method is not implemented yet.");
    }

    public async Task<OrderBook> GetOrderBookByStockAsync(int stockId)
    {
        await UpdateOrderBooks(); // Ensure we have the latest data
        return GetOrderBook(stockId);
    }

    public async Task<Stock> GetStockByIdAsync(int stockId)
    {
        var stock = await _dbService.GetStockById(stockId);
        if (stock == null)
            throw new KeyNotFoundException($"Stock with ID {stockId} not found.");
        return stock;
    }

    public async Task<List<Stock>> GetAllStocksAsync()
    {
        var stocks = await _dbService.GetStocksAsync();
        if (stocks == null || !stocks.Any())
            throw new InvalidOperationException("No stocks available in the database.");
        return stocks;
    }

    #endregion

    #region Orderbook Management
    private OrderBook GetOrderBook(int stockId)
        => _orderBooks.GetOrAdd(stockId, id => new OrderBook(stockId));

    private async Task UpdateOrderBooks()
    {
        var stocks = await _dbService.GetStocksAsync();
        foreach (var stock in stocks)
        {
            // Replace the existing book with a fresh one
            var book = new OrderBook(stock.StockId);

            var orders = await _dbService.GetOrdersByStockId(stock.StockId);
            orders = orders.Where(o => o.IsOpen()).ToList();
            foreach (var order in orders)
            {
                if (order.IsLimitOrder())
                    book.AddLimitOrder(order);
            }
            _orderBooks[stock.StockId] = book;
        }
    }

    #endregion

    #region Matching Helpers
    private OrderResult? ValidateOrder(Order incoming)
    {
        if (!IsAuthenticated) return NotAuthResult();
        if (incoming == null) return ParamError("Order is null.");
        if (incoming.UserId != UserId) return ParamError("Order owner mismatch.");
        if (incoming.Quantity <= 0) return ParamError("Quantity must be > 0.");
        if (incoming.IsLimitOrder() && incoming.Price <= 0) return ParamError("Price must be > 0.");
        return null;
    }

    private Order? TryGetBestOpposite(OrderBook book, Order incoming)
        => incoming.IsBuyOrder() ? book.RemoveBestSell() : book.RemoveBestBuy();

    private bool IsPriceCrossed(Order incoming, Order opposite)
        => incoming.IsBuyOrder()
            ? incoming.Price >= opposite.Price
            : incoming.Price <= opposite.Price;

    private async Task<Transaction> ExecuteFillAsync(Order taker, Order maker, int qty, decimal price)
    {
        taker.Fill(qty);
        maker.Fill(qty);

        var tx = new Transaction
        {
            StockId = taker.StockId,
            BuyOrderId = taker.IsBuyOrder() ? taker.OrderId : maker.OrderId,
            SellOrderId = taker.IsBuyOrder() ? maker.OrderId : taker.OrderId,
            BuyerId = taker.IsBuyOrder() ? taker.UserId : maker.UserId,
            SellerId = taker.IsBuyOrder() ? maker.UserId : taker.UserId,
            Price = price,
            Quantity = qty
        };

        if (!tx.IsValid())  throw new InvalidOperationException("Invalid transaction.");

        // Create stock price record
        var sp = new StockPrice
        {
            StockId = taker.StockId,
            Price = price,
        };
        if (!sp.IsValid()) throw new InvalidOperationException("Invalid stock price.");


        await _dbService.CreateTransaction(tx);
        await _dbService.UpdateOrder(taker);
        await _dbService.UpdateOrder(maker);
        await _dbService.CreateStockPrice(sp);

        _logger.LogInformation(
            "Matched {Taker} with {Maker} for {Qty} @ {Price}",
            taker.OrderId, maker.OrderId, qty, price);

        return tx;
    }
    #endregion

    #region Result helpers
    private OrderResult NotAuthResult() =>
        new() { Status = OrderStatus.NotAuthenticated, ErrorMessage = "User not authenticated." };
    private OrderResult OperationFailedResult() =>
        new() { Status = OrderStatus.OperationFailed, ErrorMessage = "An unexpected error occurred." };
    private OrderResult ParamError(string msg) =>
        new() { Status = OrderStatus.InvalidParameters, ErrorMessage = msg };
    private OrderResult NoMarketPriceResult(Order order, List<Transaction> transactions) =>
        new() {
            PlacedOrder = order,
            FillTransactions = transactions,
            Status = OrderStatus.NoMarketPrice,
            ErrorMessage = "No market price available."
        };
    #endregion
}