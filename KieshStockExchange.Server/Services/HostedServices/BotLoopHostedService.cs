using KieshStockExchange.Services.BackgroundServices.Interfaces;
using Microsoft.Extensions.Hosting;

namespace KieshStockExchange.Server.Services.HostedServices;

// Phase 3 Step 5: drives the bot loop from server lifetime. Gated on
// Bots:AutoStart — defaults to false so the client still owns the bot loop until
// Step 7 deletes the client copies. Flipping the config flag to true and
// restarting the server is the operational toggle.
public sealed class BotLoopHostedService : IHostedService
{
    private readonly IAiTradeService _bots;
    private readonly IConfiguration _config;
    private readonly ILogger<BotLoopHostedService> _logger;

    public BotLoopHostedService(IAiTradeService bots, IConfiguration config,
        ILogger<BotLoopHostedService> logger)
    {
        _bots = bots;
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var autoStart = _config.GetValue("Bots:AutoStart", false);
        if (!autoStart)
        {
            _logger.LogInformation("BotLoopHostedService: Bots:AutoStart=false, leaving bot loop dormant.");
            return Task.CompletedTask;
        }
        _logger.LogInformation("BotLoopHostedService: starting bot loop (Bots:AutoStart=true).");
        return _bots.StartBotAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _bots.StopBotAsync();
}
