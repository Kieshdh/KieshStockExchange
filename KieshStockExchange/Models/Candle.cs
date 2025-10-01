using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Candles")]
public class Candle : IValidatable
{
    #region Constants
    public static class Resolutions
    {
        public const int Default = OneMinute;
        public const int OneSecond = 1;
        public const int FiveSeconds = 5;
        public const int FifteenSeconds = 15;
        public const int OneMinute = 60;
        public const int FiveMinutes = 300;
        public const int FifteenMinutes = 900;
        public const int ThirtyMinutes = 1800;
        public const int OneHour = 3600;
        public const int FourHours = 14400;
        public const int OneDay = 86400;
        public const int OneWeek = 604800;
    }
    #endregion

    #region Properties
    // Id, StockId, Currency, ResolutionSeconds, OpenTime uniquely identify a candle
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

    [Ignore] public TimeSpan Bucket { get; private set; } = TimeSpan.Zero;
    [Indexed(Name = "IX_Candle_Key", Order = 3, Unique = true)]
    [Column("ResolutionSeconds") ] public int ResolutionSeconds
    {
        get => (int)Bucket.TotalSeconds;
        set {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (Bucket > TimeSpan.Zero && (int)Bucket.TotalSeconds != value)
                throw new InvalidOperationException("ResolutionSeconds is immutable once set.");
            Bucket = TimeSpan.FromSeconds(value);
        }
    }

    private DateTime _openTime = TimeHelper.NowUtc();
    [Indexed(Name = "IX_Candle_Key", Order = 4, Unique = true)]
    [Column("OpenTime")] public DateTime OpenTime
    {
        get => _openTime;
        set => _openTime = TimeHelper.EnsureUtc(value);
    }

    // OHLC values, Volume and TradeCount
    private decimal _open = 0;
    [Column("Open")] public decimal Open {
        get => _open;
        set {
            if (value <= 0m) throw new ArgumentException("Open price must be positive.");
            _open = value;
        }
    }

    private decimal _high = 0;
    [Column("High")] public decimal High {
        get => _high;
        set {
            if (value <= 0m) throw new ArgumentException("High price must be positive.");
            _high = value;
        }
    }

    private decimal _low = 0;
    [Column("Low")] public decimal Low {
        get => _low;
        set {
            if (value <= 0m) throw new ArgumentException("Low price must be positive.");
            _low = value;
        }
    }

    private decimal _close = 0;
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
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => StockId > 0 && IsValidCurrency() && IsValidResolution() &&
        IsValidTimestamp() && IsValidPrice() && IsValidVolume();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidResolution() => ResolutionSeconds is (
        Resolutions.OneSecond or Resolutions.FiveSeconds or Resolutions.FifteenSeconds
        or Resolutions.OneMinute or Resolutions.FiveMinutes or Resolutions.FifteenMinutes
        or Resolutions.ThirtyMinutes or Resolutions.OneHour or Resolutions.FourHours
        or Resolutions.OneDay or Resolutions.OneWeek
    );

    private bool IsValidTimestamp() => ResolutionSeconds > 0 && OpenTime > DateTime.MinValue && 
        OpenTime <= TimeHelper.NowUtc() && TimeHelper.FloorToBucketUtc(OpenTime, Bucket) == OpenTime;

    private bool IsValidPrice() => Open > 0m && High > 0m && Low > 0m && Close > 0m && 
        Low <= High && Open >= Low && Open <= High && Close >= Low && Close <= High;

    private bool IsValidVolume() => Volume >= 0 && TradeCount >= 0 && Volume >= TradeCount;
    #endregion

    #region String Representations
    public override string ToString() =>
        $"Candle #{CandleId}: StockId #{StockId} {Currency} {ResolutionString()} @ {OpenTime:O} ";

    public string ResolutionString()
    {
        if (ResolutionSeconds < 60)
            return $"{ResolutionSeconds}s";
        if (ResolutionSeconds < 3600)
            return $"{ResolutionSeconds / 60}m";
        if (ResolutionSeconds < 86400)
            return $"{ResolutionSeconds / 3600}h";
        return $"{ResolutionSeconds / 86400}d";
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
    [Ignore] public int ResolutionMinutes => ResolutionSeconds / 60;

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
    [Ignore] public decimal PriceChangeRatio => Open == 0m ? 0m : (Close - Open) / Open;
    [Ignore] public decimal PriceChangePercent => PriceChangeRatio * 100m;
    #endregion

    #region Helper Methods
    public void ApplyTrade(decimal price, long quantity)
    {
        if (price <= 0m) throw new ArgumentException("Trade price must be positive.", nameof(price));
        if (quantity <= 0) throw new ArgumentException("Trade volume must be positive.", nameof(quantity));
        //if (IsComplete) throw new InvalidOperationException("Cannot apply trade to a completed candle.");
        // OHLC update
        if (IsNew) Open = price;
        if (price > High || IsNew) High = price;
        if (price < Low || IsNew) Low = price;
        Close = price;
        // Update volume and trade count
        Volume += quantity;
        TradeCount += 1;

        if (!IsValid())
            throw new InvalidOperationException("Candle is not valid after applying trade.");
    }
    #endregion
}
