using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

// MarketMaker (0) is the legacy taker-side label whose bots run the normal decision path with the
// fair-weather quoting in AiBotDecisionService (Bots:MarketMakerQuoting). MarketMakerHouse (6) is the
// dedicated all-weather two-sided resting-liquidity cohort — seeded SEPARATELY from the random fleet and
// driven by MarketMakerDecisionService, exactly like the Arbitrage (5) house cohort.
// Rotator (7) is the estimate-driven rotational cohort — a separate house pass (RotatorDecisionService)
// that stays ~fully invested and rotates capital toward the bank price-estimate; seeded separately and
// exempt from the active-cap / cash-injection like the other house cohorts.
public enum AiStrategy { MarketMaker = 0, TrendFollower = 1, MeanReversion = 2, Random = 3, Scalper = 4, Arbitrage = 5, MarketMakerHouse = 6, Rotator = 7 }

public class AIUser : IValidatable
{
    // Primary Key
    private int _aiUserId = 0;
    public int AiUserId
    {
        get => _aiUserId;
        set
        {
            if (_aiUserId != 0 && value != _aiUserId)
                throw new InvalidOperationException("AiUserId is immutable once set.");
            _aiUserId = value;
        }
    }

    // Foreign Key to User
    private int _userId = 0;
    public int UserId
    {
        get => _userId;
        set
        {
            if (_userId != 0 && value != _userId)
                throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    // Random Seed for reproducibility
    private int _seed = 0;
    public int Seed
    {
        get => _seed;
        set
        {
            if (_seed != 0 && value != _seed)
                throw new InvalidOperationException("Seed is immutable once set.");
            _seed = value;
        }
    }

    // Time interval between AI decisions
    public TimeSpan DecisionInterval { get; private set; } = TimeSpan.FromSeconds(1);
    public int DecisionIntervalSeconds
    {
        get => (int)DecisionInterval.TotalSeconds;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException("Decision interval must be at least 1 second.");
            DecisionInterval = TimeSpan.FromSeconds(value);
        }
    }

    // Timestamp of creation
    private DateTime _createdAt = TimeHelper.NowUtc();
    public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    // Timestamp of last update
    private DateTime _updatedAt = TimeHelper.NowUtc();
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => _updatedAt = TimeHelper.EnsureUtc(value);
    }

    private decimal _tradeProb = 0.1m;
    public decimal TradeProb { get => _tradeProb; set => _tradeProb = RequiredPrc(value, nameof(TradeProb)); }

    private decimal _useMarketProb = 0.3m;
    public decimal UseMarketProb { get => _useMarketProb; set => _useMarketProb = RequiredPrc(value, nameof(UseMarketProb)); }

    private decimal _useSlippageMarketProb = 0.8m;
    public decimal UseSlippageMarketProb { get => _useSlippageMarketProb; set => _useSlippageMarketProb = RequiredPrc(value, nameof(UseSlippageMarketProb)); }

    private decimal _buyBiasPrc = 0.5m;
    public decimal BuyBiasPrc { get => _buyBiasPrc; set => _buyBiasPrc = RequiredPrc(value, nameof(BuyBiasPrc)); }

    private decimal _minTradeAmountPrc = 0.01m;
    public decimal MinTradeAmountPrc { get => _minTradeAmountPrc; set => _minTradeAmountPrc = RequiredPrc(value, nameof(MinTradeAmountPrc)); }

    private decimal _maxTradeAmountPrc = 0.05m;
    public decimal MaxTradeAmountPrc { get => _maxTradeAmountPrc; set => _maxTradeAmountPrc = RequiredPrc(value, nameof(MaxTradeAmountPrc)); }

    private decimal _perPositionMaxPrc = 0.25m;
    public decimal PerPositionMaxPrc { get => _perPositionMaxPrc; set => _perPositionMaxPrc = RequiredPrc(value, nameof(PerPositionMaxPrc)); }

