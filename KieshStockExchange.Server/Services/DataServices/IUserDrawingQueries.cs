using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// Server-only read/write surface for per-user chart drawings (UP-STORE). Deliberately
/// NOT on the shared <c>IDataBaseService</c> (which the MAUI client also implements) —
/// same pattern as <see cref="IBotMaintenanceQueries"/>: the one <c>PgDBService</c>
/// singleton is exposed under this extra interface via a cast in <c>Program.cs</c>, so it
/// never enters the client's <c>ApiDataBaseService</c>.
/// <para>
/// Upsert is <b>per row</b>, not batched: a single <c>ON CONFLICT DO UPDATE</c> statement
/// is atomic on its own, so no surrounding transaction is needed, and a per-row write keeps
/// one bad row from rolling back every user's coalesced writes in a flush tick.
/// </para>
/// </summary>
public interface IUserDrawingQueries
{
    Task<UserDrawingRow?> GetUserDrawingAsync(int userId, int stockId, string currency, CancellationToken ct = default);
    Task UpsertUserDrawingAsync(UserDrawingRow row, CancellationToken ct = default);
    Task DeleteUserDrawingAsync(int userId, int stockId, string currency, CancellationToken ct = default);
}
