using System.Collections.Generic;
using System.Threading.Tasks;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public interface IDataBaseService
{
    // Generic operations
    Task ResetTableAsync<T>(CancellationToken cancellationToken = default) where T : new();
    Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken cancellationToken = default);
    Task DeleteAllAsync<T>(IEnumerable<T> items, CancellationToken cancellationToken = default);
    Task UpdateAllAsync<T>(IEnumerable<T> items, CancellationToken cancellationToken = default);
    Task RunInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);

    // User operations
    Task<List<User>> GetUsersAsync(CancellationToken cancellationToken = default);
    Task<User?> GetUserById(int userId, CancellationToken cancellationToken = default);
    Task<User?> GetUserByUsername(string username, CancellationToken cancellationToken = default);
    Task CreateUser(User user, CancellationToken cancellationToken = default);
    Task UpdateUser(User user, CancellationToken cancellationToken = default);
    Task DeleteUser(User user, CancellationToken cancellationToken = default);
    Task DeleteUserById(int userId, CancellationToken cancellationToken = default);

    // Stock operations
    Task<List<Stock>> GetStocksAsync(CancellationToken cancellationToken = default);
    Task<Stock?> GetStockById(int stockId, CancellationToken cancellationToken = default);
    Task<bool> StockExist(int stockId, CancellationToken cancellationToken = default);
    Task CreateStock(Stock stock, CancellationToken cancellationToken = default);
    Task UpdateStock(Stock stock, CancellationToken cancellationToken = default);
    Task DeleteStock(Stock stock, CancellationToken cancellationToken = default);

    // StockPrice operations
    Task<List<StockPrice>> GetStockPricesAsync(CancellationToken cancellationToken = default);
    Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken cancellationToken = default);
    Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken cancellationToken = default);
    Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CancellationToken cancellationToken = default);
    Task CreateStockPrice(StockPrice stockPrice, CancellationToken cancellationToken = default);
    Task UpdateStockPrice(StockPrice stockPrice, CancellationToken cancellationToken = default);
    Task DeleteStockPrice(StockPrice stockPrice, CancellationToken cancellationToken = default);

    // Order operations
    Task<List<Order>> GetOrdersAsync(CancellationToken cancellationToken = default);
    Task<Order?> GetOrderById(int orderId, CancellationToken cancellationToken = default);
    Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken cancellationToken = default);
    Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken cancellationToken = default);
    Task CreateOrder(Order order, CancellationToken cancellationToken = default);
    Task UpdateOrder(Order order, CancellationToken cancellationToken = default);
    Task DeleteOrder(Order order, CancellationToken cancellationToken = default);

    // Transaction operations
    Task<List<Transaction>> GetTransactionsAsync(CancellationToken cancellationToken = default);
    Task<Transaction?> GetTransactionById(int transactionId, CancellationToken cancellationToken = default);
    Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken cancellationToken = default);
    Task CreateTransaction(Transaction transaction, CancellationToken cancellationToken = default);
    Task UpdateTransaction(Transaction transaction, CancellationToken cancellationToken = default);
    Task DeleteTransaction(Transaction transaction, CancellationToken cancellationToken = default);

    // Position operations
    Task<List<Position>> GetPositionsAsync(CancellationToken cancellationToken = default);
    Task<Position?> GetPositionById(int positionId, CancellationToken cancellationToken = default);
    Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken cancellationToken = default);
    Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken cancellationToken = default);
    Task CreatePosition(Position position, CancellationToken cancellationToken = default);
    Task UpdatePosition(Position position, CancellationToken cancellationToken = default);
    Task DeletePosition(Position position, CancellationToken cancellationToken = default);
    Task UpsertPosition(Position position, CancellationToken cancellationToken = default);

    // Fund operations
    Task<List<Fund>> GetFundsAsync(CancellationToken cancellationToken = default);
    Task<Fund?> GetFundById(int fundId, CancellationToken cancellationToken = default);
    Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken cancellationToken = default);
    Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken cancellationToken = default);
    Task CreateFund(Fund fund, CancellationToken cancellationToken = default);
    Task UpdateFund(Fund fund, CancellationToken cancellationToken = default);
    Task DeleteFund(Fund fund, CancellationToken cancellationToken = default);
    Task UpsertFund(Fund fund, CancellationToken cancellationToken = default);
}
