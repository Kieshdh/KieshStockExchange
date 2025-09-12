using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

public class UserOrderService : IUserOrderService
{
    #region Private Fields
    private readonly IDataBaseService _db;
    private readonly IAuthService _auth;
    private readonly IUserPortfolioService _portfolio;
    private readonly IMarketOrderService _market;
    private readonly ILogger<UserOrderService> _logger;
    #endregion

    #region Order Properties
    public List<Order> UserAllOrders { get; private set; } = new();
    public IReadOnlyList<Order> UserOpenOrders =>
        UserAllOrders.Where(o => o.IsOpen).ToList();
    public IReadOnlyList<Order> UserCancelledOrders =>
        UserAllOrders.Where(o => o.IsCancelled).ToList();
    public IReadOnlyList<Order> UserFilledOrders =>
        UserAllOrders.Where(o => o.IsFilled).ToList();
    #endregion

    #region Constructor
    public UserOrderService(
        IDataBaseService dbService,
        IAuthService authService,
        IUserPortfolioService portfolioService,
        IMarketOrderService marketService,
        ILogger<UserOrderService> logger)
    {
        _db = dbService ?? throw new ArgumentNullException(nameof(dbService));
        _auth = authService ?? throw new ArgumentNullException(nameof(authService));
        _portfolio = portfolioService ?? throw new ArgumentNullException(nameof(portfolioService));
        _market = marketService ?? throw new ArgumentNullException(nameof(marketService)); 
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Auth Helpers
    private User CurrentUser => _auth.CurrentUser;
    private int CurrentUserId => CurrentUser?.UserId ?? 0;
    private bool IsAuthenticated => CurrentUserId > 0 && _auth.IsLoggedIn;

    private bool CanModifyOrder(Order order, int targetUserId)
    {
        // Admin can modify any; otherwise order must belong to the target user (which for non-admin == current)
        if (_auth.CurrentUser?.IsAdmin == true) return true;
        return order.UserId == targetUserId && targetUserId == CurrentUserId;
    }

    private int GetTargetUserIdOrFail(int? asUserId, out OrderResult? authError)
    {
        authError = null;

        // No impersonation -> act as current user
        if (!asUserId.HasValue || asUserId.Value == CurrentUserId)
            return CurrentUserId;

        // Impersonation requested: require admin
        if (_auth.CurrentUser?.IsAdmin == true)
            return asUserId.Value;

        authError = AuthError("Only admins may act on behalf of other users.");
        return 0;
    }
    #endregion

    #region Public Methods
    public async Task<bool> RefreshOrdersAsync(int? asUserId = null, CancellationToken ct = default)
    {
        if (!IsAuthenticated)
        {
            _logger.LogWarning("Tried to update orders without authentication.");
            return false;
        }
        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authFail);
        if (authFail != null) { _logger.LogWarning(authFail.Message); return false; }

        try
        {
            UserAllOrders = (await _db.GetOrdersByUserId(targetUserId)).ToList();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching orders for user #{UserId}", targetUserId);
            return false;
        }
    }

    public async Task<OrderResult> CancelOrderAsync(int orderId, int? asUserId = null, CancellationToken ct = default)
    {
        // Check if able to cancel order
        if (!IsAuthenticated)
            return NotAuthResult();

        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authError);
        if (authError != null) return authError;

