using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// Candle stream lifecycle: (re)start the history load + live subscription on a stock/resolution switch,
// stream closed candles into the buffer, lazy-load older history on pan-to-edge, and tear the previous
// subscription down. The atomic CTS swap makes aggressive switching cancel the in-flight stream instead of
// queueing behind it.
public partial class ChartViewModel
{
    // §F7: one-shot viewport restore. Seeded from the session at construction and consumed once on the
    // first candle load so a later stock switch still snaps to live instead of re-applying a stale view.
    private (int Vis, int Off, bool YAuto, decimal? YMin, decimal? YMax)? _pendingRestore;

    // Atomic CTS swap. RestartStreamAsync cancels + disposes the previous CTS
    // before starting a new one. No SemaphoreSlim — aggressive switching
    // doesn't queue HTTP fetches behind a held gate.
    private CancellationTokenSource? _streamCts;
    private bool _loadingOlder;

    private async Task RestartStreamAsync(int? stockId, CurrencyType currency, CandleResolution res, CancellationToken outerCt)
    {
        // Atomic CTS swap. Any prior in-flight stream (gate-wait, HTTP fetch,
        // StreamCandlesLoop) sees its token cancel and bails. The new switch
        // never queues behind it — that was the aggressive-switch hang.
        var inner = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var prev = Interlocked.Exchange(ref _streamCts, inner);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            prev.Dispose();
        }

        // Unsubscribe previous key off the hot path.
        StopCandleStream();

        if (stockId is null)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _candleBuffer.Clear();
                OffsetFromLatest = 0;
                SyncLatestCandle();
                RequestRedraw();
            }).ConfigureAwait(false);
            Key = null;
            return;
        }

        Key = (stockId.Value, currency, res);
        var ct = inner.Token;

        IsBusy = true;
        var startupFailed = false;
        try { await StartStreamingCandles(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* superseded by a newer switch */ }
        catch (Exception ex)
        {
            startupFailed = true;
            _logger.LogError(ex, "Starting candle stream failed.");
        }
        finally
        {
            IsBusy = false;
            // Faulted startup: clear our CTS only if it's still the active one
            // — a newer switch may have already replaced us atomically.
            if (startupFailed && Interlocked.CompareExchange(ref _streamCts, null, inner) == inner)
            {
                try { inner.Cancel(); } catch { }
                inner.Dispose();
            }
        }
    }

    private async Task StartStreamingCandles(CancellationToken ct)
    {
        if (Key is not { } key) return;

        _candles.Subscribe(key.StockId, key.Currency, key.Res);
        await _market.SubscribeAsync(key.StockId, key.Currency, ct).ConfigureAwait(false);

        // Load enough history to fill several screens
        var bucket = TimeSpan.FromSeconds((int)key.Res);
        var now = TimeHelper.NowUtc();
        var span = bucket * Math.Max(VisibleCount * MaxFactor, MinBuffer);
        var from = now - span;
        var history = await _candles.GetHistoricalCandlesAsync(key.StockId,
            key.Currency, key.Res, from, now, ct, true).ConfigureAwait(false);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _candleBuffer.Clear();
            // History from CandleService is already sorted; preserve order on insert.
            _candleBuffer.AddRange(history);

            if (_pendingRestore is { } vs)
            {
                // §F7: first load after (re)entering the page — restore the saved viewport instead of
                // the auto-zoom/snap-to-live defaults. Consume once so a later stock switch snaps live.
                _pendingRestore = null;
                if (vs.Vis >= MinVisible && vs.Vis <= MaxVisible) VisibleCount = vs.Vis;
                OffsetFromLatest = Math.Clamp(vs.Off, MinOffset, MaxOffset);
                if (!vs.YAuto && vs.YMin is decimal mn && vs.YMax is decimal mx && mx > mn)
                {
                    // Suppress autofit so the saved Y-window sticks. This runs inside the awaited
                    // history-load block, after TradeViewModel's on-stock-change IsYAutoFit=true, so
                    // it wins on restore.
                    ManualYMin = mn;
                    ManualYMax = mx;
                    IsYAutoFit = false;
                }
            }
            else
            {
                // Auto-zoom: if the requested viewport is much wider than the data
                // actually returned (e.g. a young server's 1h ring has 5 buckets but
                // VisibleCount expects ~120), shrink VisibleCount to fit. Otherwise
                // the chart renders 5 candle-dots spread across 600 bucket-widths of
                // horizontal space — looks empty even though data is present.
                if (history.Count > 0 && history.Count < VisibleCount)
                {
                    var fit = Math.Clamp(history.Count, MinVisible, MaxVisible);
                    if (fit != VisibleCount) VisibleCount = fit;
                }

                OffsetFromLatest = 0; // snap to live on (re)load
            }

            SyncLatestCandle();
            RequestRedraw();
        }).ConfigureAwait(false);

        _ = StreamCandlesLoopAsync(key.StockId, key.Currency, key.Res, ct);
    }

    private async Task StreamCandlesLoopAsync(int stockId, CurrencyType currency, CandleResolution res, CancellationToken ct)
    {
        try
        {
            await foreach (var candle in _candles.StreamClosedCandles(stockId, currency, res, ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    UpsertCandle(_candleBuffer, candle);
                    SyncLatestCandle();
                    RequestRedraw();
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Closed candle stream error."); }
    }

    private void StopCandleStream()
    {
        if (Key is not { } key) return;
        var oldKey = key;
        Key = null;
        var ct = CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                await _candles.UnsubscribeAsync(oldKey.StockId, oldKey.Currency, oldKey.Res, ct).ConfigureAwait(false);
                await _market.Unsubscribe(oldKey.StockId, oldKey.Currency, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Unsubscribing previous candle stream failed."); }
        });
    }

    private async Task LoadOlderAsync()
    {
        if (_loadingOlder) return;
        if (Key is not { } key) return;
        _loadingOlder = true;
        try
        {
            // Buffer is sorted ascending; the earliest open time is the first element.
            var firstOpen = await MainThread.InvokeOnMainThreadAsync(() =>
                _candleBuffer.Count > 0 ? _candleBuffer[0].OpenTime : TimeHelper.NowUtc()
            ).ConfigureAwait(false);

            var bucket = TimeSpan.FromSeconds((int)key.Res);
            var to = firstOpen;
            var from = to - bucket * Math.Max(VisibleCount * 2, 50);

            var older = await _candles.GetHistoricalCandlesAsync(key.StockId, key.Currency,
                key.Res, from, to, CancellationToken.None, true).ConfigureAwait(false);
            if (older.Count == 0) return;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // older is already sorted ascending; only take buckets strictly older than the
                // current first bucket — no dedup HashSet needed since they cannot overlap.
                int insertAt = 0;
                foreach (var c in older)
                {
                    if (c.OpenTime >= firstOpen) break;
                    _candleBuffer.Insert(insertAt, c);
                    insertAt++;
                }
                if (insertAt > 0) RequestRedraw();
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Loading older candles failed."); }
        finally { _loadingOlder = false; }
    }
}
