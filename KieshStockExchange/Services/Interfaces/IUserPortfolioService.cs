using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public sealed record PortfolioSnapshot(
    IReadOnlyList<Fund> Funds,
    IReadOnlyList<Position> Positions,
    CurrencyType BaseCurrency
);

public interface IUserPortfolioService
{
    #region Snapshot and Refresh
        /// <summary>
    /// Last loaded portfolio snapshot (null until first RefreshAsync()).
    /// </summary>
    PortfolioSnapshot? Snapshot { get; }

    /// <summary>
    /// Raised whenever Snapshot is updated (after refresh or mutation).
    /// </summary>
    event EventHandler<PortfolioSnapshot>? SnapshotChanged;

    /// <summary>
    /// Reloads Funds + Positions from storage into memory.
    /// ViewModels call this whenever they decide it’s needed.
    /// Admins can pass a different user via asUserId.
    /// </summary>
    Task<bool> RefreshAsync(int? asUserId = null, CancellationToken ct = default);
    #endregion

    #region Base Currency and Normalization
    /// <summary>Current base currency (used by the UI; no I/O).</summary>
    CurrencyType GetBaseCurrency();

    /// <summary>Change base currency in memory (no I/O). Useful for UI toggles.</summary>
    void SetBaseCurrency(CurrencyType currency);

    /// <summary>
    /// Consolidate duplicate fund rows per currency and duplicate positions per stock,
    /// persist the single clean row(s), and update Snapshot.
    /// </summary>
    Task NormalizeAsync(int? asUserId = null, CancellationToken ct = default);
    #endregion

    #region Snapshot Accessors
    /// <summary>All cash rows (in memory).</summary>
    IReadOnlyList<Fund> GetFunds();

    /// <summary>Cash row for a specific currency (in memory), or null if none.</summary>
    Fund? GetFundByCurrency(CurrencyType currency);

    /// <summary>The fund in the base currency, or null if none.</summary>
    Fund? GetBaseFund();

    /// <summary>All positions (in memory).</summary>
    IReadOnlyList<Position> GetPositions();

    /// <summary>Position for a given stockId (in memory), or null if none.</summary>
    Position? GetPositionByStockId(int stockId);
    #endregion

    #region Mutations (Funds)
    /// <summary>Add money to a specific currency (creates row if needed), then updates Snapshot.</summary>
    Task<bool> AddFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>Withdraw from a specific currency (fails if insufficient), then updates Snapshot.</summary>
    Task<bool> WithdrawFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>Reserve cash for pending orders; returns false if insufficient. Updates Snapshot.</summary>
    Task<bool> ReserveFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>Release previously reserved cash back to available. Updates Snapshot.</summary>
    Task<bool> ReleaseReservedFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>Release reserved cash and withdraw it in one atomic operation.</summary>
    Task<bool> ReleaseFromReservedFundsAsync(decimal amount, CurrencyType currency,
        int? asUserId = null, CancellationToken ct = default);
    #endregion

    #region Mutations (Positions)
    /// <summary>Add to a position (e.g., after a buy). Updates Snapshot.</summary>
    Task<bool> AddPositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>Removes from a position (e.g., after a sell). Updates Snapshot.</summary>
    Task<bool> RemovePositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>Reserve shares for pending orders. Updates Snapshot.</summary>
    Task<bool> ReservePositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>Release previously reserved shares back to available. Updates Snapshot.</summary>
    Task<bool> UnreservePositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>Unreserves and removes a position in one atomic operation.</summary>
    Task<bool> ReleaseFromReservedPositionAsync(int stockId, int quantity,
        int? asUserId = null, CancellationToken ct = default);
    #endregion
}
