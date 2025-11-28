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
    #region Properties
    // Order book for each stock (keyed by stock ID and CurrencyType)
    // In-memory order books: price‐time priority
    // Buy: highest price first; Sell: lowest price first
    private readonly ConcurrentDictionary<(int, CurrencyType), OrderBook> _orderBooks = new();
    // Locks for loading order books from the database
    // The lock is per-stock, so different stocks can be processed in parallel.
    // The lock also protects loading the book from the database if needed.
    private readonly ConcurrentDictionary<(int, CurrencyType), SemaphoreSlim> _bookLocks = new();
    // Show if the book has been loaded at least once
    private readonly ConcurrentDictionary<(int, CurrencyType), bool> _bookLoaded = new();

    private int CurrentUserId => _auth.CurrentUser?.UserId ?? 0;
    private bool IsAuthenticated => _auth.IsLoggedIn && CurrentUserId > 0;
    private bool IsAdmin => _auth.CurrentUser?.IsAdmin == true;
    #endregion

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IAuthService _auth;
    private readonly IUserPortfolioService _portfolio;
    private readonly ILogger<MarketOrderService> _logger;
    private readonly IMarketDataService _market;
    private readonly IStockService _stock;
    private readonly ITransactionService _transaction;

    public MarketOrderService(IDataBaseService db, IAuthService authService, 
        IUserPortfolioService portfolio, ILogger<MarketOrderService> logger, 
        IStockService stock, IMarketDataService market, ITransactionService transaction)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _auth = authService ?? throw new ArgumentNullException(nameof(authService));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }
    #endregion

    #region Auth Helpers
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

    #region Place Order
    public async Task<OrderResult> PlaceAndMatchAsync(Order incoming, int? asUserId = null, CancellationToken ct = default)
    {
        // Check permissions
        var actingUserId = GetTargetUserIdOrFail(asUserId ?? incoming.UserId, out var authErr);
        if (authErr != null) return NotAuthResult();
        if (!CanModifyOrder(actingUserId))
            return NotAuthorizedResult("Not allowed to place orders for the requested user.");

        // Validate order
        var vr = ValidatePlacingOrder(incoming);
        if (vr != null) return vr;

        // Get the order book and wait for lock.
        await EnsureBookLoadedAsync(incoming.StockId, incoming.CurrencyType, ct).ConfigureAwait(false);
        var gate = GetBookLock(incoming.StockId, incoming.CurrencyType);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        // Keep track if we already persisted the order, so we can cancel it on error
        Order? persisted = null;
        List<Transaction> fills = new();
        try
        {
            // Resererve assets and persist the order in a transaction
            OrderResult? reserveFail = null;
            await using (var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                // Try to reserve funds or shares for the incoming order
                var err = await ReserveAssets(incoming, ct).ConfigureAwait(false);
                if (err != null)
                {
                    // Reservation failed, rollback and return error
                    reserveFail = err;
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    // Persist the incoming order
                    await _db.CreateOrder(incoming, ct).ConfigureAwait(false);
                    persisted = incoming;
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
            }
            // If reservation failed, return the error 
            if (reserveFail != null) { gate.Release(); return reserveFail; }

            _logger.LogInformation("Placing order: #{Order} for stock #{Stock} for {Quantity} @ {Price}", 
                incoming.OrderId, incoming.StockId, incoming.Quantity, incoming.Price);

            // Get the order book
            var book = GetOrCreateBook(incoming.StockId, incoming.CurrencyType);

            // Try to fill the order and create transactions
            fills = await FillTransactionsAsync(incoming, book, ct).ConfigureAwait(false);

            // if it still has remainder, place it on the book
            if (incoming.IsOpen && incoming.IsLimitOrder && incoming.RemainingQuantity > 0)
                book.UpsertOrder(incoming);

            // If it a market order and it still has remainder, cancel the remainder
            if (incoming.IsOpen && incoming.IsMarketOrder && incoming.RemainingQuantity > 0)
            {
                await CancelRemainderAsync(incoming, ct).ConfigureAwait(false);
                if (fills.Count == 0) return NoLiquidityResult(incoming, fills);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching order: {Message}", ex.Message);

            // If we already wrote the order, Make sure it is not left open.
            if (persisted is not null && persisted.IsOpen)
                await CancelRemainderAsync(persisted, ct).ConfigureAwait(false);
            return OperationFailedResult();
        }
        finally { gate.Release(); }

        // Notify market of each fill (for updating last price, etc)
        foreach (var tick in fills)
            await _market.OnTick(tick, ct).ConfigureAwait(false);

        // Return the a successful result
        return SuccessResult(incoming, fills);
    }

    private OrderResult? ValidatePlacingOrder(Order order)
    {
        if (order.Quantity <= 0) return ParamError("Quantity must be positive.");
        if (order.IsLimitOrder && order.Price <= 0m) return ParamError("Limit price must be positive.");

        if (order.IsTrueMarketOrder)
        {
            if (order.Price != 0m) return ParamError("TrueMarket must have Price = 0.");
            if (order.SlippagePercent.HasValue) return ParamError("TrueMarket cannot have slippage.");
        }
        if (order.IsSlippageOrder)
        {
            if (!order.SlippagePercent.HasValue) return ParamError("Slippage percent is required.");
            if (order.Price <= 0m) return ParamError("Slippage anchor price must be positive.");
        }
        if (order.IsInvalid) return ParamError("Order is invalid.");
        return null;
    }

    private async Task<OrderResult?> ReserveAssets(Order order, CancellationToken ct)
    {
        // Sell orders should reserve 
        if (order.IsSellOrder)
        {
            var ok = await _portfolio.ReservePositionAsync(
                order.StockId, order.Quantity, order.UserId, ct).ConfigureAwait(false);
            return ok ? null : ParamError("Insufficient shares to reserve for sell order.");
        }

        // True market orders do not reserve funds upfront
        if (order.IsTrueMarketOrder) return null;

        // Buy orders reserve funds based on total amount
        var cap = order.TotalAmount; // estimated total amount including slippage
        if (cap <= 0) return ParamError("Invalid total amount for buy order.");
        var okFunds = await _portfolio.ReserveFundsAsync(
            cap, order.CurrencyType, order.UserId, ct).ConfigureAwait(false);
        return okFunds ? null : ParamError("Insufficient funds to reserve for buy order.");
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
            var tx = await ExecuteFillAsync(incoming, bestOpposite, ct).ConfigureAwait(false);
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
            book.UpsertOrder(order); // No time priority change, simply don't care

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
        // True market orders always cross
        if (taker.IsTrueMarketOrder) return true;

        // Limit order price or market order effective limit
        var limit = taker.EffectiveTakerLimit!.Value;

        // Check if maker price crosses the taker limit
        return taker.IsBuyOrder ? maker.Price <= limit : maker.Price >= limit;
    }

    private async Task<Transaction> ExecuteFillAsync(Order taker, Order maker, CancellationToken ct)
    {
        // Determine fill quantity and price
        int qty = Math.Min(taker.RemainingQuantity, maker.RemainingQuantity);

        // Create transaction record
        var tx = CreateTransaction(taker, maker, qty);

        // Clone the taker and maker orders for persistence
        // When it fails later, the original orders are still intact
        var takerClone = taker.CloneFull();
        var makerClone = maker.CloneFull();

        // Persist changes using a cloned copy of the orders
        await PersistTransactionAsync(tx, takerClone, makerClone, ct).ConfigureAwait(false);

        // Log the fill for in memory orders
        taker.Fill(qty);
        maker.Fill(qty);

        _logger.LogInformation("Matched {Taker} with {Maker} for {Qty} @ {Price}",
            taker.OrderId, maker.OrderId, qty, maker.Price);

        // Update portfolio snapshots if needed
        await UpdatePortfolio(tx, ct).ConfigureAwait(false);

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

    private async Task PersistTransactionAsync(Transaction trade, Order taker, Order maker, CancellationToken ct)
    {
        // Start a transaction which does all in one go
        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Get the different values 
        var currency = trade.CurrencyType; 
        var qty = trade.Quantity;
        var spend = CurrencyHelper.RoundMoney(maker.Price * qty, currency);

        using (_portfolio.BeginSystemScope())
        {
            if (taker.IsTrueMarketOrder)
            {
                // For true market orders, spend straight from available funds
                var okPay = await _portfolio.WithdrawFundsAsync(
                    spend, currency, trade.BuyerId, ct).ConfigureAwait(false);
                if (!okPay) throw new InvalidOperationException("Buyer funds withdrawal failed.");
            }
            else
            {
                // What the taker pre-reserved per unit (if any)
                var buyer = taker.IsBuyOrder ? taker : maker;
                var reservedUnit = buyer.IsLimitOrder ? buyer.Price : buyer.PriceWithSlippage!.Value;

                // Release any over-reserved funds
                var release = CurrencyHelper.RoundMoney(reservedUnit * qty - spend, currency);
                if (release > 0m)
                {
                    var okRefund = await _portfolio.ReleaseReservedFundsAsync(
                        release, currency, trade.BuyerId, ct).ConfigureAwait(false);
                    if (!okRefund) throw new InvalidOperationException("Buyer release of over-reserved funds failed");
                }

                // Spend from reserved
                var okSpend = await _portfolio.ReleaseFromReservedFundsAsync(
                    spend, currency, trade.BuyerId, ct).ConfigureAwait(false);
                if (!okSpend) throw new InvalidOperationException("Buyer reserved funds spend failed.");
            }

            // Buyer gets shares
            var okAddPos = await _portfolio.AddPositionAsync(
                trade.StockId, qty, trade.BuyerId, ct).ConfigureAwait(false);
            if (!okAddPos) throw new InvalidOperationException("Buyer position add failed.");

            // Seller delivers shares
            var okPosition = await _portfolio.ReleaseFromReservedPositionAsync(
                trade.StockId, qty, trade.SellerId, ct).ConfigureAwait(false);
            if (!okPosition) throw new InvalidOperationException("Seller reserved position spend failed.");

            // Seller gets funds
            var okAddFunds = await _portfolio.AddFundsAsync(
                spend, currency, trade.SellerId, ct).ConfigureAwait(false);
            if (!okAddFunds) throw new InvalidOperationException("Seller funds add failed.");

            // Fill both orders
            taker.Fill(qty);
            maker.Fill(qty);
            if (!taker.IsValid() || !maker.IsValid())
                throw new InvalidOperationException("Order became invalid after fill.");

            // Persist transaction and updated orders
            await _db.CreateTransaction(trade, ct).ConfigureAwait(false);
            await _db.UpdateOrder(taker, ct).ConfigureAwait(false);
            await _db.UpdateOrder(maker, ct).ConfigureAwait(false);

            // Commit to the database
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task UpdatePortfolio(Transaction trade, CancellationToken ct)
    {
        if (!IsAuthenticated) return;

        try
        {
            using (_portfolio.BeginSystemScope())
            {
                await _portfolio.RefreshAsync(trade.BuyerId, ct).ConfigureAwait(false);
                await _portfolio.RefreshAsync(trade.SellerId, ct).ConfigureAwait(false);
                await _transaction.RefreshAsync(trade.BuyerId, ct).ConfigureAwait(false);
                await _transaction.RefreshAsync(trade.SellerId, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh portfolio/transactions after trade for user #{UserId}", CurrentUserId);
        }
    }
    #endregion

    #region Cancel Order
    public async Task<OrderResult> CancelOrderAsync(int orderId, int? asUserId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Get the order
        var order = await _db.GetOrderById(orderId, ct).ConfigureAwait(false);
        if (order is null || !order.IsOpen)
            return AlreadyClosedResult();

        // Check permissions
        var actingUserId = GetTargetUserIdOrFail(asUserId ?? order.UserId, out var authErr);

        if (authErr != null) return NotAuthResult();
        if (!CanModifyOrder(actingUserId))
            return NotAuthorizedResult("Not allowed to cancel orders for the requested user.");

        // Get the order book and wait for lock
        await EnsureBookLoadedAsync(order.StockId, order.CurrencyType, ct).ConfigureAwait(false);
        var gate = GetBookLock(order.StockId, order.CurrencyType);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Get the order book
            var book = GetOrCreateBook(order.StockId, order.CurrencyType); ;

            // Cancel the remainder and release reserved assets
            await CancelRemainderAsync(order, ct).ConfigureAwait(false);

            // Remove from book if present
            if (!book.RemoveById(orderId))
                _logger.LogDebug("Cancel requested, but order #{OrderId} not present in in-memory book (already removed?).", orderId);

            return SuccessfullyCancelledResult(order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel order #{OrderId} for user #{UserId}", orderId, actingUserId);
            return OperationFailedResult();
        }
        finally { gate.Release(); }
    }
    
    private async Task CancelRemainderAsync(Order order, CancellationToken ct)
    {
        using (_portfolio.BeginSystemScope())
        {
            await _db.RunInTransactionAsync(async txct =>
            {
                if (order.IsBuyOrder)
                {
                    // Only release if there is something to release
                    if (!order.IsTrueMarketOrder && order.RemainingAmount > 0m)
                    {
                        // Release reserved funds
                        var okRelease = await _portfolio.ReleaseReservedFundsAsync(order.RemainingAmount, 
                            order.CurrencyType, order.UserId, txct).ConfigureAwait(false);
                        if (!okRelease) throw new InvalidOperationException(
                            "Failed to release reserved funds for cancelled buy order.");
                    }
                } 
                else 
                {
                    // Release reserved shares
                    var okUnreserve = await _portfolio.UnreservePositionAsync(order.StockId,
                        order.RemainingQuantity, order.UserId, txct).ConfigureAwait(false);
                    if (!okUnreserve) throw new InvalidOperationException(
                        "Failed to unreserve shares for cancelled sell order.");
                }

                // Cancel the order
                order.Cancel();
                if (!order.IsValid()) throw new InvalidOperationException(
                    "Order became invalid on cancellation.");
                await _db.UpdateOrder(order, txct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        
    }
    #endregion

    #region Modify Order
    public async Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null, decimal? newPrice = null, 
        int? asUserId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Get the order
        var order = await _db.GetOrderById(orderId, ct).ConfigureAwait(false);
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
        await EnsureBookLoadedAsync(order.StockId, order.CurrencyType, ct).ConfigureAwait(false);
        var gate = GetBookLock(order.StockId, order.CurrencyType);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        List<Transaction> fills = new();
        try
        {
            // Get the order book
            var book = await GetOrderBookByStockAsync(
                order.StockId, order.CurrencyType, ct).ConfigureAwait(false);

            // Modify the order
            await ChangeOrderAsync(order, newQuantity, newPrice, ct).ConfigureAwait(false);

            // Check for any fills after modification
            fills = await FillTransactionsAsync(order, book, ct).ConfigureAwait(false);

            // if it still has remainder, place it on the book
            if (order.IsOpen && order.IsLimitOrder && order.RemainingQuantity > 0)
                book.UpsertOrder(order);
            else book.RemoveById(order.OrderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to modify order #{OrderId} for user #{UserId}", orderId, actingUserId);
            return OperationFailedResult();
        }
        finally { gate.Release(); }

        // Notify market of each fill (for updating last price, etc)
        foreach (var tick in fills)
            await _market.OnTick(tick, ct).ConfigureAwait(false);

        // Return the a successful result
        return SuccessfullyModifiedResult(order, fills);
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
        var oldRemaining = order.RemainingQuantity;   
        var newTotalQty = newQuantity ?? order.Quantity;
        if (newTotalQty < order.AmountFilled)
            throw new InvalidOperationException("New quantity < already filled.");

        var newRemaining = newTotalQty - order.AmountFilled;
        var oldUnitPrice = order.Price;
        var newUnitPrice = newPrice ?? order.Price;

        var reserveDelta = (newRemaining * newUnitPrice) - (oldRemaining * oldUnitPrice);
        var qtyDelta = newRemaining - oldRemaining;

        using (_portfolio.BeginSystemScope())
        {
            await _db.RunInTransactionAsync(async txct =>
            {
                if (order.IsBuyOrder)
                {
                    if (reserveDelta > 0)
                    {
                        var okReserve = await _portfolio.ReserveFundsAsync(reserveDelta, 
                            order.CurrencyType, order.UserId, txct).ConfigureAwait(false);
                        if (!okReserve) throw new InvalidOperationException(
                            "Failed to reserve additional funds for modified buy order.");
                    }
                    else if (reserveDelta < 0)
                    {
                        var okRelease = await _portfolio.ReleaseReservedFundsAsync(-reserveDelta, 
                            order.CurrencyType, order.UserId, txct).ConfigureAwait(false);
                        if (!okRelease) throw new InvalidOperationException(
                            "Failed to release excess reserved funds for modified buy order.");
                    }
                }
                else
                {
                    if (qtyDelta > 0)
                    {
                        var okReserve = await _portfolio.ReservePositionAsync(
                            order.StockId, qtyDelta, order.UserId, txct).ConfigureAwait(false);
                        if (!okReserve) throw new InvalidOperationException(
                            "Failed to reserve additional shares for modified sell order.");
                    }
                    else if (qtyDelta < 0)
                    {
                        var okUnreserve = await _portfolio.UnreservePositionAsync(
                            order.StockId, -qtyDelta, order.UserId, txct).ConfigureAwait(false);
                        if (!okUnreserve) throw new InvalidOperationException(
                            "Failed to unreserve excess shares for modified sell order.");
                    }
                }

                order.UpdateQuantity(newTotalQty);
                order.UpdatePrice(newUnitPrice);
                if (!order.IsValid()) throw new InvalidOperationException("Order became invalid on modification.");
                await _db.UpdateOrder(order, txct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
    }
    #endregion

    #region Orderbook Methods
    public async Task<OrderBook> GetOrderBookByStockAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureBookLoadedAsync(stockId, currency, ct).ConfigureAwait(false);
        return GetOrCreateBook(stockId, currency);
    }

    private OrderBook GetOrCreateBook(int stockId, CurrencyType currency) =>
        _orderBooks.GetOrAdd((stockId, currency),
        key => new OrderBook(stockId, currency));

    private async Task EnsureBookLoadedAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        var key = (stockId, currency);
        // Check if already loaded
        if (_bookLoaded.ContainsKey(key)) return;

        // Load existing open limit orders from the database
        var openLimits = await _db.GetOpenLimitOrders(stockId, currency, ct).ConfigureAwait(false);

        // Wait for the lock
        var gate = GetBookLock(stockId, currency);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bookLoaded.ContainsKey(key)) return;

            // Load the book from the database
            var book = GetOrCreateBook(stockId, currency);

            // Add existing open limit orders to the book
            foreach (var o in openLimits)
                book.UpsertOrder(o);

            // Mark as loaded
            _bookLoaded[key] = true;
        }
        finally { gate.Release(); }
    }
    
    private SemaphoreSlim GetBookLock(int stockId, CurrencyType currency) => 
        _bookLocks.GetOrAdd((stockId, currency), _ => new SemaphoreSlim(1, 1));
    #endregion

    #region Admin Methods
    public async Task<(bool ok, string message)> ValidateBookAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        if (!IsAdmin)
            return (false, "Only admins may validate order books.");

        await EnsureBookLoadedAsync(stockId, currency, ct).ConfigureAwait(false);
        var gate = GetBookLock(stockId, currency);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var book = GetOrCreateBook(stockId, currency);
            var ok = book.ValidateIndex(out var reason);
            return (ok, ok ? "OK" : reason);
        }
        finally { gate.Release(); }
    }

    public async Task<BookFixReport> FixBookAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        if (!IsAdmin)
            throw new UnauthorizedAccessException("Only admins may fix order books.");

        await EnsureBookLoadedAsync(stockId, currency, ct).ConfigureAwait(false);
        var gate = GetBookLock(stockId, currency);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var book = GetOrCreateBook(stockId, currency);
            return book.FixAll();
        }
        finally { gate.Release(); }
    }

    public async Task RebuildBookIndexAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        if (!IsAdmin)
            throw new UnauthorizedAccessException("Only admins may rebuild order book indexes.");

        await EnsureBookLoadedAsync(stockId, currency, ct).ConfigureAwait(false);
        var gate = GetBookLock(stockId, currency);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var book = GetOrCreateBook(stockId, currency);
            book.RebuildIndex();
        }
        finally { gate.Release(); }
    }
    #endregion

    #region Result helpers
    private OrderResult NotAuthResult() =>
        new() { Status = OrderStatus.NotAuthenticated, ErrorMessage = "User not authenticated." };
    private OrderResult NotAuthorizedResult(string msg) =>
        new() { Status = OrderStatus.NotAuthorized, ErrorMessage = msg };
    private OrderResult OperationFailedResult() =>
        new() { Status = OrderStatus.OperationFailed, ErrorMessage = "An unexpected error occurred." };
    private OrderResult ParamError(string msg) =>
        new() { Status = OrderStatus.InvalidParameters, ErrorMessage = msg };
    private OrderResult NoLiquidityResult(Order order, List<Transaction> transactions) =>
        new() {
            PlacedOrder = order,
            FillTransactions = transactions,
            Status = OrderStatus.NoLiquidity,
            ErrorMessage = order.IsBuyOrder
                ? "Not enough sell-side liquidity at or below your max price."
                : "Not enough buy-side liquidity at or above your min price."
        };
    private OrderResult AlreadyClosedResult() =>
        new() { Status = OrderStatus.AlreadyClosed, ErrorMessage = "Order not open." };
    private OrderResult SuccessfullyCancelledResult(Order order) =>
        new() {
            PlacedOrder = order,
            Status = OrderStatus.Success,
            SuccessMessage = "Order successfully cancelled."
        };
    private OrderResult SuccessResult(Order order, List<Transaction> transactions) =>
        new() {
            PlacedOrder = order,
            Status = order.IsOpen
                ? (transactions.Count > 0 ? OrderStatus.PartialFill : OrderStatus.PlacedOnBook)
                : (order.RemainingQuantity > 0 ? OrderStatus.PartialFill : OrderStatus.Filled),
            FillTransactions = transactions,
            SuccessMessage = order.IsOpen
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
            SuccessMessage = order.IsOpen
                ? (fills.Count > 0 ? "Order partially filled after modification." : "Order modified and placed on book.")
                : (order.RemainingQuantity > 0 ? "Order partially filled after modification." : "Order fully filled after modification.")

        };
    #endregion
}