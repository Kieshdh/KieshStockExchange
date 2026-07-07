using System.Linq;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §bank-estimate: the BankEstimateService republishes per-stock fair-value estimates (fractional deviation from
/// seed) on a Poisson schedule. These cover the invariants a can't-test agent must not break: disabled ⇒ no-op /
/// zero (byte-identical off), deterministic across Reset (a dedicated RNG, drawn only when enabled), the estimate
/// stays bounded, and republishes actually move it.
/// </summary>
public class BankEstimateServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const CurrencyType Usd = CurrencyType.USD;

    private static Mock<IStockService> BuildStocks(params (int sid, decimal seed)[] entries)
    {
        var byId = new Dictionary<int, Stock>();
        var listings = new Dictionary<int, List<StockListing>>();
        foreach (var e in entries)
        {
            byId[e.sid] = new Stock { StockId = e.sid };
            listings[e.sid] = new() { new StockListing { StockId = e.sid, CurrencyType = Usd, SeedPrice = e.seed } };
        }
        var mock = new Mock<IStockService>(MockBehavior.Loose);
        mock.SetupGet(s => s.ById).Returns(byId);
        mock.Setup(s => s.GetListings(It.IsAny<int>()))
            .Returns<int>(id => listings.TryGetValue(id, out var l) ? (IReadOnlyList<StockListing>)l : Array.Empty<StockListing>());
        return mock;
    }

    private static BankEstimateService Build(bool enabled)
    {
        var stocks = BuildStocks((1, 100m), (2, 50m), (3, 200m)).Object;
        var profiles = new StockProfileService(enabled: false);
        // A never-ticked sentiment service ⇒ GetSentiment ≡ 0; the estimate is then driven by the (deterministic)
        // variance draws + anti-pump cap, which is all we need to exercise the RNG path reproducibly.
        var sentiment = new BotSentimentService(stocks, profiles, NullLogger<BotSentimentService>.Instance);
        return new BankEstimateService(stocks, profiles, sentiment,
            NullLogger<BankEstimateService>.Instance, enabled: enabled,
            alpha: 0.3, poissonMeanIntervalSec: 5.0, wrongnessFraction: 0.5, sectorCount: 2);
    }

    // Drive N ticks and capture the published estimate of each stock at every tick.
    private static List<(double e1, double e2, double e3)> Drive(BankEstimateService svc, int ticks)
    {
        svc.Reset(T0);
        var series = new List<(double, double, double)>(ticks);
        for (int i = 1; i <= ticks; i++)
        {
            svc.Tick(T0.AddSeconds(i));
            series.Add((svc.BankTarget(1), svc.BankTarget(2), svc.BankTarget(3)));
        }
        return series;
    }

    [Fact]
    public void Disabled_returns_zero_and_tick_is_noop()
    {
        var svc = Build(enabled: false);
        var series = Drive(svc, 100);
        Assert.All(series, t => Assert.Equal((0.0, 0.0, 0.0), t));
        Assert.Equal(0.0, svc.BankTarget(1));
        Assert.Equal(0.0, svc.PrevBankTarget(1));
    }

    [Fact]
    public void Deterministic_across_resets()
    {
        // Same seed + same Tick schedule ⇒ identical estimate sequence (replayable).
        var a = Drive(Build(enabled: true), 400);
        var b = Drive(Build(enabled: true), 400);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Estimate_stays_bounded()
    {
        var series = Drive(Build(enabled: true), 2000);
        Assert.All(series, t =>
        {
            Assert.InRange(t.e1, -0.12, 0.12);
            Assert.InRange(t.e2, -0.12, 0.12);
            Assert.InRange(t.e3, -0.12, 0.12);
        });
    }

    [Fact]
    public void Republishes_move_the_estimate_off_zero()
    {
        // With WrongnessFraction > 0 and a short mean interval, at least one stock's estimate must have moved
        // away from 0 within a few hundred ticks — otherwise the republish path is dead.
        var series = Drive(Build(enabled: true), 300);
        Assert.Contains(series, t => t.e1 != 0.0 || t.e2 != 0.0 || t.e3 != 0.0);
    }
}
