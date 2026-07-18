using System.Data;
using System.Globalization;
using ClosedXML.Excel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.SeedServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Server.Services.SeedServices;

public sealed partial class ExcelSeedService
{
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
            // §sector: defensive — old workbooks predate the column ⇒ "" ⇒ Sector.Unknown ⇒ modulo fallback.
            var sector = stockTable.Columns.Contains("Sector") ? row["Sector"]?.ToString() ?? string.Empty : string.Empty;

            if (string.IsNullOrEmpty(symbol) || string.IsNullOrWhiteSpace(companyName))
            {
                _logger.LogWarning("Skipping stock #{StockId} with missing required fields", stockId);
                continue;
            }

            Stock stock = new Stock { StockId = stockId, Symbol = symbol, CompanyName = companyName, Sector = sector };
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

            // §3.7 dual-currency seed: an optional trailing "BalanceSecondary" column funds the
            // NON-home currency (the other supported currency). Used by the platform house account
            // and the arbitrage cohort so they start armed in both USD and EUR. Read defensively so
            // an older workbook without the column still loads (absent / 0 ⇒ no second fund).
            if (holdingTable.Columns.Contains("BalanceSecondary")
                && ParsingHelper.TryToDecimal(row["BalanceSecondary"]?.ToString(), out var secondaryBalance)
                && secondaryBalance > 0m)
            {
                var secondaryCcy = homeCcy == CurrencyType.USD ? CurrencyType.EUR : CurrencyType.USD;
                var secondaryInCcy = CurrencyHelper.RoundMoney(secondaryBalance, secondaryCcy);
                if (secondaryInCcy > 0m)
                {
                    var fund = new Fund { UserId = userId, TotalBalance = secondaryInCcy, CurrencyType = secondaryCcy };
                    if (fund.IsValid())
                        funds.Add(fund);
                    else
                        _logger.LogWarning("Failed to register secondary {Currency} fund for user #{UserId}.", secondaryCcy, userId);
                }
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
}
