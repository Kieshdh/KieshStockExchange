using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using System.Collections.Concurrent;
using System.Text;

namespace KieshStockExchange.Services.Implementations;

public class MarketOrderService : IMarketOrderService
{
    #region Fields and Properties
    // Dependencies
    private readonly IDataBaseService _db;
    private readonly IAuthService _auth;
    private readonly IUserPortfolioService _portfolio;
    private readonly ILogger<MarketOrderService> _logger;


    // Order book for each stock (keyed by stock ID and CurrencyType)
    // In-memory order books: price‐time priority
    // Buy: highest price first; Sell: lowest price first
    private readonly ConcurrentDictionary<(int, CurrencyType), OrderBook> _orderBooks = new();
    // Locks for loading order books from the database
    private readonly ConcurrentDictionary<(int, CurrencyType), SemaphoreSlim> _bookLocks = new();

    public MarketOrderService(
        IDataBaseService dbService,
        IAuthService authService,
        IUserPortfolioService portfolio,
        ILogger<MarketOrderService> logger)
    {
        _db = dbService ?? throw new ArgumentNullException(nameof(dbService));
        _auth = authService ?? throw new ArgumentNullException(nameof(authService));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Auth Helpers
    private int CurrentUserId => _auth.CurrentUser?.UserId ?? 0;
    private bool IsAuthenticated => _auth.IsLoggedIn && CurrentUserId > 0;
    private bool IsAdmin => _auth.CurrentUser?.IsAdmin == true;

    private int GetTargetUserIdOrFail(int? actingUserId, out string? error)
    {
        error = null;
        if (!IsAuthenticated)
        {
            error = "User not authenticated.";
            return 0;
        }

        if (!actingUserId.HasValue || actingUserId.Value == CurrentUserId)
            return CurrentUserId;

        if (IsAdmin) 
            return actingUserId.Value;

        error = "Only admins may act on behalf of other users.";
        return 0;
    }

    private bool CanModifyOrder(int targetUserId) =>
        IsAdmin || targetUserId == CurrentUserId;
    #endregion

    #region Other Public Methods
    public async Task<decimal> GetMarketPriceAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var latest = await _db.GetLatestStockPriceByStockId(stockId, currency, ct);
        if (latest == null)
        {
            _logger.LogWarning("No stock price found for stock #{StockId} in currency {Currency}", stockId, currency);
            return 0m;
        }
        return latest.Price;
    }

