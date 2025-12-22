using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.PortfolioServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface ISettlementEngine
{
    /// <summary> Persist an order and reserve assets </summary>
    Task<OrderResult?> SettleOrderAsync(Order incoming, CancellationToken ct = default);

    /// <summary> Persist trade and transfers assets </summary>
    Task<Transaction> SettleTradeAsync(Transaction trade, CancellationToken ct);

    /// <summary> Used when cancelling an order remainder </summary>
    Task CancelRemainderAsync(Order order, CancellationToken ct);

    /// <summary> Used when modifying an existing open order </summary>
    Task ApplyOrderChangeAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct);
}


public sealed class SettlementEngine : ISettlementEngine
{
    private readonly bool DebugMode = false;

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IPortfolioMutationService _portfolio;
    private readonly ITransactionService _transactions;
    private readonly ILogger<SettlementEngine> _logger;

    public SettlementEngine(IDataBaseService db, IPortfolioMutationService portfolio,
        ITransactionService transactions, ILogger<SettlementEngine> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Reservation and Order Persistence
    public async Task<OrderResult?> SettleOrderAsync(Order incoming, CancellationToken ct = default)
    {
        // Start a transaction which does all in one go
        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // Reserve assets
            if (incoming.IsTrueMarketBuyOrder)
            {
                // True Market Buy: reserve by budget
                var reserved = await _portfolio.ReserveFundsAsync(incoming.UserId,
                    incoming.BuyBudget!.Value, incoming.CurrencyType, ct).ConfigureAwait(false);
                if (!reserved) return OrderResultFactory.InsufficientFunds(
                    $"Insufficient funds to place true market buy order for user {incoming.UserId}.");
            }
            else if (incoming.IsBuyOrder)
            {
                // Other Buy order: reserve by total amount (Price * Quantity)
                var reserved = await _portfolio.ReserveFundsAsync(incoming.UserId,
                    incoming.TotalAmount, incoming.CurrencyType, ct).ConfigureAwait(false);
                if (!reserved) return OrderResultFactory.InsufficientFunds(
                    $"Insufficient funds to place order for user {incoming.UserId}.");
            }
            else
            {
                // Sell order: reserve by quantity of stocks
                var reserved = await _portfolio.ReservePositionAsync(incoming.UserId,
                    incoming.StockId, incoming.Quantity, ct).ConfigureAwait(false);
                if (!reserved) return OrderResultFactory.InsufficientStocks(
                    $"Insufficient stocks to place order for user {incoming.UserId}.");
            }

            // Persist order
            await _db.CreateOrder(incoming, ct).ConfigureAwait(false);

            // Commit transaction
            await tx.CommitAsync(ct).ConfigureAwait(false);

            // Log
            if (DebugMode) _logger.LogInformation("Order settled and persisted: {@Order}", incoming);

            return null; // Success

        }
        catch (OperationCanceledException)
        {
            // No commit => rollback via DisposeAsync
            throw;
        }
        catch (Exception ex)
        {
            // No commit => rollback via DisposeAsync
            return OrderResultFactory.OperationFailed($"SettleOrder failed: {ex.Message}");
        }
    }
    #endregion

    #region Trade Settlement
    #endregion

    #region Order Cancellation and Modification

    #endregion

    #region Private Helpers

    #endregion
}
