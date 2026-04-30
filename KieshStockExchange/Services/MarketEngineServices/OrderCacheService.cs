using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface IOrderCacheService
{
    List<Order> AllOrders { get; }
    IReadOnlyList<Order> OpenOrders { get; }
    IReadOnlyList<Order> ClosedOrders { get; }
    event EventHandler? OrdersChanged;
    Task<bool> RefreshAsync(int userId, CancellationToken ct = default);
}

public sealed class OrderCacheService : IOrderCacheService
{
    public List<Order> AllOrders { get; private set; } = new();
    public IReadOnlyList<Order> OpenOrders => AllOrders.Where(o => o.IsOpen).ToList();
    public IReadOnlyList<Order> ClosedOrders => AllOrders.Where(o => !o.IsOpen).ToList();

    public event EventHandler? OrdersChanged;

    private readonly IDataBaseService _db;
    private readonly ILogger<OrderCacheService> _logger;

    public OrderCacheService(IDataBaseService db, ILogger<OrderCacheService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> RefreshAsync(int userId, CancellationToken ct = default)
    {
        if (userId <= 0) return false;
        try
        {
            AllOrders = (await _db.GetOrdersByUserId(userId, ct).ConfigureAwait(false)).ToList();
            OrdersChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh orders for user #{UserId}", userId);
            return false;
        }
    }
}
