using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: DI-регистрация подсистемы автономных агентов.
/// Зачем: Program.cs должен подключать её одной строкой, а планировщик/исполнитель/UI получать зависимости через контейнер.
/// Как: репозиторий и сервис регистрируются как singleton — они не держат открытых соединений (SQLite-соединение открывается на операцию).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAutonomousAgents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<SqliteAutonomousAgentRepository>();
        services.AddSingleton<IAutonomousAgentRepository>(provider =>
            provider.GetRequiredService<SqliteAutonomousAgentRepository>());
        services.AddSingleton<AutonomousAgentService>();
        services.AddSingleton<AutonomousAgentCapsuleWriter>();
        services.AddSingleton<IAutonomousAgentRunner, AutonomousAgentRunner>();
        services.AddHostedService<AutonomousAgentScheduler>();

        return services;
    }
}
