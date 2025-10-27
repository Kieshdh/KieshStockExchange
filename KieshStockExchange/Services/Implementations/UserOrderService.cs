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
    // In‐memory cache of user orders
    public List<Order> UserAllOrders { get; private set; } = new();
    public IReadOnlyList<Order> UserOpenOrders =>
        UserAllOrders.Where(o => o.IsOpen).ToList();
    public IReadOnlyList<Order> UserCancelledOrders =>
        UserAllOrders.Where(o => o.IsCancelled).ToList();
    public IReadOnlyList<Order> UserFilledOrders =>
        UserAllOrders.Where(o => o.IsFilled).ToList();

    // Event for order changes
    public event EventHandler? OrdersChanged;
    private void NotifyOrdersChanged() => OrdersChanged?.Invoke(this, EventArgs.Empty);
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
    private User? CurrentUser => _auth.CurrentUser;
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

        if (!IsAuthenticated)
        {
            authError = NotAuthResult();
            return 0;
        }

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
        if (authFail != null) { _logger.LogWarning(authFail.ErrorMessage); return false; }

        try
        {
            UserAllOrders = (await _db.GetOrdersByUserId(targetUserId, ct)).ToList();
            NotifyOrdersChanged();
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
        try { return await _market.CancelOrderAsync(orderId, asUserId, ct); }
        catch { return OperationFailedResult(); }
        finally 
        { 
            await RefreshOrdersAsync(asUserId, ct); 
            await _portfolio.RefreshAsync(asUserId, ct);
        }
    }

    public async Task<OrderResult> ChangeOrderAsync(int orderId, int? newQuantity, 
        decimal? price, int? asUserId = null, CancellationToken ct = default)
    {
        try { return await _market.ModifyOrderAsync(orderId, newQuantity, price, asUserId, ct); }
        catch { return OperationFailedResult(); }
        finally 
        { 
            await RefreshOrdersAsync(asUserId, ct); 
            await _portfolio.RefreshAsync(asUserId, ct);
        }
    }
    #endregion

    #region Placing Orders 
    public Task<OrderResult> PlaceLimitBuyOrderAsync(int stockId, int qty, decimal limitPrice,
            CurrencyType currency, CancellationToken ct = default, int? asUserId = null) =>
        PlaceOrderAsync(stockId, qty, true, true, limitPrice, currency, asUserId, ct);

    public Task<OrderResult> PlaceLimitSellOrderAsync(int stockId, int qty, decimal limitPrice,
            CurrencyType currency, CancellationToken ct = default, int? asUserId = null) =>
        PlaceOrderAsync(stockId, qty, false, true, limitPrice, currency, asUserId, ct);

    public Task<OrderResult> PlaceMarketBuyOrderAsync(int stockId, int qty, decimal maxPrice,
            CurrencyType currency, CancellationToken ct = default, int? asUserId = null) =>
        PlaceOrderAsync(stockId, qty, true, false, maxPrice, currency, asUserId, ct);

    public Task<OrderResult> PlaceMarketSellOrderAsync(int stockId, int qty, decimal minPrice,
             CurrencyType currency, CancellationToken ct = default, int? asUserId = null) =>
        PlaceOrderAsync(stockId, qty, false, false, minPrice, currency, asUserId, ct);

    private async Task<OrderResult> PlaceOrderAsync(int stockId, int quantity, bool buyOrder, 
        bool limitOrder, decimal price, CurrencyType currency, int? asUserId, CancellationToken ct)
    {
        // Check if able to place order
        if (stockId <= 0 || quantity <= 0 || price <= 0)
            return ParamError("Order details must be positive.");

        var actingUserId = GetTargetUserIdOrFail(asUserId, out var authError);
        if (authError != null) return authError;

        try
        {
            // Find the stock
            var stock = await _db.GetStockById(stockId, ct);
            if (stock == null) return ParamError("Stock not found.");

            //  Load portfolio snapshot for the target user
            await _portfolio.RefreshAsync(actingUserId, ct);
            var fund = GetFund(actingUserId, currency);
            var holding = GetHolding(actingUserId, stockId);

            // Check if user has enough funds or shares
            var assetResult = CheckAssets(fund, holding, buyOrder, price, quantity);
            if (assetResult != null) return assetResult; 

            // Create the order
            var order = CreateOrder(actingUserId, stockId, quantity, price, currency, buyOrder, limitOrder);
            if (!order.IsValid()) return ParamError("Invalid order parameters.");

            // Send to market for placing and matching
            var result = await _market.PlaceAndMatchAsync(order, actingUserId, ct);

            if (result.PlacedOrder == null)
                _logger.LogWarning("Order placement failed for user #{UserId}", actingUserId);
            else
                _logger.LogInformation($"Order placed: {order.OrderId} for user #{actingUserId}");

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order for targetUser #{UserId}", actingUserId);
            return OperationFailedResult();
        }
        finally
        {
            // Refresh orders and portfolio after placing
            await RefreshOrdersAsync(asUserId, ct);
            await _portfolio.RefreshAsync(asUserId, ct);
        }
    }
    #endregion

    #region Private Helpers
    private Fund GetFund(int targetUserId, CurrencyType currency) =>
        _portfolio.GetFundByCurrency(currency) ?? new Fund { UserId = targetUserId, CurrencyType = currency };

    private Position GetHolding(int targetUserId, int stockId) =>
        _portfolio.GetPositionByStockId(stockId) ?? new Position { UserId = targetUserId, StockId = stockId };

    private OrderResult? CheckAssets(Fund fund, Position holding, bool buyOrder, decimal price, int quantity)
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
            if (holding.AvailableQuantity < quantity)
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
            Price = CurrencyHelper.RoundMoney(price, currency),
            CurrencyType = currency,
            OrderType = buyOrder ?
                (limitOrder ? Order.Types.LimitBuy : Order.Types.MarketBuy)
              : (limitOrder ? Order.Types.LimitSell : Order.Types.MarketSell),
        };
    }
    #endregion

    #region Helper Results
    private OrderResult NotAuthResult() => new()
        { Status = OrderStatus.NotAuthenticated, ErrorMessage = "User not authenticated." };
    private OrderResult ParamError(string msg) => new()
        { Status = OrderStatus.InvalidParameters, ErrorMessage = msg };
    private OrderResult AuthError(string msg) => new()
        { Status = OrderStatus.NotAuthorized, ErrorMessage = msg };
    private OrderResult OperationFailedResult() => new()  
        { Status = OrderStatus.OperationFailed, ErrorMessage = "An unexpected error occurred." };
    #endregion
}
