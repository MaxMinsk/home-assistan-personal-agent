using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        services.AddSingleton<SqliteConversationMemoryStore>();
        services.AddSingleton<IConversationMemoryStore>(provider =>
        {
            var sqliteStore = provider.GetRequiredService<SqliteConversationMemoryStore>();
            var memoryOptions = provider.GetRequiredService<IOptions<MemoryMcpOptions>>().Value;

            // HPA-004: route durable memory to Memory MCP only when explicitly selected and configured;
            // otherwise keep the local SQLite store (default, unchanged behavior).
            if (string.Equals(memoryOptions.StoreType, MemoryMcpOptions.StoreTypeMemoryMcp, StringComparison.OrdinalIgnoreCase)
                && memoryOptions.IsConfigured)
            {
                return new MemoryMcpConversationMemoryStore(
                    sqliteStore,
                    provider.GetRequiredService<IMemoryMcpClient>(),
                    provider.GetRequiredService<IOptions<MemoryMcpOptions>>(),
                    provider.GetRequiredService<ILogger<MemoryMcpConversationMemoryStore>>());
            }

            return sqliteStore;
        });

        return services;
    }
}
