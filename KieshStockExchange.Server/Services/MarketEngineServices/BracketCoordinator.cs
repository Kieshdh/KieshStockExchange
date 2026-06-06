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
/// §P5b short brackets (sell entry → buy-stop SL + buy-limit TPs) mirror this with cash: the SL owns a
/// CASH pool (CurrentBuyReservation = SL_worst × held) on Fund.ReservedBalance, TPs reserve 0 and draw it
/// at fill; the short entry's collateral is a separate lock. The short path lives in the OnParentFillShort
/// / OnChildFillShort / OnStopFiringShort / OnMemberCancelledShort methods (the long path is unchanged);
/// gating is fund→position via AcquireUserGatesAsync. Invariant: Σ leg buy-reservation + Σ short
/// collateral == Fund.ReservedBalance.
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
            // §F5: exclude CANCELLED legs only (NOT all closed). A cancelled SL must be invisible to
            // OnParentFillAsync so the parent fills the take-profit-only path; but a FILLED TP must stay
            // in the list because ComputeHeld sums its AmountFilled to size `held` — dropping it
            // overstates held and breaks Σ CSR == Position.ReservedQuantity. A filled TP isn't Attached,
            // so the arming loops skip it anyway.
            if (o.IsCancelled) continue;
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
        if (parent is null) return;
        await _accounts.EnsureLoadedAsync(parent.UserId, ct).ConfigureAwait(false);
        if (parent.IsSellOrder) { await OnParentFillShortAsync(parent, ct).ConfigureAwait(false); return; } // §P5b
        if (!parent.IsBuyOrder) return; // long brackets beyond here

        var (sl, tps) = await LoadLegsAsync(parent.OrderId, ct).ConfigureAwait(false);
        int held = ComputeHeld(parent, sl, tps);
        if (held <= 0) return;

        // book → gate → tx. The book lock covers TP upserts; the position gate + tx cover the
        // reservation resize and the persist. With an SL the SL owns the whole pool (Model B); a
        // take-profit-only bracket has no pool, so each armed TP reserves its own shares.
        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts
                .AcquirePositionGateAsync(parent.UserId, parent.StockId, ct).ConfigureAwait(false);
            var pos = _accounts.GetPosition(parent.UserId, parent.StockId);
            if (pos is null) return;

            if (sl is not null)
            {
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
            }
            else
            {
                // Take-profit-only bracket: no shared pool. Fill-up-whole-legs, each armed TP
                // reserving its own quantity from the freshly-acquired (available) shares — the
                // standard sell-limit reservation, so the normal fill/cancel paths release it.
                int posResBefore = pos.ReservedQuantity;
                var armed = new List<(Order Tp, int Before)>(tps.Count);
                await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    int cum = 0;
                    for (int i = 0; i < tps.Count; i++)
                    {
                        var tp = tps[i];
                        cum += tp.Quantity;
                        if (!tp.IsAttached || cum > held) continue;

                        int need = tp.Quantity;
                        if (pos.AvailableQuantity < need)
                        {
                            _logger.LogWarning(
                                "Bracket #{Parent}: TP #{Tp} can't reserve {Need} (avail {Avail}); capping.",
                                parent.OrderId, tp.OrderId, need, pos.AvailableQuantity);
                            need = pos.AvailableQuantity;
                        }
                        if (need <= 0) continue;

                        int posBeforeThis = pos.ReservedQuantity;
                        int tpBefore = tp.CurrentSellReservedQty;
                        pos.ReserveStock(need);
                        tp.TakeSellReservation(need);
                        armed.Add((tp, tpBefore));
                        tp.Status = Order.Statuses.Open;
                        tp.UpdatedAt = TimeHelper.NowUtc();
                        await _db.UpdateOrder(tp, ct).ConfigureAwait(false);
                        book.UpsertOrder(tp);
                        _ledger.LogPosition(parent.UserId, parent.StockId, tp.OrderId, "Bracket:ArmTP",
                            need, posBeforeThis, pos.ReservedQuantity, pos.Quantity, pos.Quantity);
                    }

                    if (pos.PositionId != 0) await _db.UpdateAllAsync(new[] { pos }, ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    if (pos.ReservedQuantity != posResBefore) pos.ReservedQuantity = posResBefore;
                    foreach (var (tp, before) in armed)
                        tp.RestoreReservationFromSnapshot(tp.CurrentBuyReservation, before);
                    throw;
                }
            }
        }).ConfigureAwait(false);

        if (sl is not null && sl.IsArmed) _stopWatcher.Value.Arm(sl);
        _orderCache.NotifyOrdersMutated(new[] { parent.UserId });
    }

    public async Task OnChildFillAsync(Order tp, CancellationToken ct = default)
    {
        if (tp is null || tp.ParentOrderId is not int parentId) return;
        await _accounts.EnsureLoadedAsync(tp.UserId, ct).ConfigureAwait(false);

        Order? parent = _registry.TryGet(parentId, out var pc) ? pc
                       : await _db.GetOrderById(parentId, ct).ConfigureAwait(false);
        if (parent is null) return;
        if (parent.IsSellOrder) { await OnChildFillShortAsync(tp, parent, parentId, ct).ConfigureAwait(false); return; } // §P5b
        var (sl, tps) = await LoadLegsAsync(parentId, ct).ConfigureAwait(false);
        int held = ComputeHeld(parent, sl, tps);

        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts
                .AcquirePositionGateAsync(parent.UserId, parent.StockId, ct).ConfigureAwait(false);

            await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                if (sl is not null)
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
                }
                else if (held <= 0)
                {
                    // Take-profit-only bracket: the TP fill already released its own reservation via
                    // the normal sell-limit path; nothing to resize. Retire the bracket once the
                    // covered shares are fully exited.
                    _bracketParents.TryRemove(parentId, out _);
                }

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
        if (parent.IsSellOrder) { await OnStopFiringShortAsync(sl, parent, parentId, ct).ConfigureAwait(false); return; } // §P5b
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
        if (parent.IsSellOrder) { await OnMemberCancelledShortAsync(cancelled, parent, parentId, ct).ConfigureAwait(false); return; } // §P5b

        // Group teardown only when the user cancels an UNFILLED parent (discard dormant legs) or the
        // SL (the TPs can't rest unprotected). A single-TP cancel, or a partially-filled parent
        // cancel, leaves the surviving legs protecting the held shares. The cancelled order itself +
        // its reservation were already handled by the normal cancel path.
        // §F5: cancelling the SL BEFORE the parent fills no longer tears down — the dormant TPs remain
        // and the bracket survives as take-profit-only (the !teardown early-return keeps _bracketParents,
        // and LoadLegsAsync's IsCancelled filter makes the cancelled SL invisible on the later fill). A
        // filled parent's SL-cancel still tears down (live TPs can't rest unprotected).
        bool teardown = (cancelled.OrderId == parentId && parent.AmountFilled == 0)
                     || (cancelled.IsBracketChild && cancelled.IsStopOrder && parent.AmountFilled > 0);
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

    // ───────────────────────── §P5b short brackets (sell entry → buy-to-close legs) ─────────────────────────
    // Inverted Model B: the SL owns a CASH pool (its CurrentBuyReservation = SL_worst × held); TPs reserve 0
    // and draw the pool at fill via the settler's shared-fund consume (reuse win #1). The short entry's
    // collateral is a separate ReservedBalance lock. Invariant: Σ leg-buy-reservation + Σ short-collateral
    // == Fund.ReservedBalance. Gate is fund→position (AcquireUserGatesAsync), never the legacy nesting.

    private static decimal SlWorst(Order sl)
        => ShortBracketMath.SlWorst(sl.IsStopLimitOrder, sl.Price, sl.StopPrice ?? 0m, sl.SlippagePercent ?? 0m);

    public async Task OnParentFillShortAsync(Order parent, CancellationToken ct)
    {
        var (sl, tps) = await LoadLegsAsync(parent.OrderId, ct).ConfigureAwait(false);
        int held = ComputeHeld(parent, sl, tps);
        if (held <= 0) return;
        // Short TPs are buy-limits BELOW entry; nearest-market-first (fill-up order) is highest-price-first.
        tps.Sort((a, b) => b.Price.CompareTo(a.Price));

        bool degraded = false;
        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts.AcquireUserGatesAsync(
                new[] { (parent.UserId, parent.CurrencyType) },
                new[] { (parent.UserId, parent.StockId) }, ct).ConfigureAwait(false);
            var fund = _accounts.GetFund(parent.UserId, parent.CurrencyType);
            if (fund is null) return;

            if (sl is not null)
            {
                decimal pool = ShortBracketMath.Pool(SlWorst(sl), held);
                decimal delta = pool - sl.CurrentBuyReservation;   // grow toward the worst-case pool
                if (delta > 0m && CurrencyHelper.LessThan(fund.AvailableBalance, delta, parent.CurrencyType))
                {
                    // Degrade-to-TP-only (verified policy): not enough cash to fund the SL pool → drop the
                    // SL, keep the take-profits (each reserves its own buyback), flag the short unprotected.
                    degraded = true;
                    await using var dtx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
                    try
                    {
                        _stopWatcher.Value.Disarm(sl.OrderId);
                        sl.Status = Order.Statuses.Cancelled;
                        sl.UpdatedAt = TimeHelper.NowUtc();
                        await _db.UpdateOrder(sl, ct).ConfigureAwait(false);
                        await ArmShortTpsOwnCashAsync(book, fund, parent, tps, held, ct).ConfigureAwait(false);
                        if (fund.UserId != 0) await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
                        await dtx.CommitAsync(ct).ConfigureAwait(false);
                    }
                    catch { await dtx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
                    return;
                }

                decimal resBefore = fund.ReservedBalance;
                decimal slBefore = sl.CurrentBuyReservation;
                if (delta > 0m) { fund.ReserveFunds(delta); sl.TakeBuyReservation(delta); }

                await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    sl.Quantity = held + sl.AmountFilled;          // RemainingQuantity == held shares to buy back
                    if (sl.IsAttached) sl.Arm(); else sl.Status = Order.Statuses.Pending;
                    sl.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(sl, ct).ConfigureAwait(false);

                    int cum = 0;
                    for (int i = 0; i < tps.Count; i++)
                    {
                        var tp = tps[i];
                        cum += tp.Quantity;
                        if (tp.IsAttached && cum <= held)
                        {
                            tp.Status = Order.Statuses.Open;       // rests as a buy-limit reserving 0 (draws the pool)
                            tp.UpdatedAt = TimeHelper.NowUtc();
                            await _db.UpdateOrder(tp, ct).ConfigureAwait(false);
                            book.UpsertOrder(tp);
                        }
                    }
                    if (fund.UserId != 0) await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    if (fund.ReservedBalance != resBefore) fund.ReservedBalance = resBefore;
                    sl.RestoreReservationFromSnapshot(slBefore, sl.CurrentSellReservedQty);
                    throw;
                }
                _ledger.LogFund(sl.UserId, sl.CurrencyType, sl.OrderId, "Bracket:Short:ArmSLPool",
                    Math.Max(0m, delta), resBefore, fund.ReservedBalance, fund.TotalBalance, fund.TotalBalance);
            }
            else
            {
                // Take-profit-only short bracket: each TP reserves its own buyback cash.
                await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    await ArmShortTpsOwnCashAsync(book, fund, parent, tps, held, ct).ConfigureAwait(false);
                    if (fund.UserId != 0) await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
            }
        }).ConfigureAwait(false);

        if (!degraded && sl is not null && sl.IsArmed) _stopWatcher.Value.Arm(sl);
        _orderCache.NotifyOrdersMutated(new[] { parent.UserId });
        if (degraded)
            _logger.LogWarning("Bracket #{Parent}: insufficient cash to fund the stop-loss pool; the short is now " +
                "UNPROTECTED (take-profit-only).", parent.OrderId);
    }

    // Arm the now-covered TP buy-limits, each reserving its own worst-case buyback (TP limit × qty) from the
    // fund — the take-profit-only / degraded path (no shared SL pool to draw). Caller holds the fund gate+tx.
    private async Task ArmShortTpsOwnCashAsync(OrderBook book, Fund fund, Order parent,
        List<Order> tps, int held, CancellationToken ct)
    {
        int cum = 0;
        for (int i = 0; i < tps.Count; i++)
        {
            var tp = tps[i];
            cum += tp.Quantity;
            if (!tp.IsAttached || cum > held) continue;
            decimal need = CurrencyHelper.Notional(tp.Price, tp.Quantity, parent.CurrencyType);
            if (CurrencyHelper.LessThan(fund.AvailableBalance, need, parent.CurrencyType))
                need = fund.AvailableBalance;                      // defensive cap
            if (need <= 0m) continue;
            decimal posBefore = fund.ReservedBalance;
            fund.ReserveFunds(need);
            tp.TakeBuyReservation(need);
            tp.Status = Order.Statuses.Open;
            tp.UpdatedAt = TimeHelper.NowUtc();
            await _db.UpdateOrder(tp, ct).ConfigureAwait(false);
            book.UpsertOrder(tp);
            _ledger.LogFund(parent.UserId, parent.CurrencyType, tp.OrderId, "Bracket:Short:ArmTPCash",
                need, posBefore, fund.ReservedBalance, fund.TotalBalance, fund.TotalBalance);
        }
    }

    public async Task OnChildFillShortAsync(Order tp, Order parent, int parentId, CancellationToken ct)
    {
        var (sl, tps) = await LoadLegsAsync(parentId, ct).ConfigureAwait(false);
        int held = ComputeHeld(parent, sl, tps);

        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts.AcquireUserGatesAsync(
                new[] { (parent.UserId, parent.CurrencyType) },
                new[] { (parent.UserId, parent.StockId) }, ct).ConfigureAwait(false);
            var fund = _accounts.GetFund(parent.UserId, parent.CurrencyType);
            if (fund is null) return;

            await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                if (sl is not null)
                {
                    // §P6: shrink the SL's cash pool to the new worst-case for the remaining held shares and
                    // release ONLY that pool shrinkage from the fund. The previous formula derived the release
                    // from whole-fund ReservedBalance (fund.ReservedBalance − desiredPool − collateral), which
                    // under bot load wrongly swept up this user's OTHER shorts' collateral + other orders'
                    // reservations (all share one Fund.ReservedBalance) as "cushion" and released them — a large
                    // reservation-locking leak (single-bracket admin tests never hit it, the bot soak did). The
                    // bracket-local poolDrop touches only this SL's pool, mirroring OnStopFiringShortAsync. The
                    // TP's buyback + its pro-rata collateral release were already handled by the settler.
                    decimal desiredPool = ShortBracketMath.Pool(SlWorst(sl), held);
                    decimal poolDrop = sl.CurrentBuyReservation - desiredPool;
                    if (poolDrop > 0m)
                    {
                        poolDrop = Math.Min(poolDrop, sl.CurrentBuyReservation);
                        var rel = Math.Min(poolDrop, fund.ReservedBalance);
                        if (rel > 0m)
                        {
                            var rb = fund.ReservedBalance; var tb = fund.TotalBalance;
                            fund.UnreserveFunds(rel);
                            fund.UpdatedAt = TimeHelper.NowUtc();
                            _ledger.LogFund(parent.UserId, parent.CurrencyType, sl.OrderId,
                                "Bracket:Short:TPCushionRelease", rel, rb, fund.ReservedBalance, tb, fund.TotalBalance);
                        }
                        sl.ConsumeBuyReservation(poolDrop);
                    }

                    if (held <= 0)
                    {
                        _stopWatcher.Value.Disarm(sl.OrderId);
                        book.RemoveById(sl.OrderId);
                        sl.Status = Order.Statuses.Cancelled;
                        sl.UpdatedAt = TimeHelper.NowUtc();
                        _bracketParents.TryRemove(parentId, out _);
                    }
                    else
                    {
                        sl.Quantity = held + sl.AmountFilled;
                        sl.UpdatedAt = TimeHelper.NowUtc();
                    }
                    await _db.UpdateOrder(sl, ct).ConfigureAwait(false);
                    if (fund.UserId != 0) await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
                }
                else if (held <= 0)
                {
                    // TP-only: the TP's own reservation + any savings were settled on its fill; just retire.
                    _bracketParents.TryRemove(parentId, out _);
                }

                // Cancel-the-remainder of a still-resting limit short parent (protective leg fired while the
                // entry hadn't fully filled): pull it and release its unfilled collateral inline (under the gate).
                if (parent.IsOpen && parent.IsLimitOrder && parent.RemainingQuantity > 0)
                {
                    book.RemoveById(parent.OrderId);
                    parent.Cancel();
                    parent.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(parent, ct).ConfigureAwait(false);
                    ReleaseShortCollateralInline(parent, fund);
                    if (fund.UserId != 0) await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        }).ConfigureAwait(false);

        _orderCache.NotifyOrdersMutated(new[] { parent.UserId });
    }

    public async Task OnStopFiringShortAsync(Order sl, Order parent, int parentId, CancellationToken ct)
    {
        var (_, tps) = await LoadLegsAsync(parentId, ct).ConfigureAwait(false);
        int held = parent.AmountFilled;
        for (int i = 0; i < tps.Count; i++) held -= tps[i].AmountFilled;
        if (held < 0) held = 0;

        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts.AcquireUserGatesAsync(
                new[] { (parent.UserId, parent.CurrencyType) },
                new[] { (parent.UserId, parent.StockId) }, ct).ConfigureAwait(false);
            var fund = _accounts.GetFund(parent.UserId, parent.CurrencyType);
            if (fund is null) return;

            await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                // Size the SL cash pool to the live held (release any over-reserved cushion); the SL then
                // promotes and buys `held` back, and the settler's buy-savings path releases the final
                // worst-case-vs-actual cushion on the fill.
                decimal desiredPool = ShortBracketMath.Pool(SlWorst(sl), held);
                decimal poolDrop = sl.CurrentBuyReservation - desiredPool;
                if (poolDrop > 0m)
                {
                    poolDrop = Math.Min(poolDrop, sl.CurrentBuyReservation);
                    var rel = Math.Min(poolDrop, fund.ReservedBalance);
                    if (rel > 0m)
                    {
                        var rb = fund.ReservedBalance; var tb = fund.TotalBalance;
                        fund.UnreserveFunds(rel);
                        fund.UpdatedAt = TimeHelper.NowUtc();
                        _ledger.LogFund(parent.UserId, parent.CurrencyType, sl.OrderId,
                            "Bracket:Short:SLFireResize", rel, rb, fund.ReservedBalance, tb, fund.TotalBalance);
                    }
                    sl.ConsumeBuyReservation(poolDrop);
                }
                sl.Quantity = held + sl.AmountFilled;
                sl.UpdatedAt = TimeHelper.NowUtc();
                await _db.UpdateOrder(sl, ct).ConfigureAwait(false);

                // Cancel all open TP buy-limits first (they reserve 0 → nothing to release). SL still off-book.
                for (int i = 0; i < tps.Count; i++)
                {
                    var tp = tps[i];
                    if (!tp.IsOpen) continue;
                    book.RemoveById(tp.OrderId);
                    tp.Status = Order.Statuses.Cancelled;
                    tp.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(tp, ct).ConfigureAwait(false);
                }
                if (fund.UserId != 0) await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);

                // Cancel-the-remainder of a still-resting limit short parent.
                if (parent.IsOpen && parent.IsLimitOrder && parent.RemainingQuantity > 0)
                {
                    book.RemoveById(parent.OrderId);
                    parent.Cancel();
                    parent.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(parent, ct).ConfigureAwait(false);
                    ReleaseShortCollateralInline(parent, fund);
                    if (fund.UserId != 0) await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        }).ConfigureAwait(false);

        _bracketParents.TryRemove(parentId, out _);
        _orderCache.NotifyOrdersMutated(new[] { parent.UserId });
    }

    public async Task OnMemberCancelledShortAsync(Order cancelled, Order parent, int parentId, CancellationToken ct)
    {
        // Teardown rule mirrors the long path: cancel the whole group only when an UNFILLED parent or the SL
        // (with a filled parent) is cancelled; a single-TP / partial-parent cancel leaves the rest.
        bool teardown = (cancelled.OrderId == parentId && parent.AmountFilled == 0)
                     || (cancelled.IsBracketChild && cancelled.IsStopOrder && parent.AmountFilled > 0);
        if (!teardown)
        {
            if (cancelled.OrderId == parentId) _bracketParents.TryRemove(parentId, out _);
            return;
        }

        var (sl, tps) = await LoadLegsAsync(parentId, ct).ConfigureAwait(false);
        await _books.WithBookLockAsync(parent.StockId, parent.CurrencyType, ct, async book =>
        {
            await using var gate = await _accounts.AcquireUserGatesAsync(
                new[] { (parent.UserId, parent.CurrencyType) },
                new[] { (parent.UserId, parent.StockId) }, ct).ConfigureAwait(false);
            var fund = _accounts.GetFund(parent.UserId, parent.CurrencyType);

            await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                // Cancel the SL sibling (release its cash pool, if any) unless it's the cancel target.
                if (sl is not null && sl.OrderId != cancelled.OrderId && !sl.IsClosed)
                {
                    _stopWatcher.Value.Disarm(sl.OrderId);
                    book.RemoveById(sl.OrderId);
                    if (fund is not null) ReleaseLegBuyReservationInline(sl, fund);
                    sl.Status = Order.Statuses.Cancelled;
                    sl.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(sl, ct).ConfigureAwait(false);
                }
                // Cancel every TP sibling (release own buyback cash for a TP-only/degraded bracket; 0 otherwise).
                for (int i = 0; i < tps.Count; i++)
                {
                    var tp = tps[i];
                    if (tp.OrderId == cancelled.OrderId || tp.IsClosed) continue;
                    if (tp.IsOpen) book.RemoveById(tp.OrderId);
                    if (fund is not null) ReleaseLegBuyReservationInline(tp, fund);
                    tp.Status = Order.Statuses.Cancelled;
                    tp.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(tp, ct).ConfigureAwait(false);
                }
                // If the SL was cancelled while a limit short parent still rests, pull the parent + release
                // its unfilled collateral.
                if (parent.OrderId != cancelled.OrderId && parent.IsOpen
                    && parent.IsLimitOrder && parent.RemainingQuantity > 0)
                {
                    book.RemoveById(parent.OrderId);
                    parent.Cancel();
                    parent.UpdatedAt = TimeHelper.NowUtc();
                    await _db.UpdateOrder(parent, ct).ConfigureAwait(false);
                    if (fund is not null) ReleaseShortCollateralInline(parent, fund);
                }
                if (fund is not null && fund.UserId != 0) await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        }).ConfigureAwait(false);

        _bracketParents.TryRemove(parentId, out _);
        _orderCache.NotifyOrdersMutated(new[] { parent.UserId });
    }

    // Release a sibling leg's cash buy-reservation inline (caller holds the fund gate + tx). No-op when the
    // leg reserves nothing (long legs, or a short SL-pool's TPs which reserve 0).
    private void ReleaseLegBuyReservationInline(Order leg, Fund fund)
    {
        var amt = leg.CurrentBuyReservation;
        if (amt <= 0m) return;
        var rel = Math.Min(amt, fund.ReservedBalance);
        if (rel <= 0m) return;
        var rb = fund.ReservedBalance; var tb = fund.TotalBalance;
        fund.UnreserveFunds(rel);
        fund.UpdatedAt = TimeHelper.NowUtc();
        leg.ConsumeBuyReservation(rel);
        _ledger.LogFund(leg.UserId, leg.CurrencyType, leg.OrderId, "Bracket:Short:CancelLeg:Release",
            rel, rb, fund.ReservedBalance, tb, fund.TotalBalance);
    }

    // Release a cancelled limit short parent's unfilled-portion collateral inline (caller holds fund gate+tx).
    private void ReleaseShortCollateralInline(Order parent, Fund fund)
    {
        var amt = parent.CurrentShortCollateral;
        if (amt <= 0m) return;
        var rel = Math.Min(amt, fund.ReservedBalance);
        if (rel <= 0m) return;
        var rb = fund.ReservedBalance; var tb = fund.TotalBalance;
        fund.UnreserveFunds(rel);
        fund.UpdatedAt = TimeHelper.NowUtc();
        parent.ConsumeShortCollateral(rel);
        _ledger.LogFund(parent.UserId, parent.CurrencyType, parent.OrderId, "Bracket:Short:CancelRemainder:ReleaseCollateral",
            rel, rb, fund.ReservedBalance, tb, fund.TotalBalance);
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
