using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services;
using static SQLite.TableMapping;

namespace KieshStockExchange.Helpers;

public sealed partial class LiveQuote : ObservableObject
{
    // Static info
    [ObservableProperty] private int _stockId = 0;
    [ObservableProperty] private string _symbol = "-";
    [ObservableProperty] private string _companyName = "-";
    [ObservableProperty] private CurrencyType _currency = CurrencyType.USD;

    // Timestamps
    [ObservableProperty] private DateTime _lastUpdated = DateTime.UtcNow;

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

    public LiveQuote(int stockId, CurrencyType currency)//, string symbol, string companyName)
    {
        StockId = stockId;
        Currency = currency;
        //Symbol = symbol;
        //CompanyName = companyName;
    }


    public void ApplyTick(decimal price, int shares, DateTime utcTime)
    {
        if (price <= 0m) 
            return; // ignore invalid prices

        // Live stats
        if (utcTime > LastUpdated) LastPrice = price;
        if (utcTime > LastUpdated) LastUpdated = utcTime;

        // Session stats
        if (Open <= 0m) Open = price;
        if (High == 0m || price > High) High = price;
        if (Low == 0m || price < Low) Low = price;
        ChangePct = Open > 0 ? (LastPrice - Open) / Open * 100m : 0m;
        Volume += shares;

        UpdatePriceDisplays();
    }

    private void UpdatePriceDisplays()
    {
        LastPriceDisplay = CurrencyHelper.Format(LastPrice, Currency);
        OpenPriceDisplay = CurrencyHelper.Format(Open, Currency);
        HighPriceDisplay = CurrencyHelper.Format(High, Currency);
        LowPriceDisplay = CurrencyHelper.Format(Low, Currency);
        ChangePctDisplay = ChangePct >= 0 ? $"+{ChangePct:F2}%" : $"{ChangePct:F2}%";
    }

    public override string ToString() => $"LiveQuote: ({Symbol}, {Currency.ToString()}";
}