    public async Task<OrderBook> GetOrderBookByStockAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureBookLoadedAsync(stockId, currency, ct);
        return GetOrCreateBook(stockId, currency);
    }

    public async Task<Stock> GetStockByIdAsync(int stockId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var stock = await _db.GetStockById(stockId);
        if (stock == null)
            throw new KeyNotFoundException($"Stock with #{stockId} not found.");
        return stock;
    }

    public async Task<List<Stock>> GetAllStocksAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var stocks = await _db.GetStocksAsync();
        if (stocks == null || stocks.Count == 0)
            throw new InvalidOperationException("No stocks available in the database.");
        return stocks;
    }
    #endregion

    #region Matching Methods
    public async Task<OrderResult> MatchOrderAsync(Order incoming, int? asUserId = null, CancellationToken ct = default)
    {
        // Check permissions
        var actingUserId = GetTargetUserIdOrFail(asUserId ?? incoming.UserId, out var authErr);
        if (authErr != null) return NotAuthResult();
        if (!CanModifyOrder(actingUserId))
            return ParamError("Not allowed to place orders for the requested user.");

        // Validate order
        var vr = ValidateOrder(incoming);
        if (vr != null) return vr;

        // Get the order book and wait for lock.
        // The lock is per-stock, so different stocks can be processed in parallel.
        // The lock also protects loading the book from the database if needed.
        var gate = GetBookLock(incoming.StockId, incoming.CurrencyType);
        await gate.WaitAsync(ct);
        var book = await GetOrderBookByStockAsync(incoming.StockId, incoming.CurrencyType, ct);

        try
        {
            // Try to fill the order and create transactions
            var fills = await FillTransactionsAsync(incoming, book, ct);

            // if it still has remainder, place it on the book
            if (incoming.IsOpen && incoming.IsLimitOrder && incoming.RemainingQuantity > 0)
                book.UpsertOrder(incoming);

            // If it a market order and it still has remainder, cancel the remainder
            if (incoming.IsOpen && incoming.IsMarketOrder && incoming.RemainingQuantity > 0)
            {
                await CancelRemainderAsync(incoming, ct);
                if (fills.Count == 0)
                    return NoMarketPriceResult(incoming, fills);
            }

            // Return the a successful result
            return SuccessResult(incoming, fills);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching order: {Message}", ex.Message);
            return OperationFailedResult();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OrderResult> CancelOrderAsync(int orderId, int? asUserId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Get the order
        var order = await _db.GetOrderById(orderId);
        if (order is null || !order.IsOpen)
            return AlreadyClosedResult();

        // Check permissions
        var actingUserId = GetTargetUserIdOrFail(asUserId ?? order.UserId, out var authErr);

        if (authErr != null) return NotAuthResult();
        if (!CanModifyOrder(actingUserId))
            return ParamError("Not allowed to cancel orders for the requested user.");

        // Get the order book and wait for lock
        var gate = GetBookLock(order.StockId, order.CurrencyType);
        await gate.WaitAsync(ct);
        var book = await GetOrderBookByStockAsync(order.StockId, order.CurrencyType, ct);

        try
        {
            if (!book.RemoveById(orderId))
                _logger.LogDebug("Cancel requested, but order #{OrderId} not present in in-memory book (already removed?).", orderId);

            await CancelRemainderAsync(order, ct);

            return SuccessfullyCancelledResult(order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling order: {Message}", ex.Message);
            return OperationFailedResult();
        }
        finally
        {
            gate.Release();
        }
    }
    #endregion

    #region Orderbook Helpers
    private OrderBook GetOrCreateBook(int stockId, CurrencyType currency)
        => _orderBooks.GetOrAdd((stockId, currency),
            key => new OrderBook(stockId, currency));

    private async Task EnsureBookLoadedAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var book = GetOrCreateBook(stockId, currency);
        var snapshot = book.Snapshot();

        if (snapshot.Buys.Count == 0 && snapshot.Sells.Count == 0)
        {
            var openLimits = await _db.GetOrdersByStockId(stockId);
            foreach (var o in openLimits.Where(o => 
                o.IsOpen && o.IsLimitOrder && o.CurrencyType == currency))
            {
                ct.ThrowIfCancellationRequested();
                book.UpsertOrder(o);
            }
        }
    }
    private SemaphoreSlim GetBookLock(int stockId, CurrencyType currency)
    => _bookLocks.GetOrAdd((stockId, currency), _ => new SemaphoreSlim(1, 1));
    #endregion

    #region Matching Helpers
    private OrderResult? ValidateOrder(Order incoming)
    {
        if (incoming == null) return ParamError("Order is null.");
        if (!incoming.IsValid()) return ParamError("invalid order");
        if (incoming.IsLimitOrder && incoming.Price <= 0) return ParamError("Limit price must be positive.");
        return null;
    }

    private async Task<List<Transaction>> FillTransactionsAsync(Order incoming, OrderBook book, CancellationToken ct)
    {
        // Store all transactions
        var fills = new List<Transaction>();

        // Use best opposite once per iteration
        while (incoming.IsOpen && incoming.RemainingQuantity > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Get best opposite order (removes it from book)
            var bestOpposite = incoming.IsBuyOrder ? book.PeekBestSell() : book.PeekBestBuy();
            if (bestOpposite == null) break; // no more opposite orders

            // If The price is not crossed, stop matching
            if (!IsPriceCrossed(incoming, bestOpposite)) break;

            // Execute fill and create transaction record
            var tx = await ExecuteFillAsync(incoming, bestOpposite, ct);
            fills.Add(tx);

            // Delete opposite if fully filled
            if (!bestOpposite.IsOpen)
            {
                if (incoming.IsBuyOrder) book.RemoveBestSell();
                else book.RemoveBestBuy();
                _logger.LogInformation("Order #{OrderId} fully filled and removed from book.", bestOpposite.OrderId);
            }
        }
        return fills;
    }

    private bool IsPriceCrossed(Order taker, Order maker)
        => taker.IsBuyOrder
            ? taker.Price >= maker.Price
            : taker.Price <= maker.Price;

    private async Task<Transaction> ExecuteFillAsync(Order taker, Order maker, CancellationToken ct)
    {
        // Determine fill quantity and price
        int qty = Math.Min(taker.RemainingQuantity, maker.RemainingQuantity);
        decimal tradePrice = maker.Price; // price-time: take maker’s price
        CurrencyType currency = taker.CurrencyType;

        // Fill both orders
        taker.Fill(qty);
        maker.Fill(qty);

        // Create transaction record
        var tx = new Transaction
        {
            StockId      = taker.StockId,
            BuyOrderId   = taker.IsBuyOrder ? taker.OrderId : maker.OrderId,
            SellOrderId  = taker.IsBuyOrder ? maker.OrderId : taker.OrderId,
            BuyerId      = taker.IsBuyOrder ? taker.UserId : maker.UserId,
            SellerId     = taker.IsBuyOrder ? maker.UserId : taker.UserId,
            Price        = maker.Price,
            Quantity     = qty,
            CurrencyType = currency,
        };

        if (!tx.IsValid())  throw new InvalidOperationException("Invalid transaction.");

        // Create stock price record
        var sp = new StockPrice { StockId = taker.StockId, Price = maker.Price, CurrencyType = currency };
        if (!sp.IsValid()) throw new InvalidOperationException("Invalid stock price.");

        // Persist changes
        await PersistTransactionAsync(tx, taker, maker, sp, ct);

        _logger.LogInformation("Matched {Taker} with {Maker} for {Qty} @ {Price}",
            taker.OrderId, maker.OrderId, qty, tradePrice);

        return tx;
    }

    private async Task PersistTransactionAsync(Transaction tx, Order taker, Order maker, StockPrice sp, CancellationToken ct)
    {
        await _db.RunInTransactionAsync(async dbTx =>
        {
            // Get the different values 
            var currency = tx.CurrencyType;
            var qty = tx.Quantity;
            var buyerUnit = taker.IsBuyOrder ? taker.Price : maker.Price;
            var reserved = buyerUnit * qty;
            var spend = sp.Price * qty;
            var toRelease = reserved - spend;

            // If the buyer had a different price than the spend amount,
            // then the difference needs to be returned. 
            if (toRelease > 0)
            {
                var okRelease = await _portfolio.ReleaseReservedFundsAsync(toRelease, currency, tx.BuyerId, dbTx);
                if (!okRelease) throw new InvalidOperationException("Buyer release of over-reserved funds failed");
            }

            // Buyer pays cash (unreserve reserved funds, then withdraw funds, then add shares)
            var okFund = await _portfolio.ReleaseFromReservedFundsAsync(spend, currency, tx.BuyerId, dbTx);
            if (!okFund) throw new InvalidOperationException("Buyer reserved funds spend failed.");
            var okAddPos = await _portfolio.AddPositionAsync(tx.StockId, qty, tx.BuyerId, dbTx);
            if (!okAddPos) throw new InvalidOperationException("Buyer position add failed.");

            // Seller delivers shares (unreserve reserved shares, then remove shares, then add cash)
            var okPosition = await _portfolio.ReleaseFromReservedPositionAsync(tx.StockId, qty, tx.SellerId, dbTx);
            if (!okPosition) throw new InvalidOperationException("Seller reserved position spend failed.");
            var okAddFunds = await _portfolio.AddFundsAsync(spend, currency, tx.SellerId, dbTx);
            if (!okAddFunds) throw new InvalidOperationException("Seller funds add failed.");

            // Persist transaction, stock price and updated orders
            await _db.CreateTransaction(tx, dbTx);
            await _db.CreateStockPrice(sp, dbTx);

            await _db.UpdateOrder(taker, dbTx);
            await _db.UpdateOrder(maker, dbTx);
        }, ct);
    }

    private async Task CancelRemainderAsync(Order order, CancellationToken ct)
    {
        await _db.RunInTransactionAsync(async tx =>
        {
            if (order.IsBuyOrder)
                await _portfolio.ReleaseReservedFundsAsync(order.RemainingAmount, order.CurrencyType, order.UserId, tx);
            else
                await _portfolio.UnreservePositionAsync(order.StockId, order.RemainingQuantity, order.UserId, tx);
            order.Cancel();
            await _db.UpdateOrder(order, tx);
        }, ct);
    }

    #endregion

    #region Result helpers
    private OrderResult NotAuthResult() =>
        new() { Status = OrderStatus.NotAuthenticated, Message = "User not authenticated." };
    private OrderResult OperationFailedResult() =>
        new() { Status = OrderStatus.OperationFailed, Message = "An unexpected error occurred." };
    private OrderResult ParamError(string msg) =>
        new() { Status = OrderStatus.InvalidParameters, Message = msg };
    private OrderResult NoMarketPriceResult(Order order, List<Transaction> transactions) =>
        new() {
            PlacedOrder = order,
            FillTransactions = transactions,
            Status = OrderStatus.NoMarketPrice,
            Message = "No market price available."
        };
    private OrderResult AlreadyClosedResult() =>
        new() { Status = OrderStatus.AlreadyClosed, Message = "Order not open." };
    private OrderResult SuccessfullyCancelledResult(Order order) =>
        new() {
            PlacedOrder = order,
            Status = OrderStatus.Success,
            Message = "Order successfully cancelled."
        };
    private OrderResult SuccessResult(Order order, List<Transaction> transactions) =>
        new() {
            PlacedOrder = order,
            Status = order.IsOpen
                ? (transactions.Count > 0 ? OrderStatus.PartialFill : OrderStatus.PlacedOnBook)
                : OrderStatus.Filled,
            FillTransactions = transactions,
            Message = order.IsOpen
                ? (transactions.Count > 0 ? "Order partially filled." : "Order placed on book.")
                : "Order fully filled."
        };
    #endregion
}