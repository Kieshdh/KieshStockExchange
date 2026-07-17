using SQLite;

namespace KieshStockExchange.Services.DataServices.Persistence;

/// <summary>
/// Per-user chart-drawings row (UP-STORE). Mirrors <see cref="CandleRow"/> conventions:
/// SQLite-PCL attributes for the schema shape, <c>Currency</c> stored as a string, and a
/// unique composite index that is the <c>ON CONFLICT</c> target for the upsert. The EF
/// config in <c>KseDbContext.OnModelCreating</c> is the real schema source-of-truth; these
/// attributes document intent and drive the Dapper mapping.
/// <para>
/// <c>Json</c> is the OPAQUE <c>{ "v":1, "drawings":[...] }</c> envelope — the server never
/// deserializes it.
/// </para>
/// </summary>
[Table("UserDrawings")]
public class UserDrawingRow
{
    [PrimaryKey, AutoIncrement]
    [Column("Id")] public int Id { get; set; }

    [Indexed(Name = "IX_UserDrawing_Key", Order = 1, Unique = true)]
    [Column("UserId")] public int UserId { get; set; }

    [Indexed(Name = "IX_UserDrawing_Key", Order = 2, Unique = true)]
    [Column("StockId")] public int StockId { get; set; }

    [Indexed(Name = "IX_UserDrawing_Key", Order = 3, Unique = true)]
    [Column("Currency")] public string Currency { get; set; } = "";

    [Column("Json")] public string Json { get; set; } = "";

    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
}
