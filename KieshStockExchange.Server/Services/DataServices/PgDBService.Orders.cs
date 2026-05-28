using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    #region Order operations
    public Task<List<Order>> GetOrdersAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<(List<Order> Items, int Total)> GetOrdersPageAsync(
        int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc,
        string? statusFilter, int? userIdFilter = null, int? stockIdFilter = null,
        string? sideFilter = null, string? typeFilter = null,
        IList<int>? excludeUserIds = null, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Order?> GetOrderById(int orderId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Order>> GetOrdersByIds(List<int> orderIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Order>> GetOpenLimitOrders(int stockId, CurrencyType currency, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Order>> GetOpenOrdersForUsersAsync(List<int> userIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateOrder(Order order, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateOrder(Order order, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteOrder(Order order, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion

    #region Transaction operations
    public Task<List<Transaction>> GetTransactionsAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<(List<Transaction> Items, int Total)> GetTransactionsPageAsync(
        int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc,
        int? userIdFilter = null, int? stockIdFilter = null, string? currencyFilter = null,
        IList<int>? excludeBuyerOrSellerIds = null, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Transaction?> GetTransactionById(int transactionId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Transaction>> GetTransactionsByOrderId(int orderId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Transaction>> GetTransactionsByStockIdAndTimeRange(
        int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<Transaction>> GetTransactionsSinceTime(DateTime since, int? limit = null, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Transaction?> GetLatestTransactionByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Transaction?> GetLatestTransactionBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateTransaction(Transaction transaction, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateTransaction(Transaction transaction, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteTransaction(Transaction transaction, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion
}
