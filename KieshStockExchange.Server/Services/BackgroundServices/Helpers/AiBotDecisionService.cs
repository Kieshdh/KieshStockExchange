using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

// §P6: an advanced-order decision, submitted via the entry/arm route (not the batch matcher).
//   StopMarketSell/TrailingStopSell (P6a): protect a held long.
//   ShortOpen (P6b): flat-only market short.  LongBracket (P6b)/ShortBracket (P6c): bracketed entry.
internal enum BotAdvancedKind { StopMarketSell, TrailingStopSell, ShortOpen, LongBracket, ShortBracket }

internal sealed record BotAdvancedDecision(
    BotAdvancedKind Kind, int StockId, int Quantity, CurrencyType Currency,
    decimal StopPrice = 0m, decimal TrailOffset = 0m, bool TrailIsPercent = false,
    decimal? BuyBudget = null, decimal? StopSlippagePct = null,
    IReadOnlyList<(decimal Price, int Quantity)>? TakeProfits = null);

/// <summary>
/// Stateless order computation — given a context and a user, produces an Order or null.
/// </summary>
internal sealed class AiBotDecisionService
{
    #region Services and Constructor
    // Max nudge applied to buyProb by the clamped sentiment value.
    private const decimal SentimentMaxBias = 0.20m;
    // Probability of forcing a market order, per unit of |sentiment| > 1.
    private const decimal OverflowGain     = 0.25m;
    // Cash kept un-spent on every buy so tiny rounding/race gaps don't trip Phase 1.6.
    private const decimal BuySafetyBuffer  = 5m;
    // Fat tails: tailShape∈[0,1] maps to a power exponent 1..(1+this) applied to the
    // uniform size draw. 1 = uniform (today); higher = more mass near Min, longer tail.
    private const double  TailExponentScale = 4.0;

    private readonly IMarketDataService _market;
    private readonly IAccountsCache _accounts;
    private readonly IOrderBookEngine _books;
    private readonly IStockService _stocks;
    private readonly BotSentimentService _sentiment;
    private readonly FundamentalService _funds;     // §P6 slowly-drifting per-stock fundamental
    private readonly StockProfileService _profiles; // §P6 per-stock personality (volatility class)
    private readonly ILogger<AiBotDecisionService> _logger;

    // §1 order-size fat tails (shared config; per-bot variation comes from the draw).
    private readonly bool    _fatTails;
    private readonly decimal _tradeSizeTailShape;
    private readonly decimal _blockTradeProb;
    private readonly decimal _blockTradeMultiple;

    // §2 market-maker quoting.
    private readonly bool    _mmQuoting;
    private readonly decimal _quoteHalfSpreadPrc;

    // Liquidity tuning: global multipliers over each bot's per-bot Excel values. >1 rests limit
    // orders further from market (wider band) and allows more rungs, deepening the book so market
    // sweeps hit walls instead of empty space.
    private readonly decimal _limitOffsetMult;
    private readonly decimal _maxOpenOrdersMult;

    // §P6 "tightness dial": one global multiplier over EVERY order-placement distance — limit tiers
    // (Close/Mid/Far), protective-stop + bracket-SL trigger distance, and bracket take-profit distance.
    // <1 holds the whole book closer to the current price (calmer market); 1 = unchanged. Does NOT touch
    // trade size or the slippage caps. Distinct from _limitOffsetMult, which scales only the limit ladder.
    private readonly decimal _distanceMult;

    // Value anchor: a restoring force toward each stock's fundamental (seed) price. Without it the
    // price is a driftless momentum walk with no pull back to value, so it wanders unbounded. Strength
    // is the max buy/sell-probability tilt; Scale is the deviation fraction at which the tilt saturates.
    private readonly decimal _valueAnchorStrength;
    private readonly decimal _valueAnchorScale;
    private readonly bool    _valueTargetSelection; // concentrate the anchor via stock selection (destabilizing at high gain)
    private readonly decimal _overheatCap;          // refuse to buy above / sell below fundamental by more than this (0 = off)
    private readonly decimal _marketSlippagePrc;    // low cap on every market order's slippage so none sweeps far

    // §P6 balancing: tiered-limit selection probabilities (Far = remainder), low slippage cap applied to
    // every bot protective/bracket stop fire (percent), and the max fraction of resting opposite-side
    // depth a single bot market order may sweep (structural anti-sweep).
    private readonly decimal _tierCloseProb;
    private readonly decimal _tierMidProb;
    private readonly decimal _stopSlippagePct;
    private readonly decimal _maxSweepFractionOfDepth;

    // §P6 advanced-order generation for the bot soak (all off by default).
    private readonly bool    _advancedEnabled;
    // §3.6 P6: the per-kind advanced-order probabilities are now PER-BOT (AIUser.*Prob, seeded by
    // strategy in Tools/Person.py), read directly off `user` in ComputeAdvancedDecisionAsync.
    private readonly decimal _stopOffsetMin;     // SL distance from entry/market (fraction)
    private readonly decimal _stopOffsetMax;
    private readonly decimal _tpOffsetMin;       // TP distance from entry (fraction)
    private readonly decimal _tpOffsetMax;
    private readonly decimal _bracketSlippagePct;// short-bracket SL slippage cap (percent)
    private readonly int     _advancedMaxQty;    // cap qty on advanced/bracket orders (keeps sizes modest)

