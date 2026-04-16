using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: DI-регистрация agent runtime слоя.
/// Зачем: Program.cs должен подключать MAF spike одной строкой, а остальные компоненты получать IAgentRuntime через DI.
/// Как: регистрирует status tool и IAgentRuntime как singleton, а runtime создает per-run MAF agent с актуальными MCP tools.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<AgentStatusTool>();
        services.AddSingleton<LlmRoutingTelemetry>();
        services.AddSingleton<LlmProviderCapabilitiesResolver>();
        services.AddSingleton<LlmRoutingContextProfileBuilder>();
        services.AddSingleton<LlmExecutionRouter>();
        services.AddSingleton<LlmExecutionPlanner>();
        services.AddSingleton<AgentExecutionResolver>();
        services.AddSingleton<AgentFallbackExecutor>();
        services.AddSingleton<HomeAssistantMcpToolSetResolver>();
        services.AddSingleton<AgentRuntimeDiagnosticsLogger>();
        services.AddSingleton<AgentToolCatalog>();
        services.AddSingleton<AgentCompactionPipelineFactory>();
        services.AddSingleton<AgentMafFactory>();
        services.AddSingleton<AgentRunner>();
        services.AddSingleton<IAgentRuntime, AgentRuntime>();

        return services;
    }
}
