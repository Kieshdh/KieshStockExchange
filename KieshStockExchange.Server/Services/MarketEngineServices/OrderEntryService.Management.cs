using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Server.Services.HostedServices;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed partial class OrderEntryService
{
    public async Task<OrderResult> CancelOrderAsync(int userId, int orderId, CancellationToken ct = default)
    {
        var denied = await VerifyOwnershipAsync(userId, orderId, ct).ConfigureAwait(false);
        if (denied != null) return denied;
        var result = await _engine.CancelOrderAsync(orderId, ct).ConfigureAwait(false);
        // Drop it from the armed index too (no-op when it isn't an armed stop).
        _stopWatcher.Disarm(orderId);
        return result;
    }

    public async Task<OrderResult> ModifyOrderAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default)
    {
        var denied = await VerifyOwnershipAsync(userId, orderId, ct).ConfigureAwait(false);
        return denied ?? await _engine.ModifyOrderAsync(orderId, newQuantity, newPrice, ct).ConfigureAwait(false);
    }

    // §3.6 P3: modify an armed stop's trigger / stop-limit price / quantity. Gate on ownership,
    // enforce the same direction sanity as arm-time for a new StopPrice (the engine validator stays
    // structural), then re-index the trigger watcher so it fires at the new level.
    public async Task<OrderResult> ModifyStopAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newStopPrice = null, decimal? newLimitPrice = null, CancellationToken ct = default)
    {
        var order = await _db.GetOrderById(orderId, ct).ConfigureAwait(false);
        if (order is null || order.UserId != userId)
            return OrderResultFactory.InvalidParams("Order not found.");

        // A trigger modified onto/across the market is intentionally allowed: it's already met, so the
        // watcher promotes it on the next tick and it fills like a market order. The client shows a
        // non-blocking warning before Confirm (mirrors the marketable-limit hint) — no rejection here.
        var result = await _engine.ModifyStopAsync(orderId, newQuantity, newStopPrice, newLimitPrice, ct).ConfigureAwait(false);

        // Re-index the watcher (disarm old snapshot + arm the updated trigger) so it fires at the
        // new StopPrice. Re-read the persisted order so Arm caches the fresh StopPrice/IsBuy.
        if (result.PlacedSuccessfully)
        {
            var updated = await _db.GetOrderById(orderId, ct).ConfigureAwait(false);
            _stopWatcher.Disarm(orderId);
            if (updated is { IsArmed: true }) _stopWatcher.Arm(updated);
        }
        return result;
    }

    // §F5: modify one bracket leg (the SL or a TP), dormant or live. Ownership-gated. Dispatches by
    // the leg's status: a live armed SL / resting TP delegates to the proven modify paths (which handle
    // book + reservation + notify); a dormant (Attached) leg — which reserves nothing and isn't on the
    // book — is edited in place, re-validated against the shared bracket geometry, and only its row is
    // written. newPrice is the leg's stop price (SL) or limit price (TP).
    public async Task<OrderResult> ModifyBracketLegAsync(int userId, int legId, decimal newPrice,
        int newQuantity, CancellationToken ct = default)
    {
        var legDb = await _db.GetOrderById(legId, ct).ConfigureAwait(false);
        if (legDb is null || legDb.UserId != userId || legDb.ParentOrderId is not int parentId)
            return OrderResultFactory.InvalidParams("Order not found.");
        // Resolve to the canonical registry instance — the same object the BracketCoordinator reads at
        // arm time (LoadLegsAsync prefers canonical). Editing the DB-only copy would be silently
        // overwritten by the stale canonical when the parent fills.
        var leg = _registry.TryGet(legId, out var canon) ? canon : legDb;

        // Live legs: reuse the proven paths (book + reservation + notify already handled).
        if (leg.IsArmed && leg.IsStopOrder)   // parent filled, SL armed
            return await ModifyStopAsync(userId, legId, newQuantity, newStopPrice: newPrice, ct: ct).ConfigureAwait(false);
        if (leg.IsOpen && leg.IsLimitOrder)   // parent filled, TP resting on the book
            return await ModifyOrderAsync(userId, legId, newQuantity, newPrice, ct).ConfigureAwait(false);
        if (!leg.IsAttached)
            return OrderResultFactory.InvalidParams("This order can't be modified.");
        if (newQuantity <= 0) return OrderResultFactory.InvalidParams("Quantity must be positive.");

        // Dormant (Attached) leg: a dormant bracket is a resting LIMIT parent, so the entry reference is
        // the parent's limit price. Build the post-edit leg set and re-check the shared geometry before
        // persisting only this leg (siblings untouched — F12).
        var parent = await _db.GetOrderById(parentId, ct).ConfigureAwait(false);
        if (parent is null) return OrderResultFactory.InvalidParams("Bracket parent not found.");

        var siblings = await _db.GetBracketChildrenAsync(parentId, ct).ConfigureAwait(false);
        decimal entryRef = parent.Price;
        decimal? slStop = null;
        var tps = new List<(decimal Price, int Quantity)>();
        foreach (var s in siblings)
        {
            if (s.IsCancelled) continue;
            bool isThis = s.OrderId == legId;
            if (s.IsStopOrder)
                slStop = isThis ? newPrice : s.StopPrice;
            else if (s.IsLimitOrder)
                tps.Add(isThis ? (newPrice, newQuantity) : (s.Price, s.Quantity));
        }
        // Side-aware: a short bracket (sell entry) needs the short geometry rules + TPs sorted
        // toward-market-first (descending) for the validator's strict-monotonic check.
        bool isShort = parent.IsSellOrder;
        if (isShort) tps.Sort((a, b) => b.Price.CompareTo(a.Price));
        else         tps.Sort((a, b) => a.Price.CompareTo(b.Price));

        var geometryErr = BracketGeometryValidator.Validate(entryRef, slStop, tps, parent.Quantity, leg.CurrencyType, isShort);
        if (geometryErr != null) return geometryErr;

        if (leg.IsStopOrder) leg.StopPrice = CurrencyHelper.RoundMoney(newPrice, leg.CurrencyType);
        else                 leg.Price     = CurrencyHelper.RoundMoney(newPrice, leg.CurrencyType);
        leg.Quantity = newQuantity;
        leg.UpdatedAt = TimeHelper.NowUtc();
        await _db.UpdateOrder(leg, ct).ConfigureAwait(false);

        _orderCache.NotifyOrdersMutated(new[] { userId });
        return OrderResultFactory.BracketLegModified(leg);
    }

    // The engine cancels/modifies purely by orderId and is shared with system callers,
    // so it can't tell whose order it is. This is the user-facing entry, so gate here:
    // reject anything the caller doesn't own as a uniform "not found" — never reveal
    // that someone else's order exists. Returns null when the caller owns the order.
    private async Task<OrderResult?> VerifyOwnershipAsync(int userId, int orderId, CancellationToken ct)
    {
        var order = await _db.GetOrderById(orderId, ct).ConfigureAwait(false);
        return order is null || order.UserId != userId
            ? OrderResultFactory.InvalidParams("Order not found.")
            : null;
    }
}
