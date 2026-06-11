using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using System.Collections.Concurrent;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Plain data container shared by AiBotStateService and AiBotDecisionService.
/// Fund/Position state is read live from AccountsCache (the single source of truth);
/// StocksByUser is lightweight metadata for per-user iteration.
/// Price caches use ConcurrentDictionary because OnQuoteUpdated fires on external threads.
/// </summary>
internal sealed class AiBotContext
{
    #region Services and Constructor
    private readonly IAccountsCache _accounts;
    private readonly bool _personalSentiment;

    internal AiBotContext(IAccountsCache accounts, bool personalSentiment = true)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _personalSentiment = personalSentiment;
    }
    #endregion

    #region State
    internal readonly Dictionary<int, AIUser>  AiUsersByAiUserId = new();
    internal readonly Dictionary<int, AIUser>  AiUsersByUserId   = new();
    internal readonly Dictionary<int, Random>  AiUserRngs        = new();

    // Metadata index: which stocks each user has a position in. Rebuilt every 60s
    // by RefreshAssetsAsync. Actual Fund/Position instances come from _accounts on
    // each access — no shadow copy, no drift.
    internal readonly Dictionary<int, HashSet<int>>          StocksByUser     = new();

    internal readonly Dictionary<int, Dictionary<int, Order>> OpenOrders = new();

    // Three-stage price cache: StockPrices is the raw last quote, PreviousPrices
    // is the prior raw value for tick-to-tick deltas, SmoothedPrices is EWMA
    // (α=0.15, ~6-tick window) that ChooseOrderType reads to dampen spike noise.
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal> StockPrices    = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal> PreviousPrices = new();
    internal readonly ConcurrentDictionary<(int, CurrencyType), decimal> SmoothedPrices = new();

    internal readonly Dictionary<int, DateTime> BurstEndTimes  = new();

    // §A1 inertia: per-bot directional STANCE (dir +1 buy / -1 sell) that persists until `until`, so a
    // bot's order flow is one-directional over the stance window instead of re-rolling buy/sell every
    // tick. Keyed by aiUserId, mirroring BurstEndTimes. Empty (and never touched) when the flag is off.
    internal readonly Dictionary<int, (sbyte dir, DateTime until)> Stances = new();

    // Reset by CheckDailyRefresh so a tx that fills across the day boundary isn't double-counted.
    internal readonly HashSet<int>              ProcessedTxIds = new();

    internal DateOnly LastRefreshDate = DateOnly.MinValue;

    // Empty placeholders let callers keep reading .TotalBalance / .Quantity
    // without null checks. Safe to share since the bot reads them as immutable.
    private static readonly Fund     EmptyFund     = new();
    private static readonly Position EmptyPosition = new();
    #endregion

    #region Per-tick memoization caches (patch 0001)
    // Cleared at the top of each tick by AiTradeService.CollectPendingOrdersAsync via ClearTickCaches.
    // Plain Dictionary (not ConcurrentDictionary) because they're touched only by the bot-loop
    // thread — OnQuoteUpdated writes only to StockPrices/PreviousPrices/SmoothedPrices.
    internal long TickId;
    internal readonly Dictionary<(int, CurrencyType), bool>     OverBandBuyCache  = new();
    internal readonly Dictionary<(int, CurrencyType), bool>     OverBandSellCache = new();
    internal readonly Dictionary<(int, CurrencyType), decimal>  FundamentalCache  = new();
    internal readonly Dictionary<(int, CurrencyType), decimal>  SeedPriceCache    = new();
    internal readonly Dictionary<(int, CurrencyType), decimal?> MidPriceCache     = new();
    internal readonly Dictionary<int, AiBotDecisionService.CommittedTotals> CommittedCache = new();
    internal readonly Dictionary<(int userId, CurrencyType), decimal> WatchlistMomentumCache    = new();
    internal readonly Dictionary<(int userId, CurrencyType), decimal> WatchlistSentimentCache   = new();
    internal readonly Dictionary<(int userId, CurrencyType), decimal> WatchlistValueGapCache    = new();
    internal readonly Dictionary<(int userId, CurrencyType), decimal> WatchlistRecentGapCache   = new();
    internal readonly Dictionary<int, decimal> WatchlistSharedSentimentCache = new();
    internal readonly Dictionary<(int userId, bool fast), decimal> WatchlistSlopeCache = new();

    internal void ClearTickCaches()
    {
        OverBandBuyCache.Clear(); OverBandSellCache.Clear();
        FundamentalCache.Clear(); SeedPriceCache.Clear(); MidPriceCache.Clear();
        CommittedCache.Clear();
        WatchlistMomentumCache.Clear(); WatchlistSentimentCache.Clear();
        WatchlistValueGapCache.Clear(); WatchlistRecentGapCache.Clear();
        WatchlistSharedSentimentCache.Clear(); WatchlistSlopeCache.Clear();
    }
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

    #region Inertia stance (§A1)
    /// <summary>
    /// §A1 inertia: return the bot's persistent directional stance, rolling a fresh one when none is active
    /// or the current one has expired. While a stance holds (<paramref name="now"/> &lt; until) this is a pure
    /// dictionary read — NO RNG draw. On a (re)roll it consumes EXACTLY two seeded draws in a fixed order —
    /// first the direction (from the current <paramref name="buyProb"/>), then the duration — so a flag-on run
    /// is reproducible. Callers invoke this ONLY when the inertia flag is on, so the flag-off draw sequence is
    /// byte-identical to before. Returns +1 (buy) or -1 (sell).
    /// </summary>
    internal sbyte RollOrHoldStance(int aiUserId, decimal buyProb, DateTime now, double minSec, double maxSec)
    {
        if (Stances.TryGetValue(aiUserId, out var st) && now < st.until)
            return st.dir;

        sbyte dir = Decimal01(aiUserId) < buyProb ? (sbyte)1 : (sbyte)-1;   // draw 1: side
        double lo = Math.Min(minSec, maxSec);
        double hi = Math.Max(minSec, maxSec);
        double secs = lo + (hi - lo) * (double)Decimal01(aiUserId);         // draw 2: duration
        Stances[aiUserId] = (dir, now + TimeSpan.FromSeconds(secs));
        return dir;
    }
    #endregion

    #region Personal sentiment
    // Idiosyncratic per-(bot,stock) mood added on top of the shared market sentiment.
    // Stateless and pure (hash-based) so it scales to all bots × watchlists with no
    // stored AR(1) matrix and no per-tick reroll loop.
    private const decimal PersonalLeanAmp     = 0.10m;  // fixed disposition
    private const decimal PersonalDriftAmp    = 0.10m;  // slow time-varying drift
    private const decimal PersonalReactAmp    = 0.12m;  // price-reaction weight
    private const decimal ReturnGain          = 20m;    // ±5% recent move → ±1
    private static readonly TimeSpan PersonalDriftPeriod = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Per-bot idiosyncratic sentiment for one stock: a fixed lean, a slow drift, and
    /// a price-reaction whose sign follows the bot's strategy (contrarian buys dips,
    /// momentum/panic follow the move). Returns 0 when the feature is disabled.
    /// </summary>
    internal decimal PersonalSentiment(AIUser user, int stockId, CurrencyType currency)
    {
        if (!_personalSentiment) return 0m;

        long bucket = TimeHelper.NowUtc().Ticks / PersonalDriftPeriod.Ticks;
        var lean  = HashUnit(user.Seed, user.AiUserId, stockId, 0);
        var drift = HashUnit(user.Seed, user.AiUserId, stockId, bucket);

        decimal react = 0m;
        var sign = PriceReactionSign(user.Strategy);
        if (sign != 0m &&
            SmoothedPrices.TryGetValue((stockId, currency), out var cur) && cur > 0m &&
            PreviousPrices.TryGetValue((stockId, currency), out var prev) && prev > 0m)
        {
            var ret = (cur - prev) / prev;
            react = sign * ClampSigned(ret * ReturnGain, 1m);
        }

        return PersonalLeanAmp * lean + PersonalDriftAmp * drift + PersonalReactAmp * react;
    }

    // Pure hash → [-1, +1]. Same mix as DailySeed, extended with stockId + bucket.
    // Does NOT advance any Random, so it's call-order-independent and reproducible.
    private static decimal HashUnit(int baseSeed, int userId, int stockId, long bucket)
    {
        unchecked
        {
            long h = 17;
            h = h * 31 + baseSeed;
            h = h * 31 + userId;
            h = h * 31 + stockId;
            h = h * 31 + bucket;
            // Final avalanche so adjacent buckets/stocks don't correlate.
            h ^= h >> 33; h *= unchecked((long)0xff51afd7ed558ccd); h ^= h >> 33;
            // Map the low 32 bits to [-1, +1].
            var u = (double)((ulong)h & 0xFFFFFFFF) / 0xFFFFFFFF;  // [0,1]
            return (decimal)(u * 2.0 - 1.0);
        }
    }

    // Sign of the price-reaction, from the deterministic default extreme-reaction
    // style for the strategy (NOT PickExtremeReactionStyle — that advances RNG).
    // +1 = follow price (FOMO/Panic), -1 = fade price (Contrarian), 0 = none.
    private static decimal PriceReactionSign(AiStrategy strategy) => strategy switch
    {
        AiStrategy.TrendFollower => 1m,
        AiStrategy.Scalper       => 1m,
        AiStrategy.MeanReversion => -1m,
        AiStrategy.MarketMaker   => -1m,
        _                        => 0m,
    };

    private static decimal ClampSigned(decimal x, decimal mag) =>
        x < -mag ? -mag : x > mag ? mag : x;
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
                total += CurrencyHelper.Notional(price, pos.Quantity, currency);
        }
        return total;
    }

    internal decimal FundsPercentagePortfolio(int userId, CurrencyType currency)
    {
        // Net-worth ratio used by the cash-reserve target. Uses TotalBalance —
        // open buy reservations haven't actually deployed cash yet, so a bot
        // with working bids shouldn't read as cash-poor here. Operational
        // liquidity is enforced via AvailableBalance in ComputeOrderQuantityAsync.
        var cash  = GetFund(userId, currency).TotalBalance;
        var total = PortfolioValueByCurrency(userId, currency);
        if (total <= 0m) return cash > 0m ? 1m : 0m;
        return Clamp01(cash / total);
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
        StocksByUser.Clear();
        OpenOrders.Clear();
        StockPrices.Clear();
        PreviousPrices.Clear();
        SmoothedPrices.Clear();
        BurstEndTimes.Clear();
        Stances.Clear();
        ProcessedTxIds.Clear();
        LastRefreshDate = DateOnly.MinValue;
        // §patch 0001: per-tick caches also cleared on full reset.
        ClearTickCaches();
        TickId = 0;
    }

    private static decimal Clamp01(decimal x) => x < 0m ? 0m : x > 1m ? 1m : x;
    #endregion
}
