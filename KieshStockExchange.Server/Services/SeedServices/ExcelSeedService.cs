using System.Data;
using System.Globalization;
using ClosedXML.Excel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.SeedServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Server.Services.SeedServices;

/// <summary>
/// 7b — ClosedXML port of the deleted client ExcelImportService. The parsing and
/// insert logic is preserved verbatim; only the workbook reader changed
/// (ExcelDataReader → ClosedXML) and the StockListingSeed fallback was dropped
/// because the committed workbook always carries a Listings sheet.
/// </summary>
public sealed class ExcelSeedService : IExcelSeedService
{
    private readonly IDataBaseService _db;
    private readonly IFxRateService _fx;
    private readonly ILogger<ExcelSeedService> _logger;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;

    public ExcelSeedService(IDataBaseService db, IFxRateService fx, ILogger<ExcelSeedService> logger,
        IHostEnvironment env, IConfiguration config)
    {
        _db = db;
        _fx = fx;
        _logger = logger;
        _env = env;
        _config = config;
    }

    public async Task SeedAllAsync(Stream workbook, CancellationToken ct = default)
    {
        var ds = ReadAllSheets(workbook);
        await SeedStocksAsync(ds, ct).ConfigureAwait(false);
        await SeedListingsAsync(ds, ct).ConfigureAwait(false);
        await SeedUsersAsync(ds, ct).ConfigureAwait(false);
        await SeedAIProfilesAsync(ds, ct).ConfigureAwait(false);
        await SeedHoldingsAsync(ds, ct).ConfigureAwait(false);
    }

    public Task SeedKindAsync(string kind, Stream workbook, CancellationToken ct = default)
    {
        var ds = ReadAllSheets(workbook);
        return kind.ToLowerInvariant() switch
        {
            "stocks"      => SeedStocksAsync(ds, ct),
            "listings"    => SeedListingsAsync(ds, ct),
            "users"       => SeedUsersAsync(ds, ct),
            "ai-profiles" => SeedAIProfilesAsync(ds, ct),
            "holdings"    => SeedHoldingsAsync(ds, ct),
            _ => throw new ArgumentException(
                $"Unknown seed kind '{kind}'. Expected one of: stocks, listings, users, ai-profiles, holdings.",
                nameof(kind)),
        };
    }

