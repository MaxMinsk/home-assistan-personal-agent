namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: provider agent-facing Home Assistant MCP tools.
/// Зачем: AgentRuntime должен получать готовый disposable tool set, не зная про HA auth, MCP transport и safety filtering.
/// Как: реализация открывает MCP session лениво и возвращает только tools, разрешенные текущей policy.
/// </summary>
public interface IHomeAssistantMcpAgentToolProvider
{
    Task<HomeAssistantMcpAgentToolSet> CreateReadOnlyToolSetAsync(CancellationToken cancellationToken);
}
