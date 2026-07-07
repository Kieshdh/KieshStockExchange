using System.Linq;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §bank-estimate: the FundamentalService OU reversion target is pivoted to the bank estimate, clamped INTERIOR
/// to the hard band (seed·[1 ± Band·0.8]). These cover PIN 2: (1) byte-identical when the estimate is 0/unwired,
/// and (2) an extreme estimate never escapes the hard band AND the OU still diffuses (the parked-at-the-band
/// diffusion-death pathology is guarded).
/// </summary>
public class BankEstimateAnchorPivotTests
{
    private const CurrencyType Usd = CurrencyType.USD;
    private const decimal Seed = 100m;
    private const decimal Band = 0.12m;

    private static Mock<IStockService> BuildStocks()
    {
        var byId = new Dictionary<int, Stock> { [1] = new Stock { StockId = 1 } };
        var listings = new Dictionary<int, List<StockListing>>
        {
            [1] = new() { new StockListing { StockId = 1, CurrencyType = Usd, SeedPrice = Seed } }
        };
        var mock = new Mock<IStockService>(MockBehavior.Loose);
        mock.SetupGet(s => s.ById).Returns(byId);
        mock.Setup(s => s.GetListings(It.IsAny<int>()))
            .Returns<int>(id => listings.TryGetValue(id, out var l) ? (IReadOnlyList<StockListing>)l : Array.Empty<StockListing>());
        return mock;
    }

    private static FundamentalService Build(Func<int, double>? bankTarget)
        => new(BuildStocks().Object, new StockProfileService(enabled: false),
               NullLogger<FundamentalService>.Instance, enabled: true, band: Band,
               theta: 0.02, sigma: 0.004, driftIntervalSec: 1.0, bankTarget: bankTarget);

    private static List<double> Drive(FundamentalService svc, int ticks)
    {
        // FundamentalService.Reset() anchors its drift clock to TimeHelper.NowUtc(); base the tick times on that same
        // 'now' (not a fixed past date) so each Tick's elapsed clears the 1s drift interval and the OU actually advances.
        // The RNG is re-seeded deterministically on Reset, so the diffusion sequence is independent of the absolute base.
        svc.Reset();
        var start = TimeHelper.NowUtc();
        var series = new List<double>(ticks);
        for (int i = 1; i <= ticks; i++)
        {
            svc.Tick(start.AddSeconds(i + 1));
            series.Add((double)svc.Get(1, Usd));
        }
        return series;
    }

    [Fact]
    public void Zero_estimate_is_byte_identical_to_no_pivot()
    {
        // A wired bankTarget that always returns 0 must reproduce the null-pivot OU sequence exactly (same RNG
        // stream, same target = seed) ⇒ the estimate pivot is inert at rest.
        var withNull = Drive(Build(null), 300);
        var withZero = Drive(Build(_ => 0.0), 300);
        Assert.Equal(withNull, withZero);
    }

    [Fact]
    public void Extreme_estimate_stays_in_hard_band_and_ou_still_diffuses()
    {
        // An absurd estimate (+1000% dev) must be clamped so the OU target sits at the INNER band and the
        // fundamental never escapes the hard band — while still diffusing (variance > 0, mean below the hard cap).
        var series = Drive(Build(_ => 10.0), 2000);
        double lo = (double)(Seed * (1m - Band)), hi = (double)(Seed * (1m + Band));
        Assert.All(series, v => Assert.InRange(v, lo, hi));

        double mean = series.Average();
        double variance = series.Sum(v => (v - mean) * (v - mean)) / series.Count;
        Assert.True(variance > 0.0, "OU must still diffuse (variance > 0) — target must not be parked at the hard band.");
        Assert.True(mean < hi, $"mean {mean} should sit below the hard cap {hi} (target clamped to the inner band).");
    }
}
