using HaPersonalAgent.HomeAssistant;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: резолвер доступных Home Assistant MCP tools для текущего run.
/// Зачем: загрузка MCP tool set и graceful-degradation при недоступном провайдере не должна раздувать AgentRuntime orchestration.
/// Как: по execution profile возвращает read-only tool set или explicit unavailable result с диагностическим reason.
/// </summary>
public sealed class HomeAssistantMcpToolSetResolver
{
    private readonly IHomeAssistantMcpAgentToolProvider? _homeAssistantMcpToolProvider;
    private readonly ILogger<HomeAssistantMcpToolSetResolver> _logger;

    public HomeAssistantMcpToolSetResolver(
        IHomeAssistantMcpAgentToolProvider? homeAssistantMcpToolProvider,
        ILogger<HomeAssistantMcpToolSetResolver> logger)
    {
        _homeAssistantMcpToolProvider = homeAssistantMcpToolProvider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HomeAssistantMcpAgentToolSet> CreateAsync(
        LlmExecutionPlan executionPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);

        if (!executionPlan.UsesTools)
        {
            _logger.LogInformation(
                "Agent run profile {ExecutionProfile} disables all tools for this run.",
                executionPlan.Profile);

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.NotConfigured,
                $"Tools are disabled for {executionPlan.Profile} profile.");
        }

        if (_homeAssistantMcpToolProvider is null)
        {
            _logger.LogInformation(
                "Home Assistant MCP tool provider is not registered; agent run will continue without Home Assistant tools.");

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.NotConfigured,
                "Home Assistant MCP tool provider is not registered.");
        }

        return await _homeAssistantMcpToolProvider.CreateReadOnlyToolSetAsync(cancellationToken);
    }
}
