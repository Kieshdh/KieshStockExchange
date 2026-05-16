using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using System.Collections.Concurrent;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Plain data container shared by AiBotStateService and AiBotDecisionService.
/// Fund/Position state is read live from AccountsCache (the single source of truth);
/// CurrenciesByUser / StocksByUser are lightweight metadata for per-user iteration.
/// Price caches use ConcurrentDictionary because OnQuoteUpdated fires on external threads.
/// </summary>
internal sealed class AiBotContext
{
    #region Services and Constructor
    private readonly IAccountsCache _accounts;

    internal AiBotContext(IAccountsCache accounts)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }
    #endregion

    #region State
    internal readonly Dictionary<int, AIUser>  AiUsersByAiUserId = new();
    internal readonly Dictionary<int, AIUser>  AiUsersByUserId   = new();
    internal readonly Dictionary<int, Random>  AiUserRngs        = new();

    // Metadata indexes: which currencies / stocks each user has. Rebuilt every 60s
    // by RefreshAssetsAsync. Actual Fund/Position instances come from _accounts on
    // each access — no shadow copy, no drift.
    internal readonly Dictionary<int, HashSet<CurrencyType>> CurrenciesByUser = new();
    internal readonly Dictionary<int, HashSet<int>>          StocksByUser     = new();

    internal readonly Dictionary<int, Dictionary<int, Order>> OpenOrders = new();

    // Three-stage price cache: StockPrices is the raw last quote, PreviousPrices
    // is the prior raw value for tick-to-tick deltas, SmoothedPrices is EWMA
    // (α=0.15, ~6-tick window) that ChooseOrderType reads to dampen spike noise.
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal> StockPrices    = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal> PreviousPrices = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal> SmoothedPrices = new();

    internal readonly Dictionary<int, DateTime> BurstEndTimes  = new();
    // Reset by CheckDailyRefresh so a tx that fills across the day boundary isn't double-counted.
    internal readonly HashSet<int>              ProcessedTxIds = new();

    internal DateOnly LastRefreshDate = DateOnly.MinValue;

    // Empty placeholders let callers keep reading .TotalBalance / .Quantity
    // without null checks. Safe to share since the bot reads them as immutable.
    private static readonly Fund     EmptyFund     = new();
    private static readonly Position EmptyPosition = new();
    #endregion

    #region Accessors
    internal Fund GetFund(int userId, CurrencyType currency)
        => _accounts.GetFund(userId, currency) ?? EmptyFund;

    internal Position GetPosition(int userId, int stockId)
        => _accounts.GetPosition(userId, stockId) ?? EmptyPosition;

    internal Random GetRandom(int aiUserId)
    {
        if (!AiUserRngs.ContainsKey(aiUserId))
        {
            if (!AiUsersByAiUserId.TryGetValue(aiUserId, out var ai))
                throw new KeyNotFoundException($"AIUser not found for aiUserId {aiUserId}");
            AiUserRngs[aiUserId] = new Random(DailySeed(ai.Seed, ai.AiUserId, TimeHelper.Today()));
        }
        return AiUserRngs[aiUserId];
    }

    internal decimal Decimal01(int aiUserId) => (decimal)GetRandom(aiUserId).NextDouble();

    internal static int DailySeed(int baseSeed, int userId, DateOnly date)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + baseSeed;
            h = h * 31 + userId;
            h = h * 31 + date.Year;
            h = h * 31 + date.Month;
            h = h * 31 + date.Day;
            return h & int.MaxValue;
        }
    }
    #endregion

    #region Financial Computations
    internal decimal PortfolioValueByCurrency(int userId, CurrencyType currency)
    {
        decimal total = GetFund(userId, currency).TotalBalance;
        if (!StocksByUser.TryGetValue(userId, out var stocks)) return total;
        foreach (var stockId in stocks)
        {
            var pos = _accounts.GetPosition(userId, stockId);
            if (pos is null || pos.Quantity <= 0) continue;
            if (StockPrices.TryGetValue((stockId, currency), out var price))
                total += pos.Quantity * price;
        }
        return total;
    }

    internal decimal FundsPercentagePortfolio(int userId, CurrencyType currency)
    {
        var freeCash = GetFund(userId, currency).AvailableBalance;
        var total = PortfolioValueByCurrency(userId, currency);
        if (total <= 0m) return freeCash > 0m ? 1m : 0m;
        return Clamp01(freeCash / total);
    }

    // Uses SmoothedPrices (EWMA) vs PreviousPrices to dampen noise from single large quotes.
    internal decimal ComputeWatchlistMomentum(AIUser user, CurrencyType currency)
    {
        var watch = user.Watchlist;
        if (watch == null || watch.Count == 0) return 0m;

        decimal total = 0m;
        int count = 0;
        foreach (var stockId in watch)
        {
            var key = (stockId, currency);
            if (SmoothedPrices.TryGetValue(key, out var curr) && curr > 0m &&
                PreviousPrices.TryGetValue(key, out var prev) && prev > 0m)
            {
                total += (curr - prev) / prev;
                count++;
            }
        }
        return count > 0 ? total / count : 0m;
    }
    #endregion

    #region Helpers
    internal void ClearAll()
    {
        AiUsersByAiUserId.Clear();
        AiUsersByUserId.Clear();
        AiUserRngs.Clear();
        CurrenciesByUser.Clear();
        StocksByUser.Clear();
        OpenOrders.Clear();
        StockPrices.Clear();
        PreviousPrices.Clear();
        SmoothedPrices.Clear();
        BurstEndTimes.Clear();
        ProcessedTxIds.Clear();
        LastRefreshDate = DateOnly.MinValue;
    }

    private static decimal Clamp01(decimal x) => x < 0m ? 0m : x > 1m ? 1m : x;
    #endregion
}
