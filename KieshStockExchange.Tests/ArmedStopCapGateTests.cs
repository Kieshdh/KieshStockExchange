using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §source-cap Phase 1 — the reject-at-placement GATE in AiBotDecisionService.BuildProtectiveStopAsync, driven
/// through <c>internal ComputeAdvancedDecisionAsync</c>. A bot at its armed-stop cap returns null (falls
/// through to a plain order) BEFORE EligibleWatchlist runs — so the robust, setup-light observable is whether
/// the stock service is consulted: consulted ⇒ the gate was passed, not-consulted ⇒ the cap short-circuited.
/// The bot's StopProb is forced to 1 so the single advanced roll always routes to the protective-stop builder.
/// BuyStopFraction=0 keeps it on the draw-free sell-stop path. Construction mirrors BotPrecomputeEquivalenceTests.
/// </summary>
[Collection("BotPrecomputeSerial")]
public class ArmedStopCapGateTests
{
    private const CurrencyType USD = CurrencyType.USD;

    private static (AiBotDecisionService svc, Func<AiBotContext> newCtx, Mock<IStockService> stocks) Build(int cap)
    {
        var profiles = new StockProfileService(enabled: false);

        var byId = new Dictionary<int, Stock> { [10] = new Stock { StockId = 10 } };
        var listings = new Dictionary<int, IReadOnlyList<StockListing>>
        {
            [10] = new List<StockListing> { new() { StockId = 10, CurrencyType = USD, SeedPrice = 100m, IsPrimary = true } },
        };

        var stocks = new Mock<IStockService>();
        stocks.Setup(s => s.ById).Returns(byId);
        stocks.Setup(s => s.GetListings(It.IsAny<int>()))
              .Returns((int sid) => listings.TryGetValue(sid, out var ls) ? ls : (IReadOnlyList<StockListing>)Array.Empty<StockListing>());
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>()))
              .Returns((int sid, CurrencyType c) => listings.TryGetValue(sid, out var ls) && ls.Any(l => l.CurrencyType == c));

        var market   = new Mock<IMarketDataService>();
        var books    = new Mock<IOrderBookEngine>();
        var accounts = new Mock<IAccountsCache>();
        accounts.Setup(a => a.GetFund(It.IsAny<int>(), It.IsAny<CurrencyType>())).Returns(() => new Fund { TotalBalance = 1_000_000m });
        accounts.Setup(a => a.GetPosition(It.IsAny<int>(), It.IsAny<int>())).Returns((Position?)null);

        var sentiment   = new BotSentimentService(stocks.Object, profiles, NullLogger<BotSentimentService>.Instance);
        var funds       = new FundamentalService(stocks.Object, profiles, NullLogger<FundamentalService>.Instance, enabled: false);
        var regime      = new BotRegimeService(NullLogger<BotRegimeService>.Instance);
        var activity    = new BotActivityService(stocks.Object, sentiment, NullLogger<BotActivityService>.Instance, recentReturn: _ => 0.0);
        var priceMemory = new BotPriceMemoryService(stocks.Object, NullLogger<BotPriceMemoryService>.Instance, priceLookup: _ => 0m);

        var svc = new AiBotDecisionService(
            market.Object, accounts.Object, books.Object, stocks.Object,
            sentiment, funds, profiles, regime, activity, priceMemory,
            NullLogger<AiBotDecisionService>.Instance,
            advancedEnabled: true, buyStopFraction: 0m,
            maxArmedStopsPerBot: cap, leanReload: true);

        AiBotContext NewCtx()
        {
            var ctx = new AiBotContext(accounts.Object) { TickId = 1, TickNowTicks = 0 };
            ctx.StockPrices[(10, USD)] = 100m;
            return ctx;
        }
        return (svc, NewCtx, stocks);
    }

    private static AIUser Bot()
    {
        var u = new AIUser
        {
            AiUserId = 10, UserId = 10, Seed = 1, StrategyCode = (int)AiStrategy.Random,
            IsEnabled = true, DecisionIntervalSeconds = 1, StopProb = 1m,
        };
        u.AddToWatchlist(10);
        return u;
    }

    private static AiBotContext CtxWithBot(Func<AiBotContext> newCtx, AIUser u)
    {
        var ctx = newCtx();
        ctx.AiUsersByAiUserId[10] = u; ctx.AiUsersByUserId[10] = u;
        return ctx;
    }

    [Fact]
    public async Task Cap_at_limit_returns_null_before_watchlist()
    {
        var (svc, newCtx, stocks) = Build(cap: 3);
        var u = Bot();
        var ctx = CtxWithBot(newCtx, u);
        ctx.ArmedStopCount[10] = 3;   // == cap

        var dec = await svc.ComputeAdvancedDecisionAsync(ctx, u, USD, CancellationToken.None);

        Assert.Null(dec);
        stocks.Verify(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>()), Times.Never);
    }

    [Fact]
    public async Task Below_cap_proceeds_past_the_gate()
    {
        var (svc, newCtx, stocks) = Build(cap: 3);
        var u = Bot();
        var ctx = CtxWithBot(newCtx, u);
        ctx.ArmedStopCount[10] = 2;   // < cap

        await svc.ComputeAdvancedDecisionAsync(ctx, u, USD, CancellationToken.None);

        stocks.Verify(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Cap_off_skips_the_gate_even_with_a_large_count()
    {
        var (svc, newCtx, stocks) = Build(cap: 0);
        var u = Bot();
        var ctx = CtxWithBot(newCtx, u);
        ctx.ArmedStopCount[10] = 999;   // ignored when the cap is off (byte-identical)

        await svc.ComputeAdvancedDecisionAsync(ctx, u, USD, CancellationToken.None);

        stocks.Verify(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>()), Times.AtLeastOnce);
    }
}
