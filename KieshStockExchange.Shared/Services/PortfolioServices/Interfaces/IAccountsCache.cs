using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.PortfolioServices.Interfaces;

/// <summary>
/// One row of the reservation reconciler's report: cache-side value vs the value
/// computed from this user's open orders in DB. <see cref="Delta"/> is positive
/// when the cache over-reserved (a leak) and negative when it under-reserved.
/// </summary>
public sealed record ReservationMismatch(
    int UserId,
    int? StockId,
    CurrencyType? Currency,
    decimal ExpectedReserved,
    decimal ActualReserved,
    decimal Delta,
    int OpenOrderCount);

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

    /// <summary>
    /// Acquires a per-user fund gate (one slot per (userId, currency) pair). Hold this
    /// across reservation + DB write + commit so two concurrent settlements can't race
    /// on the same <see cref="Fund.ReservedBalance"/>. Dispose to release.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireFundGateAsync(int userId, CurrencyType ccy, CancellationToken ct = default);

    /// <summary>
    /// Mirror of <see cref="AcquireFundGateAsync"/> for the per-user position gate keyed
    /// by (userId, stockId). Hold across reservation + DB write + commit.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquirePositionGateAsync(int userId, int stockId, CancellationToken ct = default);

    /// <summary>
    /// Acquires every gate in <paramref name="fundKeys"/> and <paramref name="positionKeys"/>
    /// in a single deterministic order so two batches touching the same users don't deadlock
    /// on opposite acquisition sequences. Disposing releases all gates in reverse order.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireUserGatesAsync(
        IReadOnlyCollection<(int UserId, CurrencyType Ccy)> fundKeys,
        IReadOnlyCollection<(int UserId, int StockId)> positionKeys,
        CancellationToken ct = default);

    /// <summary>
    /// Diagnostic: for every cached Position with <c>ReservedQuantity &gt; 0</c> and every
    /// Fund with <c>ReservedBalance &gt; 0</c>, computes the expected reserved value from
    /// the user's open limit orders in DB and reports any mismatch. Useful for hunting
    /// reservation leaks — a positive <see cref="ReservationMismatch.Delta"/> means the
    /// cache holds more reservation than the open orders account for (phantom).
    /// If <paramref name="clamp"/> is true, mismatches are corrected in-cache (clamped to
    /// the expected value); otherwise the method is read-only.
    /// </summary>
    Task<IReadOnlyList<ReservationMismatch>> ReconcileReservationsAsync(
        bool clamp = false, CancellationToken ct = default);

    /// <summary>
    /// Drop every cached Fund / Position / loaded-user marker. Call after a DB reseed
    /// (e.g. ExcelImportService reset) so the next EnsureLoadedAsync re-reads from DB
    /// instead of returning stale instances from before the reset.
    /// </summary>
    void Clear();
}
