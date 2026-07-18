using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// §F5: editable row for a single bracket leg (SL or one TP) inside ModifyOrderView. The originals
// are captured at Initialize time so HasChanges() can detect a diff without consulting the cache,
// and the cancel/modify dispatch in ConfirmAsync reads the parsed price+qty straight from here.
public partial class BracketLegRow : ObservableObject
{
    public required int LegId { get; init; }
    public required string Label { get; init; }       // "SL" | "TP1" | "TP2" | "TP3"
    public required bool IsStopLoss { get; init; }
    public required bool IsTakeProfit { get; init; }
    public required decimal OriginalPrice { get; init; }
    public required int OriginalQuantity { get; init; }
    public required CurrencyType Currency { get; init; }

    [ObservableProperty] private string _priceString = string.Empty;
    [ObservableProperty] private string _quantityString = string.Empty;

    public decimal? ParsedPrice()
    {
        var t = (PriceString ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(t)) return null;
        var p = CurrencyHelper.Parse(t, Currency);
        return p is decimal v && v > 0m ? v : null;
    }

    public int? ParsedQuantity()
    {
        var t = (QuantityString ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return int.TryParse(t, out var q) && q > 0 ? q : null;
    }

    public bool HasChanges()
    {
        var p = ParsedPrice();
        if (p is decimal pv && pv != OriginalPrice) return true;
        var q = ParsedQuantity();
        if (q is int qv && qv != OriginalQuantity) return true;
        return false;
    }

    // Build a row for a leg, capturing originals and seeding the editable strings.
    internal static BracketLegRow ForLeg(Order leg, string label)
    {
        bool isSL = leg.Stop == StopKind.Stop;
        var price = isSL ? (leg.StopPrice ?? 0m) : leg.Price;
        return new BracketLegRow
        {
            LegId = leg.OrderId,
            Label = label,
            IsStopLoss = isSL,
            IsTakeProfit = !isSL,
            OriginalPrice = price,
            OriginalQuantity = leg.Quantity,
            Currency = leg.CurrencyType,
            PriceString = CurrencyHelper.FormatForEdit(price, leg.CurrencyType),
            QuantityString = leg.Quantity.ToString(),
        };
    }
}
