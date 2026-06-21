using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class Candle : IValidatable
{
    // Id, StockId, Currency
    private int _candleId = 0;
    public int CandleId
    {
        get => _candleId;
        set
        {
            if (_candleId != 0) throw new InvalidOperationException("CandleId is immutable once set.");
            _candleId = value < 0 ? 0 : value;
        }
    }

    private int _stockId = 0;
    public int StockId
    {
        get => _stockId;
        set
        {
            if (_stockId != 0) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value;
        }
    }

    public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    // Bucket resolution
    public CandleResolution Resolution { get; private set; } = CandleResolution.None;
    public TimeSpan Bucket { get; private set; } = TimeSpan.Zero;
    public int BucketSeconds
    {
        get => (int)Resolution;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (Resolution != CandleResolution.None)
                throw new InvalidOperationException("ResolutionSeconds is immutable once set.");
            if (!TryFromSeconds(value, out var res))
                throw new ArgumentOutOfRangeException(nameof(value), "Unsupported resolution.");
            Resolution = res;
            Bucket = TimeSpan.FromSeconds(value);
        }
    }

    // Open time aligned to bucket
    private DateTime _openTime = TimeHelper.NowUtc();
    public DateTime OpenTime
    {
        get => _openTime;
        set => _openTime = TimeHelper.EnsureUtc(value);
    }

    private decimal _open = 0m;
    public decimal Open
    {
        get => _open;
        set
        {
            if (value <= 0m) throw new ArgumentException("Open price must be positive.");
            _open = value;
        }
    }

    private decimal _high = 0m;
    public decimal High
    {
        get => _high;
        set
        {
            if (value <= 0m) throw new ArgumentException("High price must be positive.");
            _high = value;
        }
    }

    private decimal _low = 0m;
    public decimal Low
    {
        get => _low;
        set
        {
            if (value <= 0m) throw new ArgumentException("Low price must be positive.");
            _low = value;
        }
    }

    private decimal _close = 0m;
    public decimal Close
    {
        get => _close;
        set
        {
            if (value <= 0m) throw new ArgumentException("Close price must be positive.");
            _close = value;
        }
    }

    private long _volume = 0;
    public long Volume
    {
        get => _volume;
        set
        {
            if (value < 0) throw new ArgumentException("Volume cannot be negative.");
            _volume = value;
        }
    }

    private int _tradeCount = 0;
    public int TradeCount
    {
        get => _tradeCount;
        set
        {
            if (value < 0) throw new ArgumentException("TradeCount cannot be negative.");
            _tradeCount = value;
        }
    }

    private int? _minTransactionId = null;
    public int? MinTransactionId
    {
        get => _minTransactionId;
        set
        {
            if (!value.HasValue) return;
            if (value.Value <= 0) throw new ArgumentException("MinTransactionId must be positive.");
            if (!_minTransactionId.HasValue || value < _minTransactionId)
                _minTransactionId = value.Value;
        }
    }

    private int? _maxTransactionId = null;
    public int? MaxTransactionId
    {
        get => _maxTransactionId;
        set
        {
            if (!value.HasValue) return;
            if (value.Value <= 0) throw new ArgumentException("MaxTransactionId must be positive.");
            if (!_maxTransactionId.HasValue || value > _maxTransactionId)
                _maxTransactionId = value.Value;
        }
    }

    public bool IsValid() => StockId > 0 && IsValidCurrency() && Resolution != CandleResolution.None &&
        IsValidTimestamp() && IsValidPrice() && IsValidVolume();

    public bool IsInvalid => !IsValid();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidTimestamp() => BucketSeconds > 0 && OpenTime > DateTime.MinValue &&
        OpenTime <= TimeHelper.NowUtc() && TimeHelper.FloorToBucketUtc(OpenTime, Bucket) == OpenTime;

    private bool IsValidPrice() => Open > 0m && High > 0m && Low > 0m && Close > 0m &&
        Low <= High && Open >= Low && Open <= High && Close >= Low && Close <= High;

    private bool IsValidVolume() => Volume >= 0 && TradeCount >= 0 && Volume >= TradeCount;

    public override string ToString() =>
        $"Candle #{CandleId}: StockId #{StockId} - {Currency} {BucketString()} @ {OpenTimeDisplay}";

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

    public string Summary =>
        $"{ToString()} O:{OpenDisplay} H:{HighDisplay} L:{LowDisplay} C:{CloseDisplay} V:{Volume} T:{TradeCount}";

    public string OpenTimeDisplay => OpenTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string CloseTimeDisplay => CloseTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string OpenDisplay => CurrencyHelper.Format(Open, CurrencyType);
    public string HighDisplay => CurrencyHelper.Format(High, CurrencyType);
    public string LowDisplay => CurrencyHelper.Format(Low, CurrencyType);
    public string CloseDisplay => CurrencyHelper.Format(Close, CurrencyType);
    public string PriceChangePercentDisplay => $"{PriceChangeRatio:+0.##%;-0.##%;0%}";

    public DateTime CloseTime => OpenTime.Add(Bucket);
    public int BucketMinutes => BucketSeconds / 60;
    public int BucketHours => BucketMinutes / 60;
    public int BucketDays => BucketHours / 24;

    public bool IsComplete => CloseTime <= TimeHelper.NowUtc();
    public bool IsInProgress => !IsComplete;
    public bool IsNew => Volume == 0 && TradeCount == 0;
    public bool IsOld => !IsNew;
    public bool IsBullish => Close > Open;
    public bool IsBearish => Close < Open;
    public bool IsDoji => Close == Open;
    public bool HasWick => High > Math.Max(Open, Close) || Low < Math.Min(Open, Close);

    public decimal PriceChange => Close - Open;
    public decimal PriceChangeRatio => Open == 0m ? 0m : PriceChange / Open;
    public decimal PriceChangePercent => PriceChangeRatio * 100m;

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

        // §bounce: when the trade carries a bounce-free reference (mid/micro), the candle is built off
        // it instead of the last-trade price. The bar is seeded (NewCandle) with Open from the prior
        // last-trade close, so on the FIRST mid trade we re-anchor Open=High=Low to the mid series too,
        // otherwise a seed above the mid range would break the Low<=Open<=High invariant. Gated on
        // MidPrice.HasValue so the off arm (px == tick.Price) is byte-identical to the legacy branch.
        var px = tick.MidPrice ?? tick.Price;
        if (tick.MidPrice.HasValue && TradeCount == 0)
        {
            Open = px; High = px; Low = px;
        }
        else
        {
            if (px > High) High = px;
            if (px < Low) Low = px;
        }
        Close = px;

        Volume += tick.Quantity;
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

    public Candle Clone() => new()
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
        if (seconds > 0 && Enum.IsDefined(typeof(CandleResolution), seconds))
        {
            resolution = (CandleResolution)seconds;
            return true;
        }
        resolution = CandleResolution.None;
        return false;
    }

    public static bool TryFromTimeSpan(TimeSpan span, out CandleResolution resolution) =>
        TryFromSeconds((int)span.TotalSeconds, out resolution);
}
