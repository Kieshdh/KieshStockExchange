using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Helpers;

/// <summary> Fall-back StockListings seed when the xlsx has no Listings sheet. </summary>
/// <remarks> Mirrors <c>Tools/Config.py</c> CROSS_LISTED_STOCK_IDS / EUR_ONLY_STOCK_IDS. </remarks>
internal static class StockListingSeed
{
    // Mirrors Tools/Config.py::FX_BASE_RATES["EUR/USD"].
    internal const decimal EurPerUsd = 1m / 1.08m;

    internal static readonly IReadOnlyList<int> CrossListedStockIds = new[]
    {
        1, 3, 4, 5, 6, 7, 8, 9, 14, 16, 20,
        23, 25, 28, 32, 33, 36, 38, 42, 45,
    };

    internal static readonly IReadOnlyList<int> EurOnlyStockIds = new[]
    {
        10, 19, 27, 37, 44, 46, 47, 48, 49, 50,
    };

    /// <summary> Build listing rows for the given stocks (USD seed prices in). </summary>
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
