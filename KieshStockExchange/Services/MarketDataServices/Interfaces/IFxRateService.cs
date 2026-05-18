using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketDataServices.Interfaces;

/// <summary>
/// In-memory FX rates with a slow AR(1) drift. Driven by a single
/// <see cref="Tick"/> call from the bot loop's <c>CheckTimers</c>;
/// mirrors the shape of <c>BotSentimentService</c>. No persistence.
/// </summary>
public interface IFxRateService
{
    /// <summary> Mid rate "1 from = X to". Returns 1m when from == to. </summary>
    decimal GetMidRate(CurrencyType from, CurrencyType to);

    /// <summary>
    /// Bid (selling FROM into TO at the lower side) and ask (buying TO with
    /// FROM at the upper side) around the mid rate. The 0.2% spread keeps
    /// the static-table behaviour but lets the Convert page show the cost.
    /// </summary>
    (decimal Bid, decimal Ask) GetBidAsk(CurrencyType from, CurrencyType to);

    /// <summary> Raised when a pair's mid rate is updated. </summary>
    event EventHandler<FxRateUpdatedEventArgs>? RateUpdated;

    /// <summary> Re-seed every pair from its base rate. Called by <c>AiTradeService.ResetSessionState</c>. </summary>
    void Reset();

    /// <summary>
    /// Advance any pair whose tick clock has expired (60s cadence by default).
    /// Cheap when nothing has expired.
    /// </summary>
    void Tick(DateTime now);
}

public sealed class FxRateUpdatedEventArgs : EventArgs
{
    public CurrencyType From { get; }
    public CurrencyType To { get; }
    public decimal OldMid { get; }
    public decimal NewMid { get; }

    public FxRateUpdatedEventArgs(CurrencyType from, CurrencyType to, decimal oldMid, decimal newMid)
    {
        From = from;
        To = to;
        OldMid = oldMid;
        NewMid = newMid;
    }
}
