using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    #region Stock operations
    public Task<List<Stock>> GetStocksAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Stock?> GetStockById(int stockId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> StockExists(int stockId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateStock(Stock stock, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateStock(Stock stock, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertStock(Stock stock, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteStock(Stock stock, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion

    #region StockListing operations
    public Task<List<StockListing>> GetStockListingsAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<StockListing>> GetStockListingsByStockId(int stockId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateStockListing(StockListing listing, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion

    #region StockPrice operations
    public Task<List<StockPrice>> GetStockPricesAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<StockPrice?> GetLatestStockPriceBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<StockPrice>> GetStockPricesByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteStockPrice(StockPrice stockPrice, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion
}
