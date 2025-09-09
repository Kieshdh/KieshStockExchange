using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KieshStockExchange.Services.Implementations;

public class UserOrderService : IUserOrderService
{
    #region Private Fields
    private readonly IDataBaseService _dbService;
    private readonly IAuthService _authService;
    private readonly IUserPortfolioService _portfolio;
    private readonly ILogger<UserOrderService> _logger;

    private User CurrentUser => _authService.CurrentUser;
    private IReadOnlyList<Fund> CurrentFunds => _portfolio.GetFunds();
    private IReadOnlyList<Position> CurrentPositions => _portfolio.GetPositions();
    private CurrencyType CurrencyType => _portfolio.GetBaseCurrency();

    private Fund CurrentFund => _portfolio.GetBaseFund()
        ?? new Fund { UserId = UserId, CurrencyType = CurrencyType };

    private int UserId => CurrentUser?.UserId ?? 0;
    private bool IsAuthenticated => UserId > 0 && _authService.IsLoggedIn;
    #endregion

    #region Order Properties
    public List<Order> UserAllOrders { get; private set; } = new();
    public IReadOnlyList<Order> UserOpenOrders =>
        UserAllOrders.Where(o => o.IsOpen()).ToList();
    public IReadOnlyList<Order> UserCancelledOrders =>
        UserAllOrders.Where(o => o.IsCancelled()).ToList();
    public IReadOnlyList<Order> UserFilledOrders =>
        UserAllOrders.Where(o => o.IsFilled()).ToList();
    #endregion

