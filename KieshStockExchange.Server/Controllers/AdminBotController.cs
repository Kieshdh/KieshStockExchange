using KieshStockExchange.Services.BackgroundServices.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace KieshStockExchange.Server.Controllers;

// Phase 3 follow-up: the four bot ringbuffer CSV exports now stream from the
// server. Bot data lives server-side (Step 5 moved the ringbuffers there); the
// BotDashboard's export buttons GET from these endpoints, take the body, and
// save it via the OS save-file picker.
[ApiController]
[Route("api/admin/bots")]
public sealed class AdminBotController : ControllerBase
{
    private readonly IAiTradeService _bots;
    public AdminBotController(IAiTradeService bots) => _bots = bots;

    [HttpGet("failures.csv")]
    public IActionResult Failures(CancellationToken ct)
        => Csv(_bots.BuildFailuresCsv(ct), $"{_bots.SuggestedFailuresExportFileName}.csv");

    [HttpGet("reservation-ledger.csv")]
    public IActionResult ReservationLedger(CancellationToken ct)
        => Csv(_bots.BuildReservationLedgerCsv(ct), $"{_bots.SuggestedLedgerExportFileName}.csv");

    [HttpGet("economy.csv")]
    public IActionResult Economy(CancellationToken ct)
        => Csv(_bots.BuildEconomyCsv(ct), $"{_bots.SuggestedEconomyExportFileName}.csv");

    [HttpGet("sentiment.csv")]
    public IActionResult Sentiment(CancellationToken ct)
        => Csv(_bots.BuildSentimentCsv(ct), $"{_bots.SuggestedSentimentExportFileName}.csv");

    // Counters surface alongside the CSVs so the dashboard can show
    // "Exported N rows" without parsing the body. JSON shape kept flat — one
    // poll per dashboard refresh.
    [HttpGet("counts")]
    public IActionResult Counts()
        => Ok(new BotRingCounts(
            FailureRows:        _bots.RecentFailureRecords.Count,
            LedgerRows:         _bots.ReservationLedgerEntryCount,
            EconomyRows:        _bots.EconomySampleCount,
            SentimentRows:      _bots.SentimentSampleCount));

    private FileContentResult Csv(string body, string fileName)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }
}

public sealed record BotRingCounts(int FailureRows, int LedgerRows, int EconomyRows, int SentimentRows);
