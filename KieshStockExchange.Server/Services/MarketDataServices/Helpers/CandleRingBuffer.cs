using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers;

/// <summary>
/// Lock-free-ish FIFO of the last <see cref="Capacity"/> closed candles for a
/// single (stockId, currency, resolution) key. Producer is the CandleService
/// flush loop (one thread). Readers are HTTP request threads serving
/// historical-range queries. All writes + reads acquire the same short lock —
/// candles arrive at most once per FlushInterval per key, so contention is
/// minimal.
/// </summary>
internal sealed class CandleRingBuffer
{
    private readonly object _lock = new();
    private readonly Candle[] _buf;
    private int _head;   // index where the NEXT push will land
    private int _count;  // number of valid slots (0..Capacity)

    public int Capacity { get; }

    public CandleRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _buf = new Candle[capacity];
    }

    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Append. Oldest entry is overwritten when the buffer is full.</summary>
    public void Push(Candle candle)
    {
        lock (_lock)
        {
            _buf[_head] = candle;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>
    /// Returns candles in [fromUtc, toUtc) ordered by OpenTime ascending.
    /// Also reports the oldest OpenTime currently in the buffer so callers
    /// can decide whether the DB needs to be consulted for older candles.
    /// </summary>
    public (IReadOnlyList<Candle> Candles, DateTime? OldestOpenTime) Snapshot(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc) return (Array.Empty<Candle>(), null);

        lock (_lock)
        {
            if (_count == 0) return (Array.Empty<Candle>(), null);

            var result = new List<Candle>(Math.Min(_count, 64));
            DateTime? oldest = null;

            // Iterate logical-oldest -> logical-newest.
            var startIdx = (_head - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
            {
                var c = _buf[(startIdx + i) % Capacity];
                if (oldest is null) oldest = c.OpenTime;
                if (c.OpenTime >= fromUtc && c.OpenTime < toUtc)
                    result.Add(c);
            }
            return (result, oldest);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buf, 0, _buf.Length);
            _head = 0;
            _count = 0;
        }
    }
}
