using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketDataServices.Interfaces;

/// <summary> In-memory FX rates with a slow AR(1) drift. No persistence. </summary>
public interface IFxRateService
{
    /// <summary> Mid rate "1 from = X to". Returns 1m when from == to. </summary>
    decimal GetMidRate(CurrencyType from, CurrencyType to);

    /// <summary> Bid/ask around the mid for the 0.2% Convert spread. </summary>
    (decimal Bid, decimal Ask) GetBidAsk(CurrencyType from, CurrencyType to);

    /// <summary> Raised when a pair's mid rate is updated. </summary>
    event EventHandler<FxRateUpdatedEventArgs>? RateUpdated;

    /// <summary> Re-seed every pair from its base rate. </summary>
    void Reset();

    /// <summary> Advance any pair whose 60s tick clock has expired. </summary>
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
