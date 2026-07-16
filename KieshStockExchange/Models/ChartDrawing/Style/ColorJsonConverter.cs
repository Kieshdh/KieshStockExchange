namespace KieshStockExchange.Models.ChartDrawing.Style;

// Round-trips a Maui Color through JSON as an "#AARRGGBB" hex string. Needed because Color has no
// public parameterless ctor / settable props, so System.Text.Json can't (de)serialize it directly.
// Used only for persisting DrawStyle in DrawingObject (and the default-pen blob).
//
// Color is a REFERENCE type, so System.Text.Json short-circuits nulls before the converter runs
// (JsonConverter<T>.HandleNull is false by default): a null Color?/Fill is written and read as JSON
// null without Write/Read being called. That is why DrawStyle.Fill (nullable) round-trips null→null
// with just this single Color converter — a separate JsonConverter<Color?> would erase to the SAME
// JsonConverter<Color> and must NOT be registered. Write therefore only ever runs for a non-null
// value; the former "(value ?? Default.Color)" coalesce was dead for the null case and is removed.
public sealed class ColorJsonConverter : System.Text.Json.Serialization.JsonConverter<Color>
{
    public override Color Read(ref System.Text.Json.Utf8JsonReader reader, Type type,
        System.Text.Json.JsonSerializerOptions opts)
    {
        var s = reader.GetString();
        return string.IsNullOrEmpty(s) ? DrawStyle.Default.Color : Color.FromArgb(s);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Color value,
        System.Text.Json.JsonSerializerOptions opts)
        => writer.WriteStringValue(value.ToArgbHex(true));
}
