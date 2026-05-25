using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Globalization;
using System.Text;

namespace KieshStockExchange.Services.PortfolioServices.Helpers;

/// <summary>
/// One mutation that touches user balance / position / order reservation state, or
/// one Transaction insertion. The CSV export lets the engine team grep all events
/// for a phantom-holding user in chronological order and find which side of the
/// reservation accounting drifted.
///
/// Field semantics by <see cref="Kind"/>:
/// <list type="bullet">
///   <item>"Fund" — Before1/After1 = ReservedBalance, Before2/After2 = TotalBalance, Amount = delta amount.</item>
///   <item>"Position" — Before1/After1 = ReservedQuantity, Before2/After2 = Quantity (both cast to decimal), Amount = delta qty.</item>
///   <item>"Order" — Before1/After1 = CurrentBuyReservation, Before2/After2 = CurrentSellReservedQty (cast to decimal), Amount = delta.</item>
///   <item>"Transaction" — Before1/After1/Before2/After2 unused (zero), Quantity = fill qty, Price = trade price, Amount = TotalAmount, SecondaryUserId = seller.</item>
/// </list>
/// </summary>
public sealed record LedgerEntry(
    DateTime TimestampUtc,
    string Kind,
    int UserId,
    int? SecondaryUserId,
    int? OrderId,
    int? StockId,
    CurrencyType? Currency,
    string Action,
    decimal Amount,
    decimal Before1,
    decimal After1,
    decimal Before2,
    decimal After2,
    decimal Quantity,
    decimal Price);

/// <summary>Backwards-compat alias for the old fund-only record name.</summary>
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
    /// <summary>User IDs whose mutations are recorded. Empty + TrackAll=false = no tracing.</summary>
    HashSet<int> TrackedUserIds { get; }

    /// <summary>When true, every user's mutations are recorded regardless of TrackedUserIds.
    /// Costs memory: ring fills fast under full-fleet load (~10s of buffer at 20k bots).</summary>
    bool TrackAll { get; set; }

    /// <summary>Snapshot of the bounded ring buffer in oldest → newest order.</summary>
    IReadOnlyList<LedgerEntry> Snapshot();

    /// <summary>How many entries are currently in the ring buffer.</summary>
    int EntryCount { get; }

    /// <summary>Suggested file name (without extension) for the save-file dialog.</summary>
    string SuggestedExportFileName { get; }

    /// <summary>Records one Fund.ReservedBalance / TotalBalance mutation.</summary>
    void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
        decimal amount, decimal reservedBefore, decimal reservedAfter,
        decimal totalBefore, decimal totalAfter);

    /// <summary>Records one Position.ReservedQuantity / Quantity mutation.</summary>
    void LogPosition(int userId, int stockId, int? orderId, string action,
        decimal amount, int reservedBefore, int reservedAfter,
        int quantityBefore, int quantityAfter);

    /// <summary>Records one Order.CurrentBuyReservation / CurrentSellReservedQty mutation.</summary>
    void LogOrder(int userId, int orderId, string action,
        decimal amount,
        decimal buyReservationBefore, decimal buyReservationAfter,
        int sellReservedBefore, int sellReservedAfter);

    /// <summary>Records a Transaction insertion (one fill).</summary>
    void LogTransaction(int buyerId, int sellerId, int stockId, CurrencyType ccy,
        int buyOrderId, int sellOrderId, int quantity, decimal price, decimal totalAmount);

    /// <summary>Writes the ring buffer to a CSV file at <paramref name="path"/>.</summary>
    Task<string> ExportCsvAsync(string path, CancellationToken ct = default);

    /// <summary>In-memory CSV body for the ring buffer (header + rows).</summary>
    string BuildCsv(CancellationToken ct = default);

    /// <summary>Clears the ring buffer. Useful between sessions when investigating one run at a time.</summary>
    void Clear();
}

public sealed class ReservationLedger : IReservationLedger
{
    private const int RingCapacity = 250_000;

    // Curated list of 20: admin + the latest reconcile's top under-reserve
    // offenders + the one transient-phantom hit + a few historical phantom
    // users as controls (should now be clean post-DbClosed fix, useful to
    // detect regression). Adjust at runtime via the exposed HashSet.
    public HashSet<int> TrackedUserIds { get; } = new()
    {
        20001,                                                  // admin
        1350, 499, 1205, 158, 1055, 1391, 699, 52, 274, 1084,  // top 10 under-reserve
        118, 2644,                                              // recent phantom + sell offender
        806, 578, 1625, 1247, 911, 957, 35,                    // historical phantom controls
    };

