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
    public Task<OrderResult> PlaceStopMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal buyBudget, CurrencyType currency, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice: null, buyBudget: buyBudget,
            slippagePct: null, currency, buyOrder: true, limitStop: false, ct);

    public Task<OrderResult> PlaceStopMarketSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, CurrencyType currency, decimal? slippagePct = null, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice: null, buyBudget: null,
            slippagePct: slippagePct, currency, buyOrder: false, limitStop: false, ct);

    public Task<OrderResult> PlaceStopLimitBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice, buyBudget: null,
            slippagePct: null, currency, buyOrder: true, limitStop: true, ct);

    public Task<OrderResult> PlaceStopLimitSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => ArmStopOrderAsync(userId, stockId, quantity, stopPrice, limitPrice, buyBudget: null,
            slippagePct: null, currency, buyOrder: false, limitStop: true, ct);

    // Build a stop order, enforce direction sanity against the live price (the engine validator
    // stays structural), arm it via the engine (reserve + persist Pending), and register it with
    // the trigger watcher on success. A sell-stop may carry a slippage cap (fires as a capped
    // market sell); the cap anchor is re-set to the trigger price at promotion time.
    private async Task<OrderResult> ArmStopOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal? limitPrice, decimal? buyBudget, decimal? slippagePct,
        CurrencyType currency, bool buyOrder, bool limitStop, CancellationToken ct)
    {
        var (order, error) = await BuildStopOrderAsync(userId, stockId, quantity, stopPrice,
            limitPrice, buyBudget, slippagePct, currency, buyOrder, limitStop, ct).ConfigureAwait(false);
        if (error is not null || order is null) return error!;

        var result = await _engine.ArmStopAsync(order, ct).ConfigureAwait(false);
        if (result.PlacedSuccessfully) _stopWatcher.Arm(order);
        return result;
    }

    // §A1a: the Order-construction half of ArmStopOrderAsync — direction sanity + capped-anchor
    // logic, no engine call — shared by the per-order arm above and the batch arm route.
    private async Task<(Order? order, OrderResult? error)> BuildStopOrderAsync(int userId,
        int stockId, int quantity, decimal stopPrice, decimal? limitPrice, decimal? buyBudget,
        decimal? slippagePct, CurrencyType currency, bool buyOrder, bool limitStop, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (stopPrice <= 0m) return (null, OrderResultFactory.InvalidParams("Stop price must be positive."));

        // Direction sanity: a sell-stop sits at/below market, a buy-stop at/above. Skipped only
        // when no live price is available yet (the watcher would still fire on the next cross).
        decimal market = _data.Quotes.TryGetValue((stockId, currency), out var q) && q.LastPrice > 0m
            ? q.LastPrice
            : await _data.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
        if (market > 0m)
        {
            if (buyOrder && stopPrice < market)
                return (null, OrderResultFactory.InvalidParams(
                    $"Buy-stop must be at or above the market price ({CurrencyHelper.Format(market, currency)})."));
            if (!buyOrder && stopPrice > market)
                return (null, OrderResultFactory.InvalidParams(
                    $"Sell-stop must be at or below the market price ({CurrencyHelper.Format(market, currency)})."));
        }

        // A capped market sell-stop carries a slippage cap + an anchor Price (the arm-time market,
        // re-anchored to the trigger price at promotion). Limit stops carry the limit price; an
        // uncapped market stop has Price 0 (and a budget for an uncapped buy).
        bool capped = !limitStop && slippagePct.HasValue && market > 0m;
        decimal price = limitStop ? CurrencyHelper.RoundMoney(limitPrice ?? 0m, currency)
                       : capped   ? CurrencyHelper.RoundMoney(market, currency)
                       : 0m;
        var order = new Order
        {
            UserId = userId,
            StockId = stockId,
            Quantity = quantity,
            Price = price,
            SlippagePercent = capped ? slippagePct : null,
            StopPrice = CurrencyHelper.RoundMoney(stopPrice, currency),
            BuyBudget = (!limitStop && buyOrder && !capped) ? CurrencyHelper.RoundMoney(buyBudget ?? 0m, currency) : null,
            CurrencyType = currency,
            Side = buyOrder ? OrderSide.Buy : OrderSide.Sell,
            Entry = limitStop ? EntryType.Limit : EntryType.Market,
            Stop = StopKind.Stop,
        };
        return (order, null);
    }

    // §3.6 P5 trailing stops (market-only). A trailing stop arms exactly like a static stop — same
    // reserve (shares for a sell, budget for a buy) and the same watcher — but its trigger trails a
    // monotonic watermark seeded at the arm-time market. The watcher recomputes the effective stop each
    // tick; here we just seed watermark + initial StopPrice and reuse the static arm path.
    public Task<OrderResult> PlaceTrailingStopBuyOrderAsync(int userId, int stockId, int quantity,
        decimal trailOffset, bool isPercent, decimal buyBudget, CurrencyType currency, CancellationToken ct = default)
        => ArmTrailingStopAsync(userId, stockId, quantity, trailOffset, isPercent, buyBudget, buyOrder: true, currency, ct);

    public Task<OrderResult> PlaceTrailingStopSellOrderAsync(int userId, int stockId, int quantity,
        decimal trailOffset, bool isPercent, CurrencyType currency, CancellationToken ct = default)
        => ArmTrailingStopAsync(userId, stockId, quantity, trailOffset, isPercent, buyBudget: null, buyOrder: false, currency, ct);

    private async Task<OrderResult> ArmTrailingStopAsync(int userId, int stockId, int quantity,
        decimal trailOffset, bool isPercent, decimal? buyBudget, bool buyOrder, CurrencyType currency, CancellationToken ct)
    {
        var (order, error) = await BuildTrailingStopOrderAsync(userId, stockId, quantity,
            trailOffset, isPercent, buyBudget, buyOrder, currency, ct).ConfigureAwait(false);
        if (error is not null || order is null) return error!;

        var result = await _engine.ArmStopAsync(order, ct).ConfigureAwait(false);
        if (result.PlacedSuccessfully) _stopWatcher.Arm(order);
        return result;
    }

    // §A1a: the Order-construction half of ArmTrailingStopAsync — watermark + initial-trigger
    // seeding, no engine call — shared by the per-order arm above and the batch arm route.
    private async Task<(Order? order, OrderResult? error)> BuildTrailingStopOrderAsync(int userId,
        int stockId, int quantity, decimal trailOffset, bool isPercent, decimal? buyBudget,
        bool buyOrder, CurrencyType currency, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (trailOffset <= 0m) return (null, OrderResultFactory.InvalidParams("Trail offset must be positive."));
        if (isPercent && trailOffset > 100m)
            return (null, OrderResultFactory.InvalidParams("Trailing percent offset must be between 0 and 100%."));

        // A trailing stop needs a live reference price to seed the watermark + initial trigger.
        decimal market = _data.Quotes.TryGetValue((stockId, currency), out var q) && q.LastPrice > 0m
            ? q.LastPrice
            : await _data.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
        if (market <= 0m)
            return (null, OrderResultFactory.InvalidParams("No reference price available to arm a trailing stop."));

        decimal watermark = market;
        decimal effStop = CurrencyHelper.RoundMoney(
            TrailMath.EffectiveStop(watermark, trailOffset, isPercent, buyOrder), currency);
        if (effStop <= 0m)
            return (null, OrderResultFactory.InvalidParams("Trail offset is too large for the current price."));

        var order = new Order
        {
            UserId = userId,
            StockId = stockId,
            Quantity = quantity,
            Price = 0m,                                   // fires as a market order
            StopPrice = effStop,                          // arm-time effective trigger (watcher updates it)
            TrailOffset = trailOffset,
            TrailIsPercent = isPercent,
            TrailWatermark = watermark,
            BuyBudget = buyOrder ? CurrencyHelper.RoundMoney(buyBudget ?? 0m, currency) : null,
            CurrencyType = currency,
            Side = buyOrder ? OrderSide.Buy : OrderSide.Sell,
            Entry = EntryType.Market,
            Stop = StopKind.Trailing,
        };
        return (order, null);
    }

    // §A1a: batch-arm the bots' protective sell-stops / trailing-sells. Builds each Order with
    // the same direction-sanity / capped-anchor / watermark logic as the per-order paths, hands
    // the whole list to the engine's ArmStopBatchAsync (share pre-reserve + ONE bulk-insert tx),
    // then arms the trigger watcher for every success — the batch owns watcher arming here, so
    // every successfully inserted arm is armed and none of the rejects are.
    public async Task<IReadOnlyList<OrderResult>> ArmStopSellBatchAsync(
        IReadOnlyList<StopArmRequest> requests, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (requests is null || requests.Count == 0) return Array.Empty<OrderResult>();

        var results = new OrderResult[requests.Count];
        var built = new List<(int index, Order order)>(requests.Count);
        for (int i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            if (r.Kind == StopArmKind.StopMarketBuy)
            {
                // Mis-route guard: StopMarketBuy shares the StopArmKind enum but reserves cash, not
                // shares — it must go through ArmStopBuyBatchAsync. Fail loudly instead of letting the
                // else branch below silently build a byte-wrong sell-stop.
                results[i] = OrderResultFactory.InvalidParams(
                    "ArmStopSellBatchAsync handles sell-stops only; route StopMarketBuy to ArmStopBuyBatchAsync.");
                continue;
            }
            var (order, error) = r.Kind == StopArmKind.TrailingStopSell
                ? await BuildTrailingStopOrderAsync(r.UserId, r.StockId, r.Quantity, r.TrailOffset,
                    r.TrailIsPercent, buyBudget: null, buyOrder: false, r.Currency, ct).ConfigureAwait(false)
                : await BuildStopOrderAsync(r.UserId, r.StockId, r.Quantity, r.StopPrice,
                    limitPrice: null, buyBudget: null, slippagePct: r.StopSlippagePct, r.Currency,
                    buyOrder: false, limitStop: false, ct).ConfigureAwait(false);
            if (error is not null || order is null)
            {
                results[i] = error ?? OrderResultFactory.InvalidParams("Failed to build stop order.");
                continue;
            }
            built.Add((i, order));
        }
        if (built.Count == 0) return results;

        var orders = new List<Order>(built.Count);
        for (int i = 0; i < built.Count; i++) orders.Add(built[i].order);

        var engineResults = await _engine.ArmStopBatchAsync(orders, ct).ConfigureAwait(false);

        for (int i = 0; i < built.Count; i++)
        {
            var (idx, order) = built[i];
            var result = engineResults[i];
            results[idx] = result;
            if (result.PlacedSuccessfully) _stopWatcher.Arm(order);
        }
        return results;
    }

    // §A1b: batch-arm the bot fleet's BUY-stops. Builds each as a stop-LIMIT buy with
    // limitPrice = StopPrice × 1.005 — byte-identical to the per-order PlaceStopLimitBuyOrderAsync
    // route (AiTradeService.cs:1355) — hands the list to the engine's ArmStopBuyBatchAsync (FUND
    // pre-reserve under the fund gate + one bulk-insert tx), then arms the trigger watcher for every
    // success. Arms the SAME Order instances handed to the engine: OrderId is stamped onto them by
    // InsertAllAsync and they are _registry.Register-ed in-engine, so _stopWatcher.Arm sees a real
    // OrderId (it silently drops OrderId<=0 instances).
    public async Task<IReadOnlyList<OrderResult>> ArmStopBuyBatchAsync(
        IReadOnlyList<StopArmRequest> requests, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (requests is null || requests.Count == 0) return Array.Empty<OrderResult>();

        var results = new OrderResult[requests.Count];
        var built = new List<(int index, Order order)>(requests.Count);
        for (int i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            if (r.Kind != StopArmKind.StopMarketBuy)
            {
                results[i] = OrderResultFactory.InvalidParams(
                    "ArmStopBuyBatchAsync handles StopMarketBuy only; route sell-stops to ArmStopSellBatchAsync.");
                continue;
            }
            // Carry the RAW decision StopPrice into the ×1.005 markup so the reservation base matches
            // the per-order path exactly (BuildStopOrderAsync re-rounds idempotently).
            var limitPrice = CurrencyHelper.RoundMoney(r.StopPrice * 1.005m, r.Currency);
            var (order, error) = await BuildStopOrderAsync(r.UserId, r.StockId, r.Quantity, r.StopPrice,
                limitPrice, buyBudget: null, slippagePct: null, r.Currency,
                buyOrder: true, limitStop: true, ct).ConfigureAwait(false);
            if (error is not null || order is null)
            {
                results[i] = error ?? OrderResultFactory.InvalidParams("Failed to build buy-stop order.");
                continue;
            }
            built.Add((i, order));
        }
        if (built.Count == 0) return results;

        var orders = new List<Order>(built.Count);
        for (int i = 0; i < built.Count; i++) orders.Add(built[i].order);

        var engineResults = await _engine.ArmStopBuyBatchAsync(orders, ct).ConfigureAwait(false);

        for (int i = 0; i < built.Count; i++)
        {
            var (idx, order) = built[i];
            var result = engineResults[i];
            results[idx] = result;
            if (result.PlacedSuccessfully) _stopWatcher.Arm(order);
        }
        return results;
    }
}