    private decimal _minCashReservePrc = 0.1m;
    public decimal MinCashReservePrc { get => _minCashReservePrc; set => _minCashReservePrc = RequiredPrc(value, nameof(MinCashReservePrc)); }

    private decimal _maxCashReservePrc = 0.5m;
    public decimal MaxCashReservePrc { get => _maxCashReservePrc; set => _maxCashReservePrc = RequiredPrc(value, nameof(MaxCashReservePrc)); }

    private decimal _slippageTolerancePrc = 0.02m;
    public decimal SlippageTolerancePrc { get => _slippageTolerancePrc; set => _slippageTolerancePrc = RequiredPrc(value, nameof(SlippageTolerancePrc)); }

    private decimal _minLimitOffsetPrc = 0.002m;
    public decimal MinLimitOffsetPrc { get => _minLimitOffsetPrc; set => _minLimitOffsetPrc = RequiredPrc(value, nameof(MinLimitOffsetPrc)); }

    private decimal _maxLimitOffsetPrc = 0.05m;
    public decimal MaxLimitOffsetPrc { get => _maxLimitOffsetPrc; set => _maxLimitOffsetPrc = RequiredPrc(value, nameof(MaxLimitOffsetPrc)); }

    private decimal _aggressivenessPrc = 0.5m;
    public decimal AggressivenessPrc { get => _aggressivenessPrc; set => _aggressivenessPrc = RequiredPrc(value, nameof(AggressivenessPrc)); }

    // §P6 balancing: tiered limit ladder. Close = the existing Min/MaxLimitOffsetPrc (tight, churns at
    // the touch). Mid + Far are standing walls further out. A fired (slippage-capped) stop runs into the
    // Far walls and is absorbed, so StopDistanceMax must stay below FarLimitMin (enforced in Person.py +
    // ValidateSizing). All are fractions of price; seeded per-bot in Tools/Person.py.
    private decimal _midLimitMinPrc = 0.01m;
    public decimal MidLimitMinPrc { get => _midLimitMinPrc; set => _midLimitMinPrc = RequiredPrc(value, nameof(MidLimitMinPrc)); }

    private decimal _midLimitMaxPrc = 0.05m;
    public decimal MidLimitMaxPrc { get => _midLimitMaxPrc; set => _midLimitMaxPrc = RequiredPrc(value, nameof(MidLimitMaxPrc)); }

    private decimal _farLimitMinPrc = 0.06m;
    public decimal FarLimitMinPrc { get => _farLimitMinPrc; set => _farLimitMinPrc = RequiredPrc(value, nameof(FarLimitMinPrc)); }

    private decimal _farLimitMaxPrc = 0.25m;
    public decimal FarLimitMaxPrc { get => _farLimitMaxPrc; set => _farLimitMaxPrc = RequiredPrc(value, nameof(FarLimitMaxPrc)); }

    // Protective-stop distance band (fraction below/above the reference). Kept inside the Far walls.
    private decimal _stopDistanceMinPrc = 0.02m;
    public decimal StopDistanceMinPrc { get => _stopDistanceMinPrc; set => _stopDistanceMinPrc = RequiredPrc(value, nameof(StopDistanceMinPrc)); }

    private decimal _stopDistanceMaxPrc = 0.05m;
    public decimal StopDistanceMaxPrc { get => _stopDistanceMaxPrc; set => _stopDistanceMaxPrc = RequiredPrc(value, nameof(StopDistanceMaxPrc)); }

    // Cap on the bot's total resting Far-order value, as a fraction of portfolio. The tier-aware prune
    // mass-cancels worst-first down to ½ of this when exceeded (hysteresis).
    private decimal _farBudgetPrc = 0.10m;
    public decimal FarBudgetPrc { get => _farBudgetPrc; set => _farBudgetPrc = RequiredPrc(value, nameof(FarBudgetPrc)); }

