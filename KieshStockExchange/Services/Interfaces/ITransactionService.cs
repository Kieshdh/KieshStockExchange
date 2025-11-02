using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

/// <summary>
/// Read-only view over a user’s historical transactions and P/L metrics.
/// </summary>
public interface ITransactionService
{
    /// <summary>All user transactions, newest first.</summary>
    IReadOnlyList<Transaction> AllTransactions { get; }

    /// <summary>Only buy transactions.</summary>
    IReadOnlyList<Transaction> BuyTransactions { get; }

    /// <summary>Only sell transactions.</summary>
    IReadOnlyList<Transaction> SellTransactions { get; }

    /// <summary>Reloads the cached transaction list from the data source.</summary>
    Task RefreshAsync(int? asUserId, CancellationToken ct = default);

    /// <summary>  Occurs when the transaction lists are updated </summary>
    event EventHandler? TransactionsChanged;
}

