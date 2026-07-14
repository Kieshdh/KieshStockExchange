using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class Candle : IValidatable
{
    // §bounce vwap: set once at startup by MidReference.Configure — the Shared model can't read server config.
    public static bool VwapClose = false;

    // §filtered-tape H/L (the SIP odd-lot / TradingView analog): fills below this size still count toward
    // volume/close/vwap but do NOT set High/Low — tiny prints sweeping thin levels are non-representative
    // of the accessible market and real consolidated tapes exclude them from the official range. 0 = off
    // (byte-identical). Set once at startup from config on BOTH server and client (the client builds the
    // live in-progress bar via the shared CandleAggregator).
    public static int HLMinFillSize = 0;

    // §filtered-tape H/L: running extremes over ELIGIBLE prices (seeded from Open on the first trade).
    // High/Low = these ∪ {current Close}, so an ineligible print at the extreme holds the wick only while
    // it IS the live close and releases when price returns — at finalization H/L match the consolidated
    // rule exactly. Transient like _vwapNotional (Clone carries them; never persisted).
    private decimal _hlEligHigh = 0m;
    private decimal _hlEligLow = 0m;

    // Running Σ price·qty for the vwap close; transient (CandleMapper/CandleRow map explicitly, never persisted).
    private decimal _vwapNotional = 0m;

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

    // §fear-greed: per-candle Fear/Greed composite (0..100), stamped at flush only when the composite gauge is
    // enabled — null otherwise. Nullable + unvalidated (like Min/MaxTransactionId): carried by Clone, never gates IsValid.
    private double? _marketMood = null;
    public double? MarketMood { get => _marketMood; set => _marketMood = value; }

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

        // §filtered-tape H/L: sub-threshold fills are H/L-INELIGIBLE (volume/close/vwap unaffected) —
        // High/Low = extremes over (eligible prices ∪ {Open, current Close}), the consolidated-tape
        // odd-lot rule. Off (threshold 0) every fill is eligible ⇒ values identical to the legacy update.
        var setsHL = HLMinFillSize <= 0 || tick.Quantity >= HLMinFillSize;

        if (VwapClose)
        {
            // §bounce vwap: eligible-H/L track the RAW tape (mid stamp ignored) so the running-VWAP close,
            // a convex combination of raw trade prices, stays inside the Close-enveloped [Low, High].
            var raw = tick.Price;
            SeedEligibleRange();
            if (setsHL)
            {
                if (raw > _hlEligHigh) _hlEligHigh = raw;
                if (raw < _hlEligLow) _hlEligLow = raw;
            }
            _vwapNotional += raw * tick.Quantity;
            Volume += tick.Quantity;
            TradeCount += 1;
            Close = _vwapNotional / Volume;
        }
        else
        {
            // §bounce: when the trade carries a bounce-free reference (mid/micro), the candle is built off
            // it instead of the last-trade price. The bar is seeded (NewCandle) with Open from the prior
            // last-trade close, so on the FIRST mid trade we re-anchor Open=High=Low to the mid series too,
            // otherwise a seed above the mid range would break the Low<=Open<=High invariant. Gated on
            // MidPrice.HasValue so the off arm (px == tick.Price) is byte-identical to the legacy branch.
            var px = tick.MidPrice ?? tick.Price;
            if (tick.MidPrice.HasValue && TradeCount == 0)
            {
                Open = px; High = px; Low = px;
                _hlEligHigh = px; _hlEligLow = px;   // re-anchor resets the eligible range too
            }
            SeedEligibleRange();
            if (setsHL)
            {
                if (px > _hlEligHigh) _hlEligHigh = px;
                if (px < _hlEligLow) _hlEligLow = px;
            }
            Close = px;

            Volume += tick.Quantity;
            TradeCount += 1;
        }

        // H/L recompute: eligible extremes enveloped by the current Close (an ineligible print at the
        // extreme holds the wick only while it IS the live close). Threshold 0 ⇒ every price fed the
        // eligible extremes ⇒ identical to the legacy grow-only update.
        High = Math.Max(_hlEligHigh, Close);
        Low  = Math.Min(_hlEligLow, Close);

        if (!IsValid())
            throw new InvalidOperationException("Candle is not valid after applying trade.");
    }

    // §filtered-tape H/L: arm the eligible-range accumulators. On a fresh bar they seed from the
    // (possibly re-anchored) Open; on a candle restored without its transient state (e.g. a live bar
    // rehydrated after a restart) they conservatively adopt the already-recorded range.
    private void SeedEligibleRange()
    {
        if (_hlEligHigh > 0m) return;
        if (TradeCount > 0) { _hlEligHigh = High; _hlEligLow = Low; }
        else                { _hlEligHigh = Open; _hlEligLow = Open; }
    }

    public void NoteTransactionId(int transactionId)
    {
        if (transactionId <= 0) throw new ArgumentException("Invalid transactionId.");
        if (!MinTransactionId.HasValue || transactionId < MinTransactionId.Value)
            MinTransactionId = transactionId;
        if (!MaxTransactionId.HasValue || transactionId > MaxTransactionId.Value)
            MaxTransactionId = transactionId;
    }

    public Candle Clone()
    {
        var c = new Candle
        {
            StockId = this.StockId, Currency = this.Currency,
            BucketSeconds = this.BucketSeconds, OpenTime = this.OpenTime,
            Open = this.Open, High = this.High, Low = this.Low, Close = this.Close,
            Volume = this.Volume, TradeCount = this.TradeCount,
            MinTransactionId = this.MinTransactionId, MaxTransactionId = this.MaxTransactionId,
            MarketMood = this.MarketMood,
        };
        c._vwapNotional = this._vwapNotional; // keep the running vwap alive across live-snapshot clones
        c._hlEligHigh = this._hlEligHigh;     // §filtered-tape H/L: eligible-range accumulators travel too
        c._hlEligLow = this._hlEligLow;
        return c;
    }

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
