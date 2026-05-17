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
    #region Services and Constructor
    private readonly IAccountsCache _accounts;
    private readonly IReservationLedger _ledger;
    private readonly ILogger<ReservationAuditor> _logger;

    internal ReservationAuditor(IAccountsCache accounts, IReservationLedger ledger,
        ILogger<ReservationAuditor> logger)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger   = ledger   ?? throw new ArgumentNullException(nameof(ledger));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Audit
    internal async Task AuditAsync(CancellationToken ct)
    {
        IReadOnlyList<ReservationMismatch> mismatches;
        try
        {
            // clamp:false — report only. clamp:true was unsafe: transient
            // SlippageMarket/TrueMarket orders mid-Phase-3 are missed by the
            // expected calc, so legitimate reservations got zeroed and entire
            // batch groups failed downstream with "Reservation drift". Until
            // the reconciler can snapshot atomically under per-user gates AND
            // include in-flight market orders, stay passive.
            mismatches = await _accounts.ReconcileReservationsAsync(clamp: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reservation reconcile pass failed");
            return;
        }

        if (mismatches.Count == 0)
        {
            _logger.LogInformation("Reservation reconcile: no mismatches across cached positions/funds.");
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

        _logger.LogWarning(
            "Reservation reconcile: {Mismatch} mismatches ({Phantom} phantom, {Under} under-reserved, phantomTotal≈{Total:F2}).",
            mismatches.Count, phantomCount, underCount, phantomTotal);

        var sb = new StringBuilder();
        int sample = Math.Min(6, ordered.Count);
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
    #endregion
}
