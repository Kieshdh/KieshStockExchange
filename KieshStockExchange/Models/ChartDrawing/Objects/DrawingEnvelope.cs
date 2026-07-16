using System.Text.Json.Serialization;

namespace KieshStockExchange.Models.ChartDrawing.Objects;

// UP-CORE: the versioned persistence envelope for a stock's drawings list. The serialized shape is
// { "v": 1, "drawings": [ ... ] } — this "v":1 schema is the client↔server wire contract UP-STORE
// stores as an opaque blob. The load path tolerates a legacy bare-array blob (no envelope) and
// migrates it up to v1 on next save. The [JsonPropertyName] attributes pin the exact lowercase keys
// without touching the shared JsonSerializerOptions' naming policy (which must stay default so every
// DrawStyle/DrawingObject property keeps its back-compat casing).
public sealed record DrawingEnvelope(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("drawings")] List<DrawingObject> Drawings);
