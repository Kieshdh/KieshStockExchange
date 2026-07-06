using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §3.7 Dedicated decision path for <see cref="AiStrategy.Arbitrage"/> bots — fully OUT of the
/// normal sentiment/anchor/veto/injection flow. Each bot keeps a cross-listed stock's USD and EUR
/// books coupled at the live FX rate by trading the gap: market-buy on the cheap book, market-sell
/// on the expensive one (same currency-agnostic Position, so the two legs net flat), then rebalance
/// its USD/EUR cash mix through the platform FX desk — which is what funds the house account.
///
/// Both legs are ordinary market orders through the engine, so ConservationProbe / ReservationAuditor
/// invariants apply unchanged. Inventory is bounded per-stock (MaxInventoryPerStock) and the bot only
/// opens a round-trip when both legs clear the FX spread "now", so directional risk is minimal.
/// </summary>
internal sealed class ArbitrageDecisionService
{
    #region Services and Constructor
    private readonly IOrderEntryService _entry;
    private readonly IOrderBookEngine _books;
    private readonly IAccountsCache _accounts;
    private readonly IFxRateService _fxRates;
    private readonly IUserPortfolioService _portfolio;
    private readonly IStockService _stocks;
    private readonly BotEconomyTelemetry _economy;
    private readonly ILogger<ArbitrageDecisionService> _logger;

    // The two books the simulation runs. A stock is "cross-listed" when it has a listing in both.
    private static readonly CurrencyType[] Books = { CurrencyType.USD, CurrencyType.EUR };

    // Per-bot conversion-cadence clock (rebalance the currency mix at most this often).
    private readonly Dictionary<int, DateTime> _nextConvertAt = new();

    // Convert only when the richer currency exceeds this share of the bot's total cash (USD-valued),
    // then move back toward a 50/50 split. Keeps the desk busy enough to fund the house without
    // churning on every tick.
    private readonly decimal _conversionSkewBand;

    // STRETCH (Bots:Arbitrage:BatchLegs): when true, the cohort's round-trip ENTRIES run as two
    // batched engine passes (leg1 buys, then leg2 sells sized per leg1 fill) instead of 2×N txs.
    // Flatten + FX rebalance stay per-bot. Default false ⇒ the per-bot sequential path is unchanged.
    private readonly bool _batchLegs;

    // §arb-scan Phase 2a (Bots:Arbitrage:SharedScan): compute each cross-listed stock's gap ONCE per
    // tick and share it across the cohort (today every arb bot re-scans the same books = ~5× redundant).
    // INCREMENTALLY self-invalidated: after a bot's legs mutate a stock's books, that stock is dropped so
    // the next bot recomputes it fresh — reproduces per-bot-fresh reads byte-for-byte (see CollectOppsAsync).
    private readonly Dictionary<int, Opp?> _gapMap = new();  // stockId -> best pre-threshold opp (null = none)
    private long _gapMapTick = -1;                           // generation guard vs ctx.TickId
    private readonly bool _sharedScan;

    internal ArbitrageDecisionService(IOrderEntryService entry, IOrderBookEngine books,
        IAccountsCache accounts, IFxRateService fxRates, IUserPortfolioService portfolio,
        IStockService stocks, BotEconomyTelemetry economy, ILogger<ArbitrageDecisionService> logger,
        decimal conversionSkewBand = 0.15m, bool batchLegs = false, bool sharedScan = false)
    {
        _entry     = entry     ?? throw new ArgumentNullException(nameof(entry));
        _books     = books     ?? throw new ArgumentNullException(nameof(books));
        _accounts  = accounts  ?? throw new ArgumentNullException(nameof(accounts));
        _fxRates   = fxRates   ?? throw new ArgumentNullException(nameof(fxRates));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _economy   = economy   ?? throw new ArgumentNullException(nameof(economy));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        _conversionSkewBand = Math.Max(0m, conversionSkewBand);
        _batchLegs = batchLegs;
        _sharedScan = sharedScan;
    }
    #endregion

