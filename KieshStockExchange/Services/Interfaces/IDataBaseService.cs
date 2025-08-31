using System.Collections.Generic;
using System.Threading.Tasks;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public interface IDataBaseService
{
    // Generic table operations
    Task ResetTableAsync<T>() where T : new();

    // User operations
    Task<List<User>> GetUsersAsync();
    Task<User> GetUserById(int userId);
    Task<User> GetUserByUsername(string username);
    Task CreateUser(User user);
    Task UpdateUser(User user);
    Task DeleteUser(User user);

    // Stock operations
    Task<List<Stock>> GetStocksAsync();
    Task<Stock> GetStockById(int stockId);
    Task CreateStock(Stock stock);
    Task UpdateStock(Stock stock);
    Task DeleteStock(Stock stock);

    // StockPrice operations
    Task<List<StockPrice>> GetStockPricesAsync();
    Task<StockPrice> GetStockPriceById(int stockPriceId);
    Task<List<StockPrice>> GetStockPricesByStockId(int stockId);
    Task<StockPrice> GetLatestStockPriceByStockId(int stockId);
    Task CreateStockPrice(StockPrice stockPrice);
    Task UpdateStockPrice(StockPrice stockPrice);
    Task DeleteStockPrice(StockPrice stockPrice);

    // Order operations
    Task<List<Order>> GetOrdersAsync();
    Task<Order> GetOrderById(int orderId);
    Task<List<Order>> GetOrdersByUserId(int userId);
    Task<List<Order>> GetOrdersByStockId(int stockId);
    Task CreateOrder(Order order);
    Task UpdateOrder(Order order);
    Task DeleteOrder(Order order);

    // Transaction operations
    Task<List<Transaction>> GetTransactionsAsync();
    Task<Transaction> GetTransactionById(int transactionId);
    Task<List<Transaction>> GetTransactionsByUserId(int userId);
    Task CreateTransaction(Transaction transaction);
    Task UpdateTransaction(Transaction transaction);
    Task DeleteTransaction(Transaction transaction);

    // Position operations
    Task<List<Position>> GetPositionsAsync();
    Task<Position> GetPositionById(int positionId);
    Task<List<Position>> GetPositionsByUserId(int userId);
    Task CreatePosition(Position position);
    Task UpdatePosition(Position position);
    Task DeletePosition(Position position);

    // Fund operations
    Task<List<Fund>> GetFundsAsync();
    Task<Fund> GetFundById(int fundId);
    Task<List<Fund>> GetFundsByUserId(int userId);
    Task CreateFund(Fund fund);
    Task UpdateFund(Fund fund);
    Task DeleteFund(Fund fund);
}
