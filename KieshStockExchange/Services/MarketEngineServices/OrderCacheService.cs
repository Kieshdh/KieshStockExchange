using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Per-user snapshot of order rows, partitioned into open and closed lists for direct
/// UI binding. Refreshed explicitly by the caller Ã¢â‚¬â€ there is no DB polling here.
/// </summary>
public sealed class OrderCacheService : IOrderCacheService
{
    #region Public State
    public List<Order> AllOrders { get; private set; } = new();
    public IReadOnlyList<Order> OpenOrders { get; private set; } = Array.Empty<Order>();
    public IReadOnlyList<Order> ClosedOrders { get; private set; } = Array.Empty<Order>();

    public event EventHandler? OrdersChanged;
    #endregion

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly ILogger<OrderCacheService> _logger;

    public OrderCacheService(IDataBaseService db, ILogger<OrderCacheService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Refresh
    public async Task<bool> RefreshAsync(int userId, CancellationToken ct = default)
    {
        if (userId <= 0) return false;
        try
        {
            var all = (await _db.GetOrdersByUserId(userId, ct).ConfigureAwait(false)).ToList();

            // Partition once so UI bindings don't re-filter on every property access.
            var open = new List<Order>(all.Count);
            var closed = new List<Order>(all.Count);
            foreach (var o in all)
            {
                if (o.IsOpen) open.Add(o);
                else closed.Add(o);
            }

            AllOrders = all;
            OpenOrders = open;
            ClosedOrders = closed;

            OrdersChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh orders for user #{UserId}", userId);
            return false;
        }
    }
    #endregion
}
