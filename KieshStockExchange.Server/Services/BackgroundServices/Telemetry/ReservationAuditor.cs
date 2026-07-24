using KieshStockExchange.Helpers;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Passive reservation-leak hunter. Compares the engine's cached
/// <c>ReservedQuantity</c> / <c>ReservedBalance</c> against the sums implied
/// by each user's open limit orders in DB and logs mismatches so the buggy
/// path can be spotted by pattern over time. Also fronts the reservation-
/// ledger CSV export.
/// </summary>
internal sealed class ReservationAuditor
{
    private int _sampleSize = 0; // Tunable: how many mismatches to include in the log details (top by |delta|)

    #region Services and Constructor
    private readonly IAccountsCache _accounts;
    private readonly IReservationLedger _ledger;
    private readonly ILogger<ReservationAuditor> _logger;
    // Only WARN when the over-reserved value exceeds this. With clamp=true the reconciler
    // fixes drift every pass, so the routine residual is sub-unit (≈0–2.3 across the whole
    // market) — that's self-healing rounding, not a leak, and logging it as a warning is just
    // noise. Above the threshold means something the clamp can't absorb → surface it.
    private readonly decimal _phantomWarnThreshold;

    internal ReservationAuditor(IAccountsCache accounts, IReservationLedger ledger,
        ILogger<ReservationAuditor> logger, decimal phantomWarnThreshold = 5.0m)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger   = ledger   ?? throw new ArgumentNullException(nameof(ledger));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _phantomWarnThreshold = Math.Max(0m, phantomWarnThreshold);
    }
    #endregion

    #region Audit
    internal async Task AuditAsync(bool clamp, CancellationToken ct)
    {
        IReadOnlyList<ReservationMismatch> mismatches;
        try
        {
            // Safe to clamp now: caller fires this at the post-batch quiescent frame
            // (no in-flight market orders) and ReconcileReservationsAsync corrects the
            // phantom direction under per-user gates — the two prerequisites that made
            // the old clamp unsafe.
            mismatches = await _accounts.ReconcileReservationsAsync(clamp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cooperative shutdown — the host cancelled mid-pass. Not an error.
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reservation reconcile pass failed");
            return;
        }

        if (mismatches.Count == 0)
        {
            // Clean pass — Debug so it doesn't heartbeat the operator log/viewer every cycle.
            _logger.LogDebug("Reservation reconcile: no mismatches across cached positions/funds.");
            return;
        }

        // Sort by |delta| desc so the worst leaks surface first.
        var ordered = mismatches.OrderByDescending(m => Math.Abs(m.Delta)).ToList();
        long phantomCount = 0;       // Delta > 0 — cache over-reserved (leak)
        long underCount = 0;         // Delta < 0 — cache under-reserved (refresh race / missing reserve)
        decimal phantomTotal = 0m;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Delta > 0m) { phantomCount++; phantomTotal += ordered[i].Delta; }
            else                       { underCount++; }
        }

        // Routine clamped rounding (phantomTotal under threshold) is self-healing — log at Debug
        // so it stays out of the viewer; only escalate to WARNING when the over-reservation is
        // large enough that the clamp didn't absorb it (a signal worth an operator's attention).
        if (phantomTotal <= _phantomWarnThreshold)
        {
            _logger.LogDebug(
                "Reservation reconcile: {Mismatch} mismatches ({Phantom} phantom, {Under} under-reserved, phantomTotal≈{Total:F2}) — within tolerance, clamped.",
                mismatches.Count, phantomCount, underCount, phantomTotal);
            return;
        }

        _logger.LogWarning(
            "Reservation reconcile: {Mismatch} mismatches ({Phantom} phantom, {Under} under-reserved, phantomTotal≈{Total:F2}) — exceeds tolerance {Tol:F2}.",
            mismatches.Count, phantomCount, underCount, phantomTotal, _phantomWarnThreshold);

        var sb = new StringBuilder();
        int sample = Math.Min(_sampleSize, ordered.Count);
        for (int i = 0; i < sample; i++)
        {
            var m = ordered[i];
            if (m.StockId is int sid)
            {
                sb.AppendLine(
                    $"  pos user={m.UserId} stock={sid}: expected={m.ExpectedReserved}, actual={m.ActualReserved}, delta={m.Delta} ({m.OpenOrderCount} open sells)");
            }
            else if (m.Currency is CurrencyType ccy)
            {
                sb.AppendLine(
                    $"  fund user={m.UserId} ccy={ccy}: expected={m.ExpectedReserved}, actual={m.ActualReserved}, delta={m.Delta} ({m.OpenOrderCount} open buys)");
            }
        }

        if (sb.Length > 0)
            _logger.LogWarning("Top {Sample} reservation mismatches:{NewLine}{Details}", sample, Environment.NewLine, sb.ToString().TrimEnd());
    }
    #endregion

    #region Export
    internal int LedgerEntryCount => _ledger.EntryCount;
    internal string SuggestedLedgerExportFileName => _ledger.SuggestedExportFileName;

    internal Task<string> ExportLedgerCsvAsync(string path, CancellationToken ct = default)
        => _ledger.ExportCsvAsync(path, ct);

    internal string BuildLedgerCsv(CancellationToken ct = default) => _ledger.BuildCsv(ct);
    #endregion
}
