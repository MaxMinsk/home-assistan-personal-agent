using HaPersonalAgent.Confirmation;
using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: DI-регистрация Home Assistant integration слоя.
/// Зачем: Program.cs должен подключать MCP клиент одной строкой, а Telegram/workflows получать общий IHomeAssistantMcpClient.
/// Как: регистрирует named HttpClient, SDK connector, discovery client, agent tools provider и Home Assistant executor для generic confirmation layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHomeAssistantMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(ModelContextProtocolHomeAssistantMcpConnector.HttpClientName);
        services.AddSingleton<ModelContextProtocolHomeAssistantMcpConnector>();
        services.AddSingleton<IHomeAssistantMcpConnector>(
            services => services.GetRequiredService<ModelContextProtocolHomeAssistantMcpConnector>());
        services.AddSingleton<IHomeAssistantMcpToolConnector>(
            services => services.GetRequiredService<ModelContextProtocolHomeAssistantMcpConnector>());
        services.AddSingleton<IHomeAssistantMcpClient, HomeAssistantMcpClient>();
        services.AddSingleton<HomeAssistantMcpToolPolicy>();
        services.AddSingleton<IHomeAssistantMcpAgentToolProvider, HomeAssistantMcpAgentToolProvider>();
        services.AddSingleton<IConfirmationActionExecutor, HomeAssistantMcpActionExecutor>();

        return services;
    }
}
