using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

// UP-STORE — per-user chart-drawings read/write path. Mirrors the Candle exemplar in
// PgDBService.Misc.cs (quoted-identifier column list, ON CONFLICT upsert), but upserts one
// row at a time: a single ON CONFLICT statement is atomic, and per-row writes isolate a
// poison row from every other user's coalesced writes during a flush tick.
public sealed partial class PgDBService : IUserDrawingQueries
{
    private const string UserDrawingCols = "\"Id\",\"UserId\",\"StockId\",\"Currency\",\"Json\",\"UpdatedAt\"";

    private const string UpsertUserDrawingSql = @"
        INSERT INTO ""UserDrawings"" (""UserId"",""StockId"",""Currency"",""Json"",""UpdatedAt"")
        VALUES (@UserId,@StockId,@Currency,@Json,@UpdatedAt)
        ON CONFLICT (""UserId"",""StockId"",""Currency"") DO UPDATE SET
          ""Json"" = EXCLUDED.""Json"",
          ""UpdatedAt"" = EXCLUDED.""UpdatedAt""";

    public async Task<UserDrawingRow?> GetUserDrawingAsync(
        int userId, int stockId, string currency, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<UserDrawingRow>(
            $@"SELECT {UserDrawingCols} FROM ""UserDrawings""
               WHERE ""UserId"" = @userId AND ""StockId"" = @stockId AND ""Currency"" = @currency",
            new { userId, stockId, currency });
    }

    public async Task UpsertUserDrawingAsync(UserDrawingRow row, CancellationToken ct = default)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(UpsertUserDrawingSql, row);
    }

    public async Task DeleteUserDrawingAsync(
        int userId, int stockId, string currency, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(
            @"DELETE FROM ""UserDrawings""
              WHERE ""UserId"" = @userId AND ""StockId"" = @stockId AND ""Currency"" = @currency",
            new { userId, stockId, currency });
    }
}
