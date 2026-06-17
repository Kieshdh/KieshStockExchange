using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
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
    private readonly IDataBaseService _db;
    private readonly BotTelemetryCache _cache;
    public AdminBotController(IAiTradeService bots, IDataBaseService db, BotTelemetryCache cache)
    {
        _bots = bots;
        _db = db;
        _cache = cache;
    }

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

    // Phase 3 Step 7b.1 — bot lifecycle + live status. Mirrors what the
    // BotDashboard used to read directly off IAiTradeService client-side. The
    // dashboard polls /status on a 1s timer and posts /start, /stop, /scaler
    // on button clicks.

    [HttpGet("status")]
    public ActionResult<BotStatusResponse> Status()
    {
        // FailureCategory enum → string so the JSON payload is stable across
        // enum reordering (client deserializes into a Dictionary<string, long>).
        var byCategory = new Dictionary<string, long>(_bots.FailuresByCategory.Count);
        foreach (var kv in _bots.FailuresByCategory)
            byCategory[kv.Key.ToString()] = kv.Value;

        return Ok(new BotStatusResponse(
            IsRunning:                _bots.LoopStartedAtUtc.HasValue,
            LoadedBotCount:           _bots.LoadedBotCount,
            OnlineBotCount:           _bots.OnlineBotCount,
            ActiveBotCap:             _bots.ActiveBotCap,
            MaxBotCap:                _bots.MaxBotCap,
            MinBotCap:                _bots.MinBotCap,
            AutoScale:                _bots.AutoScale,
            TickCount:                _bots.TickCount,
            TradesPlacedThisSession:  _bots.TradesPlacedThisSession,
            FailuresThisSession:      _bots.FailuresThisSession,
            TickWorkMsEwma:           _bots.TickWorkMsEwma,
            LastTickWorkMicros:       _bots.LastTickWorkMicros,
            LastLoadFraction:         _bots.LastLoadFraction,
            TradeIntervalMs:          _bots.TradeInterval.TotalMilliseconds,
            LastTradeAtUtc:           _bots.LastTradeAtUtc,
            LoopStartedAtUtc:         _bots.LoopStartedAtUtc,
            RecentFailures:           _bots.RecentFailures,
            FailuresByCategory:       byCategory,
            FailuresByStockId:        _bots.FailuresByStockId,
            RecentFailureRecordsCount: _bots.RecentFailureRecords.Count,
            CurrenciesToTrade:        _bots.CurrenciesToTrade));
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        await _bots.StartBotAsync(ct);
        return NoContent();
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        await _bots.StopBotAsync();
        return NoContent();
    }

    [HttpPost("failures/clear")]
    public IActionResult ClearFailures()
    {
        _bots.ClearFailures();
        return NoContent();
    }

    // Single endpoint covers all four scaler/cap settings. Body fields are
    // optional — null leaves the existing value alone. Matches the
    // SetActiveBotCap / SetMaxBotCap / MinBotCap / AutoScale call surface
    // BotDashboardViewModel mutates today.
    [HttpPost("scaler")]
    public IActionResult Scaler([FromBody] BotScalerSettings req)
    {
        if (req is null) return BadRequest();
        if (req.ActiveCap is not null || _bots.ActiveBotCap is not null) // null only clears if explicitly cleared
        {
            // Only call setter when the client actually wanted to change it
            // (treat ActiveCap = null in a payload with no other fields as
            // "no change"). The client's JSON serializer omits null props by
            // default, so checking presence via the deserialized record is
            // sufficient.
            _bots.SetActiveBotCap(req.ActiveCap);
        }
        if (req.MaxCap is not null) _bots.SetMaxBotCap(req.MaxCap);
        if (req.MinCap is not null) _bots.MinBotCap = req.MinCap.Value;
        if (req.AutoScale is not null) _bots.AutoScale = req.AutoScale.Value;
        return NoContent();
    }

    [HttpGet("ai-user-ids")]
    public ActionResult<IReadOnlyCollection<int>> AiUserIds()
        => Ok(_bots.GetAiUserIds());

    [HttpGet("activity-samples")]
    public ActionResult<IReadOnlyList<BotActivitySample>> ActivitySamples()
        => Ok(_bots.GetActivitySamples());

    // Server-side aggregation for the BotDashboard's "Last 24h" card. Returns
    // total bot-touched trades, sum of volume, and unique bot participants in
    // the last 24h. Previously the client fetched every transaction since the
    // 24h cutoff and aggregated locally — for a hot DB with millions of rows
    // that hit the HttpClient.Timeout (100s) before returning. Now the
    // aggregation runs in-process next to the DB.
    [HttpGet("last-24h-stats")]
    public async Task<ActionResult<BotLast24hStats>> Last24hStats(CancellationToken ct)
        => Ok(await _cache.GetLast24hAsync(ct).ConfigureAwait(false));

    // Per-bucket trades + volume for the BotDashboard activity chart. Server
    // does the windowing so the wire payload is fixed at bucketCount entries
    // (not the full transaction list).
    [HttpGet("activity-buckets")]
    public async Task<ActionResult<BotActivityBuckets>> ActivityBuckets(
        [FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc, [FromQuery] int bucketCount,
        CancellationToken ct)
    {
        if (toUtc <= fromUtc || bucketCount <= 0)
            return BadRequest("toUtc must be after fromUtc and bucketCount must be positive.");
        return Ok(await _cache.GetActivityBucketsAsync(fromUtc, toUtc, bucketCount, ct).ConfigureAwait(false));
    }

    private FileContentResult Csv(string body, string fileName)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }
}

public sealed record BotRingCounts(int FailureRows, int LedgerRows, int EconomyRows, int SentimentRows);
public sealed record BotLast24hStats(int Trades, decimal Volume, int ActiveBots);
public sealed record BotActivityBuckets(int[] Trades, decimal[] Volume);
