using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

// Server-side stub for IOrderCacheService. The cache itself is an INPC wrapper
// that lives client-side (binds order lists directly to UI). On the server,
// NotifyOrdersMutated is the only method the engine calls — in Step 6 this stub
// is replaced by a SignalR-pushing impl. Until then it's a no-op so the engine
// can be wired into DI without an IOrderCacheService consumer breaking.
public sealed class NoopOrderCacheService : IOrderCacheService
{
    public List<Order> AllOrders { get; } = new();
    public IReadOnlyList<Order> OpenOrders { get; } = Array.Empty<Order>();
    public IReadOnlyList<Order> ClosedOrders { get; } = Array.Empty<Order>();

    public event EventHandler? OrdersChanged { add { } remove { } }

    public Task<bool> RefreshAsync(int userId, CancellationToken ct = default) => Task.FromResult(false);

    public void NotifyOrdersMutated(IReadOnlyCollection<int> affectedUserIds) { }
}
