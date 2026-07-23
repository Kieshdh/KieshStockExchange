using System.Collections.Generic;
using System.Linq;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Moq;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §F1 sector-driven + size-derived StockProfile. The gating test is OFF==legacy byte-identical (the model
/// must be a no-op unless explicitly enabled); the rest lock the intended structure — size legibility
/// (big-cap calmer + higher-volume), the volume≠move axis, sector news skew, and determinism.
/// </summary>
public class StockProfileSectorSizeTests
{
    // ── The hard gate: model OFF reproduces the legacy id-hash path EXACTLY (both new knobs = 1.0). ──
    [Fact]
    public void SectorSizeOff_IsByteIdenticalToLegacy()
    {
        var legacy   = new StockProfileService(enabled: true);
        var offModel = new StockProfileService(enabled: true, sectorSizeModel: false, stocks: null!, sectors: null!);

        for (int id = 1; id <= 40; id++)
        {
            Assert.Equal(legacy.Get(id), offModel.Get(id));      // record-struct value equality over all 5 fields
            Assert.Equal(1m, legacy.Get(id).VolumeMult);         // the 2 new knobs are inert on the legacy path
            Assert.Equal(1m, legacy.Get(id).NewsFreqMult);
        }
        Assert.False(legacy.SectorSizeActive);
        Assert.False(offModel.SectorSizeActive);
    }

    [Fact]
    public void Disabled_ReturnsNeutral_EvenWithModelRequested()
    {
        var (stocks, map) = Build((1, "Semiconductors", 100, 1m));
        var svc = new StockProfileService(enabled: false, sectorSizeModel: true, stocks, map);
        var p = svc.Get(1);
        Assert.Equal(1m, p.SentimentAmplitudeMult);
        Assert.Equal(1m, p.FundamentalSigmaMult);
        Assert.Equal(1m, p.OverheatCapMult);
        Assert.Equal(1m, p.VolumeMult);
        Assert.Equal(1m, p.NewsFreqMult);
        Assert.False(svc.SectorSizeActive);
    }

    [Fact]
    public void SectorSizeActive_TrueOnlyWhenEnabledOptedInAndSectored()
    {
        var (sectored, sMap) = Build((1, "Semiconductors", 100, 1m));
        var (blank, bMap)    = Build((1, "", 100, 1m));

        Assert.True(new StockProfileService(true, true, sectored, sMap).SectorSizeActive);
        Assert.False(new StockProfileService(true, false, sectored, sMap).SectorSizeActive); // opted out
        Assert.False(new StockProfileService(false, true, sectored, sMap).SectorSizeActive); // master off
        Assert.False(new StockProfileService(true, true, blank, bMap).SectorSizeActive);     // no real sectors
    }

    // ── Size legibility + the volume≠move axis: within one sector, big-cap is CALMER but HIGHER-volume. ──
    [Fact]
    public void BigCap_IsCalmer_ButHigherVolume_ThanSmallCap_SameSector()
    {
        var (stocks, map) = Build(
            (1, "Semiconductors", 1_000_000, 100m),  // big marketcap
            (2, "Semiconductors", 1_000, 1m));       // small marketcap
        var svc = new StockProfileService(true, true, stocks, map);
        var big = svc.Get(1);
        var small = svc.Get(2);

        Assert.True(big.SentimentAmplitudeMult < small.SentimentAmplitudeMult); // big-cap swings LESS
        Assert.True(big.OverheatCapMult        < small.OverheatCapMult);        // and is leashed tighter
        Assert.True(big.VolumeMult             > small.VolumeMult);             // yet churns MORE volume
    }

    [Fact]
    public void TechSector_IsNewsier_ThanStaples()
    {
        var (stocks, map) = Build(
            (1, "Semiconductors", 1_000_000, 100m),
            (2, "Consumer Staples", 1_000, 1m));
        var svc = new StockProfileService(true, true, stocks, map);
        Assert.True(svc.Get(1).NewsFreqMult > svc.Get(2).NewsFreqMult);
    }

    [Fact]
    public void SectorSizeModel_IsDeterministic_AcrossConstructions()
    {
        (int, string, int, decimal)[] rows =
        {
            (1, "Semiconductors", 1_000, 10m),
            (2, "Financials", 500, 5m),
            (3, "Consumer Staples", 2_000, 2m),
        };
        var (s1, m1) = Build(rows);
        var (s2, m2) = Build(rows);
        var a = new StockProfileService(true, true, s1, m1);
        var b = new StockProfileService(true, true, s2, m2);
        for (int id = 1; id <= 3; id++)
            Assert.Equal(a.Get(id), b.Get(id));
    }

    private static (IStockService stocks, SectorMap map) Build(params (int id, string sector, int shares, decimal seed)[] rows)
    {
        var list = rows.Select(r => new Stock
        {
            StockId = r.id, Symbol = $"S{r.id}", CompanyName = $"C{r.id}",
            Sector = r.sector, SharesOutstanding = r.shares,
        }).ToList();
        var byId = list.ToDictionary(s => s.StockId);
        var seedById = rows.ToDictionary(r => r.id, r => r.seed);

        var mock = new Mock<IStockService>();
        mock.SetupGet(s => s.All).Returns(list);
        mock.SetupGet(s => s.ById).Returns(byId);
        mock.Setup(s => s.GetListings(It.IsAny<int>()))
            .Returns((int id) => (IReadOnlyList<StockListing>)new List<StockListing>
            {
                new StockListing
                {
                    StockId = id, Currency = "USD", IsPrimary = true,
                    SeedPrice = seedById.TryGetValue(id, out var sp) ? sp : 0m,
                },
            });
        return (mock.Object, new SectorMap(mock.Object));
    }
}
