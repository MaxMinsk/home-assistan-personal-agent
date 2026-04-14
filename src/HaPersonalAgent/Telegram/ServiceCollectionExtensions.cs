using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: DI-регистрация Telegram gateway слоя.
/// Зачем: Program.cs должен подключать Telegram transport одной строкой, а handler и adapter получать зависимости через container.
/// Как: регистрирует factory/handler как singleton и добавляет TelegramBotGateway как hosted service для long polling.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ITelegramBotClientAdapterFactory, TelegramBotClientAdapterFactory>();
        services.AddSingleton<TelegramUpdateHandler>();
        services.AddHostedService<TelegramBotGateway>();

        return services;
    }
}
