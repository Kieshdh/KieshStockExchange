using System.Text.Json;
using KieshStockExchange.Models.ChartDrawing.Style;

namespace KieshStockExchange.Tests;

/// <summary>
/// UP-CORE — ColorJsonConverter round-trip. Proves the two properties the "null→blue" fix hinges on:
/// a null nullable colour (DrawStyle.Fill) round-trips as null (System.Text.Json short-circuits null
/// for the reference-type Color converter, so the buggy coalesce never ran), and every non-null
/// "#AARRGGBB" value is preserved byte-identically.
/// </summary>
public sealed class ColorJsonConverterTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        Converters = { new ColorJsonConverter() },
    };

    [Fact]
    public void NonNullColor_RoundTripsHexUnchanged()
    {
        var s = new DrawStyle(Color.FromArgb("#4C9AFF"), 1.5f, DashKind.Solid);
        var back = JsonSerializer.Deserialize<DrawStyle>(JsonSerializer.Serialize(s, Opts), Opts);

        Assert.Equal(s.Color.ToArgbHex(true), back.Color.ToArgbHex(true));
    }

    [Fact]
    public void NullFill_RoundTripsAsNull()
    {
        var s = DrawStyle.Default with { Fill = null };
        var back = JsonSerializer.Deserialize<DrawStyle>(JsonSerializer.Serialize(s, Opts), Opts);

        Assert.Null(back.Fill);
    }

    [Fact]
    public void NonNullFill_PreservesHex()
    {
        var s = DrawStyle.Default with { Fill = Color.FromArgb("#8012AB34") };
        var back = JsonSerializer.Deserialize<DrawStyle>(JsonSerializer.Serialize(s, Opts), Opts);

        Assert.NotNull(back.Fill);
        Assert.Equal("#8012AB34", back.Fill!.ToArgbHex(true));
    }
}
