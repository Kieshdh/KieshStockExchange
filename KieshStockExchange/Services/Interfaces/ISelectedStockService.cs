using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services
{
    /// <summary>
    /// Shared, observable context for the currently selected stock and its live price.
    /// Exposes INotifyPropertyChanged so XAML bindings update when values change.
    /// </summary>
    public interface ISelectedStockService : INotifyPropertyChanged
    {
        // ---- Core selection --------------------------------------------------

        /// <summary> Check if there has been a stock selected and it is fully loaded.</summary>
        bool HasSelectedStock { get; }

        /// <summary>The fully loaded Stock currently selected (or null if none).</summary>
        Stock? SelectedStock { get; }

        /// <summary>The selected StockId (if available).</summary>
        int? StockId { get; }

        /// <summary>The selected stock's symbol (if available).</summary>
        string Symbol { get; }

        /// <summary>The selected stock's company name.</summary>
        string CompanyName { get; }

        /// <summary>The selected stock's currency (if available).</summary>
        CurrencyType Currency { get; }

        // ---- Live price ------------------------------------------------------

        /// <summary>The most recently fetched current price</summary>
        decimal CurrentPrice { get; }
        string CurrentPriceDisplay { get; }

        /// <summary>When the current price was last updated (local time).</summary>
        DateTimeOffset? PriceUpdatedAt { get; }

        // ---- Coordination helpers -------------------------------------------

        /// <summary>
        /// Await until a first selection is available. Useful for child ViewModels
        /// that may start before the parent sets the selection.
        /// </summary>
        Task<Stock> WaitForSelectionAsync();

        // ---- Commands --------------------------------------------------------

        /// <summary>Set the selection by StockId (loads the stock).</summary>
        Task Set(int stockId);

        /// <summary>Set the selection using a preloaded Stock (avoids refetch).</summary>
        Task Set(Stock stock);

        /// <summary>Fetch a fresh current price for the selected stock and publish it.</summary>
        Task UpdatePrice(CancellationToken ct = default);

        /// <summary>
        /// Start periodic price updates at the given interval. Implementations should ensure
        /// only one polling loop runs at a time.
        /// </summary>
        void StartPriceUpdates(TimeSpan interval);

        /// <summary>Stop periodic price updates if running.</summary>
        void StopPriceUpdates();

        /// <summary>Clear selection and live values (also resets WaitForSelectionAsync).</summary>
        void Reset();
    }
}