    #region Run
    internal async Task RunAsync(AiBotContext ctx, DateTime now, CancellationToken ct)
    {
        // Snapshot the throttle once per pass: when the cohort + house wealth fraction is over the
        // ceiling, suspend OPENING new round-trips (the lever) but keep flattening held inventory.
        var throttled = _economy.ArbThrottleEngaged;

        // §arb-scan: new tick ⇒ reset the shared gap map (generation guard, not a per-call Clear —
        // mirrors WatchlistByBot). OFF ⇒ never touched.
        if (_sharedScan && ctx.TickId != _gapMapTick) { _gapMap.Clear(); _gapMapTick = ctx.TickId; }

        // STRETCH: when batching legs, collect each bot's sized round-trip during the per-bot loop
        // (in ascending-aiUserId order) instead of executing it inline; the two batched passes run
        // after the loop. Null ⇒ per-bot inline execution (default, byte-identical to before).
        List<(AIUser User, RoundTripPlan Plan)>? pendingEntries =
            (_batchLegs && !throttled) ? new() : null;

        // Iterate in ascending AiUserId for the same seed-determinism contract as the main loop.
        foreach (var user in ctx.AiUsersByAiUserId.Values.OrderBy(u => u.AiUserId))
        {
            if (!user.IsEnabled || user.Strategy != AiStrategy.Arbitrage) continue;
            if (now - user.LastDecisionTime < user.DecisionInterval) continue;
            user.RecordDecision(now);

            try
            {
                var candidates = CrossListedWatchlist(user);

                // 1) Exit-retry: flatten any residual inventory (from a partial second leg or an
                //    earlier hold) on whichever book currently bids higher. Always reduces position.
                foreach (var stockId in candidates)
                {
                    var flat = await TryFlattenAsync(ctx, user, stockId, ct).ConfigureAwait(false);
                    // §arb-scan: a flatten consumed a book ⇒ drop the stock so the next bot rescans it.
                    if (_sharedScan && flat is { } fs) _gapMap.Remove(fs);
                }

                // 2) Entry: open a fresh round-trip on the best gap (unless throttled). When batching,
                //    only SIZE it here and defer execution to the post-loop batched passes.
                if (!throttled)
                {
                    if (pendingEntries is not null)
                    {
                        var plan = await PrepareRoundTripAsync(ctx, user, candidates, ct).ConfigureAwait(false);
                        if (plan is { } p) pendingEntries.Add((user, p));
                    }
                    else
                    {
                        var acted = await TryRoundTripAsync(ctx, user, candidates, ct).ConfigureAwait(false);
                        // §arb-scan: this bot's legs moved the acted stock ⇒ drop it for the next bot.
                        if (_sharedScan && acted is { } es) _gapMap.Remove(es);
                    }
                }

                // 3) Re-arm: rebalance the USD/EUR cash mix on the bot's own cadence. This is the
                //    conversion that pays the spread into the house account.
                await MaybeRebalanceAsync(user, now, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Arbitrage decision failed for AIUser {Id}.", user.AiUserId);
                user.RecordError();
            }
        }

        // STRETCH: the two batched entry passes — leg1 buys for the whole cohort, then leg2 sells
        // sized from each leg1 fill. Preserves the per-round-trip fill dependency while collapsing
        // 2×N transactions into 2 batched passes.
        if (pendingEntries is { Count: > 0 })
        {
            await ExecuteBatchedEntriesAsync(pendingEntries, ct).ConfigureAwait(false);
            // §arb-scan: the batched legs moved these stocks (post-loop ⇒ next tick clears anyway; kept
            // for consistency with the inline path).
            if (_sharedScan)
                foreach (var (_, plan) in pendingEntries) _gapMap.Remove(plan.Opp.StockId);
        }
    }
    #endregion

    #region Opportunity evaluation
    private List<int> CrossListedWatchlist(AIUser user)
    {
        var result = new List<int>();
        foreach (var sid in user.Watchlist)
            if (_stocks.IsListedIn(sid, CurrencyType.USD) && _stocks.IsListedIn(sid, CurrencyType.EUR))
                result.Add(sid);
        return result;
    }

    private readonly record struct BookTop(decimal Bid, int BidQty, decimal Ask, int AskQty);

    private async Task<BookTop?> ReadTopAsync(int stockId, CurrencyType ccy, CancellationToken ct)
    {
        var book = await _books.GetAsync(stockId, ccy, ct).ConfigureAwait(false);
        var bid = book?.PeekBestBuy();
        var ask = book?.PeekBestSell();
        if (bid is null || ask is null || bid.Price <= 0m || ask.Price <= 0m) return null;
        return new BookTop(bid.Price, bid.RemainingQuantity, ask.Price, ask.RemainingQuantity);
    }

    // A directional opportunity: buy `qty` on BuyCcy@BuyAsk, sell on SellCcy@SellBid. Rate is the
    // per-share profit (net of the FX spread paid to re-arm) over the buy-side notional.
    private readonly record struct Opp(int StockId, CurrencyType BuyCcy, CurrencyType SellCcy,
        decimal BuyAsk, int BuyAskQty, decimal SellBid, int SellBidQty, decimal Rate);

    private Opp? EvaluateDirection(int stockId, BookTop buy, BookTop sell,
        CurrencyType buyCcy, CurrencyType sellCcy)
    {
        // Proceeds are realized back into the buy currency at the FX BID (the rate the desk pays
        // when the bot converts to re-arm), so the spread is already baked into the rate.
        var fxBid = _fxRates.GetBidAsk(sellCcy, buyCcy).Bid; // sellCcy -> buyCcy
        if (fxBid <= 0m || buy.Ask <= 0m) return null;
        var proceedsInBuyCcy = sell.Bid * fxBid;
        var profit = proceedsInBuyCcy - buy.Ask;
        if (profit <= 0m) return null;
        var rate = profit / buy.Ask;
        return new Opp(stockId, buyCcy, sellCcy, buy.Ask, buy.AskQty, sell.Bid, sell.BidQty, rate);
    }

    // Bot-INDEPENDENT best pre-threshold opp for one stock (depends only on the two book tops + the FX
    // bid). Null when neither direction profits. Does NOT apply MinArbitrageRatePrc — that is per-bot.
    private async Task<Opp?> ComputeGap(int stockId, CancellationToken ct)
    {
        var usd = await ReadTopAsync(stockId, CurrencyType.USD, ct).ConfigureAwait(false);
        var eur = await ReadTopAsync(stockId, CurrencyType.EUR, ct).ConfigureAwait(false);
        if (usd is null || eur is null) return null;

        // Buy USD book / sell EUR book, and the reverse. Keep the better direction per stock.
        var a = EvaluateDirection(stockId, usd.Value, eur.Value, CurrencyType.USD, CurrencyType.EUR);
        var b = EvaluateDirection(stockId, eur.Value, usd.Value, CurrencyType.EUR, CurrencyType.USD);
        return (a, b) switch
        {
            ({ } x, { } y) => x.Rate >= y.Rate ? x : y,
            ({ } x, null)  => x,
            (null, { } y)  => y,
            _              => (Opp?)null,
        };
    }

    // Per-bot profitable opps in candidate order. SharedScan ON ⇒ read each stock's gap through the
    // per-tick shared map (first sight computes + caches; a self-invalidated stock recomputes); OFF ⇒
    // compute fresh per bot (byte-identical to the original). The per-bot threshold is applied here.
    private async Task<List<Opp>> CollectOppsAsync(AIUser user, List<int> candidates, CancellationToken ct)
    {
        var opps = new List<Opp>();
        foreach (var sid in candidates)
        {
            Opp? gap;
            if (_sharedScan)
            {
                if (!_gapMap.TryGetValue(sid, out gap))
                {
                    gap = await ComputeGap(sid, ct).ConfigureAwait(false);
                    _gapMap[sid] = gap;
                }
            }
            else
            {
                gap = await ComputeGap(sid, ct).ConfigureAwait(false);
            }
            if (gap is { } o && o.Rate >= user.MinArbitrageRatePrc) opps.Add(o);
        }
        return opps;
    }
    #endregion

    #region Execution
    // A sized, ready-to-place round-trip: the chosen opportunity, the leg1 quantity, and the
    // buy-side available cash (the leg1 market-buy budget).
    private readonly record struct RoundTripPlan(Opp Opp, int Qty, decimal Avail);

    // Opportunity selection + sizing half of a round-trip — no engine calls. Shared by the per-bot
    // inline path (TryRoundTripAsync) and the batched collection path (RunAsync).
    private async Task<RoundTripPlan?> PrepareRoundTripAsync(AiBotContext ctx, AIUser user,
        List<int> candidates, CancellationToken ct)
    {
        var opps = await CollectOppsAsync(user, candidates, ct).ConfigureAwait(false);
        if (opps.Count == 0) return null;

        var opp = PickWeighted(ctx, user, opps);

        // Size: bounded by inventory room, affordable cash on the buy book, and the touch depth on
        // BOTH legs (so each leg stays at/near the best price — keeps the round-trip near-riskless).
        var pos = _accounts.GetPosition(user.UserId, opp.StockId);
        int held = pos?.Quantity > 0 ? pos.Quantity : 0;
        int room = Math.Max(0, user.MaxInventoryPerStock - held);
        var avail = _accounts.GetFund(user.UserId, opp.BuyCcy)?.AvailableBalance ?? 0m;
        int byCash = opp.BuyAsk > 0m ? (int)Math.Floor(avail / opp.BuyAsk) : 0;
        int qty = Min4(room, byCash, opp.BuyAskQty, opp.SellBidQty);
        if (qty <= 0) return null;
        return new RoundTripPlan(opp, qty, avail);
    }

    // Returns the stockId acted on (for §arb-scan self-invalidation) or null when no plan fired.
    private async Task<int?> TryRoundTripAsync(AiBotContext ctx, AIUser user, List<int> candidates, CancellationToken ct)
    {
        var plan = await PrepareRoundTripAsync(ctx, user, candidates, ct).ConfigureAwait(false);
        if (plan is not { } p) return null;
        var opp = p.Opp;

        // Leg 1 — market-buy on the cheap book, budget-capped at the bot's available cash.
        var buy = await _entry.PlaceTrueMarketBuyOrderAsync(
            user.UserId, opp.StockId, p.Qty, p.Avail, opp.BuyCcy, ct).ConfigureAwait(false);
        int filled = buy.TotalFilledQuantity;
        if (filled <= 0) return opp.StockId;
        RecordFills(user, buy);

        // Leg 2 — market-sell the filled qty on the expensive book, but only the part the engine
        // confirms is available (never oversell into a short). Any unsold remainder is left as
        // bounded inventory and unwound by the next tick's exit-retry.
        var afterBuy = _accounts.GetPosition(user.UserId, opp.StockId);
        int sellable = Math.Min(filled, afterBuy?.AvailableQuantity ?? 0);
        if (sellable <= 0) return opp.StockId;

        var sell = await _entry.PlaceTrueMarketSellOrderAsync(
            user.UserId, opp.StockId, sellable, opp.SellCcy, ct).ConfigureAwait(false);
        RecordFills(user, sell);
        return opp.StockId;
    }

    // STRETCH (Bots:Arbitrage:BatchLegs): execute the collected round-trips as two batched passes.
    // Pass 1 places every leg1 buy in one engine batch; pass 2 places each leg2 sell sized from its
    // own leg1 fill (the dependency the per-order path enforces sequentially). Entries arrive in
    // ascending-aiUserId order and that order is preserved through both passes (determinism).
    private async Task ExecuteBatchedEntriesAsync(
        List<(AIUser User, RoundTripPlan Plan)> entries, CancellationToken ct)
    {
        var buyReqs = new List<TrueMarketBuyBatchRequest>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var (user, plan) = entries[i];
            buyReqs.Add(new TrueMarketBuyBatchRequest(
                user.UserId, plan.Opp.StockId, plan.Qty, plan.Avail, plan.Opp.BuyCcy));
        }

        var buyResults = await _entry.PlaceTrueMarketBuyBatchAsync(buyReqs, ct).ConfigureAwait(false);

        // Size leg2 from each confirmed leg1 fill (never oversell into a short).
        var sellEntries = new List<(AIUser User, RoundTripPlan Plan)>(entries.Count);
        var sellReqs = new List<TrueMarketSellBatchRequest>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var (user, plan) = entries[i];
            var buy = buyResults[i];
            int filled = buy.TotalFilledQuantity;
            if (filled <= 0) continue;
            RecordFills(user, buy);

            var afterBuy = _accounts.GetPosition(user.UserId, plan.Opp.StockId);
            int sellable = Math.Min(filled, afterBuy?.AvailableQuantity ?? 0);
            if (sellable <= 0) continue;

            sellEntries.Add((user, plan));
            sellReqs.Add(new TrueMarketSellBatchRequest(
                user.UserId, plan.Opp.StockId, sellable, plan.Opp.SellCcy));
        }
        if (sellReqs.Count == 0) return;

