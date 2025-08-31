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
    private readonly ILogger<UserOrderService> _logger;

    private User CurrentUser => _authService.CurrentUser;
    private Fund? CurrentFund;
    private List<Position> CurrentPositions = new();

    private int UserId => CurrentUser?.UserId ?? 0;
    private bool IsAuthenticated => _authService.IsLoggedIn && UserId > 0;
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

    public UserOrderService(
        IDataBaseService dbService,
        IAuthService authService,
        ILogger<UserOrderService> logger)
    {
        _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            await LoadFundsAsync();
            if (order.IsBuyOrder() && newQuantity.HasValue)
            {
                // For buy orders, check if we have enough funds
                var oldCost = order.RemainingAmount();
                var totalCost = price.HasValue ? price.Value * newQuantity.Value : order.Price * newQuantity.Value;
                if (CurrentFund.AvailableBalance < totalCost - oldCost)
                    return ParamError("Insufficient funds for this order.");
            }
            else if (order.IsSellOrder() && newQuantity.HasValue)
            {
                // For sell orders, check if we have enough shares
                var holding = (await _dbService.GetPositionsByUserId(UserId))
                              .FirstOrDefault(p => p.StockId == order.StockId);
                if (holding == null || holding.Quantity < newQuantity.Value)
                    return ParamError("Insufficient shares to sell.");
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

            /* Validate limit price
            if (limitOrder && ((buyOrder && price < latestPrice.Price) ||
                (!buyOrder && price > latestPrice.Price)))
                return ParamError("Limit price outside market bounds."); */

            // Check user assets
            await LoadFundsAsync(); // Ensure fund is loaded
            await LoadPositionsAsync();
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
                CurrentFund.ReservedBalance += price * quantity;
                await _dbService.UpdateFund(CurrentFund);
            }
            else
            {
                if (holding == null)
                    throw new InvalidOperationException("Holding should not be null here.");
                holding.Quantity -= quantity;
                await _dbService.UpdatePosition(holding);
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

    private async Task LoadFundsAsync()
    {
        CurrentFund = await _dbService.GetFundByUserId(UserId)
            ?? new Fund { UserId = UserId, TotalBalance = 0m, ReservedBalance = 0m };
        if (CurrentFund.FundId == 0) // New fund, add it to the database
            await _dbService.CreateFund(CurrentFund);
    }

    private async Task LoadPositionsAsync()
    {
        CurrentPositions = (await _dbService.GetPositionsByUserId(UserId)).ToList()
            ?? new List<Position>();
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
