using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace KieshStockExchange.Server.Services.Telemetry;

/// <summary>
/// Mints short-lived single-use tickets for the SSE log stream. EventSource
/// can't send an Authorization header, so the browser authenticates a POST
/// (header-bearer) to get a ticket, then puts only that ticket on the stream
/// URL — keeping the long-lived admin JWT out of the query string / access logs.
/// </summary>
public sealed class TelemetryTicketStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tickets = new();

    public string Issue(TimeSpan ttl)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _tickets[id] = DateTimeOffset.UtcNow.Add(ttl);
        return id;
    }

    public bool TryConsume(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return false;
        if (!_tickets.TryRemove(ticket, out var expires)) return false; // single-use
        return expires > DateTimeOffset.UtcNow;
    }
}