    // §P6: per-bot take-profit band (fraction from entry). Promoted from the global Advanced:TpOffsetPrc
    // config so bracket TP distances vary per bot. The two bracket TP legs draw from [min, max].
    private decimal _tpOffsetMinPrc = 0.01m;
    public decimal TpOffsetMinPrc { get => _tpOffsetMinPrc; set => _tpOffsetMinPrc = RequiredPrc(value, nameof(TpOffsetMinPrc)); }

    private decimal _tpOffsetMaxPrc = 0.025m;
    public decimal TpOffsetMaxPrc { get => _tpOffsetMaxPrc; set => _tpOffsetMaxPrc = RequiredPrc(value, nameof(TpOffsetMaxPrc)); }

    // Sentiment-dynamics §: per-bot "lateness" L ∈ [0,1] — the only new per-bot knob for the slope-aware
    // phase model. For the momentum cohort (TrendFollower/Scalper) it blends following the sentiment slope
    // (early, low L) against chasing the sentiment level (late/FOMO, high L). Inert for other strategies and
    // when Bots:SentimentDynamics:Enabled is off. Default 0 = pure trend-follow (sane un-reseeded behaviour).
    private decimal _lateness = 0m;
    public decimal Lateness { get => _lateness; set => _lateness = RequiredPrc(value, nameof(Lateness)); }

    // Round 2 §0012 (E5): per-bot preference for round-trip vs flip when both are sizeable. 1.0 =
    // always size entry to ≤ |inventory| (always round-trip); 0.0 = always flip; 0.5 = roughly
    // 50/50. Default 0.5 = neutral. Read by BuildBracketAsync when Bots:Advanced:BracketFlip is
    // on; inert otherwise. Per-strategy seed values come from Tools/Config.py
    // (MarketMaker 0.5, TrendFollower 0.2, MeanReversion 0.8, Random 0.5, Scalper 0.7).
    private decimal _roundtripBiasPrc = 0.5m;
    public decimal RoundtripBiasPrc { get => _roundtripBiasPrc; set => _roundtripBiasPrc = RequiredPrc(value, nameof(RoundtripBiasPrc)); }

    // §3.6 P6: per-bot, per-tick probabilities of choosing each advanced order kind (seeded + assigned
    // by strategy in Tools/Person.py). They REPLACE the global Bots:Advanced:*Prob config — the master
    // Bots:Advanced:Enabled switch still gates the whole feature. Default 0 so a bot never does advanced
    // orders unless seeded otherwise.
    private decimal _stopProb = 0m;
    public decimal StopProb { get => _stopProb; set => _stopProb = RequiredPrc(value, nameof(StopProb)); }

    private decimal _trailingProb = 0m;
    public decimal TrailingProb { get => _trailingProb; set => _trailingProb = RequiredPrc(value, nameof(TrailingProb)); }

    private decimal _shortProb = 0m;
    public decimal ShortProb { get => _shortProb; set => _shortProb = RequiredPrc(value, nameof(ShortProb)); }

    private decimal _longBracketProb = 0m;
    public decimal LongBracketProb { get => _longBracketProb; set => _longBracketProb = RequiredPrc(value, nameof(LongBracketProb)); }

    private decimal _shortBracketProb = 0m;
    public decimal ShortBracketProb { get => _shortBracketProb; set => _shortBracketProb = RequiredPrc(value, nameof(ShortBracketProb)); }

    // Probability of acting out-of-character at an extreme-sentiment event. Range [0, 0.5].
    private decimal _extremeReactionRandomnessPrc = 0.10m;
    public decimal ExtremeReactionRandomnessPrc
    {
        get => _extremeReactionRandomnessPrc;
        set
        {
            if (value < 0m || value > 0.5m)
                throw new ArgumentOutOfRangeException(nameof(ExtremeReactionRandomnessPrc),
                    "ExtremeReactionRandomnessPrc must be between 0 and 0.5.");
            _extremeReactionRandomnessPrc = value;
        }
    }

