using System.Text.Json;
using System.Text.Json.Serialization;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

// Append-only NDJSON persistence for the bot telemetry ringbuffers
// (BotFailureTracker, BotEconomyTelemetry, BotSentimentService,
// ReservationLedger). One record per line, serialized via System.Text.Json.
// Each tracker owns one store and calls Append() for every new record;
// Load(N) on construction reads the tail of the file back into its in-memory
// ringbuffer so a server restart doesn't lose the previous session's history.
//
// Writes are lock-serialized — the trackers fire from many threads. Reads
// happen once on boot, so the IO cost amortizes.
//
// File location defaults to `./data/telemetry/{name}.ndjson` relative to the
// server working directory. The directory is created if missing. Trim
// concerns (file rotation, max bytes) intentionally deferred — a single bot
// session writes a few hundred KB to a few MB depending on the tracker, and
// the user can delete the files manually if they grow inconvenient.
public sealed class RingBufferStore<T>
{
    private readonly string _path;
    private readonly object _writeLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public RingBufferStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>Append one record as a JSON line. Thread-safe.</summary>
    public void Append(T record)
    {
        var line = JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine;
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        lock (_writeLock)
        {
            // FileShare.ReadWrite so concurrent CSV-export readers (and AV /
            // Windows Search scanning the .ndjson) don't block our append.
            // Short retry loop covers the residual race where another holder
            // briefly takes the file with stricter sharing — without it the
            // engine surfaces an IOException to whoever called LogFund/
            // LogPosition, which today aborts the in-flight settlement.
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
}