    internal AiBotDecisionService(IMarketDataService market, IAccountsCache accounts,
        IOrderBookEngine books, IStockService stocks, BotSentimentService sentiment,
        FundamentalService funds, StockProfileService profiles,
        ILogger<AiBotDecisionService> logger,
        bool fatTails = true, decimal tradeSizeTailShape = 0.5m,
        decimal blockTradeProb = 0.01m, decimal blockTradeMultiple = 4m,
        bool mmQuoting = true, decimal quoteHalfSpreadPrc = 0.003m,
        decimal limitOffsetMult = 1m, decimal maxOpenOrdersMult = 1m,
        decimal distanceMult = 1m,
        decimal valueAnchorStrength = 0m, decimal valueAnchorScale = 0.15m,
        bool valueTargetSelection = false, decimal overheatCap = 0m,
        decimal marketSlippagePrc = 0.003m,
        decimal tierCloseProb = 0.6m, decimal tierMidProb = 0.3m,
        decimal stopSlippagePct = 0.3m, decimal maxSweepFractionOfDepth = 0.25m,
        bool advancedEnabled = false,
        decimal stopOffsetMin = 0.02m, decimal stopOffsetMax = 0.05m,
        decimal tpOffsetMin = 0.03m, decimal tpOffsetMax = 0.08m,
        decimal bracketSlippagePct = 5m, int advancedMaxQty = 50)
    {
        _market    = market    ?? throw new ArgumentNullException(nameof(market));
        _accounts  = accounts  ?? throw new ArgumentNullException(nameof(accounts));
        _books     = books     ?? throw new ArgumentNullException(nameof(books));
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _sentiment = sentiment ?? throw new ArgumentNullException(nameof(sentiment));
        _funds     = funds     ?? throw new ArgumentNullException(nameof(funds));
        _profiles  = profiles  ?? throw new ArgumentNullException(nameof(profiles));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        _fatTails           = fatTails;
        _tradeSizeTailShape = tradeSizeTailShape;
        _blockTradeProb     = blockTradeProb;
        _blockTradeMultiple = blockTradeMultiple;
        _mmQuoting          = mmQuoting;
        _quoteHalfSpreadPrc = quoteHalfSpreadPrc;
        _limitOffsetMult    = limitOffsetMult <= 0m ? 1m : limitOffsetMult;
        _maxOpenOrdersMult  = maxOpenOrdersMult <= 0m ? 1m : maxOpenOrdersMult;
        _distanceMult       = distanceMult <= 0m ? 1m : distanceMult;
        _valueAnchorStrength = Math.Max(0m, valueAnchorStrength);
        _valueAnchorScale    = valueAnchorScale <= 0m ? 0.15m : valueAnchorScale;
        _valueTargetSelection = valueTargetSelection;
        _overheatCap        = Math.Max(0m, overheatCap);
        _marketSlippagePrc  = marketSlippagePrc <= 0m ? 0.003m : marketSlippagePrc;
        _tierCloseProb      = Clamp01(tierCloseProb);
        _tierMidProb        = Clamp01(tierMidProb);
        _stopSlippagePct    = Math.Max(0m, stopSlippagePct);
        _maxSweepFractionOfDepth = Math.Max(0m, maxSweepFractionOfDepth);
        _advancedEnabled    = advancedEnabled;
        _stopOffsetMin      = stopOffsetMin;
        _stopOffsetMax      = stopOffsetMax;
        _tpOffsetMin        = tpOffsetMin;
        _tpOffsetMax        = tpOffsetMax;
        _bracketSlippagePct = bracketSlippagePct;
        _advancedMaxQty     = advancedMaxQty;
    }
    #endregion

    #region Public Interface
    internal bool CanPlaceMoreOrder(AiBotContext ctx, AIUser user)
    {
        // A bot with persistent errors goes quiet for the day to avoid log spam
        if (user.ErrorsToday >= 10) return false;

        var openCap = (int)Math.Ceiling(user.MaxOpenOrders * _maxOpenOrdersMult);
        if (ctx.OpenOrders.TryGetValue(user.UserId, out var orders) && orders.Count >= openCap)
            return false;

        // No daily-trades cap — it would only force churning bots dormant mid-session;
        // MaxOpenOrders + ErrorsToday throttle instead. TradesToday still counts for the UI.
        return true;
    }

