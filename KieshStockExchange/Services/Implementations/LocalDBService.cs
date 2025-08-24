using KieshStockExchange.Models;
using SQLite;

namespace KieshStockExchange.Services.Implementations;

public class LocalDBService: IDataBaseService
{
    private const string DB_NAME = "localdb.db3";
    private readonly SQLiteAsyncConnection _db;

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public LocalDBService()
    {
        string dbPath = Path.Combine(FileSystem.AppDataDirectory, DB_NAME);
        _db = new SQLiteAsyncConnection(dbPath);
    }

    private async Task InitializeAsync()
    {
        if (_initialized) return;
        await _initGate.WaitAsync();
        try
        {
            if (_initialized) return;

            // Exactly what this does: forces the native SQLite library to load and then creates tables if missing.
            await _db.CreateTableAsync<User>();
            await _db.CreateTableAsync<Stock>();
            await _db.CreateTableAsync<StockPrice>();
            await _db.CreateTableAsync<Order>();
            await _db.CreateTableAsync<Transaction>();
            await _db.CreateTableAsync<Portfolio>();
            await _db.CreateTableAsync<Fund>();

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    #region Generic table operations
    public async Task ResetTableAsync<T>() where T : new()
    {
        await InitializeAsync();
        await _db.DropTableAsync<T>();
        await _db.CreateTableAsync<T>();
    }
    #endregion

    #region User operations
    public async Task<List<User>> GetUsersAsync() {
        await InitializeAsync();
        return await _db.Table<User>().ToListAsync();
    }
    public async Task<User> GetUserById(int userId) {
        await InitializeAsync();
        return await _db.Table<User>().Where(u => u.UserId == userId).FirstOrDefaultAsync();
    }
    public async Task<User> GetUserByUsername(string username) {
        await InitializeAsync();
        return await _db.Table<User>().Where(u => u.Username == username).FirstOrDefaultAsync();
    }
    public async Task CreateUser(User user) {
        await InitializeAsync();
        await _db.InsertAsync(user);
    }
    public async Task UpdateUser(User user) {
        await InitializeAsync();
        await _db.UpdateAsync(user);
    }
    public async Task DeleteUser(User user) {
        await InitializeAsync();
        await _db.DeleteAsync(user);
    }
    #endregion

    #region Stock operations
    public async Task<List<Stock>> GetStocksAsync() {
        await InitializeAsync();
        return await _db.Table<Stock>().ToListAsync();
    }
    public async Task<Stock> GetStockById(int stockId) {
        await InitializeAsync();
        return await _db.Table<Stock>().Where(s => s.StockId == stockId).FirstOrDefaultAsync();
    }
    public async Task CreateStock(Stock stock) {
        await InitializeAsync();
        await _db.InsertAsync(stock);
    }
    public async Task UpdateStock(Stock stock) {
        await InitializeAsync();
        await _db.UpdateAsync(stock);
    }
    public async Task DeleteStock(Stock stock) {
        await InitializeAsync();
        await _db.DeleteAsync(stock);
    }
    #endregion

    #region StockPrice operations
    public async Task<List<StockPrice>> GetStockPricesAsync() {
        await InitializeAsync();
        return await _db.Table<StockPrice>().ToListAsync();
    }
    public async Task<StockPrice> GetStockPriceById(int stockPriceId) {
        await InitializeAsync();
        return await _db.Table<StockPrice>().Where(sp => sp.PriceId == stockPriceId).FirstOrDefaultAsync();
    }
    public async Task<List<StockPrice>> GetStockPricesByStockId(int stockId) {
        await InitializeAsync();
        return await _db.Table<StockPrice>().Where(sp => sp.StockId == stockId).ToListAsync();
    }
    public async Task<StockPrice> GetLatestStockPriceByStockId(int stockId) {
        await InitializeAsync();
        return await _db.Table<StockPrice>()
                        .Where(sp => sp.StockId == stockId)
                        .OrderByDescending(sp => sp.Timestamp)
                        .FirstOrDefaultAsync();
    }
    public async Task CreateStockPrice(StockPrice stockPrice) {
        await InitializeAsync();
        await _db.InsertAsync(stockPrice);
    }
    public async Task UpdateStockPrice(StockPrice stockPrice) {
        await InitializeAsync();
        await _db.UpdateAsync(stockPrice);
    }
    public async Task DeleteStockPrice(StockPrice stockPrice) {
        await InitializeAsync();
        await _db.DeleteAsync(stockPrice);
    }
    #endregion

    #region Order operations
    public async Task<List<Order>> GetOrdersAsync() {
        await InitializeAsync();
        return await _db.Table<Order>().ToListAsync();
    }
    public async Task<Order> GetOrderById(int orderId) {
        await InitializeAsync();
        return await _db.Table<Order>().Where(o => o.OrderId == orderId).FirstOrDefaultAsync();
    }
    public async Task<List<Order>> GetOrdersByUserId(int userId) {
        await InitializeAsync();
        return await _db.Table<Order>().Where(o => o.UserId == userId).ToListAsync();
    }
    public async Task<List<Order>> GetOrdersByStockId(int stockId) {
        await InitializeAsync();
        return await _db.Table<Order>().Where(o => o.StockId == stockId).ToListAsync();
    }
    public async Task CreateOrder(Order order) {
        await InitializeAsync();
        await _db.InsertAsync(order);
    }
    public async Task UpdateOrder(Order order) {
        await InitializeAsync();
        await _db.UpdateAsync(order);
    }
    public async Task DeleteOrder(Order order) {
        await InitializeAsync();
        await _db.DeleteAsync(order);
    }
    #endregion

    #region Transaction operations
    public async Task<List<Transaction>> GetTransactionsAsync() {
        await InitializeAsync();
        return await _db.Table<Transaction>().ToListAsync();
    }
    public async Task<Transaction> GetTransactionById(int transactionId) {
        await InitializeAsync();
        return await _db.Table<Transaction>().Where(t => t.TransactionId == transactionId).FirstOrDefaultAsync();
    }
    public async Task<List<Transaction>> GetTransactionsByUserId(int userId) {
        await InitializeAsync();
        return await _db.Table<Transaction>().Where(t => t.BuyerId == userId || t.SellerId == userId).ToListAsync();
    }
    public async Task CreateTransaction(Transaction transaction) {
        await InitializeAsync();
        await _db.InsertAsync(transaction);
    }
    public async Task UpdateTransaction(Transaction transaction) {
        await InitializeAsync();
        await _db.UpdateAsync(transaction);
    }
    public async Task DeleteTransaction(Transaction transaction) {
        await InitializeAsync();
        await _db.DeleteAsync(transaction);
    }
    #endregion

    #region Portfolio operations
    public async Task<List<Portfolio>> GetPortfoliosAsync() {
        await InitializeAsync();
        return await _db.Table<Portfolio>().ToListAsync();
    }
    public async Task<Portfolio> GetPortfolioById(int portfolioId) {
        await InitializeAsync();
        return await _db.Table<Portfolio>().Where(p => p.PortfolioId == portfolioId).FirstOrDefaultAsync();
    }
    public async Task<List<Portfolio>> GetPortfoliosByUserId(int userId) {
        await InitializeAsync();
        return await _db.Table<Portfolio>().Where(p => p.UserId == userId).ToListAsync();
    }
    public async Task CreatePortfolio(Portfolio portfolio) {
        await InitializeAsync();
        await _db.InsertAsync(portfolio);
    }
    public async Task UpdatePortfolio(Portfolio portfolio) {
        await InitializeAsync();
        await _db.UpdateAsync(portfolio);
    }
    public async Task DeletePortfolio(Portfolio portfolio) {
        await InitializeAsync();
        await _db.DeleteAsync(portfolio);
    }
    #endregion

    #region Fund operations
    public async Task<List<Fund>> GetFundsAsync() {
        await InitializeAsync();
        return await _db.Table<Fund>().ToListAsync();
    }
    public async Task<Fund> GetFundById(int fundId) {
        await InitializeAsync();
        return await _db.Table<Fund>().Where(f => f.FundId == fundId).FirstOrDefaultAsync();
    }
    public async Task<Fund> GetFundByUserId(int userId) {
        await InitializeAsync();
        return await _db.Table<Fund>().Where(f => f.UserId == userId).FirstOrDefaultAsync();
    }
    public async Task CreateFund(Fund fund) {
        await InitializeAsync();
        await _db.InsertAsync(fund);
    }
    public async Task UpdateFund(Fund fund) {
        await InitializeAsync();
        await _db.UpdateAsync(fund);
    }
    public async Task DeleteFund(Fund fund) {
        await InitializeAsync();
        await _db.DeleteAsync(fund);
    }
    #endregion
}
