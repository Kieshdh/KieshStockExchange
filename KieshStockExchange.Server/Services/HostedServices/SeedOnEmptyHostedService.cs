using KieshStockExchange.Server.Services.SeedServices.Interfaces;

namespace KieshStockExchange.Server.Services.HostedServices;

/// <summary>
/// 7b — on boot, if Seed:AutoOnEmptyDb is true and the database is empty, seed it
/// from the embedded workbook before anything trades. Registered before the bot
/// loop's hosted service so the seed completes first (hosted services start in
/// registration order).
/// </summary>
public sealed class SeedOnEmptyHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<SeedOnEmptyHostedService> _logger;

    public SeedOnEmptyHostedService(IServiceProvider services, IConfiguration config,
        ILogger<SeedOnEmptyHostedService> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.GetValue("Seed:AutoOnEmptyDb", false))
            return;

        // IExcelSeedService depends on the singleton IDataBaseService; resolve
        // from the root provider since hosted services run outside a request scope.
        var seed = _services.GetRequiredService<IExcelSeedService>();
        if (!await seed.IsDatabaseEmptyAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Seed:AutoOnEmptyDb is on but the database is already populated; skipping.");
            return;
        }

        _logger.LogInformation("Seed:AutoOnEmptyDb true and database empty; seeding from embedded workbook.");
        var ran = await seed.SeedAllFromEmbeddedAsync(cancellationToken).ConfigureAwait(false);
        if (!ran)
            _logger.LogWarning("Auto-seed requested but the embedded workbook was not found.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
