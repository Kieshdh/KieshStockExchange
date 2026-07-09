using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// §sector: the seed-authoritative stock→sector map the BankEstimate re-rating mechanism reads instead of the
/// old <c>stockId % SectorCount</c>. The ACTIVE sector list is the distinct real sectors present among the seeded
/// stocks, ordered by the canonical <see cref="Sector"/> enum order (= Config.SECTORS order) — that fixed order
/// IS the stable ordinal the per-sector RNG walk keys off, so it must be independent of dictionary/query order.
/// When NO stock carries a real sector (all Unknown/empty) <see cref="HasRealSectors"/> is false and callers fall
/// back to modulo ⇒ byte-identical to the pre-feature engine.
/// </summary>
public interface ISectorMap
{
    /// <summary>Number of distinct real sectors present in the seeded universe (the active-list length).</summary>
    int SectorCount { get; }

    /// <summary>True when at least one seeded stock carries a real (non-Unknown) sector.</summary>
    bool HasRealSectors { get; }

    /// <summary>The parsed sector of a stock (<see cref="Sector.Unknown"/> when unseeded/unknown).</summary>
    Sector SectorOf(int stockId);

    /// <summary>0-based stable index into the active sector list (−1 when the stock has no real sector).</summary>
    int OrdinalOf(int stockId);

    /// <summary>Stock ids in the given active-list ordinal (ascending; empty when out of range).</summary>
    IReadOnlyList<int> StockIdsInSector(int ordinal);
}

public sealed class SectorMap : ISectorMap
{
    private readonly IStockService _stocks;
    private readonly object _gate = new();

    // Built once (lazily) — the catalog may not be loaded yet at DI-construction time, so the index is
    // materialized on first access and cached. Ordering is derived from the Sector enum, never the catalog order.
    private bool _built;
    private Sector[] _active = Array.Empty<Sector>();       // active-list ordinal → sector
    private Dictionary<int, int> _ordinalByStock = new();   // stockId → active-list ordinal
    private Dictionary<int, int[]> _stocksByOrdinal = new(); // ordinal → ascending stock ids

    public SectorMap(IStockService stocks)
        => _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));

    public int SectorCount { get { EnsureBuilt(); return _active.Length; } }

    public bool HasRealSectors { get { EnsureBuilt(); return _active.Length > 0; } }

    public Sector SectorOf(int stockId)
        => _stocks.ById.TryGetValue(stockId, out var s) ? SectorInfo.Parse(s.Sector) : Sector.Unknown;

    public int OrdinalOf(int stockId)
    {
        EnsureBuilt();
        return _ordinalByStock.TryGetValue(stockId, out var o) ? o : -1;
    }

    public IReadOnlyList<int> StockIdsInSector(int ordinal)
    {
        EnsureBuilt();
        return _stocksByOrdinal.TryGetValue(ordinal, out var ids) ? ids : Array.Empty<int>();
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        lock (_gate)
        {
            if (_built) return;

            // Active list = the canonical Sector enum order, keeping only sectors actually present. Iterating the
            // enum (not the catalog) makes the ordinal deterministic regardless of stock dictionary order.
            var present = new HashSet<Sector>();
            foreach (var st in _stocks.All)
            {
                var sec = SectorInfo.Parse(st.Sector);
                if (sec != Sector.Unknown) present.Add(sec);
            }

            var active = new List<Sector>();
            foreach (Sector sec in Enum.GetValues<Sector>())
                if (sec != Sector.Unknown && present.Contains(sec)) active.Add(sec);

            var ordinalBySector = new Dictionary<Sector, int>();
            for (int i = 0; i < active.Count; i++) ordinalBySector[active[i]] = i;

            var ordinalByStock = new Dictionary<int, int>();
            var idsByOrdinal = new Dictionary<int, List<int>>();
            foreach (var st in _stocks.All.OrderBy(s => s.StockId)) // ascending ⇒ StockIdsInSector deterministic
            {
                var sec = SectorInfo.Parse(st.Sector);
                if (sec == Sector.Unknown || !ordinalBySector.TryGetValue(sec, out var ord)) continue;
                ordinalByStock[st.StockId] = ord;
                (idsByOrdinal.TryGetValue(ord, out var list) ? list : idsByOrdinal[ord] = new List<int>())
                    .Add(st.StockId);
            }

            _active = active.ToArray();
            _ordinalByStock = ordinalByStock;
            _stocksByOrdinal = idsByOrdinal.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
            _built = true;
        }
    }
}
