using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Helpers;

// Ring Buffer per stock for simple rolling metrics (prices + timestamps)
public sealed class RingBuffer
{
    #region Properties and Constructor
    private readonly object _gate = new();

    private readonly RingEntry[] Entries;
    private int Start = 0;
    private int Count = 0;
    private static long IdCounter = 0;

    public int Capacity => Entries.Length;
    public int Size => Count;
    public bool IsNotEmpty => Count > 0;
    public bool IsEmpty => Count == 0;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Entries = new RingEntry[capacity];
    }
    #endregion

    #region Add PriceEntries and Clear ring
    public void Add(decimal price, DateTime time)
    {
        lock (_gate)
        {
            int idx = (Start + Count) % Capacity;
            Entries[idx] = new RingEntry { Price = price, Time = TimeHelper.EnsureUtc(time), Id = IdCounter++ };
            if (Count < Capacity) Count++;
            else Start = (Start + 1) % Capacity;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Start = 0;
            Count = 0;
        }
    }
    #endregion

    #region Peek ends
    public RingEntry? TryPeekNewest()
    {
        lock (_gate)
        {
            if (Count == 0) return null;
            int idx = (Start + Count - 1) % Capacity;
            return Entries[idx];
        }
    }
    public RingEntry? TryPeekOldest()
    {
        lock (_gate)
        {
            if (Count == 0) return null;
            return Entries[Start];
        }
    }
    #endregion

    #region Enumerate Ring
    public IEnumerable<RingEntry> EnumerateNewestFirst()
    {
        RingEntry[] snapshot = Snapshot();
        for (int i = snapshot.Length - 1; i >= 0; i--)
            yield return snapshot[i];
    }
    public IEnumerable<RingEntry> EnumerateOldestFirst()
    {
        RingEntry[] snapshot = Snapshot();
        for (int i = 0; i < snapshot.Length; i++)
            yield return snapshot[i];
    }

    private RingEntry[] Snapshot()
    {
        lock (_gate)
        {
            int count = Count;
            int start = Start;

            var snapshot = new RingEntry[count];
            for (int i = 0; i < count; i++)
            {
                int idx = (start + i) % Capacity;
                snapshot[i] = Entries[idx];
            }
            return snapshot;
        }
    }
    #endregion
}

public readonly struct RingEntry
{
    public decimal Price { get; init; }
    public DateTime Time { get; init; }
    public long Id { get; init; }
}