using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public partial class PositionTableObject : ObservableObject
{
    public Position Position { get; }
    public string Symbol { get; }
    public string Username { get; }
    public CurrencyType NativeCurrency { get; }
    public CurrencyType BaseCurrency { get; }
    public decimal NativePrice { get; }
    public int CurrentStockId { get; }

    public int UserId => Position.UserId;
    public int Quantity => Position.Quantity;
    public int ReservedQuantity => Position.ReservedQuantity;

    public decimal NativePriceForSort => NativePrice;
    public decimal NativeValueForSort => NativePrice * Quantity;

    public string QuantityDisplay => Quantity == 0 ? "-" : Quantity.ToString();
    public string ReservedQuantityDisplay => ReservedQuantity == 0 ? "-" : ReservedQuantity.ToString();
    public string PriceDisplay => NativePrice > 0m
        ? CurrencyHelper.Format(NativePrice, NativeCurrency)
        : "-";
    public string StockValueDisplay => NativePrice > 0m
        ? CurrencyHelper.Format(CurrencyHelper.Notional(NativePrice, Quantity, NativeCurrency), NativeCurrency)
        : "-";

    public string ValueBaseDisplay
    {
        get
        {
            if (NativePrice <= 0m) return "-";
            var nativeNotional = CurrencyHelper.Notional(NativePrice, Quantity, NativeCurrency);
            var converted = CurrencyHelper.Convert(nativeNotional, NativeCurrency, BaseCurrency);
            return CurrencyHelper.Format(converted, BaseCurrency);
        }
    }

    public string StockSymbol => Symbol;

    public IAsyncRelayCommand EditCommand { get; }

    public PositionTableObject(Position position, string symbol, decimal nativePrice,
        CurrencyType nativeCurrency, CurrencyType baseCurrency,
        int currentStockId, string username, Func<int, int, Task> onEdit)
    {
        Position = position ?? throw new ArgumentNullException(nameof(position));
        Symbol = symbol;
        NativePrice = nativePrice;
        NativeCurrency = nativeCurrency;
        BaseCurrency = baseCurrency;
        CurrentStockId = currentStockId;
        Username = username;
        EditCommand = new AsyncRelayCommand(() => onEdit(UserId, CurrentStockId));
    }
}