        // If anything fails return failed result
        OrderResult result = OperationFailedResult();
        try
        {
            await WithTransactionAsync(async tx =>
            {
                // Find the order
                var order = await UpdateAndFindOrderAsync(orderId, targetUserId, tx);
                if (order == null) { result = ParamError("Order does not exist or is not open."); return; }

                // Permission check
                if (!CanModifyOrder(order, targetUserId))
                {
                    result = AuthError("No permission to cancel this order.");
                    return;
                }

                // Release reservations first
                if (order.IsBuyOrder)
                    await _portfolio.ReleaseReservedFundsAsync(order.RemainingAmount, order.CurrencyType, targetUserId, tx);
                else
                    await _portfolio.UnreservePositionAsync(order.StockId, order.RemainingQuantity, targetUserId, tx);

                // Cancel the order and persist
                order.Cancel();
                if (!order.IsValid()) { result = OperationFailedResult(); return; }

                await _db.UpdateOrder(order, tx);

                result = new OrderResult
                {
                    PlacedOrder = order,
                    Status = OrderStatus.Success,
                    Message = "Order has been cancelled."
                };
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel order #{OrderId} for user #{UserId}", orderId, targetUserId);
            return OperationFailedResult();
        }
    }

    public async Task<OrderResult> ChangeOrderAsync(int orderId, int? newQuantity, 
        decimal? price, int? asUserId = null, CancellationToken ct = default)
    {
        // Check if able to place order
        if (!IsAuthenticated)
            return NotAuthResult();

        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authError);
        if (authError != null) return authError;

        // If anything fails return failed result
        OrderResult result = OperationFailedResult();
        try
        {
            await WithTransactionAsync(async tx =>
            {
                // Load and find the order. Then check permissions. 
                var order = await UpdateAndFindOrderAsync(orderId, targetUserId, tx);
                if (order == null) { result = ParamError("Order does not exist or is not open."); return; }

                // Permission check
                if (!CanModifyOrder(order, targetUserId))
                {
                    result = AuthError("No permission to cancel this order.");
                    return;
                }

                // Validate inputs (pure checks first, before any state changes)
                var inputError = ValidateOrderChange(order, newQuantity, price);
                if (inputError != null) { result = inputError; return; }

                // Ensure portfolio is loaded
                await _portfolio.RefreshAsync(targetUserId, tx);
                if (newQuantity.HasValue && order.IsBuyOrder)
                {
                    // Check funds for any change in quantity
                    var oldQty = order.RemainingQuantity;
                    var newQty = newQuantity.Value;
                    var unit = price ?? order.Price;

                    // If the user want to increase the order, then reserve additional funds
                    if (newQty > oldQty) 
                    {
                        var deltaQty = newQty - oldQty;
                        var ok = await _portfolio.ReserveFundsAsync(unit * deltaQty, order.CurrencyType, targetUserId, tx);
                        if (!ok) { result = ParamError("Insufficient funds to increase quantity."); return; }
                    }
                    // Likewise release reserved funds
                    else if (newQty < oldQty)
                    {
                        var deltaQty = oldQty - newQty;
                        var ok = await _portfolio.ReleaseReservedFundsAsync(unit * deltaQty, order.CurrencyType, targetUserId, tx);
                        if (!ok) { result = ParamError("Not enough reserved funds to release"); return; }
                    }
                }
                else if (newQuantity.HasValue && order.IsSellOrder)
                {
                    // Check funds for any change in quantity
                    var oldQty = order.RemainingQuantity;
                    var newQty = newQuantity.Value;

                    // If the user want to increase the order, then reserve additional stocks
                    if (newQty > oldQty)
                    {
                        var delta = newQty - oldQty;
                        var ok = await _portfolio.ReservePositionAsync(order.StockId, delta, targetUserId, tx);
                        if (!ok) { result = ParamError("Insufficient shares to increase quantity."); return; }
                    }
                    // Likewise release reserved stocks
                    else if (newQty < oldQty)
                    {
                        var delta = oldQty - newQty;
                        var ok = await _portfolio.UnreservePositionAsync(order.StockId, delta, targetUserId, tx);
                        if (!ok) { result = ParamError("Insufficient shares to unreserve."); return; }
                    }
                }

                // Update order properties
                if (newQuantity.HasValue) order.UpdateQuantity(newQuantity.Value);
                if (price.HasValue) order.UpdatePrice(price.Value);
                if (!order.IsValid()) { result = ParamError("Invalid order parameters after update."); return; }

                // Persist changes
                await _db.UpdateOrder(order);

                _logger.LogInformation($"Order {orderId} for user #{targetUserId}");

                result = new OrderResult
                {
                    PlacedOrder = order,
                    Status = OrderStatus.Success,
                    Message = "Order has been successfully edited."
                };
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error editing order {orderId} for user #{targetUserId}");
            return OperationFailedResult();
        }
    }
    #endregion

    #region Placing Orders 
    public Task<OrderResult> PlaceLimitBuyOrderAsync(int stockId, int qty, decimal limitPrice,
            int? asUserId = null, CancellationToken ct = default) =>
        PlaceOrderAsync(stockId, qty, true, true, limitPrice, asUserId, ct);

    public Task<OrderResult> PlaceLimitSellOrderAsync(int stockId, int qty, decimal limitPrice,
            int? asUserId = null, CancellationToken ct = default) =>
        PlaceOrderAsync(stockId, qty, false, true, limitPrice, asUserId, ct);

    public Task<OrderResult> PlaceMarketBuyOrderAsync(int stockId, int qty, decimal maxPrice,
            int? asUserId = null, CancellationToken ct = default) =>
        PlaceOrderAsync(stockId, qty, true, false, maxPrice, asUserId, ct);

    public Task<OrderResult> PlaceMarketSellOrderAsync(int stockId, int qty, decimal minPrice,
             int? asUserId = null, CancellationToken ct = default) =>
        PlaceOrderAsync(stockId, qty, false, false, minPrice, asUserId, ct);

    private async Task<OrderResult> PlaceOrderAsync(int stockId, int quantity, 
        bool buyOrder, bool limitOrder, decimal price, int? asUserId, CancellationToken ct)
    {
        // Check if able to place order
        if (!IsAuthenticated)
            return NotAuthResult();
        if (stockId <= 0 || quantity <= 0)
            return ParamError("StockId or quantity invalid.");

        var targetUserId = GetTargetUserIdOrFail(asUserId, out var authError);
        if (authError != null) return authError;

        // If anything fails return failed result
        OrderResult result = OperationFailedResult();
        try
        {
            await WithTransactionAsync(async tx =>
            {
                var stock = await _db.GetStockById(stockId, tx);
                if (stock == null) { result = ParamError("Stock not found."); return; }

                var baseCurrency = _portfolio.GetBaseCurrency();

                //  Load portfolio snapshot for the target user
                await _portfolio.RefreshAsync(targetUserId, tx);
                var fund = GetFund(targetUserId);
                var holding = GetHolding(targetUserId, stockId);

                var OkAssets = CheckHolding(fund, holding, buyOrder, price, quantity);
                if (OkAssets != null) { result = OkAssets; return; }

                // Create the order
                var order = CreateOrder(targetUserId, stockId, quantity, price, baseCurrency, buyOrder, limitOrder);
                if (!order.IsValid()) { result = ParamError("Invalid order parameters."); return; }

                // Reserve capital / shares
                var okReserve = await ReserveAssets(order, tx);
                if (okReserve != null) { result = okReserve; return; }

                // Persist the order
                await _db.CreateOrder(order, tx);
                UserAllOrders.Add(order);

                _logger.LogInformation($"Order placed: {order.OrderId} for user #{targetUserId}");

                result = OrderSuccessResult(order, "Order has been successfully placed.");
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order for targetUser #{UserId}", targetUserId);
            return OperationFailedResult();
        }
    }
    #endregion

    #region Private Helpers
    /// <summary> Wrapper so we can get the orderresult while running inside a database transaction </summary>
    private Task WithTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct)
        => _db.RunInTransactionAsync(action, ct);

    private async Task<Order?> UpdateAndFindOrderAsync(int orderId, int targetUserId, CancellationToken ct)
    {
        await RefreshOrdersAsync(targetUserId, ct);
        return UserOpenOrders.FirstOrDefault(o => o.OrderId == orderId);
    }

    private OrderResult? ValidateOrderChange(Order order, int? newQuantity, decimal? price)
    {
        if (order.IsFilled || order.IsCancelled)
            return ParamError("Cannot edit a filled or cancelled order.");
        if (!newQuantity.HasValue && !price.HasValue)
            return ParamError("No changes specified.");
        if (newQuantity.HasValue && newQuantity < 0)
            return ParamError("Quantity cannot be negative.");
        if (newQuantity.HasValue && newQuantity == 0)
            return ParamError("New quantity cannot be zero. Cancel order instead.");
        if (newQuantity.HasValue && newQuantity > order.RemainingQuantity)
            return ParamError("New quantity must be ≤ remaining quantity.");
        if (price.HasValue && price <= 0)
            return ParamError("Price must be greater than zero.");
        if (price.HasValue && order.IsMarketOrder)
            return ParamError("Cannot change price of a market order.");
        return null;
    }

    private Fund GetFund(int targetUserId) =>
        _portfolio.GetBaseFund() ?? new Fund { UserId = targetUserId, CurrencyType = _portfolio.GetBaseCurrency() };
    private Position GetHolding(int targetUserId, int stockId) =>
        _portfolio.GetPositionByStockId(stockId) ?? new Position { UserId = targetUserId, StockId = stockId };

    private OrderResult? CheckHolding(Fund fund, Position holding, bool buyOrder, decimal price, int quantity)
    {
        if (buyOrder) // Check funds
        {
            var required = price * quantity;
            if (fund.AvailableBalance < required)
            {
                _logger.LogWarning("User {UserId} has insufficient funds: avail {Available} " +
                    "needed {Required}", fund.UserId, fund.AvailableBalance, price * quantity);
                return ParamError("Insufficient funds.");
            }
        }
        else // Check shares
        {
            if (holding.RemainingQuantity < quantity)
            {
                _logger.LogWarning("User {UserId} has insufficient shares of stock #{StockId}",
                    fund.UserId, holding.StockId);
                return ParamError("Insufficient shares.");
            }
        }
        return null;
    }
    
    private Order CreateOrder(int userId, int stockId, int quantity,
        decimal price, CurrencyType currency, bool buyOrder, bool limitOrder)
    {
        return new Order {
            UserId = userId,
            StockId = stockId,
            Quantity = quantity,
            Price = price,
            CurrencyType = currency,
            OrderType = buyOrder ?
                (limitOrder ? Order.Types.LimitBuy : Order.Types.MarketBuy)
              : (limitOrder ? Order.Types.LimitSell : Order.Types.MarketSell),
        };
    }

    private async Task<OrderResult?> ReserveAssets(Order order, CancellationToken ct)
    {
        // Reserve capital / shares
        if (order.IsBuyOrder)
        {
            var ok = await _portfolio.ReserveFundsAsync(order.Price * order.Quantity, order.CurrencyType, order.UserId, ct);
            if (!ok) return ParamError("Failed to reserve funds.");
        }
        else
        {
            var ok = await _portfolio.ReservePositionAsync(order.StockId, order.Quantity, order.UserId, ct);
            if (!ok) return ParamError("Failed to reserve shares.");
        }
        return null;
    }
    #endregion

    #region Helper Results
    private OrderResult NotAuthResult() => new()
        { Status = OrderStatus.NotAuthenticated, Message = "User not authenticated." };
    private OrderResult ParamError(string msg) => new()
        { Status = OrderStatus.InvalidParameters, Message = msg };
    private OrderResult AuthError(string msg) => new()
        { Status = OrderStatus.NotAuthorized, Message = msg };
    private OrderResult OperationFailedResult() => new()  
        { Status = OrderStatus.OperationFailed, Message = "An unexpected error occurred." };
    private OrderResult NoMarketPriceResult() => new() 
        { Status = OrderStatus.NoMarketPrice, Message = "No market price available." };
    private OrderResult OrderSuccessResult(Order order, string msg) => new()
        { Status = OrderStatus.Success, Message = msg, PlacedOrder = order };
    #endregion
}
