using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Helpers;

/// <summary>
/// Fall-back seed for the StockListings table when an older xlsx file
/// doesn't yet ship a Listings sheet. Mirrors the constants in
/// <c>Tools/Config.py</c> (CROSS_LISTED_STOCK_IDS / EUR_ONLY_STOCK_IDS):
/// 20 cross-listed (USD + EUR), 20 USD-only, 10 EUR-only = 70 rows for
/// 50 distinct StockIds.
/// </summary>
internal static class StockListingSeed
{
    // EUR/USD reference used to convert the USD seed price into an EUR
    // seed price for the EUR side of a cross-listed pair. Matches
    // Tools/Config.py::FX_BASE_RATES["EUR/USD"].
    internal const decimal EurPerUsd = 1m / 1.08m;

    // Stocks that trade on both USD and EUR books. Cap-diverse: ~5 each
    // from id ranges 1-10, 11-20, 21-30, 31-50.
    internal static readonly IReadOnlyList<int> CrossListedStockIds = new[]
    {
        1, 3, 4, 5, 6, 7, 8, 9, 14, 16, 20,
        23, 25, 28, 32, 33, 36, 38, 42, 45,
    };

    // Stocks that trade only on the EUR book. Replaced the 10 entries in
    // Tools/Config.py::STOCKS with European-domiciled tickers.
    internal static readonly IReadOnlyList<int> EurOnlyStockIds = new[]
    {
        10, 19, 27, 37, 44, 46, 47, 48, 49, 50,
    };

    /// <summary>
    /// Build listing rows for the given stocks. <paramref name="usdSeedPriceByStockId"/>
    /// is the price column from the Stocks sheet (always denominated in
    /// USD). Cross-listed stocks emit a USD primary row + an EUR row at
    /// <c>usd / FX_BASE_RATE</c>; USD-only stocks emit one USD row;
    /// EUR-only stocks emit one EUR row whose seed price is the USD
    /// number converted to EUR.
    /// </summary>
    internal static IReadOnlyList<StockListing> BuildFor(
        IReadOnlyDictionary<int, decimal> usdSeedPriceByStockId)
    {
        var crossSet = new HashSet<int>(CrossListedStockIds);
        var eurOnlySet = new HashSet<int>(EurOnlyStockIds);

        var rows = new List<StockListing>();
        foreach (var (stockId, usdPrice) in usdSeedPriceByStockId)
        {
            if (crossSet.Contains(stockId))
            {
                rows.Add(new StockListing
                {
                    StockId = stockId, CurrencyType = CurrencyType.USD,
                    IsPrimary = true,
                    SeedPrice = CurrencyHelper.RoundMoney(usdPrice, CurrencyType.USD),
                });
                rows.Add(new StockListing
                {
                    StockId = stockId, CurrencyType = CurrencyType.EUR,
                    IsPrimary = false,
                    SeedPrice = CurrencyHelper.RoundMoney(usdPrice * EurPerUsd, CurrencyType.EUR),
                });
            }
            else if (eurOnlySet.Contains(stockId))
            {
                rows.Add(new StockListing
                {
                    StockId = stockId, CurrencyType = CurrencyType.EUR,
                    IsPrimary = true,
                    SeedPrice = CurrencyHelper.RoundMoney(usdPrice * EurPerUsd, CurrencyType.EUR),
                });
            }
            else
            {
                rows.Add(new StockListing
                {
                    StockId = stockId, CurrencyType = CurrencyType.USD,
                    IsPrimary = true,
                    SeedPrice = CurrencyHelper.RoundMoney(usdPrice, CurrencyType.USD),
                });
            }
        }
        return rows;
    }
}
