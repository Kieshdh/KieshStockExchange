using ExcelDataReader;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Globalization;

namespace KieshStockExchange.Services.Implementations;

public class ExcelImportService : IExcelImportService
{
    #region Fields, properties, and constructor
    private const string EXCEL_FILE_NAME = "AiUserData.xlsx";
    private DataTable? StockDataTable = null;
    private DataTable? IdentityDataTable = null;
    private DataTable? ProfileDataTable = null;
    private DataTable? HoldingDataTable = null;

    private bool _dataLoaded = false;

    private readonly IDataBaseService _db;
    private readonly ILogger<ExcelImportService> _logger;

    public ExcelImportService(ILogger<ExcelImportService> logger, IDataBaseService db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Importing data from Excel
    public async Task ResetAndAddDatabases()
    {
        await _db.ResetTableAsync<Message>().ConfigureAwait(false);
        await _db.ResetTableAsync<Transaction>().ConfigureAwait(false);
        await _db.ResetTableAsync<Order>().ConfigureAwait(false);
        await AddStocksFromExcelAsync(false).ConfigureAwait(false);
        await AddUsersFromExcelAsync(false).ConfigureAwait(false);
        await AddAIProfileFromExcelAsync(false).ConfigureAwait(false);
        await AddHoldingsFromExcelAsync(false).ConfigureAwait(false);
    }

    public async Task CheckAndAddDatabases()
    {

        await AddStocksFromExcelAsync(true).ConfigureAwait(false);
        await AddUsersFromExcelAsync(true).ConfigureAwait(false);
        await AddAIProfileFromExcelAsync(true).ConfigureAwait(false);
        await AddHoldingsFromExcelAsync(true).ConfigureAwait(false);
    }

    private async Task AddStocksFromExcelAsync(bool checkDataLoaded = true)
    {
        LoadDataTables();
        // Check if the data is already imported
        if (checkDataLoaded)
        {
            int count = (await _db.GetStocksAsync().ConfigureAwait(false)).Count;
            if (count >= StockDataTable!.Rows.Count)
            {
                _logger.LogInformation("Stocks data already imported. Skipping import.");
                return;
            }
        }

        // Make all the new instances of the stocks and stockprices
        List<Stock> stocks = new();
        List<StockPrice> stockPrices = new();
        foreach (DataRow row in StockDataTable!.Rows)
        {
            // Get the stock ID and validate it
            if (!ParsingHelper.TryToInt(row["StockId"].ToString(), out var stockId))
            {
                _logger.LogWarning("Invalid Stock ID: '{StockIdString}'.", row["StockId"]);
                continue;
            }

            // Get other fields
            var symbol = row["Ticker"]?.ToString() ?? string.Empty;
            var companyName = row["CompanyName"]?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(symbol) || string.IsNullOrWhiteSpace(companyName))
            {
                _logger.LogWarning("Skipping stock #{StockId} with missing required fields", stockId);
                continue;
            }
            if (!ParsingHelper.TryToDecimal(row["Price (USD)"].ToString(), out var price))
            {
                _logger.LogWarning("Invalid Price for Stock ID {StockId}: '{PriceString}'.", stockId, row["Price (USD)"]);
                continue;
            }

            // Create new stock
            Stock stock = new Stock 
            { 
                StockId = stockId, 
                Symbol = symbol, 
                CompanyName = companyName 
            };
            if (!stock.IsValid())
            {
                _logger.LogWarning("Failed to register stock #{StockId}: {Symbol}.", stockId, symbol);
                continue;
            }

            // Create initial stock price
            StockPrice stockPrice = new StockPrice
            {
                StockId = stockId,
                Price = price,
                CurrencyType = CurrencyType.USD,
            };
            if (!stockPrice.IsValid())
            {
                _logger.LogWarning("Failed to register stock price for stock #{StockId}: {Symbol} at price {Price}.", stockId, symbol, price);
                continue;
            }

            stocks.Add(stock);
            stockPrices.Add(stockPrice);
        }

        // Drop the existing stocks and sp tables and insert the new stocks 
        await _db.RunInTransactionAsync(async ct =>
        {
            await _db.ResetTableAsync<Stock>(ct);
            await _db.ResetTableAsync<StockPrice>(ct);
            await _db.InsertAllAsync(stocks, ct);
            await _db.InsertAllAsync(stockPrices, ct);
        }).ConfigureAwait(false);
        
        _logger.LogInformation("Loaded in total {StockCount} stocks with initial stockprice.", stocks.Count);
    }

