using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Cumulative counters + 30s window snapshot for the bot trading loop. AiTradeService
/// increments via the Inc*/AddVolume helpers; the timer in CheckTimers calls
/// <see cref="LogWindow"/> to emit one summary line per window.
/// </summary>
internal sealed class BotStatsLogger
{
    #region Services and Constructor
    private readonly ILogger<BotStatsLogger> _logger;

    internal BotStatsLogger(ILogger<BotStatsLogger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Counters
    private long _buyTotal = 0;
    private long _sellTotal = 0;
    private long _limitTotal = 0;
    private long _slipMarketTotal = 0;
    private long _trueMarketTotal = 0;
    private long _cancelledTotal = 0;
    private decimal _volumeTotal = 0m;
    private readonly object _volumeLock = new();

    private long _buySnapshot, _sellSnapshot, _limitSnapshot, _slipSnapshot, _trueSnapshot, _cancelledSnapshot;
    private decimal _volumeSnapshot = 0m;

    internal void RecordPlacement(Order order)
    {
        if (order.IsBuyOrder)  Interlocked.Increment(ref _buyTotal);
        else                   Interlocked.Increment(ref _sellTotal);

        if      (order.IsLimitOrder)      Interlocked.Increment(ref _limitTotal);
        else if (order.IsSlippageOrder)   Interlocked.Increment(ref _slipMarketTotal);
        else if (order.IsTrueMarketOrder) Interlocked.Increment(ref _trueMarketTotal);
    }

    internal void AddVolume(decimal amount)
    {
        if (amount <= 0m) return;
        lock (_volumeLock) _volumeTotal += amount;
    }

    internal void AddCancelled(long count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _cancelledTotal, count);
    }

    internal void Reset()
    {
        Interlocked.Exchange(ref _buyTotal, 0);
        Interlocked.Exchange(ref _sellTotal, 0);
        Interlocked.Exchange(ref _limitTotal, 0);
        Interlocked.Exchange(ref _slipMarketTotal, 0);
        Interlocked.Exchange(ref _trueMarketTotal, 0);
        Interlocked.Exchange(ref _cancelledTotal, 0);
        lock (_volumeLock) _volumeTotal = 0m;
        _buySnapshot = _sellSnapshot = _limitSnapshot = _slipSnapshot = _trueSnapshot = _cancelledSnapshot = 0;
        _volumeSnapshot = 0m;
    }
    #endregion

    #region Window
    internal void LogWindow(int onlineBots, int loadedBots)
    {
        long buy    = Interlocked.Read(ref _buyTotal);
        long sell   = Interlocked.Read(ref _sellTotal);
        long lim    = Interlocked.Read(ref _limitTotal);
        long slip   = Interlocked.Read(ref _slipMarketTotal);
        long trueM  = Interlocked.Read(ref _trueMarketTotal);
        long cancel = Interlocked.Read(ref _cancelledTotal);
        decimal vol;
        lock (_volumeLock) vol = _volumeTotal;

        long dBuy    = buy    - _buySnapshot;
        long dSell   = sell   - _sellSnapshot;
        long dLim    = lim    - _limitSnapshot;
        long dSlip   = slip   - _slipSnapshot;
        long dTrue   = trueM  - _trueSnapshot;
        long dCancel = cancel - _cancelledSnapshot;
        decimal dVol = vol - _volumeSnapshot;

        _buySnapshot       = buy;
        _sellSnapshot      = sell;
        _limitSnapshot     = lim;
        _slipSnapshot      = slip;
        _trueSnapshot      = trueM;
        _cancelledSnapshot = cancel;
        _volumeSnapshot    = vol;

        // Pass RAW numbers as the structured properties (Vol via a format specifier so the rendered
        // text stays "$x" while the property stays a number). The telemetry sink forwards these as a
        // numeric Metrics map so the web viewer can aggregate the DATA across a time bucket instead of
        // re-parsing this rendered string.
        _logger.LogInformation(
            "BotStats[60s] @ {Time}: bots {Online}/{Loaded}, trades {Total} (buy {Buy}/sell {Sell}), " +
            "type (Limit {Limit}/SlipMarket {Slip}/TrueMarket {True}), cancelled {Cancelled}, volume ${Vol:N2}",
            TimeHelper.NowUtc().ToLocalTime().ToString("HH:mm:ss"), onlineBots, loadedBots,
            dBuy + dSell, dBuy, dSell, dLim, dSlip, dTrue, dCancel, dVol);
    }
    #endregion
}
