using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.BackgroundServices;

/// <summary>
/// Background service that uses configured <see cref="AIUser"/> bots
/// to place orders in the market at a fixed cadence.
/// </summary>
public interface IAiTradeService
{
    /// <summary>Interval between trading ticks for the AI loop.</summary>
    TimeSpan TradeInterval { get; }

    /// <summary>How often to run daily housekeeping checks.</summary>
    TimeSpan DailyCheckInterval { get; }

    /// <summary>How often to reload AI users' portfolios and cached prices.</summary>
    TimeSpan ReloadAssetsInterval { get; }

    /// <summary>How often to prune stale or capacity-blocking open limit orders.</summary>
    TimeSpan PruneInterval { get; }

    /// <summary>Currencies that the AI users are allowed to trade.</summary>
    IReadOnlyList<CurrencyType> CurrenciesToTrade { get; }

    /// <summary>Total bots loaded into the trading context.</summary>
    int LoadedBotCount { get; }

    /// <summary>Bots currently flagged as online and eligible to trade.</summary>
    int OnlineBotCount { get; }

    /// <summary>Optional cap on the number of online bots; null means no cap.</summary>
    int? ActiveBotCap { get; }

    /// <summary>Number of trading-loop iterations completed in this session.</summary>
    long TickCount { get; }

    /// <summary>Successful order placements since the loop last started.</summary>
    long TradesPlacedThisSession { get; }

    /// <summary>Failed order placements since the loop last started.</summary>
    long FailuresThisSession { get; }

    /// <summary>UTC timestamp of the most recent fill, or null if no fills yet.</summary>
    DateTime? LastTradeAtUtc { get; }

    /// <summary>UTC timestamp of when the current loop started, or null when stopped.</summary>
    DateTime? LoopStartedAtUtc { get; }

    /// <summary>Snapshot of the most recent failure messages (bounded ring).</summary>
    IReadOnlyList<string> RecentFailures { get; }

    /// <summary>Raised after each trading-loop tick and on lifecycle changes.</summary>
    event EventHandler? StatsChanged;

    /// <summary>
    /// Adjusts the cadence and trading universe of the AI loop at runtime.
    /// Any null argument keeps the current value.
    /// </summary>
    void Configure(TimeSpan? tradeInterval = null,
        TimeSpan? dailyCheckInterval = null, TimeSpan? reloadAssetsInterval = null,
        IEnumerable<CurrencyType>? currencies = null, TimeSpan? pruneInterval = null);

    /// <summary>Sets a runtime cap on the number of online bots; null removes the cap.</summary>
    void SetActiveBotCap(int? cap);

    /// <summary>Returns a snapshot of all loaded bot UserIds.</summary>
    IReadOnlyCollection<int> GetAiUserIds();

    /// <summary>
    /// Starts the background trading loop if it is not already running.
    /// Safe to call multiple times.
    /// </summary>
    Task StartBotAsync(CancellationToken ct = default);

    /// <summary>
    /// Requests the background trading loop to stop and waits for it to finish.
    /// </summary>
    Task StopBotAsync();
}
