using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §market-maker-cohort: dedicated decision path for <see cref="AiStrategy.MarketMakerHouse"/> bots — a
/// fixed, separately-seeded house cohort that runs fully OUT of the normal sentiment/anchor/veto/injection
/// flow (mirrors <see cref="ArbitrageDecisionService"/>). Each bot continuously maintains a two-sided resting
/// LIMIT quote around a reference that survives a one-sided book (<see cref="MarketMakerMath.Reference"/>), so
/// it supplies asks into an up-shock — the structural fix for the chaser's one-sided-book down-drift — and keeps
/// the touch continuously replenished (shrinking the bid-ask bounce).
///
/// Quotes ride the ordinary <see cref="IOrderEntryService"/> reserve→match→settle path, so ConservationProbe /
/// ReservationAuditor invariants apply unchanged. Inventory is bounded BOTH ways by <see cref="MarketMakerMath"/>'s
/// hard position cap, and bids/short-asks are additionally capped against live available cash here. The MM tracks
/// its own resting order ids privately (NOT in <c>ctx.OpenOrders</c>), so its quotes are invisible to the
/// open-order cap and immune to the age-based prune — only the MM repositions them.
///
/// Determinism: iterates ascending AiUserId, consumes NO RNG (the quote math is pure; the only dither is an
/// RNG-free hash), and reads only the stamped tick state — same seed-reproducibility contract as the main loop.
/// </summary>
internal sealed class MarketMakerDecisionService
{
    #region Services and Constructor
    private readonly IOrderEntryService _entry;
    private readonly IOrderBookEngine _books;
    private readonly IAccountsCache _accounts;
    private readonly IStockService _stocks;
    private readonly ILogger<MarketMakerDecisionService> _logger;
    private readonly MmConfig _cfg;

    // Per-bot resting-quote state, keyed by (stockId, ccy). Touched ONLY by the single bot-loop thread, so no
    // locking is needed. Deliberately NOT ctx.OpenOrders ⇒ prune-immune + invisible to the open-order cap.
    private sealed class QuoteState
    {
        public int? BidId; public decimal BidPx; public int BidQty;
        public int? AskId; public decimal AskPx; public int AskQty;
    }
    private readonly Dictionary<int, Dictionary<(int stockId, CurrencyType ccy), QuoteState>> _quotes = new();

    internal MarketMakerDecisionService(IOrderEntryService entry, IOrderBookEngine books,
        IAccountsCache accounts, IStockService stocks,
        ILogger<MarketMakerDecisionService> logger, MmConfig cfg)
    {
        _entry    = entry    ?? throw new ArgumentNullException(nameof(entry));
        _books    = books    ?? throw new ArgumentNullException(nameof(books));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _stocks   = stocks   ?? throw new ArgumentNullException(nameof(stocks));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _cfg      = cfg;
    }
    #endregion

