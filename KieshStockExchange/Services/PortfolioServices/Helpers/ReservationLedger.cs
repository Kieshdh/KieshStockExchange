using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace KieshStockExchange.Services.PortfolioServices.Helpers;

/// <summary>
/// One mutation on a tracked user's <see cref="Fund.ReservedBalance"/>. The CSV
/// export accumulates these so the engine team can sum the column and find which
/// caller is putting more in than it takes out — the reservation leak source.
/// </summary>
public sealed record ReservationLedgerEntry(
    DateTime TimestampUtc,
    int UserId,
    CurrencyType Currency,
    int? OrderId,
    string Action,
    decimal Amount,
    decimal ReservedBefore,
    decimal ReservedAfter,
    decimal TotalBefore,
    decimal TotalAfter);

public interface IReservationLedger
{
    /// <summary>User IDs whose fund mutations are recorded; empty = no tracing.</summary>
    HashSet<int> TrackedUserIds { get; }

    /// <summary>Snapshot of the bounded ring buffer in oldest → newest order.</summary>
    IReadOnlyList<ReservationLedgerEntry> Snapshot();

    /// <summary>How many entries are currently in the ring buffer.</summary>
    int EntryCount { get; }

    /// <summary>Suggested file name (without extension) for the save-file dialog.</summary>
    string SuggestedExportFileName { get; }

    /// <summary>
    /// Records one fund-reservation mutation. Cheap when the user isn't tracked —
    /// the HashSet.Contains check returns immediately and no record is allocated.
    /// </summary>
    void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
        decimal amount, decimal reservedBefore, decimal reservedAfter,
        decimal totalBefore, decimal totalAfter);

    /// <summary>Writes the ring buffer to a CSV file at <paramref name="path"/>.</summary>
    Task<string> ExportCsvAsync(string path, CancellationToken ct = default);

    /// <summary>Clears the ring buffer. Useful between sessions when investigating one run at a time.</summary>
    void Clear();
}

public sealed class ReservationLedger : IReservationLedger
{
    private const int RingCapacity = 50_000;

    // Pre-seeded with admin (20001), a few users from earlier phantom samples,
    // the buyers that hit "Reservation drift" errors, and the current top
    // phantom holders from the latest reconciler log. Adjust via the exposed
    // HashSet at runtime if you need to swap in different victims.
    public HashSet<int> TrackedUserIds { get; } = new()
    {
        20001,
        // Earlier phantom-heavy users
        159, 848, 911, 1336, 4017, 8069,
        // Buyers that hit "Reservation drift on buyer X" failures
        436, 1764, 536, 1232,
        // Top phantom users from the latest reconcile pass
        1286, 983, 404, 1350, 1051, 952, 806, 10, 539, 1256,
    };

    private readonly Queue<ReservationLedgerEntry> _entries = new();
    private readonly object _lock = new();

    public int EntryCount { get { lock (_lock) return _entries.Count; } }

    public string SuggestedExportFileName =>
        $"reservation_ledger_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}";

    public IReadOnlyList<ReservationLedgerEntry> Snapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    public void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
        decimal amount, decimal reservedBefore, decimal reservedAfter,
        decimal totalBefore, decimal totalAfter)
    {
        // Cheap reject path: avoids the record allocation + lock for the 19,990+
        // bots that aren't tracked. HashSet.Contains is O(1).
        if (!TrackedUserIds.Contains(userId)) return;

        var entry = new ReservationLedgerEntry(
            TimeHelper.NowUtc(), userId, ccy, orderId, action, amount,
            reservedBefore, reservedAfter, totalBefore, totalAfter);

        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > RingCapacity) _entries.Dequeue();
        }
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    public async Task<string> ExportCsvAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is required.", nameof(path));

        ReservationLedgerEntry[] snapshot;
        lock (_lock) snapshot = _entries.ToArray();

        var sb = new StringBuilder(2048 + snapshot.Length * 96);
        sb.AppendLine("TimestampUtc,UserId,Currency,OrderId,Action,Amount,ReservedBefore,ReservedAfter,TotalBefore,TotalAfter,DeltaReserved,DeltaTotal");
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = snapshot[i];
            var deltaReserved = e.ReservedAfter - e.ReservedBefore;
            var deltaTotal = e.TotalAfter - e.TotalBefore;
            sb.Append(e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(e.UserId).Append(',')
              .Append(e.Currency).Append(',')
              .Append(e.OrderId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
              .Append(Escape(e.Action)).Append(',')
              .Append(e.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.ReservedBefore.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.ReservedAfter.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.TotalBefore.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.TotalAfter.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(deltaReserved.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(deltaTotal.ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
        return path;
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        bool needsQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
