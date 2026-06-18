using HaPersonalAgent.Confirmation;
using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Memory;

/// <summary>
/// What: DI registration for the Memory MCP client stack.
/// Why: Program.cs wires a second MCP client (alongside Home Assistant) so durable memory can reach Memory MCP.
/// How: registers a named HttpClient, the SDK connector, and the application-facing client as singletons
/// (they hold no open connection — each operation opens a short-lived session).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(ModelContextProtocolMemoryMcpConnector.HttpClientName);
        services.AddSingleton<ModelContextProtocolMemoryMcpConnector>();
        services.AddSingleton<IMemoryMcpConnector>(
            provider => provider.GetRequiredService<ModelContextProtocolMemoryMcpConnector>());
        services.AddSingleton<IMemoryMcpClient, MemoryMcpClient>();
        services.AddSingleton<MemoryMcpCapsuleMirror>();
        services.AddSingleton<IConfirmationActionExecutor, MemoryMcpSaveActionExecutor>();

        return services;
    }
}
