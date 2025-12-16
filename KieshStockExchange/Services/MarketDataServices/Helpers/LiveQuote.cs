using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed partial class LiveQuote : ObservableObject
{
    // Static info
    [ObservableProperty] private int _stockId = 0;
    [ObservableProperty] private string _symbol = "-";
    [ObservableProperty] private string _companyName = "-";
    [ObservableProperty] private CurrencyType _currency = CurrencyType.USD;

    // Timestamps
    [ObservableProperty] private DateTime _lastUpdated = DateTime.MinValue;
    [ObservableProperty] private DateTime _sessionStartUtc = TimeHelper.UtcStartOfToday();

    // Live stats
    [ObservableProperty] private decimal _lastPrice = 0m;       // Latest Price
    [ObservableProperty] private decimal _open = 0m;            // First Price of the session
    [ObservableProperty] private decimal _high = 0m;            // Highest Price of the session
    [ObservableProperty] private decimal _low = 0m;             // Lowest Price of the session
    [ObservableProperty] private decimal _changePct = 0m;       // Change % since open  
    [ObservableProperty] private int _volume = 0;               // Total shares traded in the session

    // Price displays
    [ObservableProperty] private string _lastPriceDisplay = "-";
    [ObservableProperty] private string _openPriceDisplay = "-";
    [ObservableProperty] private string _highPriceDisplay = "-";
    [ObservableProperty] private string _lowPriceDisplay = "-";
    [ObservableProperty] private string _changePctDisplay = "-";

    public LiveQuote(int stockId, CurrencyType currency)
    {
        StockId = stockId;
        Currency = currency;
    }

    public bool ApplyTick(decimal price, int shares, DateTime utcTime)
    {
        if (price <= 0m || shares < 0) 
            return false; // ignore invalid prices or share counts

        // New session?
        if (utcTime.Date > SessionStartUtc.Date)
        {
            // New session starts at UTC midnight
            SessionStartUtc = TimeHelper.UtcStartOfDay(utcTime);
            // Reset stats
            Open = High = Low = price;
            Volume = 0;
        }
        else if (utcTime.Date < SessionStartUtc.Date)
            return false; // Ignore out-of-order ticks from previous sessions

        // Live stats
        if (Open <= 0m) Open = price;
        if (High == 0m || price > High) High = price;
        if (Low == 0m || price < Low) Low = price;
        Volume += shares;

        // Update only if the tick is newer or equal to the last update
        if (utcTime >= LastUpdated)
        {
            LastUpdated = utcTime;
            LastPrice = price;
        }

        ChangePct = Open > 0 ? (LastPrice - Open) / Open * 100m : 0m;

        UpdatePriceDisplays();
        return true;
    }

    public bool ApplySnapshot(decimal lastPrice, int sessionVolume,
        decimal open, decimal high, decimal low, DateTime lastUtc)
    {
        if (lastPrice <= 0m) return false; // Ignore invalid prices
        if (sessionVolume < 0) return false; // Ignore invalid share counts
        if (open <= 0m || high <= 0m || low <= 0m) return false; // Ignore invalid prices
        if (lastUtc == DateTime.MinValue) return false; // Ignore invalid timestamps

        // Apply the snapshot
        SessionStartUtc = TimeHelper.UtcStartOfDay(lastUtc);
        Open = open; High = high; Low = low;
        LastUpdated = lastUtc; LastPrice = lastPrice;
        Volume = Math.Max(0, sessionVolume);
        ChangePct = Open > 0 ? (LastPrice - Open) / Open * 100m : 0m;

        UpdatePriceDisplays();
        return true;
    }

    private void UpdatePriceDisplays()
    {
        LastPriceDisplay = CurrencyHelper.Format(LastPrice, Currency);
        OpenPriceDisplay = CurrencyHelper.Format(Open, Currency);
        HighPriceDisplay = CurrencyHelper.Format(High, Currency);
        LowPriceDisplay = CurrencyHelper.Format(Low, Currency);
        ChangePctDisplay = ChangePct >= 0 ? $"+{ChangePct:F2}%" : $"{ChangePct:F2}%";
    }

    public override string ToString() => $"LiveQuote: #{StockId} {Symbol} ({Currency})";
}
