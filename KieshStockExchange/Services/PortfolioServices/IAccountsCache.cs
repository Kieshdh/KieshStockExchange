using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.PortfolioServices;

/// <summary>
/// In-memory cache of <see cref="Fund"/> and <see cref="Position"/> rows for the active users.
/// Phase B of the MarketEngine perf rework: lets <c>SettlementEngine</c> avoid hitting SQLite
/// on every order or trade settlement. The cache stores the same mutable instances that the
/// settlement transaction writes back to the DB, so any mutation here is automatically
/// persisted by the existing <c>UpdateAllAsync</c> calls inside the tx.
/// </summary>
public interface IAccountsCache
{
    /// <summary> Ensure the given users' funds and positions are loaded into the cache. No-op for already-loaded users. </summary>
    Task EnsureLoadedAsync(IReadOnlyList<int> userIds, CancellationToken ct = default);

    /// <summary> Convenience overload for a single user. </summary>
    Task EnsureLoadedAsync(int userId, CancellationToken ct = default);

    /// <summary> O(1) lookup. Returns null if the user has no fund row in this currency (or not loaded). </summary>
    Fund? GetFund(int userId, CurrencyType ccy);

    /// <summary> O(1) lookup. Returns null if the user has no position row for this stock (or not loaded). </summary>
    Position? GetPosition(int userId, int stockId);

    /// <summary>
    /// Register a freshly-inserted <see cref="Position"/> so subsequent <see cref="GetPosition"/>
    /// calls see it. Only call this after the DB insert has been committed — calling earlier
    /// risks leaving a phantom row in the cache if the tx rolls back.
    /// </summary>
    void TrackNewPosition(Position pos);
}
