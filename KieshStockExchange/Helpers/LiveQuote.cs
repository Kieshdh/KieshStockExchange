using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Helpers;
public sealed partial class LiveQuote : ObservableObject
{
    [ObservableProperty] private int _stockId = 0;
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _companyName = "";
    [ObservableProperty] private DateTime _lastUpdated = DateTime.UtcNow;
    [ObservableProperty] private CurrencyType _currency = CurrencyType.USD;
    [ObservableProperty] private decimal _lastPrice = 0m;   // last traded/quoted price
    [ObservableProperty] private decimal _open = 0m;        // session open (for % change)
    [ObservableProperty] private decimal _high = 0m;        // session high
    [ObservableProperty] private decimal _low = 0m;         // session low
    [ObservableProperty] private decimal _changePct = 0m;   // (last - open)/open

    public void ApplyTick(decimal price, DateTime utcTime)
    {
        // Live stats
        LastPrice = price;
        LastUpdated = utcTime;
        // Session stats
        if (Open <= 0m) Open = price;
        if (High == 0m || price > High) High = price;
        if (Low == 0m || price < Low) Low = price;
        // Change %
        ChangePct = Open > 0 ? (LastPrice - Open) / Open * 100m : 0m;
    }
}
