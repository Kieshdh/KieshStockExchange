using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

public class UserOrderService : IUserOrderService
{
    #region Order Properties
    // In‐memory cache of user orders
    public List<Order> UserAllOrders { get; private set; } = new();
    public IReadOnlyList<Order> UserOpenOrders =>
        UserAllOrders.Where(o => o.IsOpen).ToList();
    public IReadOnlyList<Order> UserClosedOrders =>
        UserAllOrders.Where(o => o.IsClosed).ToList();
    public IReadOnlyList<Order> UserCancelledOrders =>
        UserAllOrders.Where(o => o.IsCancelled).ToList();
    public IReadOnlyList<Order> UserFilledOrders =>
        UserAllOrders.Where(o => o.IsFilled).ToList();

    // Event for order changes
    public event EventHandler? OrdersChanged;
    private void NotifyOrdersChanged() => OrdersChanged?.Invoke(this, EventArgs.Empty);
    #endregion

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IAuthService _auth;
    private readonly IUserPortfolioService _portfolio;
    private readonly IMarketOrderService _market;
    private readonly ILogger<UserOrderService> _logger;
    private readonly IStockService _stock;

    public UserOrderService(IDataBaseService db, IStockService stock,
        IAuthService auth, IUserPortfolioService portfolio,
        IMarketOrderService market, ILogger<UserOrderService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _market = market ?? throw new ArgumentNullException(nameof(market)); 
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
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

    public async Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null, 
        decimal? newPrice = null, int? asUserId = null, CancellationToken ct = default)
    {
        try { return await _market.ModifyOrderAsync(orderId, newQuantity, newPrice, asUserId, ct); }
        catch { return OperationFailedResult(); }
        finally 
        { 
            await RefreshOrdersAsync(asUserId, ct); 
            await _portfolio.RefreshAsync(asUserId, ct);
        }
    }
    #endregion

    #region Placing Orders 
    public Task<OrderResult> PlaceLimitBuyOrderAsync(int stockId, int quantity, decimal limitPrice,
            CurrencyType currency, CancellationToken ct = default, int? asUserId = null) =>
        PlaceOrderAsync(stockId, quantity, true, true, limitPrice, null, currency, asUserId, ct);

    public Task<OrderResult> PlaceLimitSellOrderAsync(int stockId, int quantity, decimal limitPrice,
            CurrencyType currency, CancellationToken ct = default, int? asUserId = null) =>
        PlaceOrderAsync(stockId, quantity, false, true, limitPrice, null, currency, asUserId, ct);

    public Task<OrderResult> PlaceTrueMarketBuyAsync(int stockId, int quantity,
        CurrencyType currency, int? asUserId = null, CancellationToken ct = default) =>
        PlaceOrderAsync(stockId, quantity, true, false, 0m, null, currency, asUserId, ct);

    public Task<OrderResult> PlaceTrueMarketSellAsync(int stockId, int quantity,
            CurrencyType currency, int? asUserId = null, CancellationToken ct = default) =>
        PlaceOrderAsync(stockId, quantity, false, false, 0m, null, currency, asUserId, ct);

    public Task<OrderResult> PlaceSlippageMarketBuyAsync(int stockId, int quantity, decimal anchorPrice,
            decimal slippagePercent, CurrencyType currency, int? asUserId = null, CancellationToken ct = default) => 
        PlaceOrderAsync(stockId, quantity, true, false, anchorPrice, slippagePercent, currency, asUserId, ct);

    public Task<OrderResult> PlaceSlippageMarketSellAsync(int stockId, int quantity, decimal anchorPrice,
            decimal slippagePercent, CurrencyType currency, int? asUserId = null, CancellationToken ct = default) =>
        PlaceOrderAsync(stockId, quantity, false, false, anchorPrice, slippagePercent, currency, asUserId, ct);


    private async Task<OrderResult> PlaceOrderAsync(int stockId, int quantity, bool buyOrder, 
        bool limitOrder, decimal price, decimal? slippage, CurrencyType currency, int? asUserId, CancellationToken ct)
    {
        // Check if able to place order
        var paramError = ValidateParameters(stockId, quantity, limitOrder, price, slippage);
        if (paramError != null) return paramError;

        var actingUserId = GetTargetUserIdOrFail(asUserId, out var authError);
        if (authError != null) return authError;

        try
        {
            //  Load portfolio snapshot for the target user
            await _portfolio.RefreshAsync(actingUserId, ct);

            // Check if user has enough funds or shares
            var assetResult = CheckAssets(actingUserId, currency, stockId, buyOrder, price, quantity);
            if (assetResult != null) return assetResult; 

            // Create the order
            var order = CreateOrder(actingUserId, stockId, quantity, price, currency, buyOrder, limitOrder, slippage);
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
    private OrderResult? ValidateParameters(int stockId, int quantity, bool limitOrder, decimal price, decimal? slippage)
    {
        // Basic parameter validation
        if (stockId <= 0 || quantity <= 0)
            return ParamError("Order details must be positive.");

        // Find the stock
        _stock.TryGetById(stockId, out var stock);
        if (stock == null) return ParamError("Stock not found.");

        // Price and slippage validation
        if (limitOrder)
        {
            if (price <= 0m)
                return ParamError("Limit price must be positive.");
        }
        else
        {
            if (slippage.HasValue)
            {
                if (slippage.Value < 0m || slippage.Value > 100m)
                    return ParamError("Slippage percent must be between 0 and 100.");
                if (price <= 0m)
                    return ParamError("Slippage anchor price must be positive.");
            }
            else if (price != 0m)
                return ParamError("TrueMarket orders must have price = 0.");
        }
        return null; // No errors
    }

    private Fund GetFund(CurrencyType currency) =>
        _portfolio.GetFundByCurrency(currency) ?? new Fund { };

    private Position GetHolding(int stockId) =>
        _portfolio.GetPositionByStockId(stockId) ?? new Position { };

    private OrderResult? CheckAssets(int userId, CurrencyType currency, int stockId, bool buyOrder, decimal price, int quantity)
    {
        if (buyOrder) // Check funds
        {
            var fund = GetFund(currency);
            var required = price * quantity;
            if (fund.AvailableBalance < required)
            {
                _logger.LogWarning("User {UserId} has insufficient funds: avail {Available} " +
                    "needed {Required}", userId, fund.AvailableBalance, price * quantity);
                return ParamError("Insufficient funds.");
            }
        }
        else // Check shares
        {
            var holding = GetHolding(stockId);
            if (holding.AvailableQuantity < quantity)
            {
                _logger.LogWarning("User {UserId} has insufficient shares of stock #{StockId}",
                    userId, holding.StockId);
                return ParamError("Insufficient shares.");
            }
        }
        return null;
    }
    
    private Order CreateOrder(int userId, int stockId, int quantity,
        decimal price, CurrencyType currency, bool buyOrder, bool limitOrder, decimal? slippagePercent)
    {
        string orderType;
        if (limitOrder)
            orderType = buyOrder ? Order.Types.LimitBuy : Order.Types.LimitSell;
        else if (slippagePercent.HasValue)
            orderType = buyOrder ? Order.Types.SlippageMarketBuy : Order.Types.SlippageMarketSell;
        else
            orderType = buyOrder ? Order.Types.TrueMarketBuy : Order.Types.TrueMarketSell;

        return new Order {
            UserId = userId,
            StockId = stockId,
            Quantity = quantity,
            Price = CurrencyHelper.RoundMoney(price, currency),
            SlippagePercent = slippagePercent,
            CurrencyType = currency,
            OrderType = orderType,
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
