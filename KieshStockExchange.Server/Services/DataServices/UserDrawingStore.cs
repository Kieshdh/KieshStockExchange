using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// Buffered write-behind for per-user chart drawings (UP-STORE). The controller POST enqueues
/// into an in-memory dirty-set that returns immediately; a relaxed background loop drains it to
/// the DB, coalescing repeated saves of the same key into one upsert. Modeled on
/// <c>CandleService.FlushLoopAsync</c> but as a <see cref="BackgroundService"/> (cleaner for a
/// low-traffic path) and with its shutdown final-drain discipline copied exactly.
/// <para>
/// A delete is a <b>tombstone</b> (<c>Json == null</c>) buffered in the same dirty-set, so saves
/// and deletes are one last-write-wins stream — this removes the delete-vs-flush resurrection race.
/// </para>
/// <para>
/// Drain ordering is <b>write-then-remove</b>: persist first (always with
/// <see cref="CancellationToken.None"/> — a drained entry lives only in the buffer, so the write
/// must not be abandoned on shutdown), then remove the key only if its value is unchanged. A newer
/// write that lands mid-drain is left for the next tick; a failed write simply stays buffered and
/// retries. At-least-once delivery is safe because the SQL upsert is idempotent.
/// </para>
/// </summary>
public sealed class UserDrawingStore : BackgroundService
{
    private readonly record struct Key(int UserId, int StockId, string Currency);

    // Json == null is a delete tombstone.
    private readonly ConcurrentDictionary<Key, (string? Json, DateTime UpdatedAt)> _dirty = new();

    private readonly IUserDrawingQueries _queries;
    private readonly ILogger<UserDrawingStore> _logger;
    private readonly TimeSpan _flushInterval;

    private DateTime _lastFlushUtc = DateTime.MinValue;

    public UserDrawingStore(IUserDrawingQueries queries, IConfiguration config, ILogger<UserDrawingStore> logger)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Relaxed cadence (no rush); clamp a floor so a misconfigured 0 can't hot-loop the drain.
        var seconds = Math.Max(2, config.GetValue("Drawings:FlushIntervalSeconds", 10));
        _flushInterval = TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Buffer a save. Cheap, lock-free, returns immediately (the POST does not wait for the DB).</summary>
    public void Enqueue(int userId, int stockId, string currency, string json)
        => _dirty[new Key(userId, stockId, currency)] = (json, TimeHelper.NowUtc());

    /// <summary>Buffer a delete as a tombstone (applied on the next drain, same window as a save).</summary>
    public void Delete(int userId, int stockId, string currency)
        => _dirty[new Key(userId, stockId, currency)] = (null, TimeHelper.NowUtc());

    /// <summary>
    /// Read-your-writes: a buffered value (or tombstone → null) wins over the DB, so a GET after a
    /// POST is consistent even before the flush lands.
    /// </summary>
    public async Task<string?> GetAsync(int userId, int stockId, string currency, CancellationToken ct)
    {
        if (_dirty.TryGetValue(new Key(userId, stockId, currency), out var buffered))
            return buffered.Json;   // null tombstone => "no drawing"
        var row = await _queries.GetUserDrawingAsync(userId, stockId, currency, ct).ConfigureAwait(false);
        return row?.Json;
    }

    /// <summary>Backlog size — for an optional health probe / tests.</summary>
    internal int DirtyCount => _dirty.Count;
    internal DateTime LastFlushUtc => _lastFlushUtc;

    /// <summary>
    /// One drain pass, write-then-remove per key. Extracted so unit tests can drive a deterministic
    /// flush without waiting on the timer. Always persists with <see cref="CancellationToken.None"/>.
    /// </summary>
    internal async Task FlushOnceAsync()
    {
        int applied = 0;
        foreach (var kvp in _dirty)   // ConcurrentDictionary enumeration tolerates concurrent writes
        {
            var key = kvp.Key;
            var val = kvp.Value;
            try
            {
                if (val.Json is null)
                {
                    await _queries.DeleteUserDrawingAsync(key.UserId, key.StockId, key.Currency, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _queries.UpsertUserDrawingAsync(new UserDrawingRow
                    {
                        UserId = key.UserId,
                        StockId = key.StockId,
                        Currency = key.Currency,
                        Json = val.Json,
                        UpdatedAt = val.UpdatedAt,
                    }, CancellationToken.None).ConfigureAwait(false);
                }

                // Remove ONLY if unchanged since the snapshot — a newer Enqueue/Delete that landed
                // during the write stays buffered and is applied on the next tick.
                _dirty.TryRemove(kvp);
                applied++;
            }
            catch (Exception ex)
            {
                // Leave the key buffered (retried next tick); one poison key can't stall the others.
                _logger.LogError(ex, "Persisting drawing for user {UserId} stock {StockId} {Currency} failed.",
                    key.UserId, key.StockId, key.Currency);
            }
        }

        if (applied > 0)
        {
            _lastFlushUtc = TimeHelper.NowUtc();
            _logger.LogInformation("Flushed {Count} chart-drawing writes.", applied);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_flushInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushOnceAsync().ConfigureAwait(false);
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { } // shutdown — fall through to the final drain
            catch (Exception ex) { _logger.LogError(ex, "Error in chart-drawings flush loop."); }
        }

        // Final drain on the way out (already CancellationToken.None internally) so the last batch
        // isn't lost on restart — mirrors CandleService's shutdown-flush.
        try { await FlushOnceAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Error in final chart-drawings flush on shutdown."); }
    }
}