    // Per-cycle (1h) cash-injection roll. Seeded inverse to portfolio size.
    private decimal _cashInjectionFrequencyPrc = 0.15m;
    public decimal CashInjectionFrequencyPrc
    {
        get => _cashInjectionFrequencyPrc;
        set
        {
            if (value < 0m || value > 0.50m)
                throw new ArgumentOutOfRangeException(nameof(CashInjectionFrequencyPrc),
                    "CashInjectionFrequencyPrc must be between 0 and 0.50.");
            _cashInjectionFrequencyPrc = value;
        }
    }

    // Deposit size as fraction of bot's current portfolio when injection fires.
    private decimal _cashInjectionAmountPrc = 0.004m;
    public decimal CashInjectionAmountPrc
    {
        get => _cashInjectionAmountPrc;
        set
        {
            if (value < 0m || value > 0.05m)
                throw new ArgumentOutOfRangeException(nameof(CashInjectionAmountPrc),
                    "CashInjectionAmountPrc must be between 0 and 0.05.");
            _cashInjectionAmountPrc = value;
        }
    }

    // §3.7 Arbitrage cohort params (AiStrategy.Arbitrage only; default 0 = inert for every other
    // strategy so existing bots stay valid). Seeded per-bot via Tools/Person.py → Profile sheet.
    // Minimum arbitrage rate (per-share profit / notional, net of the FX spread) the bot will act
    // on — kept ≥ the 0.1% ConvertSpread so an acted trade is genuinely near-riskless.
    private decimal _minArbitrageRatePrc = 0m;
    public decimal MinArbitrageRatePrc { get => _minArbitrageRatePrc; set => _minArbitrageRatePrc = RequiredPrc(value, nameof(MinArbitrageRatePrc)); }

    // Bounded inventory cap (shares) the bot may hold in a single stock while waiting for a
    // favorable exit ("hold and wait"). Caps the residual risk of a deferred second leg.
    private int _maxInventoryPerStock = 0;
    public int MaxInventoryPerStock { get => _maxInventoryPerStock; set => _maxInventoryPerStock = value < 0 ? 0 : value; }

    // How often (seconds) the bot rebalances its USD/EUR cash mix via the platform FX desk to
    // re-arm — the flow that pays the conversion spread into the house account.
    private int _conversionCadenceSeconds = 0;
    public int ConversionCadenceSeconds { get => _conversionCadenceSeconds; set => _conversionCadenceSeconds = value < 0 ? 0 : value; }

