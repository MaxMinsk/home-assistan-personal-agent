namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: agent-facing tool для диагностики Home Assistant MCP.
/// Зачем: вопросы пользователя про доступность MCP должны проходить через MAF/tool calling, а не через Telegram-specific shortcut.
/// Как: вызывает общий IHomeAssistantMcpClient discovery и возвращает безопасный HomeAssistantMcpDiscoveryResult без токенов.
/// </summary>
public sealed class HomeAssistantMcpStatusTool
{
    private readonly IHomeAssistantMcpClient _client;

    public HomeAssistantMcpStatusTool(IHomeAssistantMcpClient client)
    {
        _client = client;
    }

    public Task<HomeAssistantMcpDiscoveryResult> GetStatusAsync(CancellationToken cancellationToken) =>
        _client.DiscoverAsync(cancellationToken);
}
