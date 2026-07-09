using System.Collections.Generic;
using System.Linq;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §sector: locks the Sector enum ↔ canonical string mapping (mirrors Tools/Config.py SECTORS) and the
/// SectorMap active-list ordinal ordering (the stable RNG-walk key the BankEstimate re-rating depends on).
/// </summary>
public class SectorMapTests
{
    // Byte-identical to Config.SECTORS — the fixed ordinal order (index = active-list ordinal when all present).
    private static readonly string[] CanonicalSectors =
    {
        "Semiconductors",
        "Software & IT",
        "Communication & Internet",
        "Consumer Discretionary",
        "Consumer Staples",
        "Health Care",
        "Financials",
        "Energy & Industrials",
    };

    [Fact]
    public void Parse_RoundTrips_All8CanonicalNames()
    {
        foreach (var name in CanonicalSectors)
        {
            var sec = SectorInfo.Parse(name);
            Assert.NotEqual(Sector.Unknown, sec);
            Assert.Equal(name, SectorInfo.DisplayName(sec));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Tech")]
    [InlineData("Semiconductor")] // near-miss, not the exact canonical string
    public void Parse_JunkOrEmpty_IsUnknown(string? junk)
        => Assert.Equal(Sector.Unknown, SectorInfo.Parse(junk));

    [Fact]
    public void Parse_IsCaseInsensitiveAndTrims()
        => Assert.Equal(Sector.SoftwareIT, SectorInfo.Parse("  software & it "));

    [Fact]
    public void EnumOrder_MatchesConfigSectorsOrder()
    {
        // Enum values after Unknown, in declaration order, must display as Config.SECTORS in the same order.
        var enumOrder = System.Enum.GetValues<Sector>()
            .Where(s => s != Sector.Unknown)
            .Select(SectorInfo.DisplayName)
            .ToArray();
        Assert.Equal(CanonicalSectors, enumOrder);
    }

    [Fact]
    public void SectorMap_OrdinalFollowsEnumOrder_RegardlessOfStockOrder()
    {
        // Scrambled insertion + only a subset of sectors present ⇒ ordinal is the enum-order index over the present set.
        var stocks = BuildStocks(
            (10, "Financials"),
            (20, "Semiconductors"),
            (30, "Software & IT"),
            (40, "Financials"));
        var map = new SectorMap(stocks);

        Assert.True(map.HasRealSectors);
        Assert.Equal(3, map.SectorCount); // Semiconductors, Software & IT, Financials

        Assert.Equal(0, map.OrdinalOf(20)); // Semiconductors first in enum order
        Assert.Equal(1, map.OrdinalOf(30)); // Software & IT
        Assert.Equal(2, map.OrdinalOf(10)); // Financials
        Assert.Equal(2, map.OrdinalOf(40));

        Assert.Equal(Sector.Semiconductors, map.SectorOf(20));
        // StockIdsInSector returns ascending ids for the Financials ordinal.
        Assert.Equal(new[] { 10, 40 }, map.StockIdsInSector(2).ToArray());
    }

    [Fact]
    public void SectorMap_NoRealSectors_FallsBack()
    {
        var stocks = BuildStocks((1, ""), (2, ""), (3, "  "));
        var map = new SectorMap(stocks);

        Assert.False(map.HasRealSectors);
        Assert.Equal(0, map.SectorCount);
        Assert.Equal(-1, map.OrdinalOf(1));
        Assert.Equal(Sector.Unknown, map.SectorOf(1));
        Assert.Empty(map.StockIdsInSector(0));
    }

    private static IStockService BuildStocks(params (int Id, string Sector)[] rows)
    {
        var list = rows.Select(r => new Stock { StockId = r.Id, Symbol = $"S{r.Id}", CompanyName = $"C{r.Id}", Sector = r.Sector }).ToList();
        var byId = list.ToDictionary(s => s.StockId);
        var mock = new Mock<IStockService>();
        mock.SetupGet(s => s.All).Returns(list);
        mock.SetupGet(s => s.ById).Returns(byId);
        return mock.Object;
    }
}
