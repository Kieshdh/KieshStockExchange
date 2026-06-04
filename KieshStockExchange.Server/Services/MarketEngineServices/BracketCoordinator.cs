using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Server.Services.HostedServices;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// §3.6 P4. Reacts to bracket fills (post-commit) and manages the child legs of a bracket:
/// a stop-loss (SL) and up to three scale-out take-profit (TP) limits, OCO-grouped.
///
/// Model B (hardened design): the <b>SL reserves the full held position</b> (one shared pool on
/// Position.ReservedQuantity); the TP limits rest on the book reserving <b>nothing</b>, drawing from
/// the SL's pool. A TP fill drops Position.ReservedQuantity (the long-sell ConsumeReservedStock path)
/// and this coordinator shrinks the SL's per-order field to match; an SL fire cancels all open TPs and
/// sells the full held. The reconciler/clamp (P4 Step 0) counts the armed SL so the pool isn't zeroed.
///
/// TP arming uses fill-up-whole-legs (a TP arms at its full allocation once <c>held</c> covers its
/// cumulative threshold) rather than per-fill pro-rata: same scale-out behaviour and invariants
/// without persisting a separate per-TP allocation or resizing TP orders.
///
/// Scope: <b>long brackets only</b> (buy entry → sell-stop SL + sell-limit TPs). Short brackets are
/// rejected at placement (risk register #7 sequencing) until a follow-up.
/// </summary>
public interface IBracketCoordinator
{
    /// <summary>O(1) hot-path guard: is this order id a bracket parent with attached legs?</summary>
    bool IsBracketParent(int orderId);

    /// <summary>Seed the parent set at place time (children already persisted as Attached).</summary>
    void RegisterBracket(int parentOrderId);

    /// <summary>Rebuild the parent set from DB on server start.</summary>
    Task RehydrateAsync(CancellationToken ct = default);

    /// <summary>Parent filled (more): arm/grow the SL to held and arm any now-covered TP legs.</summary>
    Task OnParentFillAsync(Order parent, CancellationToken ct = default);

    /// <summary>A bracket TP filled q: shrink the SL to the new held; OCO + cancel-remainder.</summary>
    Task OnChildFillAsync(Order tp, CancellationToken ct = default);

    /// <summary>The SL is about to promote: cancel all open TP siblings and size the SL to held.</summary>
    Task OnStopFiringAsync(Order sl, CancellationToken ct = default);

    /// <summary>A bracket member was just cancelled (by the user). Apply group cancel-semantics:
    /// cancelling an unfilled parent or the SL tears down the whole group; cancelling a single TP or
    /// a partially-filled parent leaves the rest intact.</summary>
    Task OnMemberCancelledAsync(Order cancelled, CancellationToken ct = default);
}

public sealed class BracketCoordinator : IBracketCoordinator
{
    private readonly ConcurrentDictionary<int, byte> _bracketParents = new();

    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly IOrderRegistry _registry;
    private readonly IOrderBookEngine _books;
    // Lazy to break the DI cycle StopTriggerWatcher → OrderExecutionService → BracketCoordinator →
    // IStopWatcher(=StopTriggerWatcher). Only used at runtime (Arm/Disarm), never at construction.
    private readonly Lazy<IStopWatcher> _stopWatcher;
    private readonly IReservationLedger _ledger;
    private readonly IOrderCacheService _orderCache;
    private readonly ILogger<BracketCoordinator> _logger;

