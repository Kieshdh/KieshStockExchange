using Microsoft.Extensions.Hosting;

namespace KieshStockExchange.Server.Services.HostedServices;

// Phase 3 Step 1: stub only. Step 5 wires this to IAiTradeService.StartBotAsync /
// StopBotAsync once the bot services move server-side. Lives here from the start
// so DI registration in Program.cs is in place before the bot-loop dependency arrives.
public sealed class BotLoopHostedService : IHostedService
{
    private readonly IConfiguration _config;
    private readonly ILogger<BotLoopHostedService> _logger;

    public BotLoopHostedService(IConfiguration config, ILogger<BotLoopHostedService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var autoStart = _config.GetValue("Bots:AutoStart", true);
        _logger.LogInformation("BotLoopHostedService: AutoStart={AutoStart}. Bot loop wiring lands in Phase 3 Step 5.", autoStart);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
