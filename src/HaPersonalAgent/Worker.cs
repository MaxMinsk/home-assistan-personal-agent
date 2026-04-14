using HaPersonalAgent.Configuration;

namespace HaPersonalAgent;

public class Worker : BackgroundService
{
    private readonly ConfigurationStatusProvider _configurationStatusProvider;
    private readonly ILogger<Worker> _logger;

    public Worker(
        ILogger<Worker> logger,
        ConfigurationStatusProvider configurationStatusProvider)
    {
        _logger = logger;
        _configurationStatusProvider = configurationStatusProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{ApplicationName} starting with configuration {@ConfigurationStatus}",
            ApplicationInfo.Name,
            _configurationStatusProvider.Create());

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Worker heartbeat at: {Time}", DateTimeOffset.UtcNow);
            }

            await Task.Delay(1000, stoppingToken);
        }
    }
}
