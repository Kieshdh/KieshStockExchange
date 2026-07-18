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

            // §3.6 P6: per-bot advanced-order probabilities. Read defensively so an older workbook
            // without these columns still loads (defaults to 0 = no advanced orders for that bot).
            decimal ReadProb(string col)
                => profileTable.Columns.Contains(col)
                    && ParsingHelper.TryToDecimal(row[col]?.ToString(), out var v) ? v : 0m;
            var stopProb = ReadProb("StopProb");
            var trailingProb = ReadProb("TrailingProb");
            var shortProb = ReadProb("ShortProb");
            var longBracketProb = ReadProb("LongBracketProb");
            var shortBracketProb = ReadProb("ShortBracketProb");

            // §P6 balancing: tiered-limit bands, protective-stop distance band, Far-order budget.
            // Read defensively (same as the probs) so an older workbook without these columns still loads.
            var midLimitMinPrc = ReadProb("MidLimitMinPrc");
            var midLimitMaxPrc = ReadProb("MidLimitMaxPrc");
            var farLimitMinPrc = ReadProb("FarLimitMinPrc");
            var farLimitMaxPrc = ReadProb("FarLimitMaxPrc");
            var stopDistanceMinPrc = ReadProb("StopDistanceMinPrc");
            var stopDistanceMaxPrc = ReadProb("StopDistanceMaxPrc");
            var farBudgetPrc = ReadProb("FarBudgetPrc");
            var tpOffsetMinPrc = ReadProb("TpOffsetMinPrc");
            var tpOffsetMaxPrc = ReadProb("TpOffsetMaxPrc");

            // Sentiment-dynamics §: per-bot lateness L (0 when absent from an older workbook).
            var lateness = ReadProb("Lateness");

            // §3.7 arbitrage-cohort params. Same defensive read so an older workbook without these
            // columns still loads (0 = inert: a non-arbitrage bot ignores them entirely).
            int ReadIntCol(string col)
                => profileTable.Columns.Contains(col)
                    && ParsingHelper.TryToInt(row[col]?.ToString(), out var v) ? v : 0;
            var minArbitrageRatePrc = ReadProb("MinArbitrageRatePrc");
            var maxInventoryPerStock = ReadIntCol("MaxInventoryPerStock");
            var conversionCadenceSeconds = ReadIntCol("ConversionCadenceSeconds");

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
                    StopProb = stopProb, TrailingProb = trailingProb, ShortProb = shortProb,
                    LongBracketProb = longBracketProb, ShortBracketProb = shortBracketProb,
                    MidLimitMinPrc = midLimitMinPrc, MidLimitMaxPrc = midLimitMaxPrc,
                    FarLimitMinPrc = farLimitMinPrc, FarLimitMaxPrc = farLimitMaxPrc,
                    StopDistanceMinPrc = stopDistanceMinPrc, StopDistanceMaxPrc = stopDistanceMaxPrc,
                    FarBudgetPrc = farBudgetPrc,
                    TpOffsetMinPrc = tpOffsetMinPrc, TpOffsetMaxPrc = tpOffsetMaxPrc,
                    Lateness = lateness,
                    MinArbitrageRatePrc = minArbitrageRatePrc,
                    MaxInventoryPerStock = maxInventoryPerStock,
                    ConversionCadenceSeconds = conversionCadenceSeconds,
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
}
