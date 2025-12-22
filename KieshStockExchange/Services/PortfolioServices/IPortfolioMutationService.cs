using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.PortfolioServices;

public interface IPortfolioMutationService
{
    #region Fund Mutations
    /// <summary> Add money to a specific currency (creates row if needed). </summary>
    Task<bool> AddFundsAsync(int userId, decimal amount, CurrencyType currency, CancellationToken ct = default);

    /// <summary> Withdraw from a specific currency (fails if insufficient).</summary>
    Task<bool> WithdrawFundsAsync(int userId, decimal amount, CurrencyType currency, CancellationToken ct = default);

    /// <summary> Reserve cash for pending orders; returns false if insufficient.</summary>
    Task<bool> ReserveFundsAsync(int userId, decimal amount, CurrencyType currency, CancellationToken ct = default);

    /// <summary> Release previously reserved cash back to available.</summary>
    Task<bool> ReleaseReservedFundsAsync(int userId, decimal amount, CurrencyType currency, CancellationToken ct = default);

    /// <summary> Release reserved cash and withdraw it in one atomic operation.</summary>
    Task<bool> ReleaseFromReservedFundsAsync(int userId, decimal amount, CurrencyType currency, CancellationToken ct = default);
    #endregion

    #region Position Mutations
    /// <summary> Add to a position (e.g., after a buy).</summary>
    Task<bool> AddPositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default);

    /// <summary> Removes from a position (e.g., after a sell).\</summary>
    Task<bool> RemovePositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default);

    /// <summary> Reserve shares for pending orders.</summary>
    Task<bool> ReservePositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default);

    /// <summary> Release previously reserved shares back to available.</summary>
    Task<bool> UnreservePositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default);

    /// <summary> Unreserves and removes a position in one atomic operation.</summary>
    Task<bool> ReleaseFromReservedPositionAsync(int userId, int stockId, int quantity, CancellationToken ct = default);
    #endregion

    #region Normalization
    /// <summary> Normalize the portfolio by removing zero-quantity positions and zero-balance funds </summary>
    Task NormalizeAsync(int userId, CancellationToken ct = default);
    #endregion
}