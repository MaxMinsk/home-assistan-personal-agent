using HaPersonalAgent.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Search;

/// <summary>
/// Что: DI-регистрация слоя веб-поиска.
/// Зачем: провайдер подключается одной строкой, а смена провайдера не должна задевать каталог инструментов.
/// Как: именованный HttpClient с коротким таймаутом (фоновый запуск не должен висеть на поиске) + текущая реализация Brave.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWebSearch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(BraveWebSearchProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"{ApplicationInfo.Name.Replace(" ", "-", StringComparison.Ordinal)}/{ApplicationInfo.Version}");
        });

        services.AddSingleton<IWebSearchProvider, BraveWebSearchProvider>();

        return services;
    }
}