    internal async Task<Order?> ComputeOrderAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, CancellationToken ct = default)
    {
        var type    = ChooseOrderType(ctx, user, currency);
        // §perf C4: snapshot every "already committed" total in ONE walk of this user's open orders, then
        // reuse it below — the sell path used to re-walk OpenOrders once per sell candidate inside ChooseStockId.
        var committed = ComputeCommitted(ctx, user.UserId);
        var stockId = ChooseStockId(ctx, user, type, currency, committed);
        if (stockId <= 0) return null;

        // When the chosen stock's raw sentiment crosses ±1, force the order into a slippage-capped market
        // order in the bot's style-appropriate direction with probability proportional to the overflow.
        // No-op when the override would point at zero shares (sell with no position).
        type = ApplyExtremeReaction(ctx, user, stockId, currency, type);

        // Value-band veto: don't chase price past the band — refuse to buy a stock already far above
        // fundamental or sell one far below it. Cuts the fuel that lets a minority of stocks escape.
        if (IsBuyOrder(type) && await IsOverBandAsync(ctx, stockId, currency, isBuy: true, ct).ConfigureAwait(false)) return null;
        if (IsSellOrder(type) && await IsOverBandAsync(ctx, stockId, currency, isBuy: false, ct).ConfigureAwait(false)) return null;

        var price    = await ComputeOrderPriceAsync(ctx, user, type, stockId, currency, ct).ConfigureAwait(false);
        var quantity = await ComputeOrderQuantityAsync(ctx, user, type, stockId, currency, committed, ct).ConfigureAwait(false);
        if (quantity <= 0) return null;

        decimal? buyBudget = null;
        if (type == OrderType.TrueMarketBuy)
        {
            var mktPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
            buyBudget = mktPrice > 0m ? CurrencyHelper.Notional(mktPrice, quantity, currency) : null;
            if (buyBudget is null or <= 0m) return null;
        }

        // §3.6 decomposition: bots place plain (non-stop) orders — set the dimensions directly.
        return new Order
        {
            UserId = user.UserId, StockId = stockId, CurrencyType = currency,
            Quantity = quantity, Price = price,
            SlippagePercent = IsSlippageOrder(type) ? EffectiveSlippage(user) * 100m : null,
            BuyBudget = buyBudget,
            Side = IsBuyOrder(type) ? OrderSide.Buy : OrderSide.Sell,
            Entry = (type is OrderType.LimitBuy or OrderType.LimitSell) ? EntryType.Limit : EntryType.Market,
            Stop = StopKind.None,
        };
    }

    /// <summary>
    /// §P6a: decide whether the bot attaches a PROTECTIVE stop to an existing long this decision — a
    /// sell-stop-market below market or a P5 trailing-sell-stop. Returns null when disabled, when the gate
    /// doesn't fire, or when the bot has no free (un-protected) long shares. <b>When disabled it returns at
    /// the very top consuming NO seeded RNG</b>, so the plain-order stream stays byte-identical vs pre-P6.
    /// Submitted via the entry/arm route (not the batch matcher); fires off-loop via the stop watcher.
    /// </summary>
    internal async Task<BotAdvancedDecision?> ComputeAdvancedDecisionAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, CancellationToken ct = default)
    {
        if (!_advancedEnabled) return null;
        // §3.6 P6: the per-kind probabilities are PER-BOT (seeded by strategy in Tools/Person.py), not global
        // config. The Bots:Advanced:Enabled master switch above still gates the whole feature.
        decimal advProb = user.StopProb + user.TrailingProb + user.ShortProb + user.LongBracketProb + user.ShortBracketProb;
        if (advProb <= 0m) return null;
        decimal r = ctx.Decimal01(user.AiUserId);   // single seeded roll: gate + kind selection
        if (r >= advProb) return null;

        // Cumulative kind pick from the same roll (no extra draw). A builder that can't find an eligible
        // stock returns null → the caller falls through to a normal plain order this tick.
        // StopProb and TrailingProb both now gate a slippage-capped STATIC protective stop — bots never
        // arm an uncapped trailing fire (the §P6 "bound ALL stop fires" guarantee, bot-side).
        decimal c = user.StopProb;
        if (r < c)                          return await BuildProtectiveStopAsync(ctx, user, currency, ct).ConfigureAwait(false);
        c += user.TrailingProb; if (r < c)  return await BuildProtectiveStopAsync(ctx, user, currency, ct).ConfigureAwait(false);
        c += user.ShortProb;    if (r < c)  return await BuildShortOpenAsync(ctx, user, currency, ct).ConfigureAwait(false);
        c += user.LongBracketProb; if (r < c) return await BuildBracketAsync(ctx, user, currency, isShort: false, ct).ConfigureAwait(false);
        return await BuildBracketAsync(ctx, user, currency, isShort: true, ct).ConfigureAwait(false);
    }

    // P6a: protect the first watchlist long with FREE shares — a slippage-capped, fundamental-relative
    // static sell-stop (bots no longer arm uncapped trailing stops; see ComputeAdvancedDecisionAsync).
    private async Task<BotAdvancedDecision?> BuildProtectiveStopAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, CancellationToken ct)
    {
        var watch = user.Watchlist?.Where(id => _stocks.IsListedIn(id, currency)).ToList();
        if (watch is null || watch.Count == 0) return null;
        int stockId = 0, qty = 0;
        foreach (var id in watch)
        {
            var avail = _accounts.GetPosition(user.UserId, id)?.AvailableQuantity ?? 0;
            if (avail > 0) { stockId = id; qty = Math.Min(avail, _advancedMaxQty); break; }
        }
        if (stockId <= 0 || qty <= 0) return null;
        var price = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (price <= 0m) return null;

        // §P6 fundamental-relative + de-clustered trigger, bounded inside the Far walls, and a low
        // slippage cap on the fire. Bots NEVER arm an uncapped trailing stop: the `trailing` gate now
        // also produces a capped static stop (real users' trailing path is unchanged). The reference
        // blends market with fundamental, but the trigger is forced strictly below market so a sell-stop
        // is always valid and varied stops don't pile at one level and chain-fire.
        var offset = StopOffset(ctx, user);
        var fund = Fundamental(stockId, currency);
        var refPrice = fund > 0m ? (price + fund) / 2m : price;
        var candidate = refPrice * (1m - offset);
        var ceiling = price * (1m - 0.002m);
        var stopPrice = CurrencyHelper.RoundMoney(Math.Min(candidate, ceiling), currency);
        if (stopPrice <= 0m) return null;
        return new BotAdvancedDecision(BotAdvancedKind.StopMarketSell, stockId, qty, currency,
            StopPrice: stopPrice, StopSlippagePct: _stopSlippagePct);
    }

    // P6b: open a flat-only cash-collateralized short (market sell on a stock the bot doesn't hold). Flat-only
    // so the bot never traverses the long→short flip (risk #7). Collateral is buying-power-neutral at open, so
    // sizing is just an exposure cap, not a cash constraint.
    private async Task<BotAdvancedDecision?> BuildShortOpenAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, CancellationToken ct)
    {
        int stockId = FirstFlatStock(ctx, user, currency);
        if (stockId <= 0) return null;
        var price = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (price <= 0m) return null;
        // §P6: don't pile a fresh short into a stock already far below fundamental (downward runaway fuel).
        if (await IsOverBandAsync(ctx, stockId, currency, isBuy: false, ct).ConfigureAwait(false)) return null;
        int qty = AdvancedExposureQty(ctx, user, currency, price);
        if (qty <= 0) return null;
        // §P6 anti-sweep: the market-sell entry can't take more than a fraction of the resting bids.
        qty = await ApplyDepthCapAsync(qty, isBuy: false, stockId, currency, ct).ConfigureAwait(false);
        if (qty <= 0) return null;
        return new BotAdvancedDecision(BotAdvancedKind.ShortOpen, stockId, qty, currency);
    }

    // P6b/P6c: open a bracketed entry. Long = market buy + sell-stop below + buy-limit… *sell*-limit TPs above;
    // short = flat-only market sell + slippage-capped buy-stop above + buy-limit TPs below. Sized so the entry
    // (long: cash; short: SL cash pool) is affordable, and kept small via _advancedMaxQty.
    private async Task<BotAdvancedDecision?> BuildBracketAsync(AiBotContext ctx, AIUser user,
        CurrencyType currency, bool isShort, CancellationToken ct)
    {
        int stockId = isShort ? FirstFlatStock(ctx, user, currency) : FirstLongableStock(ctx, user, currency);
        if (stockId <= 0) return null;
        var price = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (price <= 0m) return null;

        // §P6: brackets respect the same value-band veto as plain orders — don't open a long into a stock
        // already far above fundamental (its market entry is the fuel that fed the runaway), nor a short
        // into one already far below.
        if (await IsOverBandAsync(ctx, stockId, currency, isBuy: !isShort, ct).ConfigureAwait(false)) return null;

        // SL distance (per-bot, de-clustered, bounded inside Far walls) + two TP distances (sorted so
        // TP1 is nearer market than TP2).
        var slOff = StopOffset(ctx, user);
        // §P6: take-profit distances are PER-BOT (TpOffsetMin/MaxPrc, baked tight in the Excel pipeline),
        // falling back to the global Advanced:TpOffsetPrc config for un-regenerated bots. The tightness
        // dial still applies for any leftover global-config use (1.0 in production now the values are baked).
        var tpLo = user.TpOffsetMaxPrc > 0m ? user.TpOffsetMinPrc : _tpOffsetMin;
        var tpHi = user.TpOffsetMaxPrc > 0m ? user.TpOffsetMaxPrc : _tpOffsetMax;
        var o1 = Lerp(tpLo, tpHi, ctx.Decimal01(user.AiUserId)) * _distanceMult;
        var o2 = Lerp(tpLo, tpHi, ctx.Decimal01(user.AiUserId)) * _distanceMult;
        var tpNear = Math.Min(o1, o2); var tpFar = Math.Max(o1, o2);
        if (tpFar <= tpNear) tpFar = tpNear + 0.005m * _distanceMult;   // keep TP2 strictly past TP1

        var fund = _accounts.GetFund(user.UserId, currency);
        decimal avail = fund?.AvailableBalance ?? 0m;

        decimal stopPrice; decimal? slippage; decimal? buyBudget; int qty;
        if (isShort)
        {
            stopPrice = CurrencyHelper.RoundMoney(price * (1m + slOff), currency);     // SL above entry
            slippage  = _bracketSlippagePct;                                           // capped (uncapped rejected)
            buyBudget = null;
            decimal slWorst = stopPrice * (1m + _bracketSlippagePct / 100m);           // worst-case buyback / share
            if (slWorst <= 0m) return null;
            qty = (int)Math.Floor((avail - BuySafetyBuffer) / slWorst);                // SL cash pool must fit
        }
        else
        {
            // §P6: fundamental-relative SL strictly below entry, and a low slippage cap on the fire
            // (was uncapped null — the downward-cascade source). Share-reserved, so the cap is safe.
            var fundVal  = Fundamental(stockId, currency);
            var refPrice = fundVal > 0m ? (price + fundVal) / 2m : price;
            var slCand   = refPrice * (1m - slOff);
            var slCeil   = price * (1m - 0.002m);
            stopPrice = CurrencyHelper.RoundMoney(Math.Min(slCand, slCeil), currency);  // SL below entry
            slippage  = _stopSlippagePct;                                               // capped fire
            qty = (int)Math.Floor((avail - BuySafetyBuffer) / price);                  // entry cash must fit
            buyBudget = 0m; // set after qty is known
        }
        qty = Math.Min(qty, _advancedMaxQty);
        // §P6 anti-sweep: the market entry leg can't take more than a fraction of the resting opposite
        // side (long entry buys asks, short entry sells bids) — the same structural cap as plain orders.
        qty = await ApplyDepthCapAsync(qty, isBuy: !isShort, stockId, currency, ct).ConfigureAwait(false);
        if (qty < 2) return null;   // need ≥2 for a 2-leg scale-out
        if (!isShort) buyBudget = CurrencyHelper.RoundMoney(price * qty, currency);

        var tp1Qty = qty / 2; var tp2Qty = qty - tp1Qty;
        var tps = isShort
            ? new List<(decimal, int)> { (CurrencyHelper.RoundMoney(price * (1m - tpNear), currency), tp1Qty),
                                         (CurrencyHelper.RoundMoney(price * (1m - tpFar),  currency), tp2Qty) }
            : new List<(decimal, int)> { (CurrencyHelper.RoundMoney(price * (1m + tpNear), currency), tp1Qty),
                                         (CurrencyHelper.RoundMoney(price * (1m + tpFar),  currency), tp2Qty) };

        return new BotAdvancedDecision(
            isShort ? BotAdvancedKind.ShortBracket : BotAdvancedKind.LongBracket,
            stockId, qty, currency, StopPrice: stopPrice, BuyBudget: buyBudget,
            StopSlippagePct: slippage, TakeProfits: tps);
    }

    // First watchlist stock the bot is FLAT on (per the engine view) — for flat-only shorts/short-brackets.
    private int FirstFlatStock(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        var watch = user.Watchlist?.Where(id => _stocks.IsListedIn(id, currency)).ToList();
        if (watch is null) return 0;
        foreach (var id in watch)
            if ((_accounts.GetPosition(user.UserId, id)?.Quantity ?? 0) == 0) return id;
        return 0;
    }

    // First watchlist stock the bot is flat-or-long on (never short) — for long brackets.
    private int FirstLongableStock(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        var watch = user.Watchlist?.Where(id => _stocks.IsListedIn(id, currency)).ToList();
        if (watch is null) return 0;
        foreach (var id in watch)
            if ((_accounts.GetPosition(user.UserId, id)?.Quantity ?? 0) >= 0) return id;
        return 0;
    }

    // Modest exposure-capped quantity for an advanced entry: a slice of portfolio, ≤ _advancedMaxQty.
    private int AdvancedExposureQty(AiBotContext ctx, AIUser user, CurrencyType currency, decimal price)
    {
        if (price <= 0m) return 0;
        var portfolio = ctx.PortfolioValueByCurrency(user.UserId, currency);
        if (portfolio <= 0m) return 0;
        var notional = Lerp(user.MinTradeAmountPrc, user.MaxTradeAmountPrc, ctx.Decimal01(user.AiUserId)) * portfolio;
        int qty = (int)Math.Floor(notional / price);
        return Math.Min(Math.Max(qty, 0), _advancedMaxQty);
    }
    #endregion

    #region Order Decision Logic
    private OrderType ChooseOrderType(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        // §2 Market-maker quoting: post a resting limit on the under-represented side
        // so the bot maintains a two-sided quote near mid over successive ticks. Skips
        // the directional logic below — MM bots provide liquidity, not direction. They
        // still react to shocks via ApplyExtremeReaction downstream.
        if (_mmQuoting && user.Strategy == AiStrategy.MarketMaker)
            return ChooseMarketMakerQuote(ctx, user);

        // 1. Base buy probability adjusted by cash reserve position
        var cashPrc  = ctx.FundsPercentagePortfolio(user.UserId, currency);
        var buyProb  = user.BuyBiasPrc;
        var maxShift = 0.40m;

        if (cashPrc < user.MinCashReservePrc)
        {
            var distance = user.MinCashReservePrc <= 0m ? 1m
                : (user.MinCashReservePrc - cashPrc) / user.MinCashReservePrc;
            buyProb -= maxShift * Clamp01(distance);
        }
        else if (cashPrc > user.MaxCashReservePrc)
        {
            var distance = 1m - user.MaxCashReservePrc <= 0m ? 1m
                : (cashPrc - user.MaxCashReservePrc) / (1m - user.MaxCashReservePrc);
            buyProb += maxShift * Clamp01(distance);
        }

        // 2. Strategy-aware momentum bias (uses EWMA smoothed prices)
        var momentum       = ctx.ComputeWatchlistMomentum(user, currency);
        var momentumSignal = ClampSigned(momentum * 20m, 1m); // ±5% move → ±1

        switch (user.Strategy)
        {
            // Equal magnitude on both sides so the net effect across the
            // 25/25 TF/MR split is zero in expectation.
            case AiStrategy.TrendFollower:
                buyProb += 0.175m * momentumSignal; // Chase the move
                break;
            case AiStrategy.MeanReversion:
                buyProb -= 0.175m * momentumSignal; // Fade the move
                break;
            // MarketMaker, Scalper, Random: no directional bias
        }

        // Linear sentiment bias. Watchlist-averaged so the tilt reflects the
        // broad mood for stocks this bot cares about, not any single name.
        // Clamped to ±1 here — extremes drive the forced market order
        // applied later in ComputeOrderAsync.
        var sentimentClamped = ClampSigned(AverageWatchlistSentiment(ctx, user, currency), 1m);
        buyProb += sentimentClamped * SentimentMaxBias;

        // Value anchor: tilt toward buying stocks trading below fundamental and selling those above,
        // proportional to the deviation. This is the restoring force that keeps price bounded.
        if (_valueAnchorStrength > 0m)
        {
            var gap = ClampSigned(AverageWatchlistValueGap(ctx, user, currency) / _valueAnchorScale, 1m);
            buyProb += gap * _valueAnchorStrength;
        }
        buyProb = Clamp01(buyProb);

        // 3. Strategy-aware market-order probability
        var effectiveUseMarket = user.UseMarketProb;
        switch (user.Strategy)
        {
            case AiStrategy.Scalper:
                effectiveUseMarket = Math.Min(1m, effectiveUseMarket + 0.15m * Math.Abs(momentumSignal));
                break;
            case AiStrategy.MarketMaker:
                effectiveUseMarket = Math.Max(0m, effectiveUseMarket - 0.15m);
                break;
        }

        // 4. Resolve to concrete order type. Bots never place TRUE (uncapped) market orders — every
        // market order is slippage-capped (EffectiveSlippage) so no single order can sweep a thin book
        // far and start a cascade.
        var isBuy    = ctx.Decimal01(user.AiUserId) < buyProb;
        var isMarket = ctx.Decimal01(user.AiUserId) < effectiveUseMarket;

        return isBuy
            ? isMarket ? OrderType.SlippageMarketBuy : OrderType.LimitBuy
            : isMarket ? OrderType.SlippageMarketSell : OrderType.LimitSell;
    }

    // Quote the side with fewer resting limit orders so the bot tends toward a
    // balanced two-sided book. A sell with no inventory is filtered out later in
    // ChooseStockId, so the bot simply skips that tick until a bid fills.
    private static OrderType ChooseMarketMakerQuote(AiBotContext ctx, AIUser user)
    {
        int buys = 0, sells = 0;
        if (ctx.OpenOrders.TryGetValue(user.UserId, out var orders))
        {
            foreach (var o in orders.Values)
            {
                if (!o.IsLimitOrder) continue;
                if (o.IsBuyOrder) buys++; else sells++;
            }
        }
        return buys <= sells ? OrderType.LimitBuy : OrderType.LimitSell;
    }

    private int ChooseStockId(AiBotContext ctx, AIUser user, OrderType type, CurrencyType currency,
        CommittedTotals committed)
    {
        var rng   = ctx.GetRandom(user.AiUserId);
        var watch = user.Watchlist?
            .Where(id => _stocks.IsListedIn(id, currency))
            .ToList();
        if (watch == null || watch.Count == 0) return 0;

        if (IsSellOrder(type))
        {
            var candidates = new List<int>();
            foreach (var id in watch)
            {
                var pos          = ctx.GetPosition(user.UserId, id);
                var committedSell = committed.SellSharesByStock.GetValueOrDefault(id);
                var ctxAvail     = pos.Quantity - committedSell;
                // Cross-check against the engine's AvailableQuantity to avoid
                // generating orders that would fail Phase 1.5 on stale ctx.
                var enginePos   = _accounts.GetPosition(user.UserId, id);
                var engineAvail = enginePos?.AvailableQuantity ?? 0;
                if (Math.Min(ctxAvail, engineAvail) > 0) candidates.Add(id);
            }
            return candidates.Count > 0 ? PickStock(candidates, rng, currency, ctx, buySide: false) : 0;
        }

        return PickStock(watch, rng, currency, ctx, buySide: true);
    }

    /// <summary>
    /// Roulette-wheel pick weighted by 1/StockId^alpha (lower ids = bigger cap = more weight), boosted
    /// toward the stock whose correction this order serves — overvalued on a sell, undervalued on a buy.
    /// The boost concentrates the value anchor on whatever is breaking loose instead of diluting it.
    /// </summary>
    private int PickStock(IList<int> stockIds, Random rng, CurrencyType currency, AiBotContext ctx, bool buySide)
    {
        double total = 0;
        Span<double> cum = stockIds.Count <= 256 ? stackalloc double[stockIds.Count] : new double[stockIds.Count];
        for (int i = 0; i < stockIds.Count; i++)
        {
            double w = BaseWeight(stockIds[i]);
            if (_valueAnchorStrength > 0m && _valueTargetSelection)
            {
                var f = Fundamental(stockIds[i], currency);
                if (f > 0m && ctx.SmoothedPrices.TryGetValue((stockIds[i], currency), out var p) && p > 0m)
                {
                    double gap = (double)((f - p) / f);        // >0 undervalued, <0 overvalued
                    double corrective = buySide ? gap : -gap;  // a buy fixes undervalued; a sell fixes overvalued
                    if (corrective > 0)
                        w *= 1.0 + ValuePickGain * corrective / (double)_valueAnchorScale;
                }
            }
            total += w;
            cum[i] = total;
        }
        double r = rng.NextDouble() * total;
        for (int i = 0; i < stockIds.Count; i++)
            if (r < cum[i]) return stockIds[i];
        return stockIds[^1];
    }

    private const double RuntimeWeightAlpha = 0.7;
    // Selection boost per unit of normalized deviation when the value anchor is on: a stock 1×Scale
    // off fundamental gets (1 + ValuePickGain)× the weight on the corrective side.
    private const double ValuePickGain = 12.0;

    // 1/StockId^alpha is constant per id (alpha is a compile-time const), so memoize it instead of
    // recomputing Math.Pow for every candidate on every decision. The cached double is bit-identical
    // to the previous inline computation, so selection (for a given RNG draw) is unchanged.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double> _baseWeightByStockId = new();
    private static double BaseWeight(int stockId)
        => _baseWeightByStockId.GetOrAdd(stockId, static id => 1.0 / Math.Pow(id, RuntimeWeightAlpha));
    #endregion

    #region Price and Quantity Computation
    private async Task<decimal> ComputeOrderPriceAsync(AiBotContext ctx, AIUser user, OrderType type,
        int stockId, CurrencyType currency, CancellationToken ct)
    {
        if (IsTrueMarketOrder(type)) return 0m;

        var marketPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (marketPrice <= 0m) return 0m;

        if (IsSlippageOrder(type)) return CurrencyHelper.RoundMoney(marketPrice, currency);

        // §2 MM quoting: a symmetric two-sided quote at mid ± half-spread. Only when a
        // real mid exists (a two-sided book); otherwise fall through to normal offsets.
        if (_mmQuoting && user.Strategy == AiStrategy.MarketMaker)
        {
            var mid = await GetMidPriceAsync(stockId, currency, ct).ConfigureAwait(false);
            if (mid is > 0m)
            {
                var quote = IsBuyOrder(type)
                    ? mid.Value * (1m - _quoteHalfSpreadPrc)
                    : mid.Value * (1m + _quoteHalfSpreadPrc);
                return CurrencyHelper.RoundMoney(quote, currency);
            }
        }

        // Limit anchor: midprice when both sides are present, last-trade otherwise.
        // Last-trade ratchets upward whenever buys fill at the ask faster than
        // sells at the bid; midprice stays roughly put under that imbalance.
        var anchor = await GetMidPriceAsync(stockId, currency, ct).ConfigureAwait(false)
                     ?? marketPrice;

        // §P6 tiered ladder: pick Close / Mid / Far, then widen the chosen band by the liquidity
        // multiplier and add bidirectional jitter. Close churns at the touch; Far rests standing walls
        // that absorb fired (slippage-capped) stops instead of letting them sweep empty space.
        var (tierMin, tierMax) = PickLimitTier(ctx, user);
        var minOff = tierMin * _limitOffsetMult * _distanceMult;
        var maxOff = tierMax * _limitOffsetMult * _distanceMult;
        var offset = Clamp01(Lerp(minOff, maxOff, ctx.Decimal01(user.AiUserId)));
        var jitter = (ctx.Decimal01(user.AiUserId) * 2m - 1m) * user.AggressivenessPrc;
        offset = Math.Max(minOff, Math.Min(maxOff, offset * (1m + jitter)));

        var limitPrice = IsBuyOrder(type) ? anchor * (1m - offset) : anchor * (1m + offset);

        // ~30% chance: snap toward a psychologically significant round level
        if (ctx.Decimal01(user.AiUserId) < 0.30m)
            limitPrice = SnapToRoundNumber(limitPrice);

        return CurrencyHelper.RoundMoney(limitPrice, currency);
    }

    private async Task<decimal?> GetMidPriceAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        try
        {
            var book = await _books.GetAsync(stockId, currency, ct).ConfigureAwait(false);
            if (book is null) return null;
            var bid = book.PeekBestBuy()?.Price;
            var ask = book.PeekBestSell()?.Price;
            // One-sided books would just reintroduce the ratchet; require both.
            return (bid > 0m && ask > 0m) ? (bid.Value + ask.Value) / 2m : (decimal?)null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Midprice fetch failed for stock {Stock}/{Currency}; falling back to last-trade.",
                stockId, currency);
            return null;
        }
    }

    private async Task<int> ComputeOrderQuantityAsync(AiBotContext ctx, AIUser user, OrderType type,
        int stockId, CurrencyType currency, CommittedTotals committed, CancellationToken ct)
    {
        var portfolio = ctx.PortfolioValueByCurrency(user.UserId, currency);
        if (portfolio <= 0m) return 0;

        // Fat tails: skew the uniform draw so typical orders sit near Min with a heavy
        // right tail to Max. tailShape 0 (or feature off) = uniform, as before.
        var u = ctx.Decimal01(user.AiUserId);
        if (_fatTails && _tradeSizeTailShape > 0m)
            u = (decimal)Math.Pow((double)u, 1.0 + (double)_tradeSizeTailShape * TailExponentScale);
        var tradePrc = Lerp(user.MinTradeAmountPrc, user.MaxTradeAmountPrc, u);
        var jitter   = ctx.Decimal01(user.AiUserId) * user.AggressivenessPrc;
        tradePrc     = Math.Min(tradePrc * (1m + jitter), user.MaxTradeAmountPrc);
        // Rare block trade: an occasional outsized order past the per-bot Max. The
        // downstream room/cash/position clamps truncate it to actual capacity.
        if (_fatTails && _blockTradeProb > 0m && ctx.Decimal01(user.AiUserId) < _blockTradeProb)
            tradePrc *= _blockTradeMultiple;
        if (tradePrc <= 0m) return 0;

        var marketPrice = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (marketPrice <= 0m) return 0;

        decimal estimatePrice = type switch
        {
            OrderType.TrueMarketBuy or OrderType.TrueMarketSell => marketPrice,
            OrderType.SlippageMarketBuy  => CurrencyHelper.RoundMoney(marketPrice * (1m + EffectiveSlippage(user)), currency),
            OrderType.SlippageMarketSell => CurrencyHelper.RoundMoney(marketPrice * (1m - EffectiveSlippage(user)), currency),
            _                            => marketPrice // limit
        };
        if (estimatePrice <= 0m) return 0;

        var fund       = ctx.GetFund(user.UserId, currency);
        var pos        = ctx.GetPosition(user.UserId, stockId);
        var capValue   = user.PerPositionMaxPrc * portfolio;
        var currentVal = CurrencyHelper.Notional(marketPrice, pos.Quantity, currency);
        var roomValue  = Math.Max(0m, capValue - currentVal);
        var rawTrade   = tradePrc * portfolio;

        if (IsBuyOrder(type))
        {
            var committedBuy    = committed.BuyFundsByCurrency.GetValueOrDefault(currency);
            var ctxFreeBalance  = Math.Max(0m, fund.TotalBalance - committedBuy);
            // Plan B: clamp to the engine's AvailableBalance so the bot never generates
            // an order that's doomed at Phase 1.6 — same defence as the sell branch below.
            var engineFreeBalance = _accounts.GetFund(user.UserId, currency)?.AvailableBalance ?? 0m;
            var freeBalance       = Math.Min(ctxFreeBalance, engineFreeBalance);
            // Always leave at least BuySafetyBuffer un-reserved in the user's currency.
            var spendableBalance  = Math.Max(0m, freeBalance - BuySafetyBuffer);
            var allowedBalance    = Math.Min(Math.Min(spendableBalance, rawTrade), roomValue);
            var qty = (int)Math.Floor(allowedBalance / estimatePrice);
            // Floor at 1 share when the intended notional rounds to zero but the bot can
            // still afford a share within its spendable + position room — mirrors the sell
            // branch's max(1, …) so small order fractions don't silently vanish.
            if (qty == 0 && spendableBalance >= estimatePrice && roomValue >= estimatePrice)
                qty = 1;
            // §P6: never buy PAST flat on a stock the bot is short — the buy-side mirror of risk #7
            // (a short→long flip), which P6 forbids. Clamp to the uncovered short (engine-authoritative, like
            // the sell branch); a cover can flatten but never overshoot into a long. Fully covered → qty 0.
            var enginePos = _accounts.GetPosition(user.UserId, stockId);
            if (enginePos is { Quantity: < 0 })
            {
                int shortMag       = -enginePos.Quantity;
                int committedCover = committed.CoverSharesByStock.GetValueOrDefault(stockId);
                int coverable      = Math.Max(0, shortMag - committedCover);
                qty = Math.Min(qty, coverable);
            }
            // §P6 anti-sweep: a market buy can't take more than a fraction of resting asks.
            if (IsSlippageOrder(type))
                qty = await ApplyDepthCapAsync(qty, isBuy: true, stockId, currency, ct).ConfigureAwait(false);
            return qty;
        }
        else
        {
            var committedSell = committed.SellSharesByStock.GetValueOrDefault(stockId);
            var ctxAvailable  = Math.Max(0, pos.Quantity - committedSell);
            // Plan B: same clamp as ChooseStockId — engine view is authoritative. If the
            // ctx says we have N free but engine has more reserved, take engine's number.
            var engineAvailable = _accounts.GetPosition(user.UserId, stockId)?.AvailableQuantity ?? 0;
            var availableQty    = Math.Min(ctxAvailable, engineAvailable);
            var desiredQty      = Math.Max(1, (int)Math.Floor(rawTrade / estimatePrice));
            var sellQty         = Math.Min(desiredQty, availableQty);
            // §P6 anti-sweep: a market sell can't take more than a fraction of resting bids.
            if (IsSlippageOrder(type))
                sellQty = await ApplyDepthCapAsync(sellQty, isBuy: false, stockId, currency, ct).ConfigureAwait(false);
            return sellQty;
        }
    }

    // §perf C4: the per-decision "already committed" totals, computed in a single walk of the user's open
    // orders instead of one walk per consumer (the sell path called the old per-stock helper once per
    // candidate). OpenOrders is immutable within a decision, so these snapshot totals equal what the old
    // ComputeCommittedBuyFunds / ComputeCommittedSellShares / ComputeCommittedCoverShares returned at each
    // call site. The per-order predicates are unchanged: a buy limit contributes its RemainingAmount to the
    // currency bucket AND its RemainingQuantity to the stock cover bucket; a sell limit contributes its
    // RemainingQuantity to the stock sell bucket.
    internal readonly record struct CommittedTotals(
        IReadOnlyDictionary<CurrencyType, decimal> BuyFundsByCurrency,
        IReadOnlyDictionary<int, int> SellSharesByStock,
        IReadOnlyDictionary<int, int> CoverSharesByStock);

    internal static CommittedTotals ComputeCommitted(AiBotContext ctx, int userId)
    {
        var buyFunds    = new Dictionary<CurrencyType, decimal>();
        var sellShares  = new Dictionary<int, int>();
        var coverShares = new Dictionary<int, int>();
        if (ctx.OpenOrders.TryGetValue(userId, out var orders))
            foreach (var o in orders.Values)
            {
                if (!o.IsLimitOrder) continue;
                if (o.IsBuyOrder)
                {
                    buyFunds[o.CurrencyType] = (buyFunds.TryGetValue(o.CurrencyType, out var f) ? f : 0m) + o.RemainingAmount;
                    coverShares[o.StockId]   = (coverShares.TryGetValue(o.StockId, out var c) ? c : 0) + o.RemainingQuantity;
                }
                else if (o.IsSellOrder)
                {
                    sellShares[o.StockId]    = (sellShares.TryGetValue(o.StockId, out var s) ? s : 0) + o.RemainingQuantity;
                }
            }
        return new CommittedTotals(buyFunds, sellShares, coverShares);
    }

    // ValueTask: the common path is an in-memory cache hit (no allocation); it's called several times
    // per decision (band veto, price, qty) across collect/arb/advanced, so a Task alloc per call adds
    // real GC pressure across the loop. Only the cold cache-miss path actually awaits the market.
    private ValueTask<decimal> GetStockPriceAsync(AiBotContext ctx, int stockId,
        CurrencyType currency, CancellationToken ct)
    {
        if (ctx.StockPrices.TryGetValue((stockId, currency), out var price) && price > 0m)
            return new ValueTask<decimal>(price);
        return Cold(ctx, stockId, currency, ct);

        async ValueTask<decimal> Cold(AiBotContext c, int sid, CurrencyType cur, CancellationToken token)
        {
            var p = await _market.GetLastPriceAsync(sid, cur, token).ConfigureAwait(false);
            c.StockPrices[(sid, cur)] = p;
            return p;
        }
    }

    // §P6 tiered ladder: roll Close / Mid / Far and return that tier's (min,max) offset band. Mid/Far
    // fall back to the Close band if a bot pre-dates the tier columns (all-zero), so behaviour degrades
    // gracefully on an un-regenerated workbook.
    private (decimal min, decimal max) PickLimitTier(AiBotContext ctx, AIUser user)
    {
        var r = ctx.Decimal01(user.AiUserId);
        if (r < _tierCloseProb) return (user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc);
        if (r < _tierCloseProb + _tierMidProb)
            return user.MidLimitMaxPrc > 0m
                ? (user.MidLimitMinPrc, user.MidLimitMaxPrc)
                : (user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc);
        return user.FarLimitMaxPrc > 0m
            ? (user.FarLimitMinPrc, user.FarLimitMaxPrc)
            : (user.MinLimitOffsetPrc, user.MaxLimitOffsetPrc);
    }

    // §P6: per-bot protective-stop distance — drawn from the bot's StopDistance band (config fallback
    // for un-regenerated bots), jittered ±20% to de-cluster trigger levels, and clamped strictly inside
    // the Far walls so a fired (capped) stop runs into a standing wall instead of triggering the next stop.
    private decimal StopOffset(AiBotContext ctx, AIUser user)
    {
        var lo = user.StopDistanceMinPrc > 0m ? user.StopDistanceMinPrc : _stopOffsetMin;
        var hi = user.StopDistanceMaxPrc > 0m ? user.StopDistanceMaxPrc : _stopOffsetMax;
        var off = Lerp(lo, hi, ctx.Decimal01(user.AiUserId));
        var jitter = (ctx.Decimal01(user.AiUserId) * 2m - 1m) * 0.20m;
        off *= (1m + jitter);
        // Clamp strictly inside the Far wall. The far wall and the stop both get the global distance dial
        // on the way out, so the clamp is computed pre-dial — include _limitOffsetMult (the far wall has it)
        // so the "stop fires into a standing wall" invariant survives any limit-ladder scaling.
        var farMin = user.FarLimitMinPrc > 0m ? user.FarLimitMinPrc * _limitOffsetMult : hi;
        off = Math.Min(off, farMin * 0.9m);
        return Math.Max(0.001m, off) * _distanceMult;   // §P6 tightness dial
    }

    // §P6 value-band veto (shared by the plain and advanced order paths): true when an order would chase
    // price past the personality-scaled overheat band — buying a stock already far above fundamental, or
    // selling/shorting one already far below. Brackets and shorts route through this too, so the advanced
    // path can't bypass the anchor and feed a runaway.
    private async Task<bool> IsOverBandAsync(AiBotContext ctx, int stockId, CurrencyType currency,
        bool isBuy, CancellationToken ct)
    {
        if (_overheatCap <= 0m) return false;
        var fund = Fundamental(stockId, currency);
        if (fund <= 0m) return false;
        var mkt = await GetStockPriceAsync(ctx, stockId, currency, ct).ConfigureAwait(false);
        if (mkt <= 0m) return false;
        var cap = _overheatCap * _profiles.Get(stockId).OverheatCapMult;
        var dev = (mkt - fund) / fund;
        return isBuy ? dev > cap : dev < -cap;
    }

    // §P6 liquidity-aware anti-sweep: cap a bot MARKET order to a fraction of the resting opposite-side
    // depth so no single order can sweep more than that share of the book, regardless of slippage. Pure
    // size reduction — conservation-neutral. Limits are unaffected (they rest, they don't sweep).
    private async Task<int> ApplyDepthCapAsync(int qty, bool isBuy, int stockId, CurrencyType currency,
        CancellationToken ct)
    {
        if (qty <= 0 || _maxSweepFractionOfDepth <= 0m) return qty;
        try
        {
            var book = await _books.GetAsync(stockId, currency, ct).ConfigureAwait(false);
            if (book is null) return qty;
            // Sum the side we'd sweep into (buy order eats the sells, and vice-versa) without
            // allocating a full Snapshot just to total one side.
            long oppQty = book.SumQuantity(buySide: !isBuy);
            if (oppQty <= 0) return qty; // empty opposite side — nothing to sweep
            int cap = (int)Math.Floor((decimal)oppQty * _maxSweepFractionOfDepth);
            return Math.Min(qty, Math.Max(0, cap));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Depth-cap fetch failed for stock {Stock}/{Currency}; leaving qty uncapped.",
                stockId, currency);
            return qty;
        }
    }
    #endregion

    #region Sentiment Integration
    private decimal AverageWatchlistSentiment(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        if (user.Watchlist == null || user.Watchlist.Count == 0) return 0m;
        decimal sum = 0m;
        int count = 0;
        foreach (var sid in user.Watchlist)
        {
            sum += _sentiment.GetSentiment(sid) + ctx.PersonalSentiment(user, sid, currency);
            count++;
        }
        return count > 0 ? sum / count : 0m;
    }

    // Average signed gap to fundamental across the watchlist: (seed − price)/seed. Positive = broadly
    // below fundamental (cheap → bias to buy); negative = above (rich → bias to sell).
    private decimal AverageWatchlistValueGap(AiBotContext ctx, AIUser user, CurrencyType currency)
    {
        if (user.Watchlist == null || user.Watchlist.Count == 0) return 0m;
        decimal sum = 0m;
        int count = 0;
        foreach (var sid in user.Watchlist)
        {
            var f = Fundamental(sid, currency);
            if (f <= 0m) continue;
            if (!ctx.SmoothedPrices.TryGetValue((sid, currency), out var p) || p <= 0m) continue;
            sum += (f - p) / f;
            count++;
        }
        return count > 0 ? sum / count : 0m;
    }

    // A market order's slippage, capped low so no single market order sweeps a thin book far.
    private decimal EffectiveSlippage(AIUser user) => Math.Min(user.SlippageTolerancePrc, _marketSlippagePrc);

    // Per-(stock,currency) fundamental — the slowly-drifting value the FundamentalService maintains
    // (the fixed seed price when drift is disabled). The value anchor + overheat veto track this.
    private decimal Fundamental(int stockId, CurrencyType currency)
        => _funds.Get(stockId, currency);

    private OrderType ApplyExtremeReaction(AiBotContext ctx, AIUser user,
        int stockId, CurrencyType currency, OrderType currentType)
    {
        var raw = _sentiment.GetSentiment(stockId) + ctx.PersonalSentiment(user, stockId, currency);
        var absRaw = Math.Abs(raw);
        if (absRaw <= 1m) return currentType;

        var overflow   = absRaw - 1m;
        var forcedProb = Math.Min(1m, overflow * OverflowGain);
        if (ctx.Decimal01(user.AiUserId) >= forcedProb) return currentType;

        var style = PickExtremeReactionStyle(ctx, user);
        var dir   = (raw > 0m) ? BullDirection(style) : BearDirection(style);

        // Sell override into a stock the bot doesn't hold would just fail
        // Phase 1.5 — fall back to the original type so the order still
        // has a chance of being placed.
        if (dir == ExtremeDirection.Sell)
        {
            var pos = ctx.GetPosition(user.UserId, stockId);
            if (pos.Quantity <= 0) return currentType;
        }

        return dir switch
        {
            ExtremeDirection.Buy  => OrderType.SlippageMarketBuy,
            ExtremeDirection.Sell => OrderType.SlippageMarketSell,
            _                     => currentType,
        };
    }

    private static ExtremeReactionStyle PickExtremeReactionStyle(AiBotContext ctx, AIUser user)
    {
        var defaultStyle = user.Strategy switch
        {
            AiStrategy.TrendFollower => ExtremeReactionStyle.FOMO,
            AiStrategy.MeanReversion => ExtremeReactionStyle.Contrarian,
            AiStrategy.MarketMaker   => ExtremeReactionStyle.Contrarian,
            AiStrategy.Scalper       => ExtremeReactionStyle.Panic,
            _                        => ExtremeReactionStyle.None,
        };

        // Out-of-character branch: pick a random style uniformly among the
        // four (one of them is None, so ~25% of out-of-character rolls land
        // on "no extreme reaction" — same as Random-strategy bots).
        if (ctx.Decimal01(user.AiUserId) < user.ExtremeReactionRandomnessPrc)
        {
            var pick = ctx.GetRandom(user.AiUserId).Next(4);
            return pick switch
            {
                0 => ExtremeReactionStyle.FOMO,
                1 => ExtremeReactionStyle.Contrarian,
                2 => ExtremeReactionStyle.Panic,
                _ => ExtremeReactionStyle.None,
            };
        }
        return defaultStyle;
    }

    private static ExtremeDirection BullDirection(ExtremeReactionStyle style) => style switch
    {
        ExtremeReactionStyle.FOMO       => ExtremeDirection.Buy,   // chase the top
        ExtremeReactionStyle.Contrarian => ExtremeDirection.Sell,  // fade the top
        ExtremeReactionStyle.Panic      => ExtremeDirection.Sell,  // take profit
        _                               => ExtremeDirection.None,
    };

    private static ExtremeDirection BearDirection(ExtremeReactionStyle style) => style switch
    {
        ExtremeReactionStyle.FOMO       => ExtremeDirection.Sell,  // panic the bottom
        ExtremeReactionStyle.Contrarian => ExtremeDirection.Buy,   // buy the dip
        ExtremeReactionStyle.Panic      => ExtremeDirection.Sell,  // capitulate
        _                               => ExtremeDirection.None,
    };

    private enum ExtremeReactionStyle { FOMO, Contrarian, Panic, None }
    private enum ExtremeDirection { Buy, Sell, None }
    #endregion

    #region OrderType Enum and Helpers
    private enum OrderType
    {
        TrueMarketBuy, TrueMarketSell,
        SlippageMarketBuy, SlippageMarketSell,
        LimitBuy, LimitSell
    }

    private static bool IsBuyOrder(OrderType t) =>
        t is OrderType.TrueMarketBuy or OrderType.SlippageMarketBuy or OrderType.LimitBuy;

    private static bool IsSellOrder(OrderType t) =>
        t is OrderType.TrueMarketSell or OrderType.SlippageMarketSell or OrderType.LimitSell;

    private static bool IsSlippageOrder(OrderType t) =>
        t is OrderType.SlippageMarketBuy or OrderType.SlippageMarketSell;

    private static bool IsTrueMarketOrder(OrderType t) =>
        t is OrderType.TrueMarketBuy or OrderType.TrueMarketSell;

    private static string ToOrderTypeString(OrderType t) => t switch
    {
        OrderType.TrueMarketBuy      => Order.Types.TrueMarketBuy,
        OrderType.TrueMarketSell     => Order.Types.TrueMarketSell,
        OrderType.SlippageMarketBuy  => Order.Types.SlippageMarketBuy,
        OrderType.SlippageMarketSell => Order.Types.SlippageMarketSell,
        OrderType.LimitBuy           => Order.Types.LimitBuy,
        OrderType.LimitSell          => Order.Types.LimitSell,
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };
    #endregion

    #region Math Helpers
    private static decimal Lerp(decimal a, decimal b, decimal t) => a + (b - a) * t;

    private static decimal Clamp01(decimal x) => x < 0m ? 0m : x > 1m ? 1m : x;

    private static decimal ClampSigned(decimal x, decimal magnitude) =>
        x < -magnitude ? -magnitude : x > magnitude ? magnitude : x;

    private static decimal SnapToRoundNumber(decimal price)
    {
        decimal unit = price switch
        {
            >= 500m => 5m,
            >= 100m => 1m,
            >= 20m  => 0.50m,
            _       => 0.10m
        };
        return Math.Max(0.01m, Math.Round(price / unit) * unit);
    }
    #endregion
}
