using System.Text.Json;
using System.Text.Json.Serialization;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// JsonSerializerOptions used by ApiDataBaseService for every request/response. Mirrors
/// the server's AddJsonOptions setup (JsonStringEnumConverter, web defaults) so the wire
/// shape is symmetric. Single static instance — JsonSerializerOptions is thread-safe once
/// frozen and re-using one avoids the per-call reflection cache rebuild.
/// </summary>
internal static class ApiJsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
