using System.Text.Json;
using System.Threading.Channels;
using KieshStockExchange.Server.Services.Telemetry;
using KieshStockExchange.Services.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

// Backs the browser log viewer at /admin/logs.html. The JWT never touches a
// URL: an admin-gated POST mints a short-lived single-use ticket (header-bearer
// auth), and only that ticket rides the SSE stream's query string (EventSource
// can't set headers). This is also the first admin-role gate on the server.
[ApiController]
[Route("api/admin/logs")]
public sealed class AdminLogsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly TelemetryBus _bus;
    private readonly TelemetryTicketStore _tickets;

    public AdminLogsController(TelemetryBus bus, TelemetryTicketStore tickets)
    {
        _bus = bus;
        _tickets = tickets;
    }

    [HttpPost("ticket")]
    [Authorize(Roles = "admin")]
    public IActionResult Ticket()
        => Ok(new { ticket = _tickets.Issue(TimeSpan.FromSeconds(30)) });

    [HttpGet("stream")]
    [AllowAnonymous]
    public async Task Stream([FromQuery] string? ticket, CancellationToken ct)
    {
        if (!_tickets.TryConsume(ticket))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // defeat any proxy buffering

        // Bridge bus events (published on arbitrary threads) to this request's
        // single write loop via a bounded channel — DropOldest so a stalled
        // client sheds backlog instead of growing unbounded.
        var channel = Channel.CreateBounded<TelemetryEvent>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var sub = _bus.Subscribe(evt => channel.Writer.TryWrite(evt));

        try
        {
            // One loop owns all writes (Response.Body is not safe for concurrent
            // writers): drain queued events, else emit a 15s keepalive so Caddy
            // and intermediaries don't time the idle connection out.
            while (!ct.IsCancellationRequested)
            {
                var ready = channel.Reader.WaitToReadAsync(ct).AsTask();
                var idle = Task.Delay(TimeSpan.FromSeconds(15), ct);
                if (await Task.WhenAny(ready, idle).ConfigureAwait(false) == idle)
                {
                    await Response.WriteAsync(": keepalive\n\n", ct).ConfigureAwait(false);
                    await Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }
                if (!await ready.ConfigureAwait(false)) break; // channel completed
                while (channel.Reader.TryRead(out var evt))
                {
                    var json = JsonSerializer.Serialize(evt, JsonOpts);
                    await Response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
                }
                await Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* client disconnected — expected */ }
    }
}
