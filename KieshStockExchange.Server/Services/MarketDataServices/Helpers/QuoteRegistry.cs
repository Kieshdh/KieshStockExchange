using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// Single source of truth for live <see cref="LiveQuote"/> state. Owns the in-memory
/// quote map, the dirty set, and the periodic drain loop that fires
/// <see cref="QuoteUpdated"/> for books that have received ticks since the last drain.
/// Independent of <see cref="SubscriptionTracker"/> via the <c>hasUiSubscriber</c>
/// callback supplied at construction.
/// </summary>
internal sealed class QuoteRegistry
{
    private static readonly TimeSpan TickerInterval = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<(int, CurrencyType), LiveQuote> _quotes = new();
    private readonly ReadOnlyDictionary<(int, CurrencyType), LiveQuote> _quotesView;
    private readonly ConcurrentDictionary<(int, CurrencyType), byte> _dirty = new();

    private readonly IDispatcher _dispatcher;
    private readonly IMarketLookupService _lookup;
    private readonly ILogger<QuoteRegistry> _logger;
    private readonly Func<(int, CurrencyType), bool> _hasUiSubscriber;

    private Task? _drainTask;

    public IReadOnlyDictionary<(int, CurrencyType), LiveQuote> Quotes => _quotesView;

    public event EventHandler<LiveQuote>? QuoteUpdated;

    public QuoteRegistry(
        IDispatcher dispatcher,
        IMarketLookupService lookup,
        ILogger<QuoteRegistry> logger,
        Func<(int, CurrencyType), bool> hasUiSubscriber)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hasUiSubscriber = hasUiSubscriber ?? throw new ArgumentNullException(nameof(hasUiSubscriber));
        _quotesView = new ReadOnlyDictionary<(int, CurrencyType), LiveQuote>(_quotes);
    }

    public void Start(CancellationToken ct)
    {
        _drainTask = Task.Run(() => DrainLoopAsync(ct));
    }

    public async Task StopAsync()
    {
        if (_drainTask is null) return;
        try { await _drainTask.ConfigureAwait(false); } catch { /* shutdown */ }
    }

    public async ValueTask<LiveQuote> GetOrAddAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        var quote = _quotes.GetOrAdd((stockId, currency), static k => new LiveQuote(k.Item1, k.Item2));

        try
        {
            if (quote.Symbol == "-")
            {
                var stock = await _lookup.GetStockAsync(stockId, ct).ConfigureAwait(false);
                if (stock != null)
                {
                    if (_hasUiSubscriber((stockId, currency)))
                    {
                        _dispatcher.Dispatch(() =>
                        {
                            quote.Symbol = stock.Symbol;
                            quote.CompanyName = stock.CompanyName;
                        });
                    }
                    else
                    {
                        // Bot-only book: skip the dispatcher hop. PropertyChanged still
                        // fires but no UI is bound, so the cost is negligible.
                        quote.Symbol = stock.Symbol;
                        quote.CompanyName = stock.CompanyName;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing LiveQuote for stock {StockId} in {Currency}", stockId, currency);
        }

        return quote;
    }

    public void TryRemove((int, CurrencyType) key)
    {
        _quotes.TryRemove(key, out _);
        _dirty.TryRemove(key, out _);
    }

    public void MarkDirty((int, CurrencyType) key) => _dirty[key] = 0;

    /// <summary>
    /// Runs <paramref name="mutate"/> on the UI thread when the book has any UI
    /// subscribers, otherwise inline on the calling thread. Used for snapshot apply
    /// from history seeding.
    /// </summary>
    public void ApplyOnPreferredThread((int, CurrencyType) key, Action mutate)
    {
        if (_hasUiSubscriber(key))
            _dispatcher.Dispatch(mutate);
        else
            mutate();
    }

    public void Clear()
    {
        _dirty.Clear();
        _quotes.Clear();
    }

    /// <summary>
    /// Single background loop that fires <see cref="QuoteUpdated"/> for every dirty
    /// book on each tick of <see cref="TickerInterval"/>. Replaces per-book debounce
    /// timers — zero UI-thread timers, zero per-book Dispatch calls. Subscribers
    /// self-marshal to the UI thread when they need to.
    /// </summary>
    private async Task DrainLoopAsync(CancellationToken ct)
    {
        try
        {
            // Dispose-on-cancel pattern: register a callback that disposes the timer when ct
            // fires. WaitForNextTickAsync (no-token overload) returns false on disposal
            // instead of throwing OCE — keeps the debugger's first-chance window quiet.
            using var timer = new PeriodicTimer(TickerInterval);
            using var cancelReg = ct.Register(static state => ((PeriodicTimer)state!).Dispose(), timer);

            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                if (_dirty.IsEmpty) continue;

                // Iterate the entries directly — ConcurrentDictionary's enumerator does
                // not snapshot, unlike .Keys. Concurrent dirty marks landing during the
                // loop will be picked up on the next tick.
                foreach (var kv in _dirty)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!_dirty.TryRemove(kv.Key, out _)) continue;
                    if (!_quotes.TryGetValue(kv.Key, out var q)) continue;

                    try { QuoteUpdated?.Invoke(this, q); }
                    catch (Exception ex) { _logger.LogError(ex, "QuoteUpdated handler threw."); }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _logger.LogError(ex, "Quote drain loop terminated unexpectedly."); }
    }
}
