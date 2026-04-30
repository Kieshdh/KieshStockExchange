using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;

namespace KieshStockExchange.Services.PortfolioServices;

public sealed class AccountsCache : IAccountsCache
{
    #region Private State
    private readonly ConcurrentDictionary<(int UserId, CurrencyType Ccy), Fund> _funds = new();
    private readonly ConcurrentDictionary<(int UserId, int StockId), Position> _positions = new();
    private readonly ConcurrentDictionary<int, byte> _loadedUsers = new();
    
    // Single gate around the cold-load section so we don't issue duplicate DB reads
    // when many parallel callers ask to load the same user. Hot-path lookups don't
    // touch this gate.
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    #endregion

    #region Services and Constructor
    private readonly IDataBaseService _db;

    public AccountsCache(IDataBaseService db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }
    #endregion

    #region Loading
    public Task EnsureLoadedAsync(int userId, CancellationToken ct = default)
        => EnsureLoadedAsync(new[] { userId }, ct);

    public async Task EnsureLoadedAsync(IReadOnlyList<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return;

        // Fast path: every requested user is already loaded.
        List<int>? missing = null;
        for (int i = 0; i < userIds.Count; i++)
        {
            if (!_loadedUsers.ContainsKey(userIds[i]))
            {
                missing ??= new List<int>();
                missing.Add(userIds[i]);
            }
        }
        if (missing is null) return;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — another caller may have loaded these in the meantime.
            for (int i = missing.Count - 1; i >= 0; i--)
                if (_loadedUsers.ContainsKey(missing[i])) missing.RemoveAt(i);
            if (missing.Count == 0) return;

            var funds = await _db.GetFundsForUsersAsync(missing, ct).ConfigureAwait(false);
            for (int i = 0; i < funds.Count; i++)
            {
                var f = funds[i];
                _funds[(f.UserId, f.CurrencyType)] = f;
            }

            var positions = await _db.GetPositionsForUsersAsync(missing, ct).ConfigureAwait(false);
            for (int i = 0; i < positions.Count; i++)
            {
                var p = positions[i];
                p.ReservedQuantity = 0;
                _positions[(p.UserId, p.StockId)] = p;
            }

            // Backfill ReservedQuantity from the DB. Open sell limit orders advertise shares
            // the user has already promised — without this, the first match/cancel of a
            // pre-existing maker would underflow ReservedQuantity. Buys are unaffected.
            var openOrders = await _db.GetOpenOrdersForUsersAsync(missing, ct).ConfigureAwait(false);
            for (int i = 0; i < openOrders.Count; i++)
            {
                var o = openOrders[i];
                if (!o.IsSellOrder) continue;
                var remaining = o.RemainingQuantity;
                if (remaining <= 0) continue;
                if (!_positions.TryGetValue((o.UserId, o.StockId), out var pos)) continue;
                pos.ReservedQuantity += remaining;
            }

            // Mark all requested users as loaded — even if they had no rows, so we don't
            // re-query the DB for empty results.
            for (int i = 0; i < missing.Count; i++)
                _loadedUsers[missing[i]] = 0;
        }
        finally { _loadGate.Release(); }
    }
    #endregion

    #region Lookups and Mutations
    public Fund? GetFund(int userId, CurrencyType ccy)
        => _funds.TryGetValue((userId, ccy), out var f) ? f : null;

    public Position? GetPosition(int userId, int stockId)
        => _positions.TryGetValue((userId, stockId), out var p) ? p : null;

    public void TrackNewPosition(Position pos)
    {
        if (pos is null) return;
        _positions[(pos.UserId, pos.StockId)] = pos;
    }
    #endregion
}
