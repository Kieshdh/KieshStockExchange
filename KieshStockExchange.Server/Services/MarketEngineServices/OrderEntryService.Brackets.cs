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
    public async Task<OrderResult> PlaceBracketAsync(int userId, int stockId, int quantity, EntryType entry,
        CurrencyType currency, decimal? limitPrice, decimal? buyBudget, decimal? stopPrice,
        decimal? stopLimitPrice, decimal? stopSlippagePct,
        IReadOnlyList<(decimal Price, int Quantity)> takeProfits, CancellationToken ct = default,
        OrderSide side = OrderSide.Buy, int flipQuantity = 0)
    {
        ct.ThrowIfCancellationRequested();
        if (quantity <= 0) return OrderResultFactory.InvalidParams("Quantity must be positive.");
        bool isShort = side == OrderSide.Sell;
        takeProfits ??= Array.Empty<(decimal, int)>();
        if (takeProfits.Count > 3) return OrderResultFactory.InvalidParams("A bracket supports at most 3 take-profits.");
        // stopPrice null ⇒ a take-profit-only bracket (no protective stop); then ≥1 TP is required.
        bool hasStop = stopPrice.HasValue;
        if (hasStop && stopPrice!.Value <= 0m)
            return OrderResultFactory.InvalidParams("Stop price must be positive.");
        if (!hasStop && takeProfits.Count == 0)
            return OrderResultFactory.InvalidParams("A bracket needs a stop-loss or at least one take-profit.");

        // §P5b: a short bracket SL is a BUY-to-close whose cash pool must be sizeable, so it must be a
        // stop-limit or a slippage-capped market buy-stop — an uncapped market buy-stop has unbounded
        // buyback cost. (Long sell-stop SLs reserve shares and are unaffected.)
        if (isShort && hasStop && stopLimitPrice is null && !stopSlippagePct.HasValue)
            return OrderResultFactory.InvalidParams(
                "A short bracket's stop-loss must be a stop-limit or slippage-capped (uncapped is unsizable).");

        // Entry reference: the limit price for a limit parent, else the live market price. Long: SL below /
        // TPs above. Short: SL above / TPs below. A long market entry funds from buyBudget; a short market
        // entry just sells `quantity` short (collateral handled by the H/P1 settle path), no budget.
        decimal entryRef;
        if (entry == EntryType.Limit)
        {
            if (!(limitPrice > 0m)) return OrderResultFactory.InvalidParams("Limit price must be positive.");
            entryRef = limitPrice.Value;
        }
        else
        {
            if (!isShort && !(buyBudget > 0m))
                return OrderResultFactory.InvalidParams("Buy budget must be positive for a market entry.");
            entryRef = _data.Quotes.TryGetValue((stockId, currency), out var q) && q.LastPrice > 0m
                ? q.LastPrice
                : await _data.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
            if (entryRef <= 0m) return OrderResultFactory.InvalidParams("No live market price to anchor the bracket.");
        }

        // Shared geometry rules (side-aware), also used by ModifyBracketLegAsync so an edit checks identically.
        var geometryErr = BracketGeometryValidator.Validate(
            entryRef, hasStop ? stopPrice : null, takeProfits, quantity, currency, isShort);
        if (geometryErr != null) return geometryErr;

        var legSide = isShort ? OrderSide.Buy : OrderSide.Sell;   // protective legs close the position

        // Build the parent entry (long buy / short sell).
        var parent = new Order
        {
            UserId = userId, StockId = stockId, Quantity = quantity, CurrencyType = currency,
            Side = side, Entry = entry, Stop = StopKind.None,
            Price = entry == EntryType.Limit ? CurrencyHelper.RoundMoney(limitPrice!.Value, currency) : 0m,
            BuyBudget = (!isShort && entry == EntryType.Market) ? CurrencyHelper.RoundMoney(buyBudget!.Value, currency) : null,
            // Round 2 §0007 (Path 2): persist the flip portion. 0 for a round-trip-only entry or
            // any pre-Path-2 caller (default value of the parameter).
            FlipQuantity = Math.Max(0, Math.Min(flipQuantity, quantity)),
        };

        // Build the protective stop-loss (opposite side), if any. Stop-limit when a limit price is given;
        // otherwise a slippage-capped stop-market. For a short SL (buy-stop) the slippage anchor is the
        // trigger (StopPrice) — buy slippage isn't re-anchored at promotion; for a long SL (sell-stop) it's
        // the entry ref (re-anchored to the trigger at promotion, as today). Null ⇒ take-profit-only bracket.
        Order? sl = null;
        if (hasStop)
        {
            bool slCapped = stopLimitPrice is null && stopSlippagePct.HasValue;
            sl = new Order
            {
                UserId = userId, StockId = stockId, Quantity = quantity, CurrencyType = currency,
                Side = legSide, Stop = StopKind.Stop,
                Entry = stopLimitPrice is not null ? EntryType.Limit : EntryType.Market,
                StopPrice = CurrencyHelper.RoundMoney(stopPrice!.Value, currency),
                Price = stopLimitPrice is not null ? CurrencyHelper.RoundMoney(stopLimitPrice.Value, currency)
                      : slCapped ? CurrencyHelper.RoundMoney(isShort ? stopPrice!.Value : entryRef, currency) : 0m,
                SlippagePercent = slCapped ? stopSlippagePct : null,
            };
        }

        // Build the take-profit legs (long sell-limits / short buy-limits).
        var tps = new List<Order>(takeProfits.Count);
        for (int i = 0; i < takeProfits.Count; i++)
        {
            tps.Add(new Order
            {
                UserId = userId, StockId = stockId, Quantity = takeProfits[i].Quantity, CurrencyType = currency,
                Side = legSide, Entry = EntryType.Limit, Stop = StopKind.None,
                Price = CurrencyHelper.RoundMoney(takeProfits[i].Price, currency),
            });
        }

        // Structural validation of every leg before the engine reserves/inserts anything.
        var parentErr = _validator.ValidateNew(parent);
        if (parentErr != null) return parentErr;
        if (sl is not null)
        {
            var slErr = _validator.ValidateNew(sl);
            if (slErr != null) return slErr;
        }
        for (int i = 0; i < tps.Count; i++)
        {
            var tpErr = _validator.ValidateNew(tps[i]);
            if (tpErr != null) return tpErr;
        }

        return await _engine.PlaceBracketAsync(parent, sl, tps, ct).ConfigureAwait(false);
    }

    // Round 2 §0005: batched bracket-placement route — entry-point for the bot fleet's per-tick
    // bracket cohort (LongBracket + ShortBracket). Pre-validates each request, then hands the
    // built parent/SL/TP triples to the engine's PlaceBracketBatchAsync. Per-result success/error
    // semantics are identical to the per-order PlaceBracketAsync path; the only difference is the
    // engine collapses N×CreateOrder + N×Match into bulk inserts + a serialized match loop, which
    // bypasses the per-request Npgsql round-trip dominating the soak's CollectPendingOrders phase.
    //
    // Gated by Bots:Advanced:BracketBatch (default OFF — the AiTradeService wires the flag and
    // partitions BotAdvancedDecision into the batched cohort vs the legacy per-order path). When
    // flag-off, this method is never called.
    public async Task<IReadOnlyList<OrderResult>> PlaceBracketBatchAsync(
        IReadOnlyList<BracketBatchRequest> requests, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (requests is null || requests.Count == 0) return Array.Empty<OrderResult>();

        var results = new OrderResult[requests.Count];
        var built = new List<(int index, Order parent, Order? sl, List<Order> tps)>(requests.Count);

        for (int i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            var validation = await ValidateBracketRequestAsync(r, ct).ConfigureAwait(false);
            if (validation.Error is not null)
            {
                results[i] = validation.Error;
                continue;
            }
            built.Add((i, validation.Parent!, validation.Sl, validation.Tps!));
        }
        if (built.Count == 0) return results;

        // Stable ascending-userId order preserved by the request producer; we don't re-sort here
        // (AiTradeService.SubmitAdvancedAsync sorts by AiUserId before partitioning into the batch).

        var triples = new List<(Order Parent, Order? Sl, IReadOnlyList<Order> Tps)>(built.Count);
        for (int i = 0; i < built.Count; i++) triples.Add((built[i].parent, built[i].sl, built[i].tps));

        var engineResults = await _engine.PlaceBracketBatchAsync(triples, ct).ConfigureAwait(false);
        for (int i = 0; i < built.Count; i++)
            results[built[i].index] = engineResults[i];

        return results;
    }

    // Order construction half of PlaceBracketAsync, factored out for PlaceBracketBatchAsync.
    // Performs the same input validation and side-aware geometry checks; produces a parent +
    // optional SL + the TP list without touching the engine.
    private async Task<(OrderResult? Error, Order? Parent, Order? Sl, List<Order>? Tps)>
        ValidateBracketRequestAsync(BracketBatchRequest r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (r.Quantity <= 0) return (OrderResultFactory.InvalidParams("Quantity must be positive."), null, null, null);

        bool isShort = r.Side == OrderSide.Sell;
        var takeProfits = r.TakeProfits ?? Array.Empty<BracketLeg>();
        if (takeProfits.Count > 3)
            return (OrderResultFactory.InvalidParams("A bracket supports at most 3 take-profits."), null, null, null);

        bool hasStop = r.StopPrice.HasValue;
        if (hasStop && r.StopPrice!.Value <= 0m)
            return (OrderResultFactory.InvalidParams("Stop price must be positive."), null, null, null);
        if (!hasStop && takeProfits.Count == 0)
            return (OrderResultFactory.InvalidParams("A bracket needs a stop-loss or at least one take-profit."), null, null, null);
        if (isShort && hasStop && r.StopLimitPrice is null && !r.StopSlippagePct.HasValue)
            return (OrderResultFactory.InvalidParams(
                "A short bracket's stop-loss must be a stop-limit or slippage-capped (uncapped is unsizable)."),
                null, null, null);

        decimal entryRef;
        if (r.Entry == EntryType.Limit)
        {
            if (!(r.Price > 0m))
                return (OrderResultFactory.InvalidParams("Limit price must be positive."), null, null, null);
            entryRef = r.Price.Value;
        }
        else
        {
            if (!isShort && !(r.BuyBudget > 0m))
                return (OrderResultFactory.InvalidParams("Buy budget must be positive for a market entry."), null, null, null);
            entryRef = _data.Quotes.TryGetValue((r.StockId, r.Currency), out var q) && q.LastPrice > 0m
                ? q.LastPrice
                : await _data.GetLastPriceAsync(r.StockId, r.Currency, ct).ConfigureAwait(false);
            if (entryRef <= 0m)
                return (OrderResultFactory.InvalidParams("No live market price to anchor the bracket."), null, null, null);
        }

        var legSide = isShort ? OrderSide.Buy : OrderSide.Sell;

        var parent = new Order
        {
            UserId = r.UserId, StockId = r.StockId, Quantity = r.Quantity, CurrencyType = r.Currency,
            Side = r.Side, Entry = r.Entry, Stop = StopKind.None,
            Price = r.Entry == EntryType.Limit ? CurrencyHelper.RoundMoney(r.Price!.Value, r.Currency) : 0m,
            BuyBudget = (!isShort && r.Entry == EntryType.Market) ? CurrencyHelper.RoundMoney(r.BuyBudget!.Value, r.Currency) : null,
            // Round 2 §0007 (Path 2): persist the flip portion from the batch request.
            FlipQuantity = Math.Max(0, Math.Min(r.FlipQuantity, r.Quantity)),
        };

        Order? sl = null;
        if (hasStop)
        {
            bool slCapped = r.StopLimitPrice is null && r.StopSlippagePct.HasValue;
            sl = new Order
            {
                UserId = r.UserId, StockId = r.StockId, Quantity = r.Quantity, CurrencyType = r.Currency,
                Side = legSide, Stop = StopKind.Stop,
                Entry = r.StopLimitPrice is not null ? EntryType.Limit : EntryType.Market,
                StopPrice = CurrencyHelper.RoundMoney(r.StopPrice!.Value, r.Currency),
                Price = r.StopLimitPrice is not null ? CurrencyHelper.RoundMoney(r.StopLimitPrice.Value, r.Currency)
                      : slCapped ? CurrencyHelper.RoundMoney(isShort ? r.StopPrice!.Value : entryRef, r.Currency) : 0m,
                SlippagePercent = slCapped ? r.StopSlippagePct : null,
            };
        }

        var tps = new List<Order>(takeProfits.Count);
        for (int i = 0; i < takeProfits.Count; i++)
            tps.Add(new Order
            {
                UserId = r.UserId, StockId = r.StockId, Quantity = takeProfits[i].Quantity,
                CurrencyType = r.Currency, Side = legSide, Entry = EntryType.Limit, Stop = StopKind.None,
                Price = CurrencyHelper.RoundMoney(takeProfits[i].Price, r.Currency),
            });

        // Round 2 compile-fix: BracketGeometryValidator expects tuple list; project from BracketLeg.
        var tpTuples = new List<(decimal Price, int Quantity)>(takeProfits.Count);
        for (int i = 0; i < takeProfits.Count; i++) tpTuples.Add((takeProfits[i].Price, takeProfits[i].Quantity));
        var geometryErr = BracketGeometryValidator.Validate(
            entryRef, hasStop ? r.StopPrice : null, tpTuples, r.Quantity, r.Currency, isShort);
        if (geometryErr != null) return (geometryErr, null, null, null);

        var parentErr = _validator.ValidateNew(parent);
        if (parentErr != null) return (parentErr, null, null, null);
        if (sl is not null)
        {
            var slErr = _validator.ValidateNew(sl);
            if (slErr != null) return (slErr, null, null, null);
        }
        for (int i = 0; i < tps.Count; i++)
        {
            var tpErr = _validator.ValidateNew(tps[i]);
            if (tpErr != null) return (tpErr, null, null, null);
        }
        return (null, parent, sl, tps);
    }

    // Round 2 §0005: batched flat-only market short opens. Hands a per-request Order list to the
    // engine's PlaceMarketShortBatchAsync, which collapses the cohort's validation + book-touch
    // overhead. Per-result semantics match PlaceTrueMarketSellOrderAsync.
    public async Task<IReadOnlyList<OrderResult>> PlaceMarketShortBatchAsync(
        IReadOnlyList<MarketShortBatchRequest> requests, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (requests is null || requests.Count == 0) return Array.Empty<OrderResult>();

        var orders = new List<Order>(requests.Count);
        var results = new OrderResult[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            var inputValidation = _validator.ValidateInput(r.UserId, r.StockId, r.Quantity, 0m,
                r.Currency, buyOrder: false, limitOrder: false, slippagePercent: null, buyBudget: null);
            if (inputValidation != null) { results[i] = inputValidation; continue; }
            orders.Add(CreateOrder(r.UserId, r.StockId, r.Quantity, 0m, buyBudget: null,
                r.Currency, buyOrder: false, limitOrder: false, slippagePercent: null));
        }
        if (orders.Count == 0) return results;

        var engineResults = await _engine.PlaceMarketShortBatchAsync(orders, ct).ConfigureAwait(false);

        // Map engine results back to the original index list, skipping pre-rejected slots.
        int w = 0;
        for (int i = 0; i < requests.Count; i++)
        {
            if (results[i] is not null) continue;
            results[i] = engineResults[w++];
        }
        return results;
    }

    // STRETCH (Bots:Arbitrage:BatchLegs): batch the arb cohort's leg1 true-market buys through the
    // plain PlaceAndMatchBatch path (the same route the normal bot cohort uses). Per-request
    // construction/validation mirrors PlaceTrueMarketBuyOrderAsync exactly; the only change vs the
    // per-order path is that the cohort's inserts + settles are amortised into one batch.
    public async Task<IReadOnlyList<OrderResult>> PlaceTrueMarketBuyBatchAsync(
        IReadOnlyList<TrueMarketBuyBatchRequest> requests, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (requests is null || requests.Count == 0) return Array.Empty<OrderResult>();

        var orders = new List<Order>(requests.Count);
        var results = new OrderResult[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            var inputValidation = _validator.ValidateInput(r.UserId, r.StockId, r.Quantity, 0m,
                r.Currency, buyOrder: true, limitOrder: false, slippagePercent: null, buyBudget: r.BuyBudget);
            if (inputValidation != null) { results[i] = inputValidation; continue; }
            orders.Add(CreateOrder(r.UserId, r.StockId, r.Quantity, 0m, buyBudget: r.BuyBudget,
                r.Currency, buyOrder: true, limitOrder: false, slippagePercent: null));
        }
        if (orders.Count == 0) return results;

        var engineResults = await _engine.PlaceAndMatchBatchAsync(orders, ct).ConfigureAwait(false);

        int w = 0;
        for (int i = 0; i < requests.Count; i++)
        {
            if (results[i] is not null) continue;
            results[i] = engineResults[w++];
        }
        return results;
    }

    // STRETCH (Bots:Arbitrage:BatchLegs): batch the arb cohort's leg2 true-market sells (each sized
    // by the caller from its own leg1 fill). Per-request semantics match PlaceTrueMarketSellOrderAsync.
    public async Task<IReadOnlyList<OrderResult>> PlaceTrueMarketSellBatchAsync(
        IReadOnlyList<TrueMarketSellBatchRequest> requests, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (requests is null || requests.Count == 0) return Array.Empty<OrderResult>();

        var orders = new List<Order>(requests.Count);
        var results = new OrderResult[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            var inputValidation = _validator.ValidateInput(r.UserId, r.StockId, r.Quantity, 0m,
                r.Currency, buyOrder: false, limitOrder: false, slippagePercent: null, buyBudget: null);
            if (inputValidation != null) { results[i] = inputValidation; continue; }
            orders.Add(CreateOrder(r.UserId, r.StockId, r.Quantity, 0m, buyBudget: null,
                r.Currency, buyOrder: false, limitOrder: false, slippagePercent: null));
        }
        if (orders.Count == 0) return results;

        var engineResults = await _engine.PlaceAndMatchBatchAsync(orders, ct).ConfigureAwait(false);

        int w = 0;
        for (int i = 0; i < requests.Count; i++)
        {
            if (results[i] is not null) continue;
            results[i] = engineResults[w++];
        }
        return results;
    }
}