    // Set of stock IDs in the AI's watchlist
    private readonly HashSet<int> _watchlist = new();
    public IReadOnlyCollection<int> Watchlist => _watchlist;
    public string WatchlistCsv
    {
        get => string.Join(",", _watchlist.OrderBy(x => x));
        set
        {
            _watchlist.Clear();
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, out var id) && id > 0)
                    _watchlist.Add(id);
            UpdatedAt = TimeHelper.NowUtc();
        }
    }

    private int _maxOpenOrders = 20;
    public int MaxOpenOrders { get => _maxOpenOrders; set => _maxOpenOrders = value < 0 ? 0 : value; }

    // Drawn from Tools/Config.py::HOME_CURRENCY_WEIGHTS at seed time.
    public CurrencyType HomeCurrencyType { get; set; } = CurrencyType.USD;
    public string HomeCurrency
    {
        get => HomeCurrencyType.ToString();
        set => HomeCurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    public AiStrategy Strategy { get; private set; } = AiStrategy.Random;

    private int _strategyCode = (int)AiStrategy.Random;
    public int StrategyCode
    {
        get => _strategyCode;
        set
        {
            if (!Enum.IsDefined(typeof(AiStrategy), value))
                throw new ArgumentOutOfRangeException(nameof(StrategyCode));
            _strategyCode = value;
            Strategy = (AiStrategy)value;
        }
    }

    // Runtime-only (not persisted)
    public bool IsEnabled { get; set; } = true;
    public DateOnly TradesDayUtc { get; private set; } = TimeHelper.Today();
    public int TradesToday { get; private set; } = 0;
    // Per-session (not per-calendar-day) trade count for the per-strategy telemetry. Starts at 0 on each
    // session load (ClearAll rebuilds AIUser objects) and only ever increments in RecordTrade; unlike
    // TradesToday it is never rolled by ResetDay, so it aggregates a whole Stop/Start session.
    public int TotalTradesThisSession { get; private set; } = 0;
    public int ErrorsToday { get; private set; } = 0;
    public DateTime LastDecisionTime { get; private set; } = DateTime.MinValue;
    public DateTime LastTradeTime { get; private set; } = DateTime.MinValue;
    public HashSet<int> StocksTouchedToday { get; } = new();

    public bool IsValid() => UserId > 0 && Seed > 0 && DecisionIntervalSeconds > 0 && MaxOpenOrders >= 0 &&
        MaxInventoryPerStock >= 0 && ConversionCadenceSeconds >= 0 &&
        IsValidPercentages() && ValidateSizing() && IsValidWatchlist() && IsValidTimestamps() &&
        CurrencyHelper.IsSupported(HomeCurrency);

    public bool IsInvalid => !IsValid();

    private static bool IsValidPrc(decimal val) => val >= 0 && val <= 1;
    private bool IsValidPercentages() => IsValidPrc(TradeProb) && IsValidPrc(UseMarketProb) &&
        IsValidPrc(MinTradeAmountPrc) && IsValidPrc(MaxTradeAmountPrc) && IsValidPrc(PerPositionMaxPrc) && IsValidPrc(BuyBiasPrc) &&
        IsValidPrc(MinCashReservePrc) && IsValidPrc(MaxCashReservePrc) && IsValidPrc(SlippageTolerancePrc) && IsValidPrc(AggressivenessPrc) &&
        ExtremeReactionRandomnessPrc >= 0m && ExtremeReactionRandomnessPrc <= 0.5m &&
        CashInjectionFrequencyPrc >= 0m && CashInjectionFrequencyPrc <= 0.50m &&
        CashInjectionAmountPrc    >= 0m && CashInjectionAmountPrc    <= 0.05m &&
        IsValidPrc(StopProb) && IsValidPrc(TrailingProb) && IsValidPrc(ShortProb) &&
        IsValidPrc(LongBracketProb) && IsValidPrc(ShortBracketProb) &&
        IsValidPrc(MidLimitMinPrc) && IsValidPrc(MidLimitMaxPrc) &&
        IsValidPrc(FarLimitMinPrc) && IsValidPrc(FarLimitMaxPrc) &&
        IsValidPrc(StopDistanceMinPrc) && IsValidPrc(StopDistanceMaxPrc) && IsValidPrc(FarBudgetPrc) &&
        IsValidPrc(TpOffsetMinPrc) && IsValidPrc(TpOffsetMaxPrc) &&
        IsValidPrc(Lateness) &&
        IsValidPrc(RoundtripBiasPrc) &&
        IsValidPrc(MinArbitrageRatePrc);

    private bool ValidateSizing() => MinTradeAmountPrc <= MaxTradeAmountPrc && MaxTradeAmountPrc <= PerPositionMaxPrc && MinCashReservePrc <= MaxCashReservePrc &&
        // §P6 tier ladder ordering: each tier's min ≤ max, and the protective stop sits inside the Far walls.
        MidLimitMinPrc <= MidLimitMaxPrc && FarLimitMinPrc <= FarLimitMaxPrc &&
        StopDistanceMinPrc <= StopDistanceMaxPrc && StopDistanceMaxPrc <= FarLimitMinPrc &&
        TpOffsetMinPrc <= TpOffsetMaxPrc;

    private bool IsValidWatchlist() => Watchlist.Count > 0 && Watchlist.All(id => id > 0);

    private bool IsValidTimestamps() => CreatedAt > DateTime.MinValue && CreatedAt <= TimeHelper.NowUtc() &&
        UpdatedAt >= CreatedAt && UpdatedAt <= TimeHelper.NowUtc();

    public override string ToString() => $"AIUser #{AiUserId} (User #{UserId})";

    public string SummaryIdentity =>
        $"{ToString()} • Strategy={Strategy} • Seed={Seed}";

    public string SummaryActivity =>
        $"Interval {IntervalString()} • " +
        $"Trades {TradesToday} • Errors {ErrorsToday}";

    public string SummarySizing =>
        $"TradeProb {TradeProbDisplay} • Size {TradeAmountDisplay} • " +
        $"PerPosMax {PerPositionMaxDisplay}";

    public string SummaryRisk =>
        $"Cash {CashReserveTargetPrc} • Slippage {SlippageToleranceDisplay} • " +
        $"LimitOffset {LimitOffsetDisplay} • Aggro {AggressivenessDisplay}";

    public string Summary =>
        $"{SummaryIdentity} | {SummaryActivity} | {SummarySizing} | {SummaryRisk}";

    public string IntervalString()
    {
        int s = DecisionIntervalSeconds;
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m";
        if (s < 86400) return $"{s / 3600}h";
        return $"{s / 86400}d";
    }

    public string TradeProbDisplay => $"{TradeProb:P0}";
    public string UseMarketProbDisplay => $"{UseMarketProb:P0}";
    public string TradeAmountDisplay => $"{MinTradeAmountPrc:P0} - {MaxTradeAmountPrc:P0}";
    public string PerPositionMaxDisplay => $"{PerPositionMaxPrc:P0}";
    public string CashReserveTargetPrc => $"{MinCashReservePrc:P0} - {MaxCashReservePrc:P0}";
    public string SlippageToleranceDisplay => $"{SlippageTolerancePrc:P0}";
    public string LimitOffsetDisplay => $"{MinLimitOffsetPrc:P0} - {MaxLimitOffsetPrc:P0}";
    public string AggressivenessDisplay => $"{AggressivenessPrc:P0}";

    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string UpdatedAtDisplay => UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");

    private static decimal RequiredPrc(decimal value, string name)
    {
        if (value < 0 || value > 1)
            throw new ArgumentOutOfRangeException($"{name} must be between 0 and 1.");
        return value;
    }

    private void Touched() => UpdatedAt = TimeHelper.NowUtc();

    public void ResetDay()
    {
        var today = TimeHelper.Today();
        if (TradesDayUtc >= today) return;
        TradesDayUtc = today;
        TradesToday = 0;
        ErrorsToday = 0;
        StocksTouchedToday.Clear();
        Touched();
    }

    public bool AddToWatchlist(int stockId)
    {
        if (stockId <= 0) return false;
        var added = _watchlist.Add(stockId);
        if (added) Touched();
        return added;
    }

    public bool RemoveFromWatchlist(int stockId)
    {
        var removed = _watchlist.Remove(stockId);
        if (removed) Touched();
        return removed;
    }

    public void RecordDecision(DateTime whenUtc)
    {
        LastDecisionTime = TimeHelper.EnsureUtc(whenUtc);
        Touched();
    }

    public void RecordError()
    {
        ErrorsToday++;
        Touched();
    }

    public void RecordTrade(Transaction tx)
    {
        if (tx == null || tx.IsInvalid || !tx.InvolvesUser(UserId)) return;
        TradesToday++;
        TotalTradesThisSession++;
        StocksTouchedToday.Add(tx.StockId);
        LastTradeTime = tx.Timestamp;
        Touched();
    }
}