    public bool TrackAll { get; set; } = false;

    private readonly Queue<LedgerEntry> _entries = new();
    private readonly object _lock = new();
    private readonly KieshStockExchange.Services.BackgroundServices.Helpers.RingBufferStore<LedgerEntry> _store
        = new("data/telemetry/reservation_ledger.ndjson");

    public ReservationLedger()
    {
        // Replay the prior session's ledger tail so post-restart diagnostics
        // still have the historical context. The ledger is the noisiest
        // tracker (250k entries cap) so the read is heavier than the others,
        // but only runs once at boot.
        var prior = _store.LoadTail(RingCapacity);
        foreach (var e in prior) _entries.Enqueue(e);
    }

    public int EntryCount { get { lock (_lock) return _entries.Count; } }

    public string SuggestedExportFileName =>
        $"reservation_ledger_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}";

    public IReadOnlyList<LedgerEntry> Snapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    public void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
        decimal amount, decimal reservedBefore, decimal reservedAfter,
        decimal totalBefore, decimal totalAfter)
    {
        if (!TrackAll && !TrackedUserIds.Contains(userId)) return;
        Append(new LedgerEntry(
            TimeHelper.NowUtc(), "Fund", userId, null, orderId, null, ccy, action,
            amount, reservedBefore, reservedAfter, totalBefore, totalAfter, 0m, 0m));
    }

    public void LogPosition(int userId, int stockId, int? orderId, string action,
        decimal amount, int reservedBefore, int reservedAfter,
        int quantityBefore, int quantityAfter)
    {
        if (!TrackAll && !TrackedUserIds.Contains(userId)) return;
        Append(new LedgerEntry(
            TimeHelper.NowUtc(), "Position", userId, null, orderId, stockId, null, action,
            amount, reservedBefore, reservedAfter, quantityBefore, quantityAfter, 0m, 0m));
    }

    public void LogOrder(int userId, int orderId, string action,
        decimal amount,
        decimal buyReservationBefore, decimal buyReservationAfter,
        int sellReservedBefore, int sellReservedAfter)
    {
        if (!TrackAll && !TrackedUserIds.Contains(userId)) return;
        Append(new LedgerEntry(
            TimeHelper.NowUtc(), "Order", userId, null, orderId, null, null, action,
            amount, buyReservationBefore, buyReservationAfter,
            sellReservedBefore, sellReservedAfter, 0m, 0m));
    }

    public void LogTransaction(int buyerId, int sellerId, int stockId, CurrencyType ccy,
        int buyOrderId, int sellOrderId, int quantity, decimal price, decimal totalAmount)
    {
        // Transactions are logged when the buyer OR seller is tracked.
        if (!TrackAll && !TrackedUserIds.Contains(buyerId) && !TrackedUserIds.Contains(sellerId)) return;
        Append(new LedgerEntry(
            TimeHelper.NowUtc(), "Transaction", buyerId, sellerId, buyOrderId, stockId, ccy,
            $"Tx:Sell#{sellOrderId}", totalAmount, 0m, 0m, 0m, 0m, quantity, price));
    }

    private void Append(LedgerEntry entry)
    {
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > RingCapacity) _entries.Dequeue();
        }
        _store.Append(entry);
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    public string BuildCsv(CancellationToken ct = default)
    {
        LedgerEntry[] snapshot;
        lock (_lock) snapshot = _entries.ToArray();

        var sb = new StringBuilder(2048 + snapshot.Length * 128);
        sb.AppendLine("TimestampUtc,Kind,UserId,SecondaryUserId,OrderId,StockId,Currency,Action,Amount,Before1,After1,Before2,After2,Quantity,Price,Delta1,Delta2");
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = snapshot[i];
            var delta1 = e.After1 - e.Before1;
            var delta2 = e.After2 - e.Before2;
            sb.Append(e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Kind).Append(',')
              .Append(e.UserId).Append(',')
              .Append(e.SecondaryUserId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
              .Append(e.OrderId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
              .Append(e.StockId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
              .Append(e.Currency?.ToString() ?? string.Empty).Append(',')
              .Append(Escape(e.Action)).Append(',')
              .Append(e.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Before1.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.After1.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Before2.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.After2.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Quantity.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Price.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(delta1.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(delta2.ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
        return sb.ToString();
    }

    public async Task<string> ExportCsvAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is required.", nameof(path));
        await File.WriteAllTextAsync(path, BuildCsv(ct), ct).ConfigureAwait(false);
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
