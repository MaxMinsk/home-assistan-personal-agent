using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName));

        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName));

        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName));

        services.AddOptions<HomeAssistantOptions>()
            .Bind(configuration.GetSection(HomeAssistantOptions.SectionName));

        services.AddSingleton<ConfigurationStatusProvider>();

        return services;
    }
}
