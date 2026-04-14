using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: DI-регистрация storage слоя.
/// Зачем: consumers должны получать SQLite factory и repository через DI, чтобы не создавать подключения вручную.
/// Как: регистрирует SqliteConnectionFactory и AgentStateRepository как singleton, потому что они не держат открытое соединение.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<AgentStateRepository>();

        return services;
    }
}
