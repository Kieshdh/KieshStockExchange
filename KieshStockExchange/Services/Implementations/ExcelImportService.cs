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
    private DataTable? IdentityDataTable = null;
    private DataTable? ProfileDataTable = null;
    private DataTable? StockDataTable = null;
    private DataTable? PreferenceDataTable = null;
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
    public async Task AddUsersFromExcelAsync(bool checkDataLoaded = true)
    {
        LoadDataTables();
        // Check if the data is already imported
        if (checkDataLoaded)
        {
            int count = (await _db.GetUsersAsync()).Count;
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
            if (!ParsingHelper.TryToInt(row["ID"].ToString(), out var userId))
            {
                _logger.LogWarning("Invalid User ID: '{UserIdString}'.", row["ID"]);
                continue;
            }

            // Get other fields
            var username = row["Username"]?.ToString() ?? string.Empty;
            var email = row["Email"]?.ToString() ?? string.Empty;
            var password = "hallo123";
            var fullName = row["Full Name"]?.ToString() ?? string.Empty;
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
        });
        _logger.LogInformation("Loaded in total {userCount} users", users.Count);
    }

    public async Task AddAIUserBehaviourDataFromExcelAsync(bool checkDataLoaded = true)
    {
        LoadDataTables();
    }

    public async Task AddStocksFromExcelAsync(bool checkDataLoaded = true)
    {
        LoadDataTables();
        // Check if the data is already imported
        if (checkDataLoaded)
        {
            int count = (await _db.GetStocksAsync()).Count;
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
            if (!ParsingHelper.TryToInt(row["ID"].ToString(), out var stockId))
            {
                _logger.LogWarning("Invalid Stock ID: '{StockIdString}'.", row["ID"]);
                continue;
            }

            // Get other fields
            var symbol = row["Symbol"]?.ToString() ?? string.Empty;
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
        });
        
        _logger.LogInformation("Loaded in total {StockCount} stocks with initial stockprice.", stocks.Count);
    }

    public async Task AddHoldingsFromExcelAsync(bool checkDataLoaded = true)
    {
        LoadDataTables();
        int stockCount = StockDataTable!.Rows.Count;

        // Check if the data is already imported
        if (checkDataLoaded)
        {
            int countFunds = (await _db.GetFundsAsync()).Count;
            int countPositions = (await _db.GetPositionsAsync()).Count;
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
            if (!ParsingHelper.TryToInt(row["ID"].ToString(), out var userId))
            {
                _logger.LogWarning("Invalid User ID: '{UserIdString}'.", row["ID"]);
                continue;
            }
            if (!ParsingHelper.TryToDecimal(row["Balance"].ToString(), out var balance))
            {
                _logger.LogWarning("Invalid Balance for User ID {UserId}: '{BalanceString}'.", userId, row["Balance"]);
                continue;
            }

            int[] stocks = new int[stockCount];
            for (int i = 1; i <= stockCount; i++)
            {
                if (!ParsingHelper.TryToInt(row[i+1].ToString(), out stocks[i - 1]))
                {
                    _logger.LogWarning("Invalid Stock{i} for User ID {UserId}: '{StockString}'.", i, userId, row[$"Stock{i}"]);
                    stocks[i - 1] = 0;
                }
            }

            // Create new fund
            Fund fund = new Fund
            {
                UserId = userId,
                TotalBalance = balance,
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
        });
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
        PreferenceDataTable = ReadExcelFile(EXCEL_FILE_NAME, 3);
        HoldingDataTable = ReadExcelFile(EXCEL_FILE_NAME, 4);

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
