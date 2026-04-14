namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: низкоуровневый connector для invocable MCP tools.
/// Зачем: discovery может закрыть session сразу, а agent execution требует session, живущую до конца agent run.
/// Как: реализация на ModelContextProtocol SDK возвращает HomeAssistantMcpToolSession с McpClientTool instances.
/// </summary>
public interface IHomeAssistantMcpToolConnector
{
    Task<HomeAssistantMcpToolSession> ConnectToolsAsync(
        Uri endpoint,
        string accessToken,
        CancellationToken cancellationToken);
}
