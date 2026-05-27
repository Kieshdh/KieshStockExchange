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

        var options = new DbContextOptionsBuilder<KseDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new KseDbContext(options);
    }
}
