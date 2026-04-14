namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: низкоуровневый connector к MCP transport.
/// Зачем: application health logic можно тестировать без реального Home Assistant и без сетевого MCP handshake.
/// Как: реализация на ModelContextProtocol SDK получает tools/prompts, а fake-реализации в тестах имитируют ошибки HTTP/SDK.
/// </summary>
public interface IHomeAssistantMcpConnector
{
    Task<HomeAssistantMcpDiscovery> DiscoverAsync(
        Uri endpoint,
        string accessToken,
        CancellationToken cancellationToken);
}
