using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.PortfolioServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface ISettlementEngine
{
    // Reservation when placing a new order
    Task<OrderResult?> ReserveAssetsAndPersistOrderAsync(
        Order order, CancellationToken ct, Action<Order> onPersisted);

    // Called for each matched trade
    Task<Transaction> SettleTradeAsync(Transaction trade, CancellationToken ct);

    // Used when cancelling an order remainder
    Task CancelRemainderAsync(Order order, CancellationToken ct);

    // Used when modifying an existing open order
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
    public async Task<OrderResult?> ReserveAssetsAndPersistOrderAsync(
        Order order, CancellationToken ct, Action<Order> onPersisted)
    {
        ct.ThrowIfCancellationRequested();

        throw new NotImplementedException();
    }
    #endregion

    #region Trade Settlement
    #endregion

    #region Order Cancellation and Modification

    #endregion
}
