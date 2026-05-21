using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.PortfolioServices;

public sealed class WatchlistService : IWatchlistService
{
    private readonly IDataBaseService _db;
    private readonly IAuthService _auth;
    private readonly ILogger<WatchlistService>? _logger;

    // Single shared lock: the watchlist is small (typically <50 entries) and
    // mutations are infrequent (user clicks star), so a coarse lock is fine.
    private readonly object _gate = new();
    private readonly List<UserWatchlistEntry> _entries = new();
    private int _cachedUserId = 0;

    public event EventHandler? Changed;

    public WatchlistService(IDataBaseService db, IAuthService auth, ILogger<WatchlistService>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _logger = logger;
    }

    public IReadOnlyList<int> GetStockIds()
    {
        lock (_gate)
            return _entries.Select(e => e.StockId).ToList();
    }

    public bool IsWatched(int stockId)
    {
        lock (_gate)
            return _entries.Any(e => e.StockId == stockId);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var userId = _auth.CurrentUserId;
        if (userId <= 0)
        {
            Clear();
            return;
        }

        var fresh = await _db.GetWatchlistByUserId(userId, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(fresh.OrderBy(e => e.SortOrder));
            _cachedUserId = userId;
        }
        RaiseChanged();
    }

    public async Task<bool> ToggleAsync(int stockId, CancellationToken ct = default)
    {
        var userId = _auth.CurrentUserId;
        if (userId <= 0)
            throw new InvalidOperationException("Cannot toggle watchlist while signed out.");
        if (stockId <= 0)
            throw new ArgumentOutOfRangeException(nameof(stockId));

        UserWatchlistEntry? existing;
        lock (_gate)
            existing = _entries.FirstOrDefault(e => e.StockId == stockId);

        if (existing is not null)
        {
            await _db.DeleteWatchlistEntry(userId, stockId, ct).ConfigureAwait(false);
            lock (_gate)
                _entries.RemoveAll(e => e.StockId == stockId);
            RaiseChanged();
            return false;
        }

        int nextOrder;
        lock (_gate)
            nextOrder = _entries.Count == 0 ? 0 : _entries.Max(e => e.SortOrder) + 1;

        var entry = new UserWatchlistEntry
        {
            UserId = userId,
            StockId = stockId,
            SortOrder = nextOrder,
            AddedAt = TimeHelper.NowUtc()
        };
        await _db.UpsertWatchlistEntry(entry, ct).ConfigureAwait(false);
        lock (_gate)
            _entries.Add(entry);
        RaiseChanged();
        return true;
    }

    public async Task ReorderAsync(IReadOnlyList<int> stockIdsInOrder, CancellationToken ct = default)
    {
        if (stockIdsInOrder is null) throw new ArgumentNullException(nameof(stockIdsInOrder));
        var userId = _auth.CurrentUserId;
        if (userId <= 0)
            throw new InvalidOperationException("Cannot reorder watchlist while signed out.");

        List<UserWatchlistEntry> reordered;
        lock (_gate)
        {
            if (stockIdsInOrder.Count != _entries.Count
                || stockIdsInOrder.Distinct().Count() != stockIdsInOrder.Count
                || stockIdsInOrder.Any(id => _entries.All(e => e.StockId != id)))
            {
                throw new ArgumentException(
                    "Reorder input must be a permutation of the current watchlist.", nameof(stockIdsInOrder));
            }

            // Build the new ordered list off the existing entries (keep AddedAt).
            reordered = stockIdsInOrder.Select((stockId, i) =>
            {
                var prev = _entries.First(e => e.StockId == stockId);
                return new UserWatchlistEntry
                {
                    UserId = userId,
                    StockId = stockId,
                    SortOrder = i,
                    AddedAt = prev.AddedAt
                };
            }).ToList();
        }

        await _db.ReplaceWatchlistAsync(userId, reordered, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(reordered);
        }
        RaiseChanged();
    }

    public void Clear()
    {
        bool wasPopulated;
        lock (_gate)
        {
            wasPopulated = _entries.Count > 0;
            _entries.Clear();
            _cachedUserId = 0;
        }
        if (wasPopulated) RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WatchlistService.Changed subscriber threw.");
        }
    }
}
