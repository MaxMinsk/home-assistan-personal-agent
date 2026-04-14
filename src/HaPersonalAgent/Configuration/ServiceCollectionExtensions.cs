using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: DI-регистрация configuration слоя.
/// Зачем: остальной код должен зависеть от typed options и сервисов, а не читать IConfiguration напрямую в каждом классе.
/// Как: метод биндит секции конфигурации в options classes и регистрирует ConfigurationStatusProvider как singleton.
/// </summary>
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