    public BracketCoordinator(IDataBaseService db, IAccountsCache accounts, IOrderRegistry registry,
        IOrderBookEngine books, Lazy<IStopWatcher> stopWatcher, IReservationLedger ledger,
        IOrderCacheService orderCache, ILogger<BracketCoordinator> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _books = books ?? throw new ArgumentNullException(nameof(books));
        _stopWatcher = stopWatcher ?? throw new ArgumentNullException(nameof(stopWatcher));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _orderCache = orderCache ?? throw new ArgumentNullException(nameof(orderCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsBracketParent(int orderId) => _bracketParents.ContainsKey(orderId);

    public void RegisterBracket(int parentOrderId)
    {
        if (parentOrderId > 0) _bracketParents[parentOrderId] = 0;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        try
        {
            var children = await _db.GetActiveBracketChildrenAsync(ct).ConfigureAwait(false);
            foreach (var ch in children)
                if (ch.ParentOrderId is int pid) _bracketParents[pid] = 0;
            _logger.LogInformation("BracketCoordinator rehydrated {Count} bracket parent(s).", _bracketParents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BracketCoordinator rehydrate failed; brackets won't self-manage until re-registered.");
        }
    }

    // Canonical children for a parent: the SL (stop) + the TP limits, resolved through the registry
    // so we mutate the live instances the engine/reconciler share.
    private async Task<(Order? Sl, List<Order> Tps)> LoadLegsAsync(int parentId, CancellationToken ct)
    {
        var raw = await _db.GetBracketChildrenAsync(parentId, ct).ConfigureAwait(false);
        Order? sl = null;
        var tps = new List<Order>(3);
        foreach (var r in raw)
        {
            var o = _registry.TryGet(r.OrderId, out var canon) ? canon : r;
            if (o.IsStopOrder) sl = o;
            else if (o.IsLimitOrder) tps.Add(o);
        }
        // Fill-up order: a long bracket's sell TPs fill nearest-market-first (lowest price first).
        tps.Sort((a, b) => a.Price.CompareTo(b.Price));
        return (sl, tps);
    }

    // Shares still open in the bracket = parent acquired − everything the legs already sold.
    private static int ComputeHeld(Order parent, Order? sl, List<Order> tps)
    {
        int exited = (sl?.AmountFilled ?? 0);
        for (int i = 0; i < tps.Count; i++) exited += tps[i].AmountFilled;
        return Math.Max(0, parent.AmountFilled - exited);
    }

    public async Task OnParentFillAsync(Order parent, CancellationToken ct = default)
    {
        if (parent is null || !parent.IsBuyOrder) return; // long brackets only
        await _accounts.EnsureLoadedAsync(parent.UserId, ct).ConfigureAwait(false);

        var (sl, tps) = await LoadLegsAsync(parent.OrderId, ct).ConfigureAwait(false);
        if (sl is null) return; // a bracket must have an SL
        int held = ComputeHeld(parent, sl, tps);
        if (held <= 0) return;

        // book → gate → tx. The book lock covers TP upserts; the position gate + tx cover the SL
        // reservation resize and the persist.
        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts
                .AcquirePositionGateAsync(parent.UserId, parent.StockId, ct).ConfigureAwait(false);
            var pos = _accounts.GetPosition(parent.UserId, parent.StockId);
            if (pos is null) return;

            // SL holds the full pool: grow its reservation to `held` from the freshly-acquired
            // (available) shares. delta>0 on parent fills; never <0 here (TP fills shrink it).
            int delta = held - sl.CurrentSellReservedQty;
            int posResBefore = pos.ReservedQuantity;
            int slBefore = sl.CurrentSellReservedQty;
            if (delta > 0)
            {
                if (pos.AvailableQuantity < delta)
                {
                    _logger.LogWarning(
                        "Bracket #{Parent}: SL can't reserve {Delta} more (avail {Avail}); capping to available.",
                        parent.OrderId, delta, pos.AvailableQuantity);
                    delta = pos.AvailableQuantity;
                }
                if (delta > 0)
                {
                    pos.ReserveStock(delta);
                    sl.TakeSellReservation(delta);
                }
            }

            await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                sl.Quantity = sl.CurrentSellReservedQty + sl.AmountFilled; // RemainingQuantity == held pool
                if (sl.IsAttached) sl.Arm();                              // Attached → Pending
                else { sl.Status = Order.Statuses.Pending; }
                sl.UpdatedAt = TimeHelper.NowUtc();
                await _db.UpdateOrder(sl, ct).ConfigureAwait(false);

                // Fill-up: arm whole TP legs whose cumulative allocation is covered by held.
                int cum = 0;
                for (int i = 0; i < tps.Count; i++)
                {
                    var tp = tps[i];
                    cum += tp.Quantity;
                    if (tp.IsAttached && cum <= held)
                    {
                        tp.Status = Order.Statuses.Open; // TP rests on the book reserving nothing
                        tp.UpdatedAt = TimeHelper.NowUtc();
                        await _db.UpdateOrder(tp, ct).ConfigureAwait(false);
                        book.UpsertOrder(tp);
                    }
                }

                if (pos.PositionId != 0) await _db.UpdateAllAsync(new[] { pos }, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                // Restore the cache reservation taken above; DB rolled back.
                if (pos.ReservedQuantity != posResBefore) pos.ReservedQuantity = posResBefore;
                sl.RestoreReservationFromSnapshot(sl.CurrentBuyReservation, slBefore);
                throw;
            }

            _ledger.LogOrder(sl.UserId, sl.OrderId, "Bracket:ArmSL",
                Math.Max(0, delta), sl.CurrentBuyReservation, sl.CurrentBuyReservation,
                slBefore, sl.CurrentSellReservedQty);
        }).ConfigureAwait(false);

        if (sl.IsArmed) _stopWatcher.Value.Arm(sl);
        _orderCache.NotifyOrdersMutated(new[] { parent.UserId });
    }

    public async Task OnChildFillAsync(Order tp, CancellationToken ct = default)
    {
        if (tp is null || tp.ParentOrderId is not int parentId) return;
        await _accounts.EnsureLoadedAsync(tp.UserId, ct).ConfigureAwait(false);

        Order? parent = _registry.TryGet(parentId, out var pc) ? pc
                       : await _db.GetOrderById(parentId, ct).ConfigureAwait(false);
        if (parent is null) return;
        var (sl, tps) = await LoadLegsAsync(parentId, ct).ConfigureAwait(false);
        if (sl is null) return;
        int held = ComputeHeld(parent, sl, tps);

        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts
                .AcquirePositionGateAsync(parent.UserId, parent.StockId, ct).ConfigureAwait(false);

            await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                // The TP fill already dropped Position.ReservedQuantity (ConsumeReservedStock in
                // TradeSettler). Only the SL's per-order field lags — bring it down to `held` so
                // SL.CSR == Position.ReservedQuantity again (closes the post-commit transient).
                int slDrop = sl.CurrentSellReservedQty - held;
                if (slDrop > 0) sl.ConsumeSellReservation(slDrop);

                if (held <= 0)
                {
                    // Whole position exited via TPs — retire the SL.
                    _stopWatcher.Value.Disarm(sl.OrderId);
                    book.RemoveById(sl.OrderId);
                    sl.Status = Order.Statuses.Cancelled;
                    sl.UpdatedAt = TimeHelper.NowUtc();
                    _bracketParents.TryRemove(parentId, out _);
                }
                else
                {
                    sl.Quantity = sl.CurrentSellReservedQty + sl.AmountFilled;
                    sl.UpdatedAt = TimeHelper.NowUtc();
                }
                await _db.UpdateOrder(sl, ct).ConfigureAwait(false);

                // Cancel-the-remainder: a protective leg fired while a LIMIT parent still rests →
                // stop acquiring more of a position we're now exiting.
                if (parent.IsOpen && parent.IsLimitOrder && parent.RemainingQuantity > 0)
                {
                    book.RemoveById(parent.OrderId);
                    parent.Cancel();
                    parent.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(parent, ct).ConfigureAwait(false);
                    // Parent's residual buy reservation is released by the normal cancel path is
                    // not invoked here; release it inline to keep funds reconciled.
                    await ReleaseParentBuyReservationAsync(parent, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);

        _orderCache.NotifyOrdersMutated(new[] { parent.UserId });
    }

    public async Task OnStopFiringAsync(Order sl, CancellationToken ct = default)
    {
        if (sl is null || sl.ParentOrderId is not int parentId) return;
        Order? parent = _registry.TryGet(parentId, out var pc) ? pc
                       : await _db.GetOrderById(parentId, ct).ConfigureAwait(false);
        if (parent is null) return;
        var (_, tps) = await LoadLegsAsync(parentId, ct).ConfigureAwait(false);

        // Held = what the parent acquired minus what the TPs already sold (the SL hasn't fired yet).
        int held = parent.AmountFilled;
        for (int i = 0; i < tps.Count; i++) held -= tps[i].AmountFilled;
        if (held < 0) held = 0;

        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts
                .AcquirePositionGateAsync(parent.UserId, parent.StockId, ct).ConfigureAwait(false);
            await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                // Size the SL to the live held pool before it promotes (risk #4: a TP fill in the
                // transient window may have dropped Position.ReservedQuantity below the SL's stale
                // CSR — reconcile both so the promote sells exactly `held`, never more).
                int slOver = sl.CurrentSellReservedQty - held;
                if (slOver > 0) sl.ConsumeSellReservation(slOver);
                sl.Quantity = sl.CurrentSellReservedQty + sl.AmountFilled;
                sl.UpdatedAt = TimeHelper.NowUtc();
                await _db.UpdateOrder(sl, ct).ConfigureAwait(false);

                // Cancel all open TP legs first (book-remove; they reserve nothing). The SL is still
                // off-book (Pending) during this, so there's no double-sell window before it promotes.
                for (int i = 0; i < tps.Count; i++)
                {
                    var tp = tps[i];
                    if (!tp.IsOpen) continue;
                    book.RemoveById(tp.OrderId);
                    tp.Status = Order.Statuses.Cancelled;
                    tp.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(tp, ct).ConfigureAwait(false);
                }

                // Cancel-the-remainder of a still-resting limit parent.
                if (parent.IsOpen && parent.IsLimitOrder && parent.RemainingQuantity > 0)
                {
                    book.RemoveById(parent.OrderId);
                    parent.Cancel();
                    parent.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(parent, ct).ConfigureAwait(false);
                    await ReleaseParentBuyReservationAsync(parent, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);

        _bracketParents.TryRemove(parentId, out _);
        _orderCache.NotifyOrdersMutated(new[] { parent.UserId });
    }

    public async Task OnMemberCancelledAsync(Order cancelled, CancellationToken ct = default)
    {
        if (cancelled is null) return;
        int? pidNullable = cancelled.IsBracketChild ? cancelled.ParentOrderId
                          : (IsBracketParent(cancelled.OrderId) ? cancelled.OrderId : (int?)null);
        if (pidNullable is not int parentId) return;

        Order? parent = _registry.TryGet(parentId, out var pc) ? pc
                       : await _db.GetOrderById(parentId, ct).ConfigureAwait(false);
        if (parent is null) { _bracketParents.TryRemove(parentId, out _); return; }

        // Group teardown only when the user cancels an UNFILLED parent (discard dormant legs) or the
        // SL (the TPs can't rest unprotected). A single-TP cancel, or a partially-filled parent
        // cancel, leaves the surviving legs protecting the held shares. The cancelled order itself +
        // its reservation were already handled by the normal cancel path.
        bool teardown = (cancelled.OrderId == parentId && parent.AmountFilled == 0)
                     || (cancelled.IsBracketChild && cancelled.IsStopOrder);
        if (!teardown)
        {
            if (cancelled.OrderId == parentId) _bracketParents.TryRemove(parentId, out _);
            return;
        }

        var (sl, tps) = await LoadLegsAsync(parentId, ct).ConfigureAwait(false);
        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts
                .AcquirePositionGateAsync(parent.UserId, parent.StockId, ct).ConfigureAwait(false);
            await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                // Cancel the SL sibling unless it's the order the user already cancelled. A dormant
                // (Attached) SL reserves nothing; an armed SL would have been the cancel target
                // (released by the normal path), so here it's always reservation-free.
                if (sl is not null && sl.OrderId != cancelled.OrderId && !sl.IsClosed)
                {
                    _stopWatcher.Value.Disarm(sl.OrderId);
                    book.RemoveById(sl.OrderId);
                    sl.Status = Order.Statuses.Cancelled;
                    sl.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(sl, ct).ConfigureAwait(false);
                }
                // Cancel every TP sibling (book-remove; they reserve nothing).
                for (int i = 0; i < tps.Count; i++)
                {
                    var tp = tps[i];
                    if (tp.OrderId == cancelled.OrderId || tp.IsClosed) continue;
                    if (tp.IsOpen) book.RemoveById(tp.OrderId);
                    tp.Status = Order.Statuses.Cancelled;
                    tp.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(tp, ct).ConfigureAwait(false);
                }
                // If the SL was cancelled while a limit parent still rests, pull the parent too.
                if (parent.OrderId != cancelled.OrderId && parent.IsOpen
                    && parent.IsLimitOrder && parent.RemainingQuantity > 0)
                {
                    book.RemoveById(parent.OrderId);
                    parent.Cancel();
                    parent.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(parent, ct).ConfigureAwait(false);
                    await ReleaseParentBuyReservationAsync(parent, ct).ConfigureAwait(false);
                }
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);

        _bracketParents.TryRemove(parentId, out _);
        _orderCache.NotifyOrdersMutated(new[] { parent.UserId });
    }

    // Release a cancelled limit parent's remaining buy reservation (its unfilled portion's cash),
    // under the fund gate, mirroring the order-modifier/canceller release shape.
    private async Task ReleaseParentBuyReservationAsync(Order parent, CancellationToken ct)
    {
        if (!parent.IsBuyOrder || parent.CurrentBuyReservation <= 0m) return;
        await using var gate = await _accounts
            .AcquireFundGateAsync(parent.UserId, parent.CurrencyType, ct).ConfigureAwait(false);
        var fund = _accounts.GetFund(parent.UserId, parent.CurrencyType);
        if (fund is null) return;
        var toRelease = Math.Min(parent.CurrentBuyReservation, fund.ReservedBalance);
        if (toRelease <= 0m) return;
        var resB = fund.ReservedBalance;
        var totB = fund.TotalBalance;
        fund.UnreserveFunds(toRelease);
        fund.UpdatedAt = TimeHelper.NowUtc();
        var orderBefore = parent.CurrentBuyReservation;
        parent.ConsumeBuyReservation(toRelease);
        _ledger.LogFund(parent.UserId, parent.CurrencyType, parent.OrderId,
            "Bracket:CancelRemainder:Release", toRelease, resB, fund.ReservedBalance, totB, fund.TotalBalance);
        _ledger.LogOrder(parent.UserId, parent.OrderId, "Bracket:CancelRemainder:Release",
            toRelease, orderBefore, parent.CurrentBuyReservation,
            parent.CurrentSellReservedQty, parent.CurrentSellReservedQty);
        await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
    }
}