        var sellResults = await _entry.PlaceTrueMarketSellBatchAsync(sellReqs, ct).ConfigureAwait(false);
        for (int i = 0; i < sellEntries.Count; i++)
            RecordFills(sellEntries[i].User, sellResults[i]);
    }

    // Flatten any leftover position on a cross-listed stock by selling on the higher-bidding book
    // (USD-valued). Bounded, always reduces inventory; clears partial-fill residue and stale holds.
    // Returns the stockId flattened (for §arb-scan self-invalidation) or null when nothing was placed.
    private async Task<int?> TryFlattenAsync(AiBotContext ctx, AIUser user, int stockId, CancellationToken ct)
    {
        var pos = _accounts.GetPosition(user.UserId, stockId);
        int avail = pos?.AvailableQuantity ?? 0;
        if (avail <= 0) return null;

        var usd = await ReadTopAsync(stockId, CurrencyType.USD, ct).ConfigureAwait(false);
        var eur = await ReadTopAsync(stockId, CurrencyType.EUR, ct).ConfigureAwait(false);

        decimal usdBidUsd = usd is { } u ? u.Bid : 0m;
        decimal eurBidUsd = eur is { } e ? e.Bid * _fxRates.GetMidRate(CurrencyType.EUR, CurrencyType.USD) : 0m;
        if (usdBidUsd <= 0m && eurBidUsd <= 0m) return null;

        var (ccy, top) = usdBidUsd >= eurBidUsd
            ? (CurrencyType.USD, usd)
            : (CurrencyType.EUR, eur);
        if (top is null) return null;

        int qty = Math.Min(avail, top.Value.BidQty);
        if (qty <= 0) return null;

        var sell = await _entry.PlaceTrueMarketSellOrderAsync(user.UserId, stockId, qty, ccy, ct).ConfigureAwait(false);
        RecordFills(user, sell);
        return stockId;
    }
    #endregion

    #region Currency rebalance (funds the house)
    private async Task MaybeRebalanceAsync(AIUser user, DateTime now, CancellationToken ct)
    {
        var cadence = user.ConversionCadenceSeconds;
        if (cadence <= 0) return;
        if (_nextConvertAt.TryGetValue(user.AiUserId, out var next) && now < next) return;
        _nextConvertAt[user.AiUserId] = now + TimeSpan.FromSeconds(cadence);

        var usd = _accounts.GetFund(user.UserId, CurrencyType.USD)?.AvailableBalance ?? 0m;
        var eur = _accounts.GetFund(user.UserId, CurrencyType.EUR)?.AvailableBalance ?? 0m;
        var eurInUsd = eur * _fxRates.GetMidRate(CurrencyType.EUR, CurrencyType.USD);
        var totalUsd = usd + eurInUsd;
        if (totalUsd <= 0m) return;

        var usdShare = usd / totalUsd;
        // Only act when one side is clearly richer than the other.
        if (Math.Abs(usdShare - 0.5m) <= _conversionSkewBand / 2m) return;

        // Move half the imbalance back toward 50/50, converting the richer currency into the poorer.
        var halfGapUsd = Math.Abs(usd - eurInUsd) / 2m;
        if (halfGapUsd <= 0m) return;

        CurrencyType from, to;
        decimal amount;
        if (usd > eurInUsd)
        {
            from = CurrencyType.USD; to = CurrencyType.EUR;
            amount = CurrencyHelper.RoundMoney(halfGapUsd, CurrencyType.USD);
        }
        else
        {
            from = CurrencyType.EUR; to = CurrencyType.USD;
            // halfGapUsd is USD-valued; convert the target move into EUR units to spend.
            amount = CurrencyHelper.RoundMoney(
                halfGapUsd * _fxRates.GetMidRate(CurrencyType.USD, CurrencyType.EUR), CurrencyType.EUR);
        }
        if (amount <= 0m) return;

        using var scope = _portfolio.BeginSystemScope();
        await _portfolio.ConvertAsync(amount, from, to, note: "arb-rebalance",
            asUserId: user.UserId, ct).ConfigureAwait(false);
    }
    #endregion

    #region Helpers
    // Weighted pick ∝ rate² so the cohort leans into the biggest gaps, with per-bot RNG jitter so
    // they don't all pile onto the same stock.
    private static Opp PickWeighted(AiBotContext ctx, AIUser user, List<Opp> opps)
    {
        if (opps.Count == 1) return opps[0];
        double total = 0;
        foreach (var o in opps) total += (double)o.Rate * (double)o.Rate;
        if (total <= 0) return opps[0];

        var roll = ctx.GetRandom(user.AiUserId).NextDouble() * total;
        double acc = 0;
        foreach (var o in opps)
        {
            acc += (double)o.Rate * (double)o.Rate;
            if (roll <= acc) return o;
        }
        return opps[^1];
    }

    private void RecordFills(AIUser user, OrderResult result)
    {
        if (result.FillTransactions.Count == 0) return;
        for (int i = 0; i < result.FillTransactions.Count; i++)
            user.RecordTrade(result.FillTransactions[i]);
    }

    private static int Min4(int a, int b, int c, int d) => Math.Min(Math.Min(a, b), Math.Min(c, d));
    #endregion
}