    private async Task AddUsersFromExcelAsync(bool checkDataLoaded = true)
    {
        LoadDataTables();
        // Check if the data is already imported
        if (checkDataLoaded)
        {
            int count = (await _db.GetUsersAsync().ConfigureAwait(false)).Count;
            int tableCount = IdentityDataTable!.Rows.Count;
            if (count >= tableCount)
            {
                _logger.LogInformation("Users data already imported. Skipping import.");
                return;
            }
            _logger.LogInformation("Users data not fully imported. Total {UserCount} user. " +
                "Expected at least {TableCount} users. Importing data...", count, tableCount);
        }

        // Get the new users
        List<User> users = new List<User>();
        foreach (DataRow row in IdentityDataTable!.Rows)
        {
            // Get the user ID and validate it
            if (!ParsingHelper.TryToInt(row["UserId"].ToString(), out var userId))
            {
                _logger.LogWarning("Invalid User ID: '{UserIdString}'.", row["UserId"]);
                continue;
            }

            // Get other fields
            var username = row["Username"]?.ToString() ?? string.Empty;
            var email = row["Email"]?.ToString() ?? string.Empty;
            var password = "hallo123";
            var fullName = row["FullName"]?.ToString() ?? string.Empty;
            DateTime? birthdate = row["Birthdate"] is DateTime d ? d
                : (DateTime.TryParse(row["Birthdate"]?.ToString(), out var bd) ? bd : null);

            // Validate required fields
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email)
                 || string.IsNullOrWhiteSpace(fullName) || !birthdate.HasValue)
            {
                _logger.LogWarning("Skipping user #{UserId} with missing required fields", userId);
                continue;
            }

