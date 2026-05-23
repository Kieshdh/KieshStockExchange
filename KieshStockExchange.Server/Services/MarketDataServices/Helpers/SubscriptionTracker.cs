using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// Pure in-memory ref-count bookkeeping for live-quote subscriptions. Tracks total
/// subscribers (UI + bot) and UI-only subscribers per (stockId, currency) book, plus
/// the current candle resolution and a cached snapshot of subscribed keys. No I/O,
/// no async, no logger — the tick path can call <see cref="HasUiSubscribers"/> /
/// <see cref="HasAnySubscribers"/> on the hot path without dragging dependencies in.
/// </summary>
internal sealed class SubscriptionTracker
{
    private readonly ConcurrentDictionary<(int, CurrencyType), int> _total = new();
    private readonly ConcurrentDictionary<(int, CurrencyType), int> _ui = new();

    private volatile IReadOnlyCollection<(int, CurrencyType)> _subscribedSnapshot
        = Array.Empty<(int, CurrencyType)>();

    public IReadOnlyCollection<(int, CurrencyType)> Subscribed => _subscribedSnapshot;

    public CandleResolution CurrentResolution { get; set; } = CandleResolution.Default;

    public IReadOnlyCollection<(int, CurrencyType)> SnapshotSubscribed() => _subscribedSnapshot;

    public (int total, bool firstSubscriber) Increment((int, CurrencyType) key, bool forUi)
    {
        if (forUi)
            _ui.AddOrUpdate(key, 1, static (_, c) => c + 1);

        var total = _total.AddOrUpdate(key, 1, static (_, c) => c + 1);
        var first = total == 1;
        if (first) RebuildSnapshot();
        return (total, first);
    }

    public (int total, bool lastSubscriber) Decrement((int, CurrencyType) key, bool forUi)
    {
        if (forUi)
        {
            _ui.AddOrUpdate(key, 0, static (_, c) => Math.Max(0, c - 1));
            if (_ui.TryGetValue(key, out var uiCount) && uiCount == 0)
                _ui.TryRemove(key, out _);
        }

        var total = _total.AddOrUpdate(key, 0, static (_, c) => Math.Max(0, c - 1));
        var last = total == 0;

        if (last)
        {
            _total.TryRemove(key, out _);
            _ui.TryRemove(key, out _);
            RebuildSnapshot();
        }

        return (total, last);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAnySubscribers((int, CurrencyType) key)
        => _total.TryGetValue(key, out var c) && c > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasUiSubscribers((int, CurrencyType) key)
        => _ui.TryGetValue(key, out var c) && c > 0;

    private void RebuildSnapshot()
    {
        var list = new List<(int, CurrencyType)>(_total.Count);
        foreach (var kv in _total)
            if (kv.Value > 0) list.Add(kv.Key);
        _subscribedSnapshot = list;
    }
}
