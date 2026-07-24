using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

// Append-only NDJSON persistence for the bot telemetry ringbuffers
// (BotFailureTracker, BotEconomyTelemetry, BotSentimentService,
// ReservationLedger). One record per line, serialized via System.Text.Json.
// Each tracker owns one store and calls Append() for every new record;
// LoadTail(N) on construction reads the tail of the file back into its in-memory
// ringbuffer so a server restart doesn't lose the previous session's history.
//
// Append is non-blocking: the producer enqueues the serialized line on a
// bounded Channel and a single background consumer drains to disk. This keeps
// disk latency off the engine settlement path — a stalled drive or AV scan
// adds queue depth, not engine latency. The synchronous fallback fires only
// if the queue is full (capacity 50K lines), so audit data is never silently
// dropped.
//
// File location defaults to `./data/telemetry/{name}.ndjson` relative to the
// server working directory. The directory is created if missing. Trim
// concerns (file rotation, max bytes) intentionally deferred — a single bot
// session writes a few hundred KB to a few MB depending on the tracker, and
// the user can delete the files manually if they grow inconvenient.
public sealed class RingBufferStore<T> : IAsyncDisposable
{
    private const int QueueCapacity = 50_000;

    private readonly string _path;
    private readonly object _syncWriteLock = new();
    private readonly Channel<byte[]> _queue;
    private readonly Task _consumerLoop;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ILogger? _logger;
    private long _droppedCount;
    private long _backpressureFallbackCount;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public RingBufferStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _consumerLoop = Task.Run(() => DrainAsync(_shutdownCts.Token));
    }

    /// <summary>Append one record as a JSON line. Non-blocking unless the queue is full.</summary>
    public void Append(T record)
    {
        var line = JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);

        // Hot path: enqueue and return. Engine settlement never waits on disk.
        if (_queue.Writer.TryWrite(bytes)) return;

        // Backpressure: queue full (50K pending). Either the consumer is stuck
        // or the producer is genuinely outrunning disk. Fall back to a
        // synchronous write so audit data isn't silently dropped — slow but
        // correct. Log every 1000th occurrence so we know it's happening.
        var n = Interlocked.Increment(ref _backpressureFallbackCount);
        if (n % 1000 == 1)
            _logger?.LogWarning("RingBufferStore {Path}: queue full, falling back to sync write (count {N})", _path, n);
        WriteToDisk(bytes);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var bytes in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                WriteToDisk(bytes);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "RingBufferStore {Path}: drain loop crashed", _path);
        }
    }

    private void WriteToDisk(byte[] bytes)
    {
        // FileShare.ReadWrite so concurrent CSV-export readers (and AV /
        // Windows Search scanning the .ndjson) don't block the writer.
        // Short retry covers the residual race where an outside holder briefly
        // takes the file with stricter sharing.
        lock (_syncWriteLock)
        {
            const int maxAttempts = 5;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite, bufferSize: 4096, FileOptions.SequentialScan);
                    fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(attempt * 5); // 5, 10, 15, 20ms backoff
                }
                catch (IOException ex)
                {
                    // Final attempt failed — drop the line rather than wedging
                    // the consumer loop indefinitely. Counter surfaces in logs.
                    Interlocked.Increment(ref _droppedCount);
                    _logger?.LogWarning(ex, "RingBufferStore {Path}: dropped one record after retry exhaustion", _path);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Read the last <paramref name="maxRecords"/> records back. Returns empty
    /// when the file doesn't exist. Malformed lines are skipped silently — a
    /// partial last line from a crash is the most likely cause and shouldn't
    /// block boot.
    /// </summary>
    public IReadOnlyList<T> LoadTail(int maxRecords)
    {
        if (maxRecords <= 0 || !File.Exists(_path)) return Array.Empty<T>();
        string[] lines;
        try { lines = File.ReadAllLines(_path); }
        catch (IOException) { return Array.Empty<T>(); }

        var start = Math.Max(0, lines.Length - maxRecords);
        var result = new List<T>(lines.Length - start);
        for (int i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var rec = JsonSerializer.Deserialize<T>(line, JsonOpts);
                if (rec is null) continue;
                result.Add(rec);
            }
            catch (JsonException)
            {
                // Tolerate corrupt tail; skip and continue.
            }
        }
        return result;
    }

    /// <summary>
    /// Truncate the backing file, dropping all persisted history (so a restart's LoadTail finds nothing).
    /// The in-memory ring is the caller's concern. Used by manual "clear" actions, not the hot path.
    /// </summary>
    public void Clear()
    {
        lock (_syncWriteLock)
        {
            try { if (File.Exists(_path)) File.WriteAllText(_path, string.Empty); }
            catch (IOException ex) { _logger?.LogWarning(ex, "RingBufferStore {Path}: clear failed", _path); }
        }
    }

    /// <summary>
    /// Closes the producer side of the queue and waits for the consumer to
    /// flush every pending record to disk. Bot loop's stop path should call
    /// this before the engine shuts down so the audit tail is durable.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _consumerLoop.ConfigureAwait(false); } catch { }
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }
}
