using System.Text.Json;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;

namespace KieshStockExchange.Tests;

/// <summary>
/// UP-CORE — drawings persistence back-compat + the "v":1 envelope. Proves:
///  • a legacy bare-array blob (pre-UP-CORE, without the new fields) deserializes — and because STJ
///    leaves absent ctor params at default(T) (NOT the C# default), the load path re-applies the
///    non-zero trailing defaults (0.15 / Medium / 0.5) via DrawingBackCompat;
///  • the v1 envelope serializes to { "v":1, "drawings":[...] } and round-trips;
///  • the load-path root-token sniff reads BOTH a legacy array and a v1 envelope into an equal list.
/// </summary>
public sealed class DrawingSerializationTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        Converters = { new ColorJsonConverter() },
    };

    // A pre-UP-CORE blob: only the original DrawingObject/DrawStyle members, no new fields, bare array.
    private const string LegacyJson =
        "[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Kind\":2," +
        "\"T1\":\"2020-01-01T00:00:00\",\"P1\":10,\"T2\":\"2020-01-02T00:00:00\",\"P2\":20," +
        "\"Style\":{\"Color\":\"#4C9AFF\",\"Thickness\":1.5,\"Dash\":0,\"Arrow\":false,\"Ending\":0,\"Head\":0}," +
        "\"Points\":null}]";

    // Mirrors ChartViewModel.LoadDrawingsForSelected's root-token sniff (array = legacy, object = v1).
    private static List<DrawingObject>? Sniff(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.Deserialize<List<DrawingObject>>(Opts)
            : doc.RootElement.TryGetProperty("drawings", out var arr)
                ? arr.Deserialize<List<DrawingObject>>(Opts)
                : null;
    }

    [Fact]
    public void LegacyBlob_NormalizesToTrailingDefaults()
    {
        var list = JsonSerializer.Deserialize<List<DrawingObject>>(LegacyJson, Opts);
        Assert.NotNull(list);
        // System.Text.Json leaves an ABSENT ctor parameter at default(T), not its C# default value, so
        // the legacy blob deserializes FillOpacity/Size/Smoothing as 0/Small/0. The load path re-applies
        // the non-zero trailing defaults via DrawingBackCompat — the load-bearing back-compat step.
        var d = DrawingBackCompat.ApplyLegacyTrailingDefaults(Assert.Single(list!));

        // Original fields survive.
        Assert.Equal(DrawTool.Trend, d.Kind);
        Assert.Equal(10m, d.P1);
        Assert.Equal(20m, d.P2);

        // Non-zero trailing DrawStyle defaults re-applied.
        Assert.Null(d.Style.Fill);
        Assert.Equal(0.15f, d.Style.FillOpacity, 4);
        Assert.Equal(SizeKind.Medium, d.Style.Size);

        // DrawingObject trailing defaults (default(T) ones STJ already gets right; Smoothing re-applied).
        Assert.Null(d.Text);
        Assert.Equal(0m, d.P3);
        Assert.Equal(0m, d.Qty);
        Assert.False(d.Locked);
        Assert.Equal(0.5f, d.Smoothing, 4);
        Assert.Equal(0, d.Direction);
    }

    [Fact]
    public void Envelope_SerializesWithLowercaseKeys()
    {
        var env = new DrawingEnvelope(1, new List<DrawingObject>());
        var json = JsonSerializer.Serialize(env, Opts);

        Assert.Contains("\"v\":1", json);
        Assert.Contains("\"drawings\":", json);
    }

    [Fact]
    public void Sniff_ReadsBothLegacyArrayAndV1Envelope_ToEqualList()
    {
        var drawing = new DrawingObject(
            Guid.Parse("22222222-2222-2222-2222-222222222222"), DrawTool.HLine,
            DateTime.UnixEpoch, 42m, DateTime.UnixEpoch, 0m, DrawStyle.Default);
        var list = new List<DrawingObject> { drawing };

        var bareArray = JsonSerializer.Serialize(list, Opts);                       // legacy shape
        var envelope = JsonSerializer.Serialize(new DrawingEnvelope(1, list), Opts); // v1 shape

        var fromArray = Sniff(bareArray);
        var fromEnvelope = Sniff(envelope);

        Assert.NotNull(fromArray);
        Assert.NotNull(fromEnvelope);
        // Points is null on both, so record-struct structural equality is reliable here.
        Assert.Equal(fromArray!, fromEnvelope!);
        Assert.Equal(drawing, Assert.Single(fromEnvelope!));
    }
}
