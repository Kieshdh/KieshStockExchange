using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

/// <summary>
/// Read-only view over a user’s historical transactions and P/L metrics.
/// </summary>
public interface ITransactionService
{
    /// <summary>All user transactions, newest first.</summary>
    IReadOnlyList<Transaction> All { get; }

    /// <summary>Only buy transactions.</summary>
    IReadOnlyList<Transaction> Buys { get; }

    /// <summary>Only sell transactions.</summary>
    IReadOnlyList<Transaction> Sells { get; }

    /// <summary>Reloads the cached transaction list from the data source.</summary>
    Task<IReadOnlyList<Transaction>> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the current (cached) transaction list.</summary>
    Task<IReadOnlyList<Transaction>> GetCurrentAsync(CancellationToken cancellationToken = default);
}

