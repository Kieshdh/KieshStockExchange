using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed partial class LiveQuote : ObservableObject
{
    #region Observable Properties
    // Static info
    [ObservableProperty] private int _stockId = 0;
    [ObservableProperty] private string _symbol = "-";
    [ObservableProperty] private string _companyName = "-";
    [ObservableProperty] private CurrencyType _currency = CurrencyType.USD;

    // Timestamps
    [ObservableProperty] private DateTime _lastUpdated = DateTime.MinValue;
    [ObservableProperty] private DateTime _sessionStartUtc = TimeHelper.UtcStartOfToday();

    // Live session stats
    [ObservableProperty] private decimal _lastPrice = 0m;
    [ObservableProperty] private decimal _open = 0m;
    [ObservableProperty] private decimal _high = 0m;
    [ObservableProperty] private decimal _low = 0m;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBullish))]
    [NotifyPropertyChangedFor(nameof(IsBearish))]
    private decimal _changePct = 0m;
    [ObservableProperty] private int _volume = 0;
    // Cumulative traded value for the session (sum of price*shares per tick).
    [ObservableProperty] private decimal _dollarVolume = 0m;

    // Pre-formatted strings used by the UI
    [ObservableProperty] private string _lastPriceDisplay = "-";
    [ObservableProperty] private string _openPriceDisplay = "-";
    [ObservableProperty] private string _highPriceDisplay = "-";
    [ObservableProperty] private string _lowPriceDisplay = "-";
    [ObservableProperty] private string _changePctDisplay = "-";
    [ObservableProperty] private string _dollarVolumeDisplay = "-";
    #endregion

    #region Constructor
    public LiveQuote(int stockId, CurrencyType currency)
    {
        StockId = stockId;
        Currency = currency;
    }
    #endregion

    #region Tick and Snapshot Application
    /// <summary>
    /// Apply a single executed tick to the live session stats. Returns false for invalid
    /// inputs or out-of-order ticks from a previous session.
    /// </summary>
    public bool ApplyTick(decimal price, int shares, DateTime utcTime)
    {
        // Ignore invalid prices or share counts
        if (price <= 0m || shares < 0)
            return false;

        // New session: reset on UTC midnight rollover
        if (utcTime.Date > SessionStartUtc.Date)
        {
            SessionStartUtc = TimeHelper.UtcStartOfDay(utcTime);
            Open = High = Low = price;
            Volume = 0;
            DollarVolume = 0m;
        }
        // Ignore out-of-order ticks from previous sessions
        else if (utcTime.Date < SessionStartUtc.Date)
            return false;

        // Live stats
        if (Open <= 0m) Open = price;
        if (High == 0m || price > High) High = price;
        if (Low == 0m || price < Low) Low = price;
        Volume += shares;
        DollarVolume += price * shares;

        // Only advance LastPrice/LastUpdated if the tick is newer or equal
        if (utcTime >= LastUpdated)
        {
            LastUpdated = utcTime;
            LastPrice = price;
        }

        ChangePct = Open > 0 ? (LastPrice - Open) / Open * 100m : 0m;

        UpdatePriceDisplays();
        return true;
    }

    /// <summary>
    /// Replace the current session stats with a server-provided snapshot. Returns false
    /// if any of the snapshot values are invalid.
    /// </summary>
    public bool ApplySnapshot(decimal lastPrice, int sessionVolume,
        decimal open, decimal high, decimal low, DateTime lastUtc)
    {
        // Validate the snapshot
        if (lastPrice <= 0m) return false;
        if (sessionVolume < 0) return false;
        if (open <= 0m || high <= 0m || low <= 0m) return false;
        if (lastUtc == DateTime.MinValue) return false;

        // Apply the snapshot
        SessionStartUtc = TimeHelper.UtcStartOfDay(lastUtc);
        Open = open; High = high; Low = low;
        LastUpdated = lastUtc; LastPrice = lastPrice;
        Volume = Math.Max(0, sessionVolume);
        // We don't have per-tick history at snapshot time; approximate dollar
        // volume as shares * average(open, last). Re-derived correctly as soon
        // as live ticks start applying via ApplyTick.
        DollarVolume = Math.Max(0m, Volume * ((Open + LastPrice) / 2m));
        ChangePct = Open > 0 ? (LastPrice - Open) / Open * 100m : 0m;

        UpdatePriceDisplays();
        return true;
    }
    #endregion

    #region Display Formatting
    // Cached formatter inputs — skip CurrencyHelper.Format calls when nothing changed.
    private decimal _fmtLastPrice = decimal.MinValue;
    private decimal _fmtOpen = decimal.MinValue;
    private decimal _fmtHigh = decimal.MinValue;
    private decimal _fmtLow = decimal.MinValue;
    private decimal _fmtChangePct = decimal.MinValue;
    private decimal _fmtDollarVolume = decimal.MinValue;
    private CurrencyType _fmtCurrency = (CurrencyType)(-1);

    // Reformat everything on currency change; otherwise only what actually moved.
    private void UpdatePriceDisplays()
    {
        var currencyChanged = _fmtCurrency != Currency;
        if (currencyChanged) _fmtCurrency = Currency;

        if (currencyChanged || _fmtLastPrice != LastPrice)
        {
            _fmtLastPrice = LastPrice;
            LastPriceDisplay = CurrencyHelper.Format(LastPrice, Currency);
        }
        if (currencyChanged || _fmtOpen != Open)
        {
            _fmtOpen = Open;
            OpenPriceDisplay = CurrencyHelper.Format(Open, Currency);
        }
        if (currencyChanged || _fmtHigh != High)
        {
            _fmtHigh = High;
            HighPriceDisplay = CurrencyHelper.Format(High, Currency);
        }
        if (currencyChanged || _fmtLow != Low)
        {
            _fmtLow = Low;
            LowPriceDisplay = CurrencyHelper.Format(Low, Currency);
        }
        if (_fmtChangePct != ChangePct)
        {
            _fmtChangePct = ChangePct;
            ChangePctDisplay = ChangePct >= 0 ? $"+{ChangePct:F2}%" : $"{ChangePct:F2}%";
        }
        if (currencyChanged || _fmtDollarVolume != DollarVolume)
        {
            _fmtDollarVolume = DollarVolume;
            DollarVolumeDisplay = CurrencyHelper.Format(DollarVolume, Currency);
        }
    }

    public override string ToString() => $"LiveQuote: #{StockId} {Symbol} ({Currency})";
    #endregion

    #region Display helpers (for XAML data triggers)
    /// <summary>True when the day-change is positive — used by Market/Trending
    /// rows to colour the Change cell green via DataTrigger.</summary>
    public bool IsBullish => ChangePct > 0m;

    /// <summary>True when the day-change is negative — colours the Change
    /// cell red.</summary>
    public bool IsBearish => ChangePct < 0m;
    #endregion
}
