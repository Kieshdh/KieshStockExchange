using KieshStockExchange.Helpers;
using Microsoft.Extensions.Logging;
using System.Text;

namespace KieshStockExchange.Services.PortfolioServices.Helpers;

/// <summary>
/// §3.7 Session FX-desk telemetry. The house is a pure profit account: the bulk FROM↔TO swap
/// settles against the (infinite) external FX market at mid, so the desk carries no inventory and
/// only the spread sticks to the house. This records what the desk did over the session — total
/// conversions, per-direction volume, the spread captured per currency, and the NET currency the
/// simulation sourced from / dumped to external FX ("net spend of the FX rate"). Thread-safe:
/// conversions arrive from the bot loop and user requests concurrently.
/// </summary>
public sealed class FxDeskTelemetry
{
    private readonly ILogger<FxDeskTelemetry> _logger;
    private readonly object _gate = new();

    // Log a rolling session summary at most this often (one compact line, not per-conversion spam).
    private static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(30);
    private DateTime _lastLog = DateTime.MinValue;

    private long _count;
    // Per (from,to): conversion count + gross volume (FROM debited, TO credited to users).
    private readonly Dictionary<(CurrencyType From, CurrencyType To), DirStat> _byDirection = new();
    // Signed net currency the sim gained(+ bought) / gave up(- sold) via the desk = net FX spend.
    private readonly Dictionary<CurrencyType, decimal> _netFlow = new();
    // Spread captured into the house, per credited (TO) currency = house session profit.
    private readonly Dictionary<CurrencyType, decimal> _spreadByCurrency = new();

    public FxDeskTelemetry(ILogger<FxDeskTelemetry> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary> Clear session aggregates (called on each bot-loop Start). </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _count = 0;
            _byDirection.Clear();
            _netFlow.Clear();
            _spreadByCurrency.Clear();
            _lastLog = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Record one settled conversion. <paramref name="amount"/> is the FROM debited from the user,
    /// <paramref name="converted"/> the TO credited to the user, <paramref name="spread"/> the TO
    /// credited to the house. The external FX leg is the fair-value swap (converted + spread).
    /// </summary>
    public void RecordConversion(CurrencyType from, CurrencyType to,
        decimal amount, decimal converted, decimal spread)
    {
        var fairValue = converted + spread; // TO bought from external FX at mid
        string? summary = null;
        lock (_gate)
        {
            _count++;
            var key = (from, to);
            _byDirection.TryGetValue(key, out var d);
            _byDirection[key] = new DirStat(d.Count + 1, d.FromVolume + amount, d.ToVolume + converted);

            // FROM sold to external (sim loses it), TO bought from external (sim gains it).
            _netFlow[from] = _netFlow.GetValueOrDefault(from) - amount;
            _netFlow[to]   = _netFlow.GetValueOrDefault(to)   + fairValue;
            _spreadByCurrency[to] = _spreadByCurrency.GetValueOrDefault(to) + spread;

            var now = TimeHelper.NowUtc();
            if (now - _lastLog >= LogEvery)
            {
                _lastLog = now;
                summary = BuildSummary();
            }
        }
        if (summary != null) _logger.LogInformation("{Summary}", summary);
    }

    /// <summary> Force-emit the current session summary (e.g. at shutdown). </summary>
    public void LogSummary()
    {
        string summary;
        lock (_gate) summary = BuildSummary();
        _logger.LogInformation("{Summary}", summary);
    }

    // Caller holds _gate.
    private string BuildSummary()
    {
        var sb = new StringBuilder(160);
        sb.Append("FX desk session: ").Append(_count).Append(" converts");

        sb.Append(" | net FX spend ");
        AppendCcyMap(sb, _netFlow);

        sb.Append(" | spread captured ");
        AppendCcyMap(sb, _spreadByCurrency);

        if (_byDirection.Count > 0)
        {
            sb.Append(" |");
            foreach (var kv in _byDirection.OrderBy(k => k.Key.From).ThenBy(k => k.Key.To))
                sb.Append(' ').Append(kv.Key.From).Append("->").Append(kv.Key.To)
                  .Append(' ').Append(kv.Value.Count).Append('x')
                  .Append(" (").Append(CurrencyHelper.Format(kv.Value.FromVolume, kv.Key.From)).Append(')');
        }
        return sb.ToString();
    }

    private static void AppendCcyMap(StringBuilder sb, Dictionary<CurrencyType, decimal> map)
    {
        if (map.Count == 0) { sb.Append("none"); return; }
        bool first = true;
        foreach (var kv in map.OrderBy(k => k.Key))
        {
            if (!first) sb.Append(", ");
            sb.Append(CurrencyHelper.Format(kv.Value, kv.Key));
            first = false;
        }
    }

    private readonly record struct DirStat(long Count, decimal FromVolume, decimal ToVolume);
}