    #region Constructor
    public UserOrderService(
        IDataBaseService dbService,
        IAuthService authService,
        IUserPortfolioService portfolioService,
        ILogger<UserOrderService> logger)
    {
        _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _portfolio = portfolioService ?? throw new ArgumentNullException(nameof(portfolioService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Public Methods
    public async Task<bool> RefreshOrdersAsync()
    {
        if (!IsAuthenticated)
        {
            _logger.LogWarning("Tried to update orders without authentication.");
            return false;
        }

        try
        {
            UserAllOrders = (await _dbService.GetOrdersByUserId(UserId)).ToList();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching orders for user {UserId}", UserId);
            return false;
        }
    }

    public async Task<OrderResult> CancelOrderAsync(int orderId)
    {
        if (!IsAuthenticated)
            return NotAuthResult();

        try
        {
            var order = UserOpenOrders.FirstOrDefault(o => o.OrderId == orderId)
                     ?? (await UpdateAndFindOrderAsync(orderId));
            if (order == null)
                return ParamError("Order does not exist or is not open.");

            if (!_authService.CurrentUser.IsAdmin && order.UserId != UserId)
                return AuthError("No permission to cancel this order.");

            order.Cancel();
            if (!order.IsValid())
                return OperationFailedResult();

            // Release reserved funds or shares
            await _portfolio.RefreshAsync();
            if (order.IsBuyOrder())
            {
                await _portfolio.ReleaseReservedFundsAsync(
                    order.RemainingAmount(), CurrencyType);
            }
            else
            {
                await _portfolio.UnreservePositionAsync(
                    order.StockId, order.RemainingQuantity());
            }

            await _dbService.UpdateOrder(order);

            return new OrderResult
            {
                PlacedOrder = order,
                Status = OrderStatus.Success,
                Message = "Order has been cancelled."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel order {OrderId} for {UserId}", orderId, UserId);
            return OperationFailedResult();
        }
    }

    public async Task<OrderResult> ChangeOrderAsync(int orderId, int? newQuantity, decimal? price)
    {
        if (!IsAuthenticated)
            return NotAuthResult();
        try
        {
            // Load and find the order. Then check permissions. 
            var order = await UpdateAndFindOrderAsync(orderId);
            if (order == null)
                return ParamError("Order does not exist or is not open.");
            if (!_authService.CurrentUser.IsAdmin && order.UserId != UserId)
                return AuthError("No permission to edit this order.");

            // Check parameters
            if (order.IsFilled() || order.IsCancelled())
                return ParamError("Cannot edit a filled or cancelled order.");
            if (newQuantity.HasValue && newQuantity <= order.RemainingQuantity() )
                return ParamError("New quantity must be less than or equal to remaining quantity.");
            if (price.HasValue && price <= 0)
                return ParamError("Price must be greater than zero.");
            if (newQuantity.HasValue && newQuantity == 0)
                return ParamError("New quantity cannot be zero. Cancel order instead.");
            if (price.HasValue && order.IsMarketOrder())
                return ParamError("Cannot change price of a market order.");

            // Ensure fund is loaded
            await _portfolio.RefreshAsync();

            if (order.IsBuyOrder() && newQuantity.HasValue)
            {
                // Check funds for any increase in quantity
                var oldQty = order.RemainingQuantity();
                var newQty = newQuantity.Value;
                var unit = price ?? order.Price;

                if (newQty > oldQty)
                {
                    // need to reserve the delta
                    var deltaQty = newQty - oldQty;
                    var ok = await _portfolio.ReserveFundsAsync(unit * deltaQty, CurrencyType);
                    if (!ok) return ParamError("Insufficient funds to increase quantity.");
                }
                else if (newQty < oldQty)
                {
                    // release the delta
                    var deltaQty = oldQty - newQty;
                    await _portfolio.ReleaseReservedFundsAsync(unit * deltaQty, CurrencyType);
                }
            }
            else if (order.IsSellOrder() && newQuantity.HasValue)
            {
                var oldQty = order.RemainingQuantity();
                var newQty = newQuantity.Value;

                if (newQty > oldQty)
                {
                    var delta = newQty - oldQty;
                    var ok = await _portfolio.ReservePositionAsync(order.StockId, delta);
                    if (!ok) return ParamError("Insufficient shares to increase quantity.");
                }
                else if (newQty < oldQty)
                {
                    var delta = oldQty - newQty;
                    await _portfolio.UnreservePositionAsync(order.StockId, delta);
                }
            }

            // Update order properties
            if (newQuantity.HasValue)
                order.UpdateQuantity(newQuantity.Value);
            if (price.HasValue)
                order.UpdatePrice(price.Value);
            if (!order.IsValid())
                return ParamError("Invalid order parameters after update.");

            // Persist changes
            await _dbService.UpdateOrder(order);
            _logger.LogInformation($"Order {orderId} edited for user {UserId}");
            return new OrderResult
            {
                PlacedOrder = order,
                Status = OrderStatus.Success,
                Message = "Order has been successfully edited."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error editing order {orderId} for user {UserId}");
            return OperationFailedResult();
        }
    }

    // Public entry points (limit vs market)
    public Task<OrderResult> PlaceLimitBuyOrderAsync(int stockId, int qty, decimal limitPrice) =>
        PlaceOrderAsync(stockId, qty, true, true, limitPrice);

    public Task<OrderResult> PlaceLimitSellOrderAsync(int stockId, int qty, decimal limitPrice) =>
        PlaceOrderAsync(stockId, qty, false, true, limitPrice);

    public Task<OrderResult> PlaceMarketBuyOrderAsync(int stockId, int qty, decimal maxPrice) =>
        PlaceOrderAsync(stockId, qty, true, false, maxPrice);

    public Task<OrderResult> PlaceMarketSellOrderAsync(int stockId, int qty, decimal minPrice) =>
        PlaceOrderAsync(stockId, qty, false, false, minPrice);
    #endregion

    #region Private Helpers
    private async Task<OrderResult> PlaceOrderAsync(
        int stockId, int quantity, bool buyOrder, bool limitOrder, decimal price)
    {
        if (!IsAuthenticated)
            return NotAuthResult();
        if (stockId <= 0 || quantity <= 0)
            return ParamError("StockId or quantity invalid.");

        try
        {
            var stock = await _dbService.GetStockById(stockId);
            if (stock == null)
                return ParamError("Stock not found.");

            // Determine price
            var latestPrice = await _dbService.GetLatestStockPriceByStockId(stockId);
            if (latestPrice == null)
                return NoMarketPriceResult();

            // Check user assets
            await _portfolio.RefreshAsync();
            Position? holding = CurrentPositions.FirstOrDefault(p => p.StockId == stockId);
            if (buyOrder) // Check funds
            {
                if (CurrentFund.AvailableBalance < price * quantity)
                {
                    _logger.LogWarning("User {UserId} has insufficient funds: {Available} needed {Required}",
                        UserId, CurrentFund.AvailableBalance, price * quantity);
                    return ParamError("Insufficient funds.");
                }
            }
            else // Check shares
            {
                if (holding == null || holding.RemainingQuantity < quantity)
                {
                    _logger.LogWarning("User {UserId} has insufficient shares of stock {StockId}", UserId, stockId);
                    return ParamError("Insufficient shares.");
                }
            }

            // Create the order
            var order = CreateOrder(stockId, quantity, price, buyOrder, limitOrder);
            if (!order.IsValid())
                return ParamError("Invalid order parameters.");

            // Update assets
            if (buyOrder)
            {
                // Reserve the maximum spend this user agreed to (limit or max price * qty)
                var ok = await _portfolio.ReserveFundsAsync(price * quantity, CurrencyType);
                if (!ok) return ParamError("Failed to reserve funds.");
            }
            else
            {
                // Reserve the shares the user intends to sell (keeps total, moves to reserved)
                var ok = await _portfolio.ReservePositionAsync(stockId, quantity);
                if (!ok) return ParamError("Failed to reserve shares.");
            }
            // Persist the order
            await _dbService.CreateOrder(order);
            UserAllOrders.Add(order);

            _logger.LogInformation($"Order placed: {order.OrderId} for user {UserId}");

            return new OrderResult
            {
                Status = OrderStatus.Success,
                Message = "Order executed successfully.",
                PlacedOrder = order
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order for user {UserId}", UserId);
            return OperationFailedResult();
        }
    }

    private async Task<Order?> UpdateAndFindOrderAsync(int orderId)
    {
        await RefreshOrdersAsync();
        return UserOpenOrders.FirstOrDefault(o => o.OrderId == orderId);
    }

    private Order CreateOrder(
        int stockId, int quantity, decimal price, bool buyOrder, bool limitOrder)
    {
        return new Order {
            UserId = UserId,
            StockId = stockId,
            Quantity = quantity,
            Price = price,
            OrderType = buyOrder ?
                (limitOrder ? Order.Types.LimitBuy : Order.Types.MarketBuy)
              : (limitOrder ? Order.Types.LimitSell : Order.Types.MarketSell),
        };
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
    #endregion
}
