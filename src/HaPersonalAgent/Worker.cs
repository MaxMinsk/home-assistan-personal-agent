using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;

namespace HaPersonalAgent;

/// <summary>
/// Что: фоновый сервис, который удерживает Generic Host живым в add-on контейнере.
/// Зачем: пока Telegram и MCP background services еще не подключены, процессу нужен один hosted service, иначе ему нечего выполнять.
/// Как: при старте пишет безопасный статус конфигурации без секретов и затем ждет отмены токена остановки без heartbeat-спама.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ConfigurationStatusProvider _configurationStatusProvider;
    private readonly IMemoryMcpClient _memoryMcpClient;
    private readonly ILogger<Worker> _logger;

    public Worker(
        ILogger<Worker> logger,
        ConfigurationStatusProvider configurationStatusProvider,
        IMemoryMcpClient memoryMcpClient)
    {
        _logger = logger;
        _configurationStatusProvider = configurationStatusProvider;
        _memoryMcpClient = memoryMcpClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{ApplicationName} starting with configuration {@ConfigurationStatus}",
            ApplicationInfo.Name,
            _configurationStatusProvider.Create());

        var memoryHealth = await _memoryMcpClient.DiscoverAsync(stoppingToken);
        _logger.LogInformation(
            "Memory MCP health check: status {Status}, endpoint {Endpoint}, server version {ServerVersion}, tools {ToolCount}. {Reason}",
            memoryHealth.Status,
            string.IsNullOrEmpty(memoryHealth.EndpointUrl) ? "(none)" : memoryHealth.EndpointUrl,
            memoryHealth.ServerVersion ?? "n/a",
            memoryHealth.ToolCount,
            memoryHealth.Reason ?? string.Empty);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
