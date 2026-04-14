namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: application-facing клиент Home Assistant MCP.
/// Зачем: Telegram, будущий Web UI и workflows должны получать MCP health/discovery через общий контракт, а не напрямую через SDK.
/// Как: DiscoverAsync лениво подключается к MCP endpoint только по запросу и возвращает безопасный статус вместо падения приложения.
/// </summary>
public interface IHomeAssistantMcpClient
{
    Task<HomeAssistantMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);
}
