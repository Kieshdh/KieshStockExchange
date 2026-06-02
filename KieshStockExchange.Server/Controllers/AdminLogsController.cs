using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using KieshStockExchange.Server.Services.Telemetry;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace KieshStockExchange.Server.Controllers;

// Server-Sent Events stream of live bot telemetry, for the browser viewer at
// /admin/logs.html. AllowAnonymous because EventSource can't send an
// Authorization header — the admin JWT rides ?token= and is validated inline.
// This is also the first admin-role gate on the server.
[ApiController]
[Route("api/admin/logs")]
[AllowAnonymous]
public sealed class AdminLogsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly TelemetryBus _bus;
    private readonly JwtSettings _jwt;

    public AdminLogsController(TelemetryBus bus, JwtSettings jwt)
    {
        _bus = bus;
        _jwt = jwt;
    }

    [HttpGet("stream")]
    public async Task Stream([FromQuery] string? token, CancellationToken ct)
    {
        if (!IsValidAdmin(token))
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

    private bool IsValidAdmin(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = _jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
            return principal.IsInRole("admin");
        }
        catch { return false; }
    }
}
