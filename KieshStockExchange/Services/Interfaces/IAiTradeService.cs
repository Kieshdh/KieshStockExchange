using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

/// <summary>
/// Background service that uses configured <see cref="AIUser"/> bots
/// to place orders in the market at a fixed cadence.
/// </summary>
public interface IAiTradeService
{
    #region Timer and Currency Configuration 
    /// <summary>Interval between trading ticks for the AI loop.</summary>
    TimeSpan TradeInterval { get; }

    /// <summary>How often to recompute which AI users are online.</summary>
    TimeSpan OnlineCheckInterval { get; }

    /// <summary>How often to run daily housekeeping checks.</summary>
    TimeSpan DailyCheckInterval { get; }

    /// <summary>How often to reload AI users' portfolios and cached prices.</summary>
    TimeSpan ReloadAssetsInterval { get; }

    /// <summary>Currencies that the AI users are allowed to trade.</summary>
    IReadOnlyList<CurrencyType> CurrenciesToTrade { get; }

    /// <summary>
    /// Adjusts the cadence and trading universe of the AI loop at runtime.
    /// Any null argument keeps the current value.
    /// </summary>
    void Configure(TimeSpan? tradeInterval = null, TimeSpan? onlineCheckInterval = null,
        TimeSpan? dailyCheckInterval = null, TimeSpan? reloadAssetsInterval = null,
        IEnumerable<CurrencyType>? currencies = null);
    #endregion

    #region Lifecycle Management
    /// <summary>
    /// Starts the background trading loop if it is not already running.
    /// Safe to call multiple times.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Requests the background trading loop to stop and waits for it to finish.
    /// </summary>
    Task StopAsync();
    #endregion
}

