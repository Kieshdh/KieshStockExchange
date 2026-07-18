using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;

namespace KieshStockExchange.Services.PortfolioServices;

public sealed partial class AccountsCache : IAccountsCache
{
    #region Private State
    private readonly ConcurrentDictionary<(int UserId, CurrencyType Ccy), Fund> _funds = new();
    private readonly ConcurrentDictionary<(int UserId, int StockId), Position> _positions = new();
    private readonly ConcurrentDictionary<int, byte> _loadedUsers = new();

    // Single gate around the cold-load section so we don't issue duplicate DB reads
    // when many parallel callers ask to load the same user. Hot-path lookups don't
    // touch this gate.
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // Per-user reservation gates. Before the batch path's _writeGate restructure (Section 1b)
    // every fund/position mutation was serialised by SQLite's writer; once the batch gives
    // up its single root tx, two flows touching the same (user, currency) or (user, stockId)
    // can race on ReserveFunds/ReserveStock/UnreserveStock. These semaphores serialise the
    // mutation step at the (user, resource) granularity — far more concurrency than the
    // global writer gate, but enough to prevent the lost-update bug on ReservedBalance /
    // ReservedQuantity. Gates are created lazily and live for the process lifetime.
    private readonly ConcurrentDictionary<(int UserId, CurrencyType Ccy), SemaphoreSlim> _fundGates = new();
    private readonly ConcurrentDictionary<(int UserId, int StockId), SemaphoreSlim> _posGates = new();
    #endregion

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IOrderRegistry _registry;
    private readonly IReservationLedger _ledger;
    private readonly ILogger<AccountsCache> _logger;

    public AccountsCache(IDataBaseService db, IOrderRegistry registry,
        IReservationLedger ledger, ILogger<AccountsCache> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Loading
    #endregion

    #region Lookups and Mutations
    public Fund? GetFund(int userId, CurrencyType ccy)
        => _funds.TryGetValue((userId, ccy), out var f) ? f : null;

    public Position? GetPosition(int userId, int stockId)
        => _positions.TryGetValue((userId, stockId), out var p) ? p : null;

    public async Task ApplyExternalFundDeltaAsync(int userId, CurrencyType ccy, decimal delta, CancellationToken ct = default)
    {
        if (delta == 0m) return;
        // Only meaningful once this user's funds are cached; otherwise the next
        // EnsureLoadedAsync cold-loads the already-updated DB row.
        if (!_funds.ContainsKey((userId, ccy))) return;

        await using var gate = await AcquireFundGateAsync(userId, ccy, ct).ConfigureAwait(false);
        if (!_funds.TryGetValue((userId, ccy), out var fund)) return;

        var totBefore = fund.TotalBalance;
        try
        {
            if (delta > 0m) fund.AddFunds(delta);
            else fund.WithdrawFunds(-delta);
        }
        catch (ArgumentException ex)
        {
            // Cache diverged from the DB value the caller validated against — drop the
            // user so the next EnsureLoadedAsync re-reads the authoritative DB balance.
            _logger.LogWarning(ex, "ApplyExternalFundDelta {Delta} for user {UserId}/{Ccy} rejected; invalidating cache.",
                delta, userId, ccy);
            _funds.TryRemove((userId, ccy), out _);
            _loadedUsers.TryRemove(userId, out _);
            return;
        }

        _ledger.LogFund(userId, ccy, null, "ApplyExternalFundDelta",
            delta, fund.ReservedBalance, fund.ReservedBalance, totBefore, fund.TotalBalance);
    }

    public void TrackNewPosition(Position pos)
    {
        if (pos is null) return;
        _positions[(pos.UserId, pos.StockId)] = pos;
    }

    public void Clear()
    {
        _funds.Clear();
        _positions.Clear();
        _loadedUsers.Clear();
        // Per-user gates are intentionally NOT cleared — disposing them while held
        // by an in-flight settle/cancel would deadlock that caller. They're cheap to
        // keep (one SemaphoreSlim per touched user); the next caller still gets
        // mutual exclusion against any survivors.
    }
    #endregion

    #region Reservation Reconciler
    #endregion

    #region Per-User Gates
    public async ValueTask<IAsyncDisposable> AcquireFundGateAsync(
        int userId, CurrencyType ccy, CancellationToken ct = default)
    {
        var gate = _fundGates.GetOrAdd((userId, ccy), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new SemaphoreRelease(gate);
    }

    public async ValueTask<IAsyncDisposable> AcquirePositionGateAsync(
        int userId, int stockId, CancellationToken ct = default)
    {
        var gate = _posGates.GetOrAdd((userId, stockId), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new SemaphoreRelease(gate);
    }

    public async ValueTask<IAsyncDisposable> AcquireUserGatesAsync(
        IReadOnlyCollection<(int UserId, CurrencyType Ccy)> fundKeys,
        IReadOnlyCollection<(int UserId, int StockId)> positionKeys,
        CancellationToken ct = default)
    {
        // Acquire in a globally-deterministic order so concurrent callers can never form
        // an AB/BA deadlock on overlapping user sets. We materialise to arrays and sort
        // up front: funds first (ordered by userId then currency byte), then positions
        // (ordered by userId then stockId). Any two batches that touch the same gates
        // hit them in this same sequence.
        var fundArr = fundKeys is { Count: > 0 }
            ? fundKeys.Distinct().OrderBy(k => k.UserId).ThenBy(k => (int)k.Ccy).ToArray()
            : Array.Empty<(int, CurrencyType)>();
        var posArr = positionKeys is { Count: > 0 }
            ? positionKeys.Distinct().OrderBy(k => k.UserId).ThenBy(k => k.StockId).ToArray()
            : Array.Empty<(int, int)>();

        var acquired = new List<SemaphoreSlim>(fundArr.Length + posArr.Length);
        try
        {
            for (int i = 0; i < fundArr.Length; i++)
            {
                var gate = _fundGates.GetOrAdd(fundArr[i], static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired.Add(gate);
            }
            for (int i = 0; i < posArr.Length; i++)
            {
                var gate = _posGates.GetOrAdd(posArr[i], static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired.Add(gate);
            }
            return new MultiSemaphoreRelease(acquired);
        }
        catch
        {
            // If we threw partway, release whatever we already acquired so callers
            // aren't holding gates they never returned from.
            for (int i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
            throw;
        }
    }

    private sealed class SemaphoreRelease : IAsyncDisposable
    {
        private SemaphoreSlim? _gate;
        public SemaphoreRelease(SemaphoreSlim gate) => _gate = gate;
        public ValueTask DisposeAsync()
        {
            var g = Interlocked.Exchange(ref _gate, null);
            g?.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MultiSemaphoreRelease : IAsyncDisposable
    {
        private List<SemaphoreSlim>? _gates;
        public MultiSemaphoreRelease(List<SemaphoreSlim> gates) => _gates = gates;
        public ValueTask DisposeAsync()
        {
            var gates = Interlocked.Exchange(ref _gates, null);
            if (gates is null) return ValueTask.CompletedTask;
            // Release in reverse acquisition order — symmetric with the nested
            // semantics of a stack of using-blocks.
            for (int i = gates.Count - 1; i >= 0; i--) gates[i].Release();
            return ValueTask.CompletedTask;
        }
    }
    #endregion
}
