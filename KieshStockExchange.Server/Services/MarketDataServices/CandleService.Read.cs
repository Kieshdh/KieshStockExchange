using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed partial class CandleService
{
    public async Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(
        int stockId, CurrencyType currency, CandleResolution resolution,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default, bool fillGaps = false)
    {
        CheckKey(stockId, currency, resolution);

        // Align the range to bucket boundaries to avoid partial first/last buckets
        var span = TimeSpan.FromSeconds((int)resolution);
        var fromAligned = TimeHelper.FloorToBucketUtc(fromUtc, span);
        var toAligned = TimeHelper.NextBucketBoundaryUtc(toUtc, span);

        // Hot-ring fast path: if the per-key ring covers fromAligned, serve
        // entirely from RAM. Chart switches between resolutions hit this for
        // the common "last hour / last day" windows once the ring is warm.
        if (_recent.TryGetValue((stockId, currency, resolution), out var ring))
        {
            var (ringCandles, oldest) = ring.Snapshot(fromAligned, toAligned);
            if (oldest is DateTime o && o <= fromAligned && ringCandles.Count > 0)
            {
                if (!fillGaps) return ringCandles;
                return FillGaps(ringCandles, toAligned, span, stockId, currency);
            }
        }

        // Load from DB and sort by time
        var list = await _db.GetCandlesByStockIdAndTimeRange(stockId, currency,
            span, fromAligned, toAligned, ct).ConfigureAwait(false);
        list.Sort(static (a, b) => a.OpenTime.CompareTo(b.OpenTime));

        // No persisted candles for this resolution: rebuild from transactions and persist them.
        if (list.Count == 0)
        {
            var ticks = await _db.GetTransactionsByStockIdAndTimeRange(
                stockId, currency, fromAligned, toAligned, ct: ct).ConfigureAwait(false);
            if (ticks.Count > 0)
            {
                list = ReplayTicksBuildClosed(stockId, currency, resolution, ticks, toAligned, ct);
                list.Sort(static (a, b) => a.OpenTime.CompareTo(b.OpenTime));
                await PersistAndPublishAsync((stockId, currency, resolution), list, ct).ConfigureAwait(false);
            }
        }

        if (!fillGaps) return list;
        if (list.Count == 0) return list;

        return FillGaps(list, toAligned, span, stockId, currency);
    }

    /// <summary>
    /// Walks the candle list forward, inserting flat-priced fillers at any missing
    /// bucket so the chart can render gapless. Anything before the first real candle
    /// is omitted — the left edge stays the stock's first trade, not a synthesized
    /// pre-history.
    /// </summary>
    private IReadOnlyList<Candle> FillGaps(IReadOnlyList<Candle> list, DateTime toAligned, TimeSpan span,
        int stockId, CurrencyType currency)
    {
        var result = new List<Candle>(list.Count);
        var firstRealOpen = list[0].OpenTime;
        decimal lastPrice = list[0].Open;
        var i = 0;

        for (var t = firstRealOpen; t < toAligned; t = t.Add(span))
        {
            if (i < list.Count && list[i].OpenTime == t)
            {
                var c = list[i++];
                result.Add(c);
                lastPrice = c.Close;
            }
            else
            {
                result.Add(NewCandle(stockId, currency, t, span, lastPrice));
            }
        }
        return result;
    }
}
