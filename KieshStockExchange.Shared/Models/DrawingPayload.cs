namespace KieshStockExchange.Models;

/// <summary>
/// Wire DTO for the opaque chart-drawings blob (UP-STORE). The server never
/// deserializes <see cref="Json"/> — it is the raw <c>{ "v":1, "drawings":[...] }</c>
/// envelope string produced by the client. A DTO (rather than a bare
/// <c>[FromBody] string</c>) avoids the System.Text.Json quirk where a bare string
/// body must be a JSON-quoted literal; <c>{ "json": "…" }</c> binds cleanly on both ends.
/// </summary>
public sealed record DrawingPayload(string Json);
