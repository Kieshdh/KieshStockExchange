using ExcelDataReader;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Globalization;
using System.Linq;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices;

public class ExcelImportService : IExcelImportService
{
    #region Fields, properties, and constructor
    private const string EXCEL_FILE_NAME = "AiUserData.xlsx";
    private DataTable? StockDataTable = null;
    private DataTable? ListingDataTable = null; // optional — falls back to StockListingSeed
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
        await _db.ResetTableAsync<Order>().ConfigureAwait(false);
        await _db.ResetTableAsync<Transaction>().ConfigureAwait(false);
        await _db.ResetTableAsync<Message>().ConfigureAwait(false);
        await _db.ResetTableAsync<Candle>().ConfigureAwait(false);
        await AddStocksFromExcelAsync(false).ConfigureAwait(false);
        await AddStockListingsFromExcelAsync(false).ConfigureAwait(false);
        await AddUsersFromExcelAsync(false).ConfigureAwait(false);
        await AddAIProfileFromExcelAsync(false).ConfigureAwait(false);
        await AddHoldingsFromExcelAsync(false).ConfigureAwait(false);
    }

    public async Task CheckAndAddDatabases()
    {
        await AddStocksFromExcelAsync(true).ConfigureAwait(false);
        await AddStockListingsFromExcelAsync(true).ConfigureAwait(false);
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

        // Stocks sheet carries a USD reference price; cross-listed EUR
        // seed prices are derived by AddStockListingsFromExcelAsync.
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
                CompanyName = companyName,
            };
            if (!stock.IsValid())
            {
                _logger.LogWarning("Failed to register stock #{StockId}: {Symbol}.", stockId, symbol);
                continue;
            }

            // Initial StockPrice rows for the primary listings are written by
            // AddStockListingsFromExcelAsync (one StockPrice per listing). The
            // Stocks sheet's "Price (USD)" is just the upstream input — track
            // it here so the listing step can read it without re-parsing.
            stockPrices.Add(new StockPrice
            {
                StockId = stockId,
                Price = price,
                CurrencyType = CurrencyType.USD,
            });

            stocks.Add(stock);
        }

        // Drop the existing stocks and sp tables and insert the new stocks.
        // StockListings is reset by AddStockListingsFromExcelAsync immediately
        // after this so listings stay consistent with the Stocks set.
        await _db.RunInTransactionAsync(async ct =>
        {
            await _db.ResetTableAsync<Stock>(ct);
            await _db.ResetTableAsync<StockPrice>(ct);
            await _db.InsertAllAsync(stocks, ct);
            // Stash the raw USD prices in StockPrices for the listings pass
            // to read; AddStockListingsFromExcelAsync rebuilds StockPrice
            // properly for every listing currency.
            await _db.InsertAllAsync(stockPrices, ct);
        }).ConfigureAwait(false);

        _logger.LogInformation("Loaded in total {StockCount} stocks.", stocks.Count);
    }

    private async Task AddStockListingsFromExcelAsync(bool checkDataLoaded = true)
    {
        LoadDataTables();

        // Skip if the listings already cover every stock (a no-op on warm boots).
        if (checkDataLoaded)
        {
            int existing = (await _db.GetStockListingsAsync().ConfigureAwait(false)).Count;
            int stockCount = (await _db.GetStocksAsync().ConfigureAwait(false)).Count;
            if (stockCount > 0 && existing >= stockCount)
            {
                _logger.LogInformation("StockListings data already imported. Skipping import.");
                return;
            }
        }

        var usdPrices = await ReadUsdSeedPricesAsync().ConfigureAwait(false);

        // Prefer the explicit Listings sheet when present; otherwise derive
        // from the StockListingSeed constants (back-compat for older xlsx
        // files that don't carry the sheet yet).
        IReadOnlyList<StockListing> listings;
        if (ListingDataTable is not null && ListingDataTable.Rows.Count > 0)
            listings = ReadListingsFromSheet(ListingDataTable, usdPrices);
        else
        {
            _logger.LogInformation(
                "No Listings sheet in {File}; falling back to StockListingSeed.", EXCEL_FILE_NAME);
            listings = StockListingSeed.BuildFor(usdPrices);
        }

        // One StockPrice row per (StockId, Currency) so the engine has
        // an initial last-price for every listing it'll trade.
        var prices = listings
            .Where(l => l.SeedPrice > 0m)
            .Select(l => new StockPrice
            {
                StockId = l.StockId,
                CurrencyType = l.CurrencyType,
                Price = l.SeedPrice,
            })
            .Where(sp => sp.IsValid())
            .ToList();

        await _db.RunInTransactionAsync(async ct =>
        {
            await _db.ResetTableAsync<StockListing>(ct);
            await _db.ResetTableAsync<StockPrice>(ct);
            foreach (var listing in listings)
                if (listing.IsValid())
                    await _db.CreateStockListing(listing, ct).ConfigureAwait(false);
            await _db.InsertAllAsync(prices, ct);
        }).ConfigureAwait(false);

        _logger.LogInformation(
            "Loaded {Listings} listings across {Currencies} currencies, with {Prices} initial prices.",
            listings.Count,
            listings.Select(l => l.CurrencyType).Distinct().Count(),
            prices.Count);
    }

    private async Task<Dictionary<int, decimal>> ReadUsdSeedPricesAsync()
    {
        // Stocks sheet's "Price (USD)" was just stashed into StockPrices by
        // AddStocksFromExcelAsync; read it back so this method works whether
        // called directly or after a full ResetAndAdd cycle.
        var rows = await _db.GetStockPricesAsync().ConfigureAwait(false);
        var dict = new Dictionary<int, decimal>(rows.Count);
        foreach (var sp in rows)
            dict[sp.StockId] = sp.Price; // last write wins; expected one row per stock
        return dict;
    }

    private static IReadOnlyList<StockListing> ReadListingsFromSheet(
        DataTable sheet, IReadOnlyDictionary<int, decimal> usdPrices)
    {
        var rows = new List<StockListing>(sheet.Rows.Count);
        foreach (DataRow row in sheet.Rows)
        {
            if (!ParsingHelper.TryToInt(row["StockId"]?.ToString(), out var stockId)) continue;

            var ccy = CurrencyHelper.FromIsoCodeOrDefault(row["Currency"]?.ToString(), CurrencyType.USD);

            bool isPrimary = false;
            var primaryRaw = row["IsPrimary"]?.ToString();
            if (!string.IsNullOrWhiteSpace(primaryRaw))
            {
                if (bool.TryParse(primaryRaw, out var bv)) isPrimary = bv;
                else if (int.TryParse(primaryRaw, out var iv)) isPrimary = iv != 0;
            }

            decimal seedPrice;
            if (!ParsingHelper.TryToDecimal(row["SeedPrice"]?.ToString(), out seedPrice) || seedPrice <= 0m)
            {
                // Fall back to the Stocks-sheet USD price for the primary listing,
                // and the EUR-converted equivalent for an EUR row missing a price.
                usdPrices.TryGetValue(stockId, out var usd);
                seedPrice = ccy == CurrencyType.EUR
                    ? CurrencyHelper.RoundMoney(usd * StockListingSeed.EurPerUsd, CurrencyType.EUR)
                    : CurrencyHelper.RoundMoney(usd, ccy);
            }

            rows.Add(new StockListing
            {
                StockId = stockId,
                CurrencyType = ccy,
                IsPrimary = isPrimary,
                SeedPrice = seedPrice,
            });
        }
        return rows;
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
                : DateTime.TryParse(row["Birthdate"]?.ToString(), out var bd) ? bd : null;

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

        // Reset the existing users table and insert the new users. Also cascade-reset
        // Orders and Transactions in the same root tx - those reference UserIds whose
        // ownership semantics may have shifted in the new xlsx. Leaving them behind is
        // what produces stale orders against fresh positions.
        await _db.RunInTransactionAsync(async ct =>
        {
            await _db.ResetTableAsync<Order>(ct);
            await _db.ResetTableAsync<Transaction>(ct);
            await _db.ResetTableAsync<User>(ct);
            await _db.InsertAllAsync(users, ct);
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
                !ParsingHelper.TryToDecimal(row["BuyBiasPrc"].ToString(), out var buyBiasPrc) ||
                !ParsingHelper.TryToDecimal(row["MinTradeAmountPrc"].ToString(), out var minTradeAmountPrc) ||
                !ParsingHelper.TryToDecimal(row["MaxTradeAmountPrc"].ToString(), out var maxTradeAmountPrc) ||
                !ParsingHelper.TryToDecimal(row["PerPositionMaxPrc"].ToString(), out var perPositionMaxPrc) ||
                !ParsingHelper.TryToDecimal(row["MinCashReservePrc"].ToString(), out var minCashReservePrc) ||
                !ParsingHelper.TryToDecimal(row["MaxCashReservePrc"].ToString(), out var maxCashReservePrc) ||
                !ParsingHelper.TryToDecimal(row["SlippageTolerancePrc"].ToString(), out var slippageTolerancePrc) ||
                !ParsingHelper.TryToDecimal(row["MinLimitOffsetPrc"].ToString(), out var minLimitOffsetPrc) ||
                !ParsingHelper.TryToDecimal(row["MaxLimitOffsetPrc"].ToString(), out var maxLimitOffsetPrc) ||
                !ParsingHelper.TryToDecimal(row["AggressivenessPrc"].ToString(), out var aggressivenessPrc) ||
                !ParsingHelper.TryToDecimal(row["ExtremeReactionRandomnessPrc"].ToString(), out var extremeRandomnessPrc) ||
                !ParsingHelper.TryToDecimal(row["CashInjectionFrequencyPrc"].ToString(), out var cashInjectionFrequencyPrc) ||
                !ParsingHelper.TryToDecimal(row["CashInjectionAmountPrc"].ToString(), out var cashInjectionAmountPrc))
            {
                _logger.LogWarning("Invalid percentage value(s) for User #{UserId}. Skipping.", userId);
                continue;
            }

            var watchlistCsv = row["WatchlistCsv"]?.ToString() ?? string.Empty;

            // HomeCurrency is optional — older xlsx files without the column
            // default every bot to USD, matching the legacy single-currency
            // behaviour exactly.
            string homeCurrency = "USD";
            if (ProfileDataTable!.Columns.Contains("HomeCurrency"))
            {
                var raw = row["HomeCurrency"]?.ToString();
                if (!string.IsNullOrWhiteSpace(raw) && CurrencyHelper.IsSupported(raw))
                    homeCurrency = raw.Trim().ToUpperInvariant();
            }

            // Create new AI user profile
            try
            {
                var aiUser = new AIUser
                {
                    UserId = userId, Seed = seed, DecisionIntervalSeconds = intervalSeconds,
                    TradeProb = tradeProb, UseMarketProb = useMarketProb, BuyBiasPrc = buyBiasPrc,
                    UseSlippageMarketProb = useSlippageMarketProb,
                    MinTradeAmountPrc = minTradeAmountPrc, MaxTradeAmountPrc = maxTradeAmountPrc,
                    PerPositionMaxPrc = perPositionMaxPrc, MinCashReservePrc = minCashReservePrc,
                    MaxCashReservePrc = maxCashReservePrc, SlippageTolerancePrc = slippageTolerancePrc,
                    MinLimitOffsetPrc = minLimitOffsetPrc, MaxLimitOffsetPrc = maxLimitOffsetPrc,
                    AggressivenessPrc = aggressivenessPrc, MinOpenPositions = minOpenPositions,
                    MaxOpenPositions = maxOpenPositions, MaxDailyTrades = maxDailyTrades,
                    MaxOpenOrders = maxOpenOrders, WatchlistCsv = watchlistCsv, StrategyCode = strategyCode,
                    ExtremeReactionRandomnessPrc = extremeRandomnessPrc,
                    CashInjectionFrequencyPrc = cashInjectionFrequencyPrc,
                    CashInjectionAmountPrc = cashInjectionAmountPrc,
                    HomeCurrency = homeCurrency,
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
            if (countFunds >= HoldingDataTable!.Rows.Count && countPositions >= HoldingDataTable.Rows.Count * stockCount)
            {
                _logger.LogInformation("Holdings data already imported. Skipping import.");
                return;
            }
        }

        // Map UserId → HomeCurrency once; bots without a profile row default to USD.
        var profiles = await _db.GetAIUsersAsync().ConfigureAwait(false);
        var homeCurrencyByUserId = profiles.ToDictionary(p => p.UserId, p => p.HomeCurrencyType);

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

            // The Balance column is already in the bot's home currency (Person.py
            // converts USD seed prices into EUR when the bot is EUR-home, so the
            // resulting balance + holdings figure is denominated in the home
            // currency). One Fund row per bot, in the home currency only.
            var homeCcy = homeCurrencyByUserId.TryGetValue(userId, out var ccy) ? ccy : CurrencyType.USD;
            var balanceInCcy = CurrencyHelper.RoundMoney(balance, homeCcy);
            if (balanceInCcy > 0m)
            {
                var fund = new Fund
                {
                    UserId = userId,
                    TotalBalance = balanceInCcy,
                    CurrencyType = homeCcy,
                };
                if (fund.IsValid())
                    funds.Add(fund);
                else
                    _logger.LogWarning(
                        "Failed to register {Currency} fund for user #{UserId}.", homeCcy, userId);
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
            positions.AddRange(userPositions);
        }

        // Drop the existing funds + positions tables and reseed. 
        await _db.RunInTransactionAsync(async ct =>
        {
            await _db.ResetTableAsync<Order>(ct);
            await _db.ResetTableAsync<Transaction>(ct);
            await _db.ResetTableAsync<Position>(ct);
            await _db.ResetTableAsync<Fund>(ct);
            await _db.InsertAllAsync(funds, ct);
            await _db.InsertAllAsync(positions, ct);
        }).ConfigureAwait(false);

        // Engine in-memory caches (AccountsCache / OrderRegistry) live server-side
        // after Phase 3. The client no longer holds engine state; any cache
        // invalidation happens server-side when the bot loop next refreshes.

        _logger.LogInformation("Loaded in total {FundCount} funds with {PositionCount} positions.", funds.Count, positions.Count);
    }
    #endregion

    #region Other Methods
    private void LoadDataTables()
    {
        if (_dataLoaded) return;

        var ds = ReadAllSheets(EXCEL_FILE_NAME);
        StockDataTable    = RequireSheet(ds, "Stocks");
        IdentityDataTable = RequireSheet(ds, "Identity");
        ProfileDataTable  = RequireSheet(ds, "Profile");
        HoldingDataTable  = RequireSheet(ds, "Holding");
        // Optional — AddStockListingsFromExcelAsync falls back to StockListingSeed when null.
        ListingDataTable  = ds.Tables["Listings"];

        _dataLoaded = true;
    }

    private static DataSet ReadAllSheets(string filePath)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        var conf = new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration
            {
                UseHeaderRow = true // First row is the column header.
            }
        };

        return reader.AsDataSet(conf);
    }

    private static DataTable RequireSheet(DataSet ds, string sheetName)
    {
        var table = ds.Tables[sheetName];
        if (table is null)
            throw new InvalidDataException(
                $"Excel sheet '{sheetName}' is missing from '{EXCEL_FILE_NAME}'. " +
                $"Found sheets: [{string.Join(", ", ds.Tables.Cast<DataTable>().Select(t => t.TableName))}].");
        return table;
    }
    #endregion
}
