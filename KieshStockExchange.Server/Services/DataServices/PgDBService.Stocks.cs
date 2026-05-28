using Dapper;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    private const string StockCols     = @"""StockId"",""Symbol"",""CompanyName"",""CreatedAt""";
    private const string ListingCols   = @"""ListingId"",""StockId"",""Currency"",""IsPrimary"",""SeedPrice"",""CreatedAt""";
    private const string StockPriceCols = @"""PriceId"",""StockId"",""Price"",""Currency"",""Timestamp""";

    #region Stock operations
    public async Task<List<Stock>> GetStocksAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<StockRow>($@"SELECT {StockCols} FROM ""Stocks""");
        return rows.Select(StockMapper.ToDomain).ToList();
    }

    public async Task<Stock?> GetStockById(int stockId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<StockRow>(
            $@"SELECT {StockCols} FROM ""Stocks"" WHERE ""StockId"" = @stockId",
            new { stockId });
        return row is null ? null : StockMapper.ToDomain(row);
    }

    public async Task<bool> StockExists(int stockId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return await c.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS(SELECT 1 FROM ""Stocks"" WHERE ""StockId"" = @stockId)",
            new { stockId });
    }

    public async Task CreateStock(Stock stock, CancellationToken ct = default)
    {
        if (!stock.IsValid()) throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await using var c = await OpenAsync(ct);
        var row = StockMapper.ToRow(stock);
        row.StockId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Stocks"" (""Symbol"",""CompanyName"",""CreatedAt"")
            VALUES (@Symbol,@CompanyName,@CreatedAt)
            RETURNING ""StockId""", row);
        stock.StockId = row.StockId;
    }

    public async Task UpdateStock(Stock stock, CancellationToken ct = default)
    {
        if (!stock.IsValid()) throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""Stocks"" SET ""Symbol"" = @Symbol, ""CompanyName"" = @CompanyName, ""CreatedAt"" = @CreatedAt
            WHERE ""StockId"" = @StockId", StockMapper.ToRow(stock));
    }

    public async Task UpsertStock(Stock stock, CancellationToken ct = default)
    {
        if (!stock.IsValid()) throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await using var c = await OpenAsync(ct);
        var row = StockMapper.ToRow(stock);
        var returned = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Stocks"" (""StockId"",""Symbol"",""CompanyName"",""CreatedAt"")
            VALUES (@StockId,@Symbol,@CompanyName,@CreatedAt)
            ON CONFLICT (""StockId"") DO UPDATE SET
              ""Symbol"" = EXCLUDED.""Symbol"", ""CompanyName"" = EXCLUDED.""CompanyName"",
              ""CreatedAt"" = EXCLUDED.""CreatedAt""
            RETURNING ""StockId""", row);
        stock.StockId = returned;
    }

    public async Task DeleteStock(Stock stock, CancellationToken ct = default)
    {
        if (stock.StockId == 0)
            throw new ArgumentException("Stock entity must have a valid StockId", nameof(stock));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""Stocks"" WHERE ""StockId"" = @StockId", new { stock.StockId });
    }
    #endregion

    #region StockListing operations
    public async Task<List<StockListing>> GetStockListingsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<StockListingRow>($@"SELECT {ListingCols} FROM ""StockListings""");
        return rows.Select(StockListingMapper.ToDomain).ToList();
    }

    public async Task<List<StockListing>> GetStockListingsByStockId(int stockId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<StockListingRow>(
            $@"SELECT {ListingCols} FROM ""StockListings"" WHERE ""StockId"" = @stockId",
            new { stockId });
        return rows.Select(StockListingMapper.ToDomain).ToList();
    }

    public async Task CreateStockListing(StockListing listing, CancellationToken ct = default)
    {
        if (!listing.IsValid()) throw new ArgumentException("StockListing entity is not valid", nameof(listing));
        await using var c = await OpenAsync(ct);
        var row = StockListingMapper.ToRow(listing);
        row.ListingId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""StockListings"" (""StockId"",""Currency"",""IsPrimary"",""SeedPrice"",""CreatedAt"")
            VALUES (@StockId,@Currency,@IsPrimary,@SeedPrice,@CreatedAt)
            RETURNING ""ListingId""", row);
        listing.ListingId = row.ListingId;
    }
    #endregion

    #region StockPrice operations
    public async Task<List<StockPrice>> GetStockPricesAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<StockPriceRow>($@"SELECT {StockPriceCols} FROM ""StockPrices""");
        return rows.Select(StockPriceMapper.ToDomain).ToList();
    }

    public async Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<StockPriceRow>(
            $@"SELECT {StockPriceCols} FROM ""StockPrices"" WHERE ""PriceId"" = @stockPriceId",
            new { stockPriceId });
        return row is null ? null : StockPriceMapper.ToDomain(row);
    }

    public async Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<StockPriceRow>(
            $@"SELECT {StockPriceCols} FROM ""StockPrices"" WHERE ""StockId"" = @stockId",
            new { stockId });
        return rows.Select(StockPriceMapper.ToDomain).ToList();
    }

    public async Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<StockPriceRow>(
            $@"SELECT {StockPriceCols} FROM ""StockPrices""
               WHERE ""StockId"" = @stockId AND ""Currency"" = @currency
               ORDER BY ""Timestamp"" DESC LIMIT 1",
            new { stockId, currency = currency.ToString() });
        return row is null ? null : StockPriceMapper.ToDomain(row);
    }

    public async Task<StockPrice?> GetLatestStockPriceBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<StockPriceRow>(
            $@"SELECT {StockPriceCols} FROM ""StockPrices""
               WHERE ""StockId"" = @stockId AND ""Currency"" = @currency AND ""Timestamp"" <= @time
               ORDER BY ""Timestamp"" DESC LIMIT 1",
            new { stockId, currency = currency.ToString(), time });
        return row is null ? null : StockPriceMapper.ToDomain(row);
    }

    public async Task<List<StockPrice>> GetStockPricesByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<StockPriceRow>(
            $@"SELECT {StockPriceCols} FROM ""StockPrices""
               WHERE ""StockId"" = @stockId AND ""Currency"" = @currency
                 AND ""Timestamp"" >= @from AND ""Timestamp"" < @to
               ORDER BY ""Timestamp"" DESC",
            new { stockId, currency = currency.ToString(), from, to });
        return rows.Select(StockPriceMapper.ToDomain).ToList();
    }

    public async Task CreateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
    {
        if (!stockPrice.IsValid()) throw new ArgumentException("StockPrice entity is not valid", nameof(stockPrice));
        await using var c = await OpenAsync(ct);
        var row = StockPriceMapper.ToRow(stockPrice);
        row.PriceId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""StockPrices"" (""StockId"",""Price"",""Currency"",""Timestamp"")
            VALUES (@StockId,@Price,@Currency,@Timestamp)
            RETURNING ""PriceId""", row);
        stockPrice.PriceId = row.PriceId;
    }

    public async Task UpdateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
    {
        if (!stockPrice.IsValid()) throw new ArgumentException("StockPrice entity is not valid", nameof(stockPrice));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""StockPrices"" SET ""StockId"" = @StockId, ""Price"" = @Price,
              ""Currency"" = @Currency, ""Timestamp"" = @Timestamp
            WHERE ""PriceId"" = @PriceId", StockPriceMapper.ToRow(stockPrice));
    }

    public async Task DeleteStockPrice(StockPrice stockPrice, CancellationToken ct = default)
    {
        if (stockPrice.PriceId == 0)
            throw new ArgumentException("StockPrice entity must have a valid PriceId", nameof(stockPrice));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""StockPrices"" WHERE ""PriceId"" = @PriceId", new { stockPrice.PriceId });
    }
    #endregion
}
