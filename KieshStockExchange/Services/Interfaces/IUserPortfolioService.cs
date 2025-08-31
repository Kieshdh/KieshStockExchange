using KieshStockExchange.Models;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services;

public sealed record PortfolioSnapshot(
    IReadOnlyList<Fund> Funds,           // multiple rows, each with CurrencyType
    IReadOnlyList<Position> Positions,   // one row per (User, Stock) ideally
    CurrencyType BaseCurrency            // user’s chosen base currency
);

public interface IUserPortfolioService
{
    #region Refresh, Snapshot and Base Currency
    /// <summary>
    /// Reloads Funds + Positions from storage into memory.
    /// ViewModels call this whenever they decide it’s needed.
    /// </summary>
    Task<bool> RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Last loaded portfolio snapshot (null until first RefreshAsync()).
    /// </summary>
    PortfolioSnapshot? Snapshot { get; }

    /// <summary>Current base currency (used by the UI; no I/O).</summary>
    CurrencyType GetBaseCurrency();

    /// <summary>Change base currency in memory (no I/O). Useful for UI toggles.</summary>
    void SetBaseCurrency(CurrencyType currency);
    #endregion

    #region Funds and Positions
    /// <summary>All cash rows (in memory).</summary>
    IReadOnlyList<Fund> GetFunds();

    /// <summary>Cash row for a specific currency (in memory), or null if none.</summary>
    Fund? GetFunds(CurrencyType currency);

    /// <summary>All positions (in memory).</summary>
    IReadOnlyList<Position> GetPositions();

    /// <summary>Position for a given stockId (in memory), or null if none.</summary>
    Position? GetPosition(int stockId);
    #endregion

    #region Modifications
    /// <summary>
    /// Add money to a specific currency (creates row if needed), then updates Snapshot.
    /// </summary>
    Task AddFundsAsync(decimal amount, CurrencyType currency, string? reference = null, CancellationToken ct = default);

    /// <summary>
    /// Withdraw from a specific currency (fails if insufficient), then updates Snapshot.
    /// </summary>
    Task WithdrawFundsAsync(decimal amount, CurrencyType currency, string? reference = null, CancellationToken ct = default);

    /// <summary>
    /// Reserve cash for pending orders; returns false if insufficient. Updates Snapshot.
    /// </summary>
    Task<bool> ReserveFundsAsync(decimal amount, CurrencyType currency, CancellationToken ct = default);

    /// <summary>
    /// Release previously reserved cash back to available. Updates Snapshot.
    /// </summary>
    Task<bool> ReleaseReservedFundsAsync(decimal amount, CurrencyType currency, CancellationToken ct = default);

    /// <summary>
    /// Upsert a position: apply a trade delta and recompute weighted average price.
    /// Use positive quantity for buys, negative for sells. Updates Snapshot.
    /// </summary>
    Task UpsertPositionAsync(int stockId, decimal quantityDelta, decimal executionPrice, CancellationToken ct = default);

    /// <summary>
    /// Remove a position (e.g., admin fix or when quantity reaches zero). Updates Snapshot.
    /// </summary>
    Task RemovePositionAsync(int stockId, CancellationToken ct = default);

    /// <summary> Consolidate any duplicate fund rows per currency and duplicate positions per stock,
    /// then persist the single clean row and update Snapshot. </summary>
    Task NormalizeAsync(CancellationToken ct = default);
    #endregion
}
