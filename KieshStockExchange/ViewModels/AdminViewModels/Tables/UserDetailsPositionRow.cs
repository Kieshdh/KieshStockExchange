using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.AdminViewModels.Tables;

public sealed class UserDetailsPositionRow
{
    public Position Position { get; }
    public string Symbol { get; }
    public string QuantityDisplay => Position.Quantity == 0 ? "-" : Position.Quantity.ToString();
    public string ReservedDisplay => Position.ReservedQuantity == 0 ? "-" : Position.ReservedQuantity.ToString();
    public string PriceDisplay { get; }
    public string ValueDisplay { get; }

    public IAsyncRelayCommand EditCommand { get; }

    public UserDetailsPositionRow(Position position, string symbol, decimal? lastPrice, Func<Task> onEdit)
    {
        Position = position;
        Symbol = symbol;
        if (lastPrice.HasValue)
        {
            PriceDisplay = CurrencyHelper.Format(lastPrice.Value, CurrencyType.USD);
            ValueDisplay = CurrencyHelper.Format(
                CurrencyHelper.Notional(lastPrice.Value, position.Quantity, CurrencyType.USD),
                CurrencyType.USD);
        }
        else
        {
            PriceDisplay = "—";
            ValueDisplay = "—";
        }
        EditCommand = new AsyncRelayCommand(onEdit);
    }
}
