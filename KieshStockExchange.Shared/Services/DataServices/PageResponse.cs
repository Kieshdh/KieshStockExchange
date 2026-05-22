namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// Wire shape for paged endpoints. The server-side methods on IDataBaseService return
/// (List&lt;T&gt; Items, int Total) tuples; tuples don't JSON-serialise cleanly, so the
/// controllers project to this record and the client deserialises back.
/// </summary>
public sealed record PageResponse<T>(IReadOnlyList<T> Items, int Total);