            // Create new user
            User user = new User
            {
                UserId = userId, Username = username, Email = email,
                PasswordHash = SecurityHelper.HashPassword(password),
                FullName = fullName, BirthDate = birthdate
            };
            if (!user.IsValid())
            {
                _logger.LogWarning("Failed to register user #{UserId}: {Username}.", userId, username);
                continue;
            }
            users.Add(user);
        }

        // Reset the existing users table and insert the new users
        await _db.RunInTransactionAsync(async ct =>
        {
            await _db.ResetTableAsync<User>();
            await _db.InsertAllAsync(users);
        }).ConfigureAwait(false);
        _logger.LogInformation("Loaded in total {userCount} users", users.Count);
    }

    private async Task AddAIProfileFromExcelAsync(bool checkDataLoaded = true)
    {
        LoadDataTables();
        // Check if the data is already imported
        if (checkDataLoaded)
        {
            int count = (await _db.GetAIUsersAsync().ConfigureAwait(false)).Count;
            int tableCount = ProfileDataTable!.Rows.Count;

            if (count >= tableCount)
            {
                _logger.LogInformation("AI profile data already imported. Skipping import.");
                return;
            }

            _logger.LogInformation("AI profile data not fully imported. Total {ExistingCount} " +
                "AIUsers. Expected at least {TableCount}. Importing data...", count, tableCount);
        }

        // Get the new AI users
        List<AIUser> aiUsers = new List<AIUser>();
        foreach (DataRow row in ProfileDataTable!.Rows)
        {
            // Get the user ID and validate it
            if (!ParsingHelper.TryToInt(row["UserId"].ToString(), out var userId))
            {
                _logger.LogWarning("Invalid User ID: '{UserIdString}'.", row["ID"]);
                continue;
            }

            // Integer values
            if (!ParsingHelper.TryToInt(row["Seed"].ToString(), out var seed) ||
                !ParsingHelper.TryToInt(row["DecisionIntervalSeconds"].ToString(), out var intervalSeconds) ||
                !ParsingHelper.TryToInt(row["MinOpenPositions"].ToString(), out var minOpenPositions) ||
                !ParsingHelper.TryToInt(row["MaxOpenPositions"].ToString(), out var maxOpenPositions) ||
                !ParsingHelper.TryToInt(row["MaxDailyTrades"].ToString(), out var maxDailyTrades) ||
                !ParsingHelper.TryToInt(row["MaxOpenOrders"].ToString(), out var maxOpenOrders) ||
                !ParsingHelper.TryToInt(row["Strategy"].ToString(), out var strategyCode))
            {
                _logger.LogWarning("Invalid integer field for AI user #{UserId}.", userId);
                continue;
            }

            // Decimal values
            if (!ParsingHelper.TryToDecimal(row["TradeProb"].ToString(), out var tradeProb) ||
                !ParsingHelper.TryToDecimal(row["UseMarketProb"].ToString(), out var useMarketProb) ||
                !ParsingHelper.TryToDecimal(row["UseSlippageMarketProb"].ToString(), out var useSlippageMarketProb) ||
                !ParsingHelper.TryToDecimal(row["OnlineProb"].ToString(), out var onlineProb) ||
                !ParsingHelper.TryToDecimal(row["BuyBiasPrc"].ToString(), out var buyBiasPrc) ||
                !ParsingHelper.TryToDecimal(row["MinTradeAmountPrc"].ToString(), out var minTradeAmountPrc) ||
                !ParsingHelper.TryToDecimal(row["MaxTradeAmountPrc"].ToString(), out var maxTradeAmountPrc) ||
                !ParsingHelper.TryToDecimal(row["PerPositionMaxPrc"].ToString(), out var perPositionMaxPrc) ||
                !ParsingHelper.TryToDecimal(row["MinCashReservePrc"].ToString(), out var minCashReservePrc) ||
                !ParsingHelper.TryToDecimal(row["MaxCashReservePrc"].ToString(), out var maxCashReservePrc) ||
                !ParsingHelper.TryToDecimal(row["SlippageTolerancePrc"].ToString(), out var slippageTolerancePrc) ||
                !ParsingHelper.TryToDecimal(row["MinLimitOffsetPrc"].ToString(), out var minLimitOffsetPrc) ||
                !ParsingHelper.TryToDecimal(row["MaxLimitOffsetPrc"].ToString(), out var maxLimitOffsetPrc) ||
                !ParsingHelper.TryToDecimal(row["AggressivenessPrc"].ToString(), out var aggressivenessPrc))
            {
                _logger.LogWarning("Invalid percentage value(s) for User #{UserId}. Skipping.", userId);
                continue;
            }

            var watchlistCsv = row["WatchlistCsv"]?.ToString() ?? string.Empty;

            // Create new AI user profile
            try
            {
                var aiUser = new AIUser
                {
                    UserId = userId, Seed = seed, DecisionIntervalSeconds = intervalSeconds,
                    TradeProb = tradeProb, UseMarketProb = useMarketProb, BuyBiasPrc = buyBiasPrc,
                    UseSlippageMarketProb = useSlippageMarketProb, OnlineProb = onlineProb, 
                    MinTradeAmountPrc = minTradeAmountPrc, MaxTradeAmountPrc = maxTradeAmountPrc,
                    PerPositionMaxPrc = perPositionMaxPrc, MinCashReservePrc = minCashReservePrc,
                    MaxCashReservePrc = maxCashReservePrc, SlippageTolerancePrc = slippageTolerancePrc,
                    MinLimitOffsetPrc = minLimitOffsetPrc, MaxLimitOffsetPrc = maxLimitOffsetPrc,
                    AggressivenessPrc = aggressivenessPrc, MinOpenPositions = minOpenPositions,
                    MaxOpenPositions = maxOpenPositions, MaxDailyTrades = maxDailyTrades,
                    MaxOpenOrders = maxOpenOrders, WatchlistCsv = watchlistCsv, StrategyCode = strategyCode
                };
                if (!aiUser.IsValid())
                {
                    _logger.LogWarning("Failed to register AI profile for user #{UserId}.", userId);
                    continue;
                }
                aiUsers.Add(aiUser);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception while creating AI profile for user #{UserId}.", userId);
                continue;
            }
        }

        // Reset the existing AI users table and insert the new AI user profiles
        await _db.RunInTransactionAsync(async ct =>
        {
            await _db.ResetTableAsync<AIUser>(ct);
            await _db.InsertAllAsync(aiUsers, ct);
        }).ConfigureAwait(false);

        _logger.LogInformation("Loaded in total {AiUserCount} AI user profiles.", aiUsers.Count);
    }

    private async Task AddHoldingsFromExcelAsync(bool checkDataLoaded = true)
    {
        LoadDataTables();
        int stockCount = StockDataTable!.Rows.Count;

        // Check if the data is already imported
        if (checkDataLoaded)
        {
            int countFunds = (await _db.GetFundsAsync().ConfigureAwait(false)).Count;
            int countPositions = (await _db.GetPositionsAsync().ConfigureAwait(false)).Count;
            if (countFunds >= HoldingDataTable!.Rows.Count && countPositions >= HoldingDataTable.Rows.Count * 21)
            {
                _logger.LogInformation("Holdings data already imported. Skipping import.");
                return;
            }
        }

        List<Fund> funds = new();
        List<Position> positions = new();
        foreach (DataRow row in HoldingDataTable!.Rows)
        {
            // Validate required fields
            if (!ParsingHelper.TryToInt(row["UserId"].ToString(), out var userId))
            {
                _logger.LogWarning("Invalid User ID: '{UserIdString}'.", row["UserId"]);
                continue;
            }
            if (!ParsingHelper.TryToDecimal(row["Balance"].ToString(), out var balance))
            {
                _logger.LogWarning("Invalid Balance for User ID {UserId}: '{BalanceString}'.", userId, row["Balance"]);
                continue;
            }

            int[] stocks = new int[stockCount];
            for (int i = 0; i < stockCount; i++)
            {
                if (!ParsingHelper.TryToInt(row[i+2].ToString(), out stocks[i]))
                {
                    _logger.LogWarning("Invalid Stock{i} for User ID {UserId}: '{StockString}'.", i, userId, row[$"Stock{i+1}"]);
                    stocks[i] = 0;
                }
            }

            // Create new fund
            Fund fund = new Fund
            {
                UserId = userId,
                TotalBalance = balance,
                CurrencyType = CurrencyType.USD,
            };
            if (!fund.IsValid())
            {
                _logger.LogWarning("Failed to register fund for user #{UserId}: {username}.", userId, row["Username"].ToString());
                continue;
            }

            // Create new positions
            List<Position> userPositions = new();
            for (int i = 1; i <= stockCount; i++)
            {
                Position position = new Position
                {
                    UserId = userId,
                    StockId = i,
                    Quantity = stocks[i - 1],
                };
                if (!position.IsValid())
                {
                    _logger.LogWarning("Failed to register position for user #{UserId}: Stock {StockId} with quantity {Quantity}.", userId, i, stocks[i - 1]);
                    continue;
                }
                userPositions.Add(position);
            }

            // Add to the lists
            funds.Add(fund);
            positions.AddRange(userPositions);
        }

        // Drop the existing funds table from the database
        await _db.RunInTransactionAsync(async ct =>
        {
            await _db.ResetTableAsync<Position>();
            await _db.ResetTableAsync<Fund>();
            await _db.InsertAllAsync(funds);
            await _db.InsertAllAsync(positions);
        }).ConfigureAwait(false);
        _logger.LogInformation("Loaded in total {FundCount} funds with {PositionCount} positions.", funds.Count, positions.Count);

    }
    #endregion

    #region Other Methods
    private void LoadDataTables()
    {
        if (_dataLoaded) return;

        StockDataTable = ReadExcelFile(EXCEL_FILE_NAME, 0);
        IdentityDataTable = ReadExcelFile(EXCEL_FILE_NAME, 1);
        ProfileDataTable = ReadExcelFile(EXCEL_FILE_NAME, 2);
        HoldingDataTable = ReadExcelFile(EXCEL_FILE_NAME, 3);

        _dataLoaded = true;
    }

    private static DataTable ReadExcelFile(string filePath, int SheetNumber)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        var conf = new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration
            {
                UseHeaderRow = true // ✅ This tells it to use the first row as column names
            }
        };

        var result = reader.AsDataSet(conf);
        return result.Tables[SheetNumber];
    }
    #endregion
}
