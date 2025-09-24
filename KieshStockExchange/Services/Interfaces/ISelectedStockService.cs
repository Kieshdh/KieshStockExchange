using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

/// <summary>
/// Shared, observable context for the currently selected stock and its live price.
/// Exposes INotifyPropertyChanged so XAML bindings update when values change.
/// </summary>
public interface ISelectedStockService : INotifyPropertyChanged
{
    // ---- Current selection & live values -----------------------------------
    bool HasSelectedStock { get; }
    Stock? SelectedStock { get; }
    int? StockId { get; }
    string Symbol { get; }
    string CompanyName { get; }
    CurrencyType Currency { get; }

    // ---- Current Price info ---------------------------------------
    decimal CurrentPrice { get; }
    string CurrentPriceDisplay { get; }

    DateTimeOffset? PriceUpdatedAt { get; }

    Task<Stock> WaitForSelectionAsync();

    // ---- Methods ------------------------------------------------

    /// <summary>Set the selection by StockId (loads the stock).</summary>
    Task Set(int stockId, CancellationToken ct = default);

    /// <summary>Set the selection using a preloaded Stock (avoids refetch).</summary>
    Task Set(Stock stock, CancellationToken ct = default);

    /// <summary>Change the currency for the current selection (if any).</summary>
    Task ChangeCurrencyAsync(CurrencyType currency, CancellationToken ct = default);

    /// <summary>Clear the current selection and live price.</summary>
    void Reset();

    /// <summary>Dispose the service and its resources.</summary>
    void Dispose();
}
