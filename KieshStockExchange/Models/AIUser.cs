using KieshStockExchange.Helpers;
using SQLite;

namespace KieshStockExchange.Models;

public enum AiStrategy { MarketMaker = 0, TrendFollower = 1, MeanReversion = 2, Random = 3, Scalper = 4 }

[Table("AIUsers")] public class AIUser : IValidatable
{
    #region Basic Properties
    // Primary Key
    private int _aiUserId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("AiUserId")] public int AiUserId
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
    [Indexed(Name = "IX_UserAi", Unique = true)]
    [Column("UserId")] public int UserId
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
    [Column("Seed")] public int Seed
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
    [Ignore] public TimeSpan DecisionInterval { get; private set; } = TimeSpan.FromSeconds(1);
    [Column("DecisionIntervalSeconds")] public int DecisionIntervalSeconds
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
    [Column("CreatedAt")] public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    // Timestamp of last update
    private DateTime _updatedAt = TimeHelper.NowUtc();
    [Column("UpdatedAt")] public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => _updatedAt = TimeHelper.EnsureUtc(value);
    }
    #endregion

    #region Percentage Properties
    // Probability of making a trade decision each interval
    private decimal _tradeProb = 0.1m;
    [Column("TradeProb")] public decimal TradeProb
    {
        get => _tradeProb;
        set => _tradeProb = RequiredPrc(value, nameof(TradeProb));
    }

    // Probability of using market orders vs limit orders
    private decimal _useMarketProb = 0.3m;
    [Column("UseMarketProb")] public decimal UseMarketProb
    {
        get => _useMarketProb;
        set => _useMarketProb = RequiredPrc(value, nameof(UseMarketProb));
    }

    // Probability of using slipped market orders
    private decimal _useSlippageMarketProb = 0.8m;
    [Column("UseSlippageMarketProb")] public decimal UseSlippageMarketProb
    {
        get => _useSlippageMarketProb;
        set => _useSlippageMarketProb = RequiredPrc(value, nameof(UseSlippageMarketProb));
    }

    // Percentage of time the AI is active (0 to 1)
    private decimal _onlineProb = 1m;
    [Column("OnlineProb")] public decimal OnlineProb
    {
        get => _onlineProb;
        set => _onlineProb = RequiredPrc(value, nameof(OnlineProb));
    }

    // Buy bias percentage (0 to 1)
    private decimal _buyBiasPrc = 0.5m;
    [Column("BuyBiasPrc")] public decimal BuyBiasPrc
    {
        get => _buyBiasPrc;
        set => _buyBiasPrc = RequiredPrc(value, nameof(BuyBiasPrc));
    }

    // Minimum trade amount as a percentage of total portfolio (0 to 1)
    private decimal _minTradeAmountPrc = 0.01m;
    [Column("MinTradeAmountPrc")] public decimal MinTradeAmountPrc
    {
        get => _minTradeAmountPrc;
        set => _minTradeAmountPrc = RequiredPrc(value, nameof(MinTradeAmountPrc));
    }

    // Maximum trade amount as a percentage of total portfolio (0 to 1)
    private decimal _maxTradeAmountPrc = 0.05m;
    [Column("MaxTradeAmountPrc")] public decimal MaxTradeAmountPrc
    {
        get => _maxTradeAmountPrc;
        set => _maxTradeAmountPrc = RequiredPrc(value, nameof(MaxTradeAmountPrc));
    }

    // Maximum percentage of portfolio per position (0 to 1)
    private decimal _perPositionMaxPrc = 0.25m;
    [Column("PerPositionMaxPrc")] public decimal PerPositionMaxPrc
    {
        get => _perPositionMaxPrc;
        set => _perPositionMaxPrc = RequiredPrc(value, nameof(PerPositionMaxPrc));
    }

    // Minimum cash reserve target as a percentage of total portfolio (0 to 1)
    private decimal _minCashReservePrc = 0.1m;
    [Column("MinCashReservePrc")] public decimal MinCashReservePrc
    {
        get => _minCashReservePrc;
        set => _minCashReservePrc = RequiredPrc(value, nameof(MinCashReservePrc));
    }

    // Maximum cash reserve target as a percentage of total portfolio (0 to 1)
    private decimal _maxCashReservePrc = 0.5m;
    [Column("MaxCashReservePrc")] public decimal MaxCashReservePrc
    {
        get => _maxCashReservePrc;
        set => _maxCashReservePrc = RequiredPrc(value, nameof(MaxCashReservePrc));
    }

    // Acceptable slippage percentage for trades (0 to 1)
    private decimal _slippageTolerancePrc = 0.02m;
    [Column("SlippageTolerancePrc")] public decimal SlippageTolerancePrc
    {
        get => _slippageTolerancePrc;
        set => _slippageTolerancePrc = RequiredPrc(value, nameof(SlippageTolerancePrc));
    }

    // Minimum Limit Order Offset Percentage (0 to 1)
    private decimal _minLimitOffsetPrc = 0.002m;
    [Column("MinLimitOffsetPrc")] public decimal MinLimitOffsetPrc
    {
        get => _minLimitOffsetPrc;
        set => _minLimitOffsetPrc = RequiredPrc(value, nameof(MinLimitOffsetPrc));
    }

    // Maximum Limit Order Offset Percentage (0 to 1)
    private decimal _maxLimitOffsetPrc = 0.05m;
    [Column("MaxLimitOffsetPrc")] public decimal MaxLimitOffsetPrc
    {
        get => _maxLimitOffsetPrc;
        set => _maxLimitOffsetPrc = RequiredPrc(value, nameof(MaxLimitOffsetPrc));
    }

    // The higher the aggressiveness, the more likely to take risks (0 to 1)
    private decimal _aggressivenessPrc = 0.5m;
    [Column("AggressivenessPrc")] public decimal AggressivenessPrc
    {
        get => _aggressivenessPrc;
        set => _aggressivenessPrc = RequiredPrc(value, nameof(AggressivenessPrc));
    }
    #endregion

    #region Trading Strategy Properties
    // Set of stock IDs in the AI's watchlist
    private readonly HashSet<int> _watchlist = new();
    [Ignore] public IReadOnlyCollection<int> Watchlist => _watchlist;
    [Column("WatchlistCsv")] public string WatchlistCsv
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

    // Minimum number of open positions the AI should maintain
    private int _minOpenPositions = 0;
    [Column("MinOpenPositions")] public int MinOpenPositions
    {
        get => _minOpenPositions;
        set => _minOpenPositions = value < 0 ? 0 : value;
    }

    // Maximum number of open positions the AI can hold
    private int _maxOpenPositions = 15;
    [Column("MaxOpenPositions")] public int MaxOpenPositions
    {
        get => _maxOpenPositions;
        set => _maxOpenPositions = value < 0 ? 0 : value;
    }

    // Maximum number of trades the AI can make in a day
    // When it exceeds then stop trading for the day while keeping orders open
    private int _maxDailyTrades = 50;
    [Column("MaxDailyTrades")] public int MaxDailyTrades
    {
        get => _maxDailyTrades;
        set => _maxDailyTrades = value < 0 ? 0 : value;
    }

    // Maximum number of open orders the AI can have at once
    private int _maxOpenOrders = 20;
    [Column("MaxOpenOrders")] public int MaxOpenOrders
    {
        get => _maxOpenOrders;
        set => _maxOpenOrders = value < 0 ? 0 : value;
    }

    // Trading strategy used by the AI
    [Ignore] public AiStrategy Strategy { get; private set; } = AiStrategy.Random;

    private int _strategyCode = (int)AiStrategy.Random;
    [Indexed][Column("Strategy")] public int StrategyCode
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
    #endregion

    #region Temporary runtime Properties
    [Ignore] public bool IsEnabled { get; set; } = false; // If the current AI is enabled to place active trades
    [Ignore] public DateOnly TradesDayUtc { get; private set; } = TimeHelper.Today();
    [Ignore] public int TradesToday { get; private set; } = 0; // Number of trades made today
    [Ignore] public int ErrorsToday { get; private set; } = 0; // Number of errors encountered today
    [Ignore] public DateTime LastDecisionTime { get; private set; } = DateTime.MinValue; // Timestamp of last decision
    [Ignore] public DateTime LastTradeTime { get; private set; } = DateTime.MinValue; // Timestamp of last trade
    [Ignore] public HashSet<int> StocksTouchedToday { get; } = new(); // Set of stock IDs traded today
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0 && Seed > 0 && DecisionIntervalSeconds > 0 && MaxDailyTrades >= 0 && MaxOpenOrders >= 0 &&
        IsValidPercentages() && ValidateSizing() && ValidatePositions() && IsValidWatchlist() && IsValidTimestamps();

    public bool IsInvalid => !IsValid();

    private static bool IsValidPrc(decimal val) => val >= 0 && val <= 1;
    private bool IsValidPercentages() => IsValidPrc(TradeProb) && IsValidPrc(UseMarketProb) && IsValidPrc(OnlineProb) &&
        IsValidPrc(MinTradeAmountPrc) && IsValidPrc(MaxTradeAmountPrc) && IsValidPrc(PerPositionMaxPrc) && IsValidPrc(BuyBiasPrc) &&
        IsValidPrc(MinCashReservePrc) && IsValidPrc(MaxCashReservePrc) && IsValidPrc(SlippageTolerancePrc) && IsValidPrc(AggressivenessPrc);

    private bool ValidateSizing() => MinTradeAmountPrc <= MaxTradeAmountPrc && MaxTradeAmountPrc <= PerPositionMaxPrc && MinCashReservePrc <= MaxCashReservePrc;

    private bool ValidatePositions() => MinOpenPositions >= 0 && MaxOpenPositions >= MinOpenPositions;

    private bool IsValidWatchlist() => Watchlist.Count > 0 && Watchlist.All(id => id > 0);

    private bool IsValidTimestamps() => CreatedAt > DateTime.MinValue && CreatedAt <= TimeHelper.NowUtc() &&
        UpdatedAt >= CreatedAt && UpdatedAt <= TimeHelper.NowUtc();
    #endregion

    #region String Representations
    public override string ToString() => $"AIUser #{AiUserId} (User #{UserId})";

    [Ignore] public string Summary => $"AIUser #{AiUserId} • Active {OnlineProb:P0} • " +
        $"Trade {TradeProb:P0} • Size {MinTradeAmountPrc:P0}-{MaxTradeAmountPrc:P0} • " +
        $"Pos {MinOpenPositions}-{MaxOpenPositions} • Δ {IntervalString()}";

    public string IntervalString()
    {
        int s = DecisionIntervalSeconds;
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m";
        if (s < 86400) return $"{s / 3600}h";
        return $"{s / 86400}d";
    }

    [Ignore] public string TradeProbDisplay => $"{TradeProb:P0}";
    [Ignore] public string UseMarketProbDisplay => $"{UseMarketProb:P0}";
    [Ignore] public string OnlineProbDisplay => $"{OnlineProb:P0}";
    [Ignore] public string TradeAmountDisplay => $"{MinTradeAmountPrc:P0} - {MaxTradeAmountPrc:P0}";
    [Ignore] public string PerPositionMaxDisplay => $"{PerPositionMaxPrc:P0}";
    [Ignore] public string CashReserveTargetPrc => $"{MinCashReservePrc:P0} - {MaxCashReservePrc:P0}";
    [Ignore] public string SlippageToleranceDisplay => $"{SlippageTolerancePrc:P0}";
    [Ignore] public string LimitOffsetDisplay => $"{MinLimitOffsetPrc:P0} - {MaxLimitOffsetPrc:P0}";
    [Ignore] public string AggressivenessDisplay => $"{AggressivenessPrc:P0}";

    [Ignore] public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    [Ignore] public string UpdatedAtDisplay => UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    #endregion

    #region Private Helper methods
    private static decimal RequiredPrc(decimal value, string name)
    {
        if (value < 0 || value > 1)
            throw new ArgumentOutOfRangeException($"{name} must be between 0 and 1.");
        return value;
    }

    private void Touched() => UpdatedAt = TimeHelper.NowUtc(); // Update timestamp
    #endregion

    #region Public Helper methods
    public void ResetDay()
    {
        var today = TimeHelper.Today();
        if (TradesDayUtc >= today) return;

        // Reset daily counters
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
        StocksTouchedToday.Add(tx.StockId);
        LastTradeTime = tx.Timestamp;
        Touched();
    }
    #endregion
}