    public async Task<bool> SeedAllFromEmbeddedAsync(CancellationToken ct = default)
    {
        var path = EmbeddedWorkbookPath();
        if (!File.Exists(path))
        {
            _logger.LogWarning("Embedded workbook not found at {Path}; skipping seed.", path);
            return false;
        }
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read);
        await SeedAllAsync(stream, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> IsDatabaseEmptyAsync(CancellationToken ct = default)
        => (await _db.GetStocksAsync(ct).ConfigureAwait(false)).Count == 0;

    private string EmbeddedWorkbookPath()
    {
        var rel = _config["Seed:EmbeddedWorkbookPath"] ?? "Resources/Raw/AIUserData.xlsx";
        return Path.Combine(_env.ContentRootPath, rel);
    }

    #region Seed steps (ported verbatim from the client ExcelImportService)

    private async Task SeedStocksAsync(DataSet ds, CancellationToken ct)
    {
        var stockTable = RequireSheet(ds, "Stocks");

        // Prices live on the Listings sheet; SeedListings owns all StockPrice rows.
        List<Stock> stocks = new();
        foreach (DataRow row in stockTable.Rows)
        {
            if (!ParsingHelper.TryToInt(row["StockId"].ToString(), out var stockId))
            {
                _logger.LogWarning("Invalid Stock ID: '{StockIdString}'.", row["StockId"]);
                continue;
            }

            var symbol = row["Ticker"]?.ToString() ?? string.Empty;
            var companyName = row["CompanyName"]?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(symbol) || string.IsNullOrWhiteSpace(companyName))
            {
                _logger.LogWarning("Skipping stock #{StockId} with missing required fields", stockId);
                continue;
            }

            Stock stock = new Stock { StockId = stockId, Symbol = symbol, CompanyName = companyName };
            if (!stock.IsValid())
            {
                _logger.LogWarning("Failed to register stock #{StockId}: {Symbol}.", stockId, symbol);
                continue;
            }

            stocks.Add(stock);
        }

        await _db.RunInTransactionAsync(async c =>
        {
            await _db.ResetTableAsync<Stock>(c);
            await _db.InsertAllAsync(stocks, c);
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Loaded in total {StockCount} stocks.", stocks.Count);
    }

    private async Task SeedListingsAsync(DataSet ds, CancellationToken ct)
    {
        var usdPrices = await ReadUsdSeedPricesAsync(ct).ConfigureAwait(false);

        var listingTable = RequireSheet(ds, "Listings");
        IReadOnlyList<StockListing> listings = ReadListingsFromSheet(listingTable, usdPrices);

        var prices = listings
            .Where(l => l.SeedPrice > 0m)
            .Select(l => new StockPrice { StockId = l.StockId, CurrencyType = l.CurrencyType, Price = l.SeedPrice })
            .Where(sp => sp.IsValid())
            .ToList();

        await _db.RunInTransactionAsync(async c =>
        {
            await _db.ResetTableAsync<StockListing>(c);
            await _db.ResetTableAsync<StockPrice>(c);
            foreach (var listing in listings)
                if (listing.IsValid())
                    await _db.CreateStockListing(listing, c).ConfigureAwait(false);
            await _db.InsertAllAsync(prices, c);
        }, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Loaded {Listings} listings across {Currencies} currencies, with {Prices} initial prices.",
            listings.Count,
            listings.Select(l => l.CurrencyType).Distinct().Count(),
            prices.Count);
    }

    private async Task<Dictionary<int, decimal>> ReadUsdSeedPricesAsync(CancellationToken ct)
    {
        var rows = await _db.GetStockPricesAsync(ct).ConfigureAwait(false);
        var dict = new Dictionary<int, decimal>(rows.Count);
        foreach (var sp in rows)
            dict[sp.StockId] = sp.Price; // last write wins; expected one row per stock
        return dict;
    }

    private IReadOnlyList<StockListing> ReadListingsFromSheet(
        DataTable sheet, IReadOnlyDictionary<int, decimal> usdPrices)
    {
        // FX rate comes from the live IFxRateService (AR(1) walker around 1.08).
        // GetMidRate(USD,EUR) returns the EUR-per-USD multiplier the fallback needs.
        var eurPerUsd = _fx.GetMidRate(CurrencyType.USD, CurrencyType.EUR);
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

            if (!ParsingHelper.TryToDecimal(row["SeedPrice"]?.ToString(), out var seedPrice) || seedPrice <= 0m)
            {
                usdPrices.TryGetValue(stockId, out var usd);
                seedPrice = ccy == CurrencyType.EUR
                    ? CurrencyHelper.RoundMoney(usd * eurPerUsd, CurrencyType.EUR)
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

    private async Task SeedUsersAsync(DataSet ds, CancellationToken ct)
    {
        var identityTable = RequireSheet(ds, "Identity");

        List<User> users = new();
        foreach (DataRow row in identityTable.Rows)
        {
            if (!ParsingHelper.TryToInt(row["UserId"].ToString(), out var userId))
            {
                _logger.LogWarning("Invalid User ID: '{UserIdString}'.", row["UserId"]);
                continue;
            }

            var username = row["Username"]?.ToString() ?? string.Empty;
            var email = row["Email"]?.ToString() ?? string.Empty;
            var password = "hallo123";
            var fullName = row["FullName"]?.ToString() ?? string.Empty;
            DateTime? birthdate = row["Birthdate"] is DateTime d ? d
                : DateTime.TryParse(row["Birthdate"]?.ToString(), out var bd) ? bd : null;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email)
                 || string.IsNullOrWhiteSpace(fullName) || !birthdate.HasValue)
            {
                _logger.LogWarning("Skipping user #{UserId} with missing required fields", userId);
                continue;
            }

            bool isAdmin = false;
            if (identityTable.Columns.Contains("IsAdmin"))
            {
                var raw = row["IsAdmin"]?.ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (bool.TryParse(raw, out var bv)) isAdmin = bv;
                    else if (int.TryParse(raw, out var iv)) isAdmin = iv != 0;
                }
            }

            User user = new User
            {
                UserId = userId, Username = username, Email = email,
                PasswordHash = SecurityHelper.HashPassword(password),
                FullName = fullName, BirthDate = birthdate, IsAdmin = isAdmin
            };
            if (!user.IsValid())
            {
                _logger.LogWarning("Failed to register user #{UserId}: {Username}.", userId, username);
                continue;
            }
            users.Add(user);
        }

        await _db.RunInTransactionAsync(async c =>
        {
            await _db.ResetTableAsync<Order>(c);
            await _db.ResetTableAsync<Transaction>(c);
            await _db.ResetTableAsync<User>(c);
            await _db.InsertAllAsync(users, c);
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Loaded in total {UserCount} users", users.Count);
    }

    private async Task SeedAIProfilesAsync(DataSet ds, CancellationToken ct)
    {
        var profileTable = RequireSheet(ds, "Profile");

        List<AIUser> aiUsers = new();
        foreach (DataRow row in profileTable.Rows)
        {
            if (!ParsingHelper.TryToInt(row["UserId"].ToString(), out var userId))
            {
                _logger.LogWarning("Invalid User ID: '{UserIdString}'.", row["UserId"]);
                continue;
            }

            if (!ParsingHelper.TryToInt(row["Seed"].ToString(), out var seed) ||
                !ParsingHelper.TryToInt(row["DecisionIntervalSeconds"].ToString(), out var intervalSeconds) ||
                !ParsingHelper.TryToInt(row["MaxOpenOrders"].ToString(), out var maxOpenOrders) ||
                !ParsingHelper.TryToInt(row["Strategy"].ToString(), out var strategyCode))
            {
                _logger.LogWarning("Invalid integer field for AI user #{UserId}.", userId);
                continue;
            }

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

            string homeCurrency = "USD";
            if (profileTable.Columns.Contains("HomeCurrency"))
            {
                var raw = row["HomeCurrency"]?.ToString();
                if (!string.IsNullOrWhiteSpace(raw) && CurrencyHelper.IsSupported(raw))
                    homeCurrency = raw.Trim().ToUpperInvariant();
            }

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
                    AggressivenessPrc = aggressivenessPrc,
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

        await _db.RunInTransactionAsync(async c =>
        {
            await _db.ResetTableAsync<AIUser>(c);
            await _db.InsertAllAsync(aiUsers, c);
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Loaded in total {AiUserCount} AI user profiles.", aiUsers.Count);
    }

    private async Task SeedHoldingsAsync(DataSet ds, CancellationToken ct)
    {
        var stockTable = RequireSheet(ds, "Stocks");
        var holdingTable = RequireSheet(ds, "Holding");
        int stockCount = stockTable.Rows.Count;

        var profiles = await _db.GetAIUsersAsync(ct).ConfigureAwait(false);
        var homeCurrencyByUserId = profiles.ToDictionary(p => p.UserId, p => p.HomeCurrencyType);

        List<Fund> funds = new();
        List<Position> positions = new();
        foreach (DataRow row in holdingTable.Rows)
        {
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
                if (!ParsingHelper.TryToInt(row[i + 2].ToString(), out stocks[i]))
                {
                    _logger.LogWarning("Invalid Stock{i} for User ID {UserId}.", i, userId);
                    stocks[i] = 0;
                }
            }

            var homeCcy = homeCurrencyByUserId.TryGetValue(userId, out var ccy) ? ccy : CurrencyType.USD;
            var balanceInCcy = CurrencyHelper.RoundMoney(balance, homeCcy);
            if (balanceInCcy > 0m)
            {
                var fund = new Fund { UserId = userId, TotalBalance = balanceInCcy, CurrencyType = homeCcy };
                if (fund.IsValid())
                    funds.Add(fund);
                else
                    _logger.LogWarning("Failed to register {Currency} fund for user #{UserId}.", homeCcy, userId);
            }

            for (int i = 1; i <= stockCount; i++)
            {
                Position position = new Position { UserId = userId, StockId = i, Quantity = stocks[i - 1] };
                if (!position.IsValid())
                {
                    _logger.LogWarning("Failed to register position for user #{UserId}: Stock {StockId} qty {Quantity}.",
                        userId, i, stocks[i - 1]);
                    continue;
                }
                positions.Add(position);
            }
        }

        await _db.RunInTransactionAsync(async c =>
        {
            await _db.ResetTableAsync<Order>(c);
            await _db.ResetTableAsync<Transaction>(c);
            await _db.ResetTableAsync<Position>(c);
            await _db.ResetTableAsync<Fund>(c);
            await _db.InsertAllAsync(funds, c);
            await _db.InsertAllAsync(positions, c);
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Loaded in total {FundCount} funds with {PositionCount} positions.",
            funds.Count, positions.Count);
    }

    #endregion

    #region ClosedXML reader

    /// <summary>
    /// Reads every worksheet into a header-keyed DataTable so the seed methods
    /// can address cells by column name (row["StockId"]) and by index
    /// (row[i+2]) exactly as the original ExcelDataReader-backed code did.
    /// Date cells are stored as DateTime; everything else as string.
    /// </summary>
    private static DataSet ReadAllSheets(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ds = new DataSet();
        foreach (var ws in wb.Worksheets)
        {
            var dt = new DataTable(ws.Name);
            bool headerDone = false;
            int colCount = 0;
            foreach (var row in ws.RowsUsed())
            {
                if (!headerDone)
                {
                    foreach (var cell in row.CellsUsed())
                        dt.Columns.Add(cell.GetString().Trim(), typeof(object));
                    colCount = dt.Columns.Count;
                    headerDone = true;
                    continue;
                }

                var values = new object?[colCount];
                for (int c = 1; c <= colCount; c++)
                {
                    var cell = row.Cell(c); // IXLRow.Cell(n) is absolute column n
                    values[c - 1] = cell.DataType == XLDataType.DateTime
                        ? cell.GetDateTime()
                        : cell.GetString();
                }
                dt.Rows.Add(values);
            }
            ds.Tables.Add(dt);
        }
        return ds;
    }

    private static DataTable RequireSheet(DataSet ds, string sheetName)
    {
        var table = ds.Tables[sheetName];
        if (table is null)
            throw new InvalidDataException(
                $"Excel sheet '{sheetName}' is missing. Found sheets: " +
                $"[{string.Join(", ", ds.Tables.Cast<DataTable>().Select(t => t.TableName))}].");
        return table;
    }

    #endregion
}
