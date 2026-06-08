using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KieshStockExchange.Server.Data;

/// <summary>
/// 7c — design-time factory so `dotnet ef migrations add` / `database update`
/// can construct the context without booting the whole app. Reads the same
/// connection string env var the runtime factory uses; falls back to local dev.
/// </summary>
public sealed class KseDbContextFactory : IDesignTimeDbContextFactory<KseDbContext>
{
    public KseDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("KSE_DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=kse;Username=kse;Password=kse-dev";

        // Design-time only (migrations). Generous command timeout so a data-migration UPDATE over
        // a large dev Orders table (millions of soak rows) doesn't hit the 30s default. Prod
        // reseeds an empty DB, so this never matters there; the runtime uses Dapper, not this.
        var options = new DbContextOptionsBuilder<KseDbContext>()
            .UseNpgsql(connectionString, o => o.CommandTimeout(600))
            .Options;
        return new KseDbContext(options);
    }
}
