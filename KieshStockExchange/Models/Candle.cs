using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public enum CandleResolution : int
{
    None = 0,
    Default = 300, // 5 minutes
    OneSecond = 1,
    FiveSeconds = 5,
    FifteenSeconds = 15,
    OneMinute = 60,
    FiveMinutes = 300,
    FifteenMinutes = 900,
    ThirtyMinutes = 1800,
    OneHour = 3600,
    FourHours = 14400,
    OneDay = 86400,
    OneWeek = 604800
}

[Table("Candles")]
public class Candle : IValidatable
{
    #region Properties
    // Id, StockId, Currency
    private int _candleId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("CandleId")] public int CandleId {
        get => _candleId;
        set { 
            if (_candleId != 0) throw new InvalidOperationException("CandleId is immutable once set.");
            _candleId = value < 0 ? 0 : value;
        }
    }

    private int _stockId = 0;
    [Indexed(Name = "IX_Candle_Key", Order = 1, Unique = true)]
    [Column("StockId")] public int StockId { 
        get => _stockId;
        set {
            if (_stockId != 0) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value;
        }
    }

    [Ignore] public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    [Indexed(Name = "IX_Candle_Key", Order = 2, Unique = true)]
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    // Bucket resolution
    [Ignore] public CandleResolution Resolution { get; private set; } = CandleResolution.None;
    [Ignore] public TimeSpan Bucket { get; private set; } = TimeSpan.Zero;
    [Indexed(Name = "IX_Candle_Key", Order = 3, Unique = true)]
    [Column("BucketSeconds") ] public int BucketSeconds
    {
        get => (int)Resolution;
        set {
            // Validate
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            // Immutable once set
            if (Resolution != CandleResolution.None)
                throw new InvalidOperationException("ResolutionSeconds is immutable once set.");
            // Check if valid resolution
            if (!TryFromSeconds(value, out var res))
                throw new ArgumentOutOfRangeException(nameof(value), "Unsupported resolution.");
            Resolution = res;
            Bucket = TimeSpan.FromSeconds(value);
        }
    }

    // Open time aligned to bucket
    private DateTime _openTime = TimeHelper.NowUtc();
    [Indexed(Name = "IX_Candle_Key", Order = 4, Unique = true)]
    [Column("OpenTime")] public DateTime OpenTime
    {
        get => _openTime;
        set => _openTime = TimeHelper.EnsureUtc(value);
    }

    // OHLC values, Volume and TradeCount
    private decimal _open = 0m;
    [Column("Open")] public decimal Open {
        get => _open;
        set {
            if (value <= 0m) throw new ArgumentException("Open price must be positive.");
            _open = value;
        }
    }

    private decimal _high = 0m;
    [Column("High")] public decimal High {
        get => _high;
        set {
            if (value <= 0m) throw new ArgumentException("High price must be positive.");
            _high = value;
        }
    }

    private decimal _low = 0m;
    [Column("Low")] public decimal Low {
        get => _low;
        set {
            if (value <= 0m) throw new ArgumentException("Low price must be positive.");
            _low = value;
        }
    }

    private decimal _close = 0m;
    [Column("Close")] public decimal Close {
        get => _close;
        set {
            if (value <= 0m) throw new ArgumentException("Close price must be positive.");
            _close = value;
        }
    }

    private long _volume = 0;
    [Column("Volume")] public long Volume {
        get => _volume;
        set {
            if (value < 0) throw new ArgumentException("Volume cannot be negative.");
            _volume = value;
        }
    }

    private int _tradeCount = 0;
    [Column("TradeCount")] public int TradeCount {
        get => _tradeCount;
        set {
            if (value < 0) throw new ArgumentException("TradeCount cannot be negative.");
            _tradeCount = value;
        }
    }

    private int? _minTransactionId = null;
    [Column("MinTransactionId")] public int? MinTransactionId {
        get => _minTransactionId;
        set {
            if (!value.HasValue) return;
            if (value.Value <= 0) throw new ArgumentException("MinTransactionId must be positive.");
            if (!_minTransactionId.HasValue || value < _minTransactionId)
                _minTransactionId = value.Value;
        }
    }

    private int? _maxTransactionId = null;
    [Column("MaxTransactionId")] public int? MaxTransactionId {
        get => _maxTransactionId;
        set {
            if (!value.HasValue) return;
            if (value.Value <= 0) throw new ArgumentException("MaxTransactionId must be positive.");
            if (!_maxTransactionId.HasValue || value > _maxTransactionId)
                _maxTransactionId = value.Value;
        }
    }
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => StockId > 0 && IsValidCurrency() && Resolution != CandleResolution.None &&
        IsValidTimestamp() && IsValidPrice() && IsValidVolume();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidTimestamp() => BucketSeconds > 0 && OpenTime > DateTime.MinValue && 
        OpenTime <= TimeHelper.NowUtc() && TimeHelper.FloorToBucketUtc(OpenTime, Bucket) == OpenTime;

    private bool IsValidPrice() => Open > 0m && High > 0m && Low > 0m && Close > 0m && 
        Low <= High && Open >= Low && Open <= High && Close >= Low && Close <= High;

    private bool IsValidVolume() => Volume >= 0 && TradeCount >= 0 && Volume >= TradeCount;

    #endregion

    #region String Representations
    public override string ToString() =>
        $"Candle #{CandleId}: StockId #{StockId} {Currency} {BucketString()} @ {OpenTime:O} ";

    public string BucketString()
    {
        if (BucketSeconds < 60)
            return $"{BucketSeconds}s";
        if (BucketMinutes < 60)
            return $"{BucketMinutes}m";
        if (BucketHours < 24)
            return $"{BucketHours}h";
        return $"{BucketDays}d";
    }

    [Ignore] public string Summary =>
        $"{ToString()} O:{OpenDisplay} H:{HighDisplay} L:{LowDisplay} C:{CloseDisplay} V:{Volume} T:{TradeCount}";

    [Ignore] public string OpenTimeDisplay => OpenTime.ToString("yyyy-MM-dd HH:mm");

    [Ignore] public string OpenDisplay => CurrencyHelper.Format(Open, CurrencyType);
    [Ignore] public string HighDisplay => CurrencyHelper.Format(High, CurrencyType);
    [Ignore] public string LowDisplay => CurrencyHelper.Format(Low, CurrencyType);
    [Ignore] public string CloseDisplay => CurrencyHelper.Format(Close, CurrencyType);
    [Ignore] public string PriceChangePercentDisplay => $"{PriceChangeRatio:+0.##%;-0.##%;0%}";
    #endregion

    #region Helper Variables
    // Time calculations
    [Ignore] public DateTime CloseTime => OpenTime.Add(Bucket);
    [Ignore] public int BucketMinutes => BucketSeconds / 60;
    [Ignore] public int BucketHours => BucketMinutes / 60;
    [Ignore] public int BucketDays => BucketHours / 24;

    // Bool states
    [Ignore] public bool IsComplete => CloseTime <= TimeHelper.NowUtc();
    [Ignore] public bool IsInProgress => !IsComplete;
    [Ignore] public bool IsNew => Volume == 0 && TradeCount == 0;
    [Ignore] public bool IsOld => !IsNew;
    [Ignore] public bool IsBullish => Close > Open;
    [Ignore] public bool IsBearish => Close < Open;
    [Ignore] public bool IsDoji => Close == Open;
    [Ignore] public bool HasWick => High > Math.Max(Open, Close) || Low < Math.Min(Open, Close);


    // Price change calculations
    [Ignore] public decimal PriceChange => Close - Open;
    [Ignore] public decimal PriceChangeRatio => Open == 0m ? 0m : PriceChange / Open;
    [Ignore] public decimal PriceChangePercent => PriceChangeRatio * 100m;
    #endregion

    #region Helper Methods
    public void ApplyTrade(Transaction tick)
    {
        if (!tick.IsValid()) 
            throw new ArgumentException("Invalid tick.");
        if (tick.StockId != StockId)
            throw new ArgumentException("Tick stock does not match candle stock.");
        if (tick.Currency != Currency)
            throw new ArgumentException("Tick currency does not match candle currency.");
        if (tick.Timestamp < OpenTime || tick.Timestamp >= CloseTime)
            throw new ArgumentException("Tick time is outside candle time range.");
        NoteTransactionId(tick.TransactionId);

        // OHLC update
        var price = tick.Price;
        if (IsNew) Open = price;
        if (price > High || IsNew) High = price;
        if (price < Low || IsNew) Low = price;
        Close = price;

        // Update volume and trade count
        Volume += tick.Quantity; ;
        TradeCount += 1;

        if (!IsValid())
            throw new InvalidOperationException("Candle is not valid after applying trade.");
    }

    public void NoteTransactionId(int transactionId)
    {
        if (transactionId <= 0) throw new ArgumentException("Invalid transactionId.");
        if (!MinTransactionId.HasValue || transactionId < MinTransactionId.Value)
            MinTransactionId = transactionId;
        if (!MaxTransactionId.HasValue || transactionId > MaxTransactionId.Value)
            MaxTransactionId = transactionId;
    }

    public Candle Clone() => new Candle
    {
        StockId = this.StockId, Currency = this.Currency,
        BucketSeconds = this.BucketSeconds, OpenTime = this.OpenTime,
        Open = this.Open, High = this.High, Low = this.Low, Close = this.Close,
        Volume = this.Volume, TradeCount = this.TradeCount,
        MinTransactionId = this.MinTransactionId, MaxTransactionId = this.MaxTransactionId,
    };

    public Candle CloneWithId()
    {
        var c = Clone();
        c.CandleId = this.CandleId;
        return c;
    }

    public static bool TryFromSeconds(int seconds, out CandleResolution resolution)
    {
        if (Enum.IsDefined(typeof(CandleResolution), seconds))
        {
            resolution = (CandleResolution)seconds;
            return true;
        }
        resolution = CandleResolution.None;
        return false;
    }

    public static bool TryFromTimeSpan(TimeSpan span, out CandleResolution resolution) =>
        TryFromSeconds((int)span.TotalSeconds, out resolution);
    #endregion
}