    #region Run
    internal async Task RunAsync(AiBotContext ctx, DateTime now, CancellationToken ct)
    {
        // §perf: filter the small MM cohort FIRST, then sort the handful — materializing + sorting all ~20k users
        // every tick was pure waste (mirrors the rotator's cohort selection). Ascending AiUserId preserves the
        // same seed-determinism contract as the main loop.
        var cohort = new List<AIUser>();
        foreach (var u in ctx.AiUsersByAiUserId.Values)
        {
            if (!u.IsEnabled || u.Strategy != AiStrategy.MarketMakerHouse) continue;
            if (now - u.LastDecisionTime < u.DecisionInterval) continue;
            cohort.Add(u);
        }
        cohort.Sort((a, b) => a.AiUserId.CompareTo(b.AiUserId));
        foreach (var user in cohort)
        {
            if (user.MaxInventoryPerStock <= 0) continue; // cap-less MM would be unbounded — skip
            user.RecordDecision(now);

            var ccy = user.HomeCurrencyType; // §decision: home-currency only
            try
            {
                foreach (var stockId in user.Watchlist)
                {
                    if (!_stocks.IsListedIn(stockId, ccy)) continue;
                    await ReQuoteAsync(ctx, user, stockId, ccy, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Market-maker decision failed for AIUser {Id}.", user.AiUserId);
                user.RecordError();
            }
        }
    }
    #endregion

    #region Quoting
    private async Task ReQuoteAsync(AiBotContext ctx, AIUser user, int stockId, CurrencyType ccy, CancellationToken ct)
    {
        // Reference inputs — all from deterministic, already-stamped state.
        decimal bestBid = 0m, bestAsk = 0m; long bidQty = 0L, askQty = 0L;
        if (_books.TryGetLoaded(stockId, ccy, out var book) && book is not null)
        {
            bestBid = book.PeekBestBuy()?.Price ?? 0m;
            bestAsk = book.PeekBestSell()?.Price ?? 0m;
            bidQty  = book.PeekBestQty(buySide: true);
            askQty  = book.PeekBestQty(buySide: false);
        }
        decimal last = ctx.StockPrices.TryGetValue((stockId, ccy), out var lp) ? lp : 0m;
        decimal ewma = ctx.SmoothedPrices.TryGetValue((stockId, ccy), out var sp) ? sp : 0m;
        decimal seed = SeedPrice(stockId, ccy);

        var reference = MarketMakerMath.Reference(bestBid, bestAsk, bidQty, askQty, last, ewma, seed,
            _cfg.UseMicro, out var oneSided);

        int inv = _accounts.GetPosition(user.UserId, stockId)?.Quantity ?? 0;
        int cap = user.MaxInventoryPerStock;
        var quote = MarketMakerMath.Quote(reference, oneSided, inv, cap, ccy, _cfg, user.AiUserId, stockId);

        var st = StateFor(user.AiUserId, stockId, ccy);
        await SyncSideAsync(user, stockId, ccy, isBuy: true,  desiredPx: quote.Bid.Price, desiredQty: quote.Bid.Qty, reference, st, ct).ConfigureAwait(false);
        await SyncSideAsync(user, stockId, ccy, isBuy: false, desiredPx: quote.Ask.Price, desiredQty: quote.Ask.Qty, reference, st, ct).ConfigureAwait(false);
    }

    // Reconcile one side against its tracked resting order, then keep / cancel-replace / place-fresh. Cancel
    // happens BEFORE the replacement is placed so the stale reservation is released first (no transient
    // double-reserve that could trip the cash / position cap). Cash + §F14-collateral caps are applied here
    // against LIVE AvailableBalance (read after the cancel), which already nets any still-resting reservations.
    private async Task SyncSideAsync(AIUser user, int stockId, CurrencyType ccy, bool isBuy,
        decimal desiredPx, int desiredQty, decimal reference, QuoteState st, CancellationToken ct)
    {
        int? id     = isBuy ? st.BidId  : st.AskId;
        decimal cur = isBuy ? st.BidPx  : st.AskPx;
        int curQty  = isBuy ? st.BidQty : st.AskQty;
        bool wantQuote = desiredQty > 0 && desiredPx > 0m;

        if (id is int oid)
        {
            bool moved = reference > 0m
                && Math.Abs(desiredPx - cur) / reference > _cfg.RequoteThresholdBps / 10000m;
            if (wantQuote && !moved && desiredQty == curQty) return; // still good — leave it resting
            await _entry.CancelOrderAsync(user.UserId, oid, ct).ConfigureAwait(false); // releases the reservation
            ClearSide(st, isBuy);
        }

        if (!wantQuote) return;

        desiredQty = ApplyCashCap(user, stockId, ccy, isBuy, desiredPx, desiredQty);
        if (desiredQty <= 0) return;

        var result = isBuy
            ? await _entry.PlaceLimitBuyOrderAsync(user.UserId, stockId, desiredQty, desiredPx, ccy, ct).ConfigureAwait(false)
            : await _entry.PlaceLimitSellOrderAsync(user.UserId, stockId, desiredQty, desiredPx, ccy, ct).ConfigureAwait(false);

        // A quote may cross on entry (it took resting liquidity) — record those fills for the bot's stats.
        var fills = result.FillTransactions;
        for (int i = 0; i < fills.Count; i++) user.RecordTrade(fills[i]);

        if (result.PlacedSuccessfully && result.NewOrderId is int newId && result.RemainingQuantity > 0)
        {
            SetSide(st, isBuy, newId, desiredPx, result.RemainingQuantity);
            MarketMakerProbe.RecordResting(isBuy, result.RemainingQuantity,
                CurrencyHelper.Notional(desiredPx, result.RemainingQuantity, ccy));
        }
        else
        {
            ClearSide(st, isBuy); // fully filled on entry, or rejected — nothing rests
        }
    }

    // Bid: cap shares to MaxCashFrac of available cash. Ask: the covered part (held shares) needs no cash; the
    // SHORT part (§F14) reserves collateral, so cap the short shares to MaxCashFrac of available cash too. Both
    // reads are post-cancel, so AvailableBalance/AvailableQuantity already reflect the released reservation.
    private int ApplyCashCap(AIUser user, int stockId, CurrencyType ccy, bool isBuy, decimal px, int qty)
    {
        if (px <= 0m) return 0;
        decimal avail = _accounts.GetFund(user.UserId, ccy)?.AvailableBalance ?? 0m;
        int byCash = (int)Math.Floor(avail * _cfg.MaxCashFrac / px);

        if (isBuy) return Math.Min(qty, Math.Max(0, byCash));

        int availShares = Math.Max(0, _accounts.GetPosition(user.UserId, stockId)?.AvailableQuantity ?? 0);
        int shortPart = qty - availShares;
        if (shortPart <= 0) return qty;                 // fully covered by holdings — no collateral needed
        int affordableShort = Math.Max(0, byCash);
        return availShares + Math.Min(shortPart, affordableShort);
    }
    #endregion

    #region Helpers
    private decimal SeedPrice(int stockId, CurrencyType ccy)
    {
        var listings = _stocks.GetListings(stockId);
        for (int i = 0; i < listings.Count; i++)
            if (listings[i].CurrencyType == ccy) return listings[i].SeedPrice;
        return 0m;
    }

    private QuoteState StateFor(int aiUserId, int stockId, CurrencyType ccy)
    {
        if (!_quotes.TryGetValue(aiUserId, out var perStock))
            _quotes[aiUserId] = perStock = new Dictionary<(int, CurrencyType), QuoteState>();
        if (!perStock.TryGetValue((stockId, ccy), out var st))
            perStock[(stockId, ccy)] = st = new QuoteState();
        return st;
    }

    private static void SetSide(QuoteState st, bool isBuy, int id, decimal px, int qty)
    {
        if (isBuy) { st.BidId = id; st.BidPx = px; st.BidQty = qty; }
        else       { st.AskId = id; st.AskPx = px; st.AskQty = qty; }
    }

    private static void ClearSide(QuoteState st, bool isBuy)
    {
        if (isBuy) { st.BidId = null; st.BidPx = 0m; st.BidQty = 0; }
        else       { st.AskId = null; st.AskPx = 0m; st.AskQty = 0; }
    }
    #endregion
}
