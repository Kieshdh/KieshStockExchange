namespace KieshStockExchange.Services.MarketDataServices;

// Server-side shim for MAUI's Microsoft.Maui.Dispatching.IDispatcher. The client
// copies of MarketDataService / QuoteRegistry / TickPipeline use IDispatcher to
// marshal QuoteUpdated event invocations back to the UI thread. Server-side there
// is no UI thread — events fire on whatever thread the engine is on, and SignalR
// broadcasts on its own ThreadPool worker. Same constructor signatures keep the
// server copies textually identical to the client copies (easier diff during
// Step 7's client-cleanup pass), while NoopDispatcher just runs the action inline.
public interface IDispatcher
{
    bool Dispatch(Action action);
}

internal sealed class NoopDispatcher : IDispatcher
{
    public bool Dispatch(Action action)
    {
        action?.Invoke();
        return true;
    }
}
