using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    // Show if the book has been loaded at least once
    private readonly ConcurrentDictionary<(int, CurrencyType), bool> _bookLoaded = new();

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

    private int GetTargetUserIdOrFail(int? actingUserId, out string? authError)
    {
        authError = null;
        if (!IsAuthenticated)
        {
            authError = "User not authenticated.";
            return 0;
        }

        if (!actingUserId.HasValue || actingUserId.Value == CurrentUserId)
            return CurrentUserId;

        if (IsAdmin) 
            return actingUserId.Value;

        authError = "Only admins may act on behalf of other users.";
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
        var stock = await _db.GetStockById(stockId, ct);
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

    #region Place Order
    public async Task<OrderResult> PlaceAndMatchAsync(Order incoming, int? asUserId = null, CancellationToken ct = default)
    {
        // Check permissions
        var actingUserId = GetTargetUserIdOrFail(asUserId ?? incoming.UserId, out var authErr);
        if (authErr != null) return NotAuthResult();
        if (!CanModifyOrder(actingUserId))
            return NotAuthorizedResult("Not allowed to place orders for the requested user.");

        // Validate order
        var vr = ValidateOrder(incoming);
        if (vr != null) return vr;

        // Get the order book and wait for lock.
        // The lock is per-stock, so different stocks can be processed in parallel.
        // The lock also protects loading the book from the database if needed.
        var gate = GetBookLock(incoming.StockId, incoming.CurrencyType);
        await gate.WaitAsync(ct);
        var book = await GetOrderBookByStockAsync(incoming.StockId, incoming.CurrencyType, ct);

        Order? persisted = null;
        try
        {
            // Try to reserve funds or shares for the incoming order
            var okReserve = await ReserveAssets(incoming, ct);
            if (okReserve != null) return okReserve;

            // Persist the incoming order
            await _db.CreateOrder(incoming, ct);
            persisted = incoming;

            _logger.LogInformation("Placing order: #{Order} for stock #{Stock} for {Quantity} @ {Price}", 
                incoming.OrderId, incoming.StockId, incoming.Quantity, incoming.Price);

            // Try to fill the order and create transactions
            var fills = await FillTransactionsAsync(incoming, book, ct);

            // if it still has remainder, place it on the book
            if (incoming.IsOpen && incoming.IsLimitOrder && incoming.RemainingQuantity > 0)
                book.UpsertOrder(incoming);

            // If it a market order and it still has remainder, cancel the remainder
            if (incoming.IsOpen && incoming.IsMarketOrder && incoming.RemainingQuantity > 0)
            {
                await CancelRemainderAsync(incoming, ct);
                if (fills.Count == 0) return NoLiquidityResult(incoming, fills);
            }

            // Return the a successful result
            return SuccessResult(incoming, fills);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching order: {Message}", ex.Message);

            // If we already wrote the order, Make sure it is not left open.
            if (persisted is not null)
            {
                if (persisted.IsOpen)
                {
                    // Either place it so it gets matched later…
                    try { book.UpsertOrder(persisted); } catch { }
                }
                // Or release everything if we don’t trust current state:
                try { await CancelRemainderAsync(persisted, ct); } catch { }
            }
            return OperationFailedResult();
        }
        finally { gate.Release(); }
    }

    private OrderResult? ValidateOrder(Order order)
    {
        if (order == null) return ParamError("Order is null.");
        if (!order.IsValid()) return ParamError("invalid order");
        if (order.IsLimitOrder && order.Price <= 0) return ParamError("Limit price must be positive.");
        return null;
    }

    private async Task<OrderResult?> ReserveAssets(Order order, CancellationToken ct)
    {
        bool okReserve;
        if (order.IsBuyOrder)
        {
            var value = order.TotalAmount;
            if (value <= 0) return ParamError("Total amount to reserve must be positive.");
            okReserve = await _portfolio.ReserveFundsAsync(value, order.CurrencyType, order.UserId, ct);
            if (!okReserve) return ParamError("Insufficient funds to reserve for buy order.");
        }
        else
        {
            var qty = order.Quantity;
            if (qty <= 0) return ParamError("Quantity to reserve must be positive.");
            okReserve = await _portfolio.ReservePositionAsync(order.StockId, qty, order.UserId, ct);
            if (!okReserve) return ParamError("Insufficient shares to reserve for sell order.");
        }
        return null;
    }
    #endregion

    #region Fill Order for Transaction Helper
    private async Task<List<Transaction>> FillTransactionsAsync(Order incoming, OrderBook book, CancellationToken ct)
    {
        // Store all transactions
        var fills = new List<Transaction>();
        var selfOrders = new List<Order>();

        // Use best opposite once per iteration
        while (incoming.IsOpen && incoming.RemainingQuantity > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Get best opposite order
            var bestOpposite = incoming.IsBuyOrder ? book.PeekBestSell() : book.PeekBestBuy();
            if (bestOpposite == null) break; // no more opposite orders
            if (!CheckBestOpposite(incoming, book, bestOpposite, out var selfOrder))
            {
                if (selfOrder != null)
                    selfOrders.Add(selfOrder);
                continue; // skip invalid or self-trade orders
            }

            // If The price is not crossed, stop matching
            if (!IsPriceCrossed(incoming, bestOpposite)) break;

            // Execute fill and create transaction record
            var tx = await ExecuteFillAsync(incoming, bestOpposite, ct);
            fills.Add(tx);

            // Delete opposite if fully filled
            if (!bestOpposite.IsOpen)
            {
                _ = incoming.IsBuyOrder ? book.RemoveBestSell() : book.RemoveBestBuy();
                _logger.LogInformation("Order #{OrderId} fully filled and removed from book.", bestOpposite.OrderId);
            }
        }
        // Return any self-trade orders to the book
        foreach (var order in selfOrders)
            book.UpsertOrder(order);

        return fills;
    }

    private bool CheckBestOpposite(Order incoming, OrderBook book, Order bestOpposite, out Order? selfOrder)
    {
       selfOrder = null;
        if (!bestOpposite.IsOpen || bestOpposite.RemainingQuantity <= 0)
        {
            // This should not happen, but if it does, remove it from the book
            _ = incoming.IsBuyOrder ? book.RemoveBestSell() : book.RemoveBestBuy();
            _logger.LogWarning("Found non-open or empty opposite order #{OrderId} in book; removing it.", bestOpposite.OrderId);
            return false;
        }
        if (bestOpposite.StockId != incoming.StockId || bestOpposite.CurrencyType != incoming.CurrencyType)
        {
            // This should never happen
            _ = incoming.IsBuyOrder ? book.RemoveBestSell() : book.RemoveBestBuy();
            _logger.LogError("Data integrity error: opposite order #{OrderId} has wrong stock or currency.", bestOpposite.OrderId);
            return false;
        }
        if (bestOpposite.UserId == incoming.UserId)
        {
            // Prevent self-trading
            selfOrder = incoming.IsBuyOrder ? book.RemoveBestSell() : book.RemoveBestBuy();
            if (selfOrder != null)
                _logger.LogInformation("Skipping self-trade attempt for user #{UserId} on order #{OrderId}.", incoming.UserId, incoming.OrderId);
            return false;
        }
        return true;
    }

    private bool IsPriceCrossed(Order taker, Order maker)
    {
        if (taker.CurrencyType != maker.CurrencyType)
            throw new InvalidOperationException("Currency mismatch in matching.");

        return taker.IsBuyOrder
            ? taker.Price >= maker.Price
            : taker.Price <= maker.Price;
    }

    private async Task<Transaction> ExecuteFillAsync(Order taker, Order maker, CancellationToken ct)
    {
        // Determine fill quantity and price
        int qty = Math.Min(taker.RemainingQuantity, maker.RemainingQuantity);

        // Fill both orders
        taker.Fill(qty);
        maker.Fill(qty);

        // Create transaction record
        var tx = CreateTransaction(taker, maker, qty);

        // Create stock price record
        var sp = new StockPrice { StockId = taker.StockId, Price = maker.Price, CurrencyType = taker.CurrencyType };
        if (!sp.IsValid()) throw new InvalidOperationException("Invalid stock price.");

        // Persist changes
        await PersistTransactionAsync(tx, taker, maker, sp, ct);

        _logger.LogInformation("Matched {Taker} with {Maker} for {Qty} @ {Price}",
            taker.OrderId, maker.OrderId, qty, maker.Price);

        return tx;
    }

    private Transaction CreateTransaction(Order taker, Order maker, int quantity)
    {
        var tx = new Transaction
        {
            StockId = taker.StockId,
            BuyOrderId = taker.IsBuyOrder ? taker.OrderId : maker.OrderId,
            SellOrderId = taker.IsBuyOrder ? maker.OrderId : taker.OrderId,
            BuyerId = taker.IsBuyOrder ? taker.UserId : maker.UserId,
            SellerId = taker.IsBuyOrder ? maker.UserId : taker.UserId,
            Price = maker.Price, // Trade at maker's price
            Quantity = quantity,
            CurrencyType = taker.CurrencyType,
        };
        if (!tx.IsValid()) throw new InvalidOperationException("Invalid transaction.");
        return tx;
    }

    private async Task PersistTransactionAsync(Transaction trade, Order taker, Order maker, StockPrice sp, CancellationToken ct)
    {
        await _db.RunInTransactionAsync(async tx =>
        {
            // Get the different values 
            var currency = trade.CurrencyType;
            var qty = trade.Quantity;
            var buyerUnit = taker.IsBuyOrder ? taker.Price : maker.Price;
            var reserved = buyerUnit * qty;
            var spend = sp.Price * qty;
            var toRelease = reserved - spend;

            // If the buyer had a different price than the spend amount,
            // then the difference needs to be returned. 
            if (toRelease > 0)
            {
                var okRelease = await _portfolio.ReleaseReservedFundsAsync(toRelease, currency, trade.BuyerId, tx);
                if (!okRelease) throw new InvalidOperationException("Buyer release of over-reserved funds failed");
            }

            // Buyer pays cash (unreserve reserved funds, then withdraw funds, then add shares)
            var okFund = await _portfolio.ReleaseFromReservedFundsAsync(spend, currency, trade.BuyerId, tx);
            if (!okFund) throw new InvalidOperationException("Buyer reserved funds spend failed.");
            var okAddPos = await _portfolio.AddPositionAsync(trade.StockId, qty, trade.BuyerId, tx);
            if (!okAddPos) throw new InvalidOperationException("Buyer position add failed.");

            // Seller delivers shares (unreserve reserved shares, then remove shares, then add cash)
            var okPosition = await _portfolio.ReleaseFromReservedPositionAsync(trade.StockId, qty, trade.SellerId, tx);
            if (!okPosition) throw new InvalidOperationException("Seller reserved position spend failed.");
            var okAddFunds = await _portfolio.AddFundsAsync(spend, currency, trade.SellerId, tx);
            if (!okAddFunds) throw new InvalidOperationException("Seller funds add failed.");

            // Persist transaction, stock price and updated orders
            await _db.CreateTransaction(trade, tx);
            await _db.CreateStockPrice(sp, tx);

            await _db.UpdateOrder(taker, tx);
            await _db.UpdateOrder(maker, tx);
        }, ct);
    }
    #endregion

    #region Cancel Order
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
            return NotAuthorizedResult("Not allowed to cancel orders for the requested user.");

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
            _logger.LogError(ex, "Failed to cancel order #{OrderId} for user #{UserId}", orderId, actingUserId);
            return OperationFailedResult();
        }
        finally
        {
            gate.Release();
        }
    }
    
    private async Task CancelRemainderAsync(Order order, CancellationToken ct)
    {
        await _db.RunInTransactionAsync(async tx =>
        {
            if (order.IsBuyOrder)
            {
                var okRelease = await _portfolio.ReleaseReservedFundsAsync(order.RemainingAmount, order.CurrencyType, order.UserId, tx);
                if (!okRelease) throw new InvalidOperationException("Failed to release reserved funds for cancelled buy order.");
            } else {
                var okUnreserve = await _portfolio.UnreservePositionAsync(order.StockId, order.RemainingQuantity, order.UserId, tx);
                if (!okUnreserve) throw new InvalidOperationException("Failed to unreserve shares for cancelled sell order.");
            }

            order.Cancel();

            await _db.UpdateOrder(order, tx);
        }, ct);
    }
    #endregion

    #region Modify Order
    public async Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null, decimal? newPrice = null, 
        int? asUserId = null, CancellationToken ct = default)
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
            return NotAuthorizedResult($"Not allowed to modify orders for the requested user.");

        // Check parameters
        var vr = ValidateOrderChange(order, newQuantity, newPrice);
        if (vr != null) return vr;

        // Get the order book and wait for lock
        var gate = GetBookLock(order.StockId, order.CurrencyType);
        await gate.WaitAsync(ct);
        var book = await GetOrderBookByStockAsync(order.StockId, order.CurrencyType, ct);

        try
        {
            await ChangeOrderAsync(order, newQuantity, newPrice, ct);

            book.UpsertOrder(order);

            var fills = await FillTransactionsAsync(order, book, ct);

            return SuccessfullyModifiedResult(order, fills);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to modify order #{OrderId} for user #{UserId}", orderId, actingUserId);
            return OperationFailedResult();
        }
        finally
        {
            gate.Release();
        }
    }
    
    private OrderResult? ValidateOrderChange(Order order, int? newQuantity, decimal? newPrice)
    {
        if (!order.IsOpen)
            return AlreadyClosedResult();
        if (newQuantity.HasValue && newQuantity.Value <= 0)
            return ParamError("New quantity must be positive.");
        if (newPrice.HasValue && newPrice.Value <= 0)
            return ParamError("New price must be positive.");
        if (newQuantity.HasValue && newQuantity.Value < order.AmountFilled)
            return ParamError("New quantity cannot be less than already filled amount.");
        if (newPrice.HasValue && order.IsMarketOrder)
            return ParamError("Cannot change price of a market order.");

        return null;
    }

    private async Task ChangeOrderAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct)
    {
        var oldRemaining = order.RemainingQuantity;                   // depends on AmountFilled
        var newTotalQty = newQuantity ?? order.Quantity;
        if (newTotalQty < order.AmountFilled)
            throw new InvalidOperationException("New quantity < already filled.");

        var newRemaining = newTotalQty - order.AmountFilled;
        var oldUnitPrice = order.Price;
        var newUnitPrice = newPrice ?? order.Price;

        var reserveDelta = (newRemaining * newUnitPrice) - (oldRemaining * oldUnitPrice);
        var qtyDelta = newRemaining - oldRemaining;

        await _db.RunInTransactionAsync(async tx =>
        {
            if (order.IsBuyOrder)
            {
                if (reserveDelta > 0)
                {
                    var okReserve = await _portfolio.ReserveFundsAsync(reserveDelta, order.CurrencyType, order.UserId, tx);
                    if (!okReserve) throw new InvalidOperationException("Failed to reserve additional funds for modified buy order.");
                }
                else if (reserveDelta < 0)
                {
                    var okRelease = await _portfolio.ReleaseReservedFundsAsync(-reserveDelta, order.CurrencyType, order.UserId, tx);
                    if (!okRelease) throw new InvalidOperationException("Failed to release excess reserved funds for modified buy order.");
                }
            }
            else
            {
                if (qtyDelta > 0)
                {
                    var okReserve = await _portfolio.ReservePositionAsync(order.StockId, qtyDelta, order.UserId, tx);
                    if (!okReserve) throw new InvalidOperationException("Failed to reserve additional shares for modified sell order.");
                }
                else if (qtyDelta < 0)
                {
                    var okUnreserve = await _portfolio.UnreservePositionAsync(order.StockId, -qtyDelta, order.UserId, tx);
                    if (!okUnreserve) throw new InvalidOperationException("Failed to unreserve excess shares for modified sell order.");
                }
            }

            order.UpdateQuantity(newTotalQty);
            order.UpdatePrice(newUnitPrice);
            await _db.UpdateOrder(order, tx);
        }, ct);
    }
    #endregion

    #region Orderbook Helpers
    private OrderBook GetOrCreateBook(int stockId, CurrencyType currency) =>
        _orderBooks.GetOrAdd((stockId, currency),
        key => new OrderBook(stockId, currency));

    private async Task EnsureBookLoadedAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        // Check if already loaded
        if (_bookLoaded.ContainsKey((stockId, currency)))
            return;

        // Load the book from the database
        var book = GetOrCreateBook(stockId, currency);
        var snapshot = book.Snapshot();

        // Add existing open limit orders to the book
        var openLimits = await _db.GetOrdersByStockId(stockId, ct);
        foreach (var o in openLimits
                .Where(o => o.IsOpen && o.IsLimitOrder && o.CurrencyType == currency)
                .OrderBy(o => o.CreatedAt))
            book.UpsertOrder(o);

        // Mark as loaded
        _bookLoaded[(stockId, currency)] = true;
    }
    
    private SemaphoreSlim GetBookLock(int stockId, CurrencyType currency) => 
        _bookLocks.GetOrAdd((stockId, currency), _ => new SemaphoreSlim(1, 1));
    #endregion

    #region Admin Methods
    public async Task<(bool ok, string message)> ValidateBookAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        if (!IsAdmin)
            return (false, "Only admins may validate order books.");

        var gate = GetBookLock(stockId, currency);
        await gate.WaitAsync(ct);
        try
        {
            var book = await GetOrderBookByStockAsync(stockId, currency, ct);
            var ok = book.ValidateIndex(out var reason);
            return (ok, ok ? "OK" : reason);
        }
        finally { gate.Release(); }
    }

    public async Task<BookFixReport> FixBookAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        if (!IsAdmin)
            throw new UnauthorizedAccessException("Only admins may fix order books.");

        var gate = GetBookLock(stockId, currency);
        await gate.WaitAsync(ct);
        try
        {
            var book = await GetOrderBookByStockAsync(stockId, currency, ct);
            return book.FixAll();
        }
        finally { gate.Release(); }
    }

    public async Task RebuildBookIndexAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        if (!IsAdmin)
            throw new UnauthorizedAccessException("Only admins may rebuild order book indexes.");

        var gate = GetBookLock(stockId, currency);
        await gate.WaitAsync(ct);
        try
        {
            var book = await GetOrderBookByStockAsync(stockId, currency, ct);
            book.RebuildIndex();
        }
        finally { gate.Release(); }
    }
    #endregion

    #region Result helpers
    private OrderResult NotAuthResult() =>
        new() { Status = OrderStatus.NotAuthenticated, Message = "User not authenticated." };
    private OrderResult NotAuthorizedResult(string msg) =>
        new() { Status = OrderStatus.NotAuthorized, Message = msg };
    private OrderResult OperationFailedResult() =>
        new() { Status = OrderStatus.OperationFailed, Message = "An unexpected error occurred." };
    private OrderResult ParamError(string msg) =>
        new() { Status = OrderStatus.InvalidParameters, Message = msg };
    private OrderResult NoLiquidityResult(Order order, List<Transaction> transactions) =>
        new() {
            PlacedOrder = order,
            FillTransactions = transactions,
            Status = OrderStatus.NoLiquidity,
            Message = order.IsBuyOrder
                ? "Not enough sell-side liquidity at or below your max price."
                : "Not enough buy-side liquidity at or above your min price."
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
                : (order.RemainingQuantity > 0 ? OrderStatus.PartialFill : OrderStatus.Filled),
            FillTransactions = transactions,
            Message = order.IsOpen
                ? (transactions.Count > 0 ? "Order partially filled." : "Order placed on book.")
                : (order.RemainingQuantity > 0 ? "Order partially filled." : "Order fully filled.")
        };
    private OrderResult SuccessfullyModifiedResult(Order order, List<Transaction> fills) =>
        new() {
            PlacedOrder = order,
            Status = order.IsOpen
                ? (fills.Count > 0 ? OrderStatus.PartialFill : OrderStatus.PlacedOnBook)
                : (order.RemainingQuantity > 0 ? OrderStatus.PartialFill : OrderStatus.Filled),
            FillTransactions = fills,
            Message = order.IsOpen
                ? (fills.Count > 0 ? "Order partially filled after modification." : "Order modified and placed on book.")
                : (order.RemainingQuantity > 0 ? "Order partially filled after modification." : "Order fully filled after modification.")

        };
    #endregion
}