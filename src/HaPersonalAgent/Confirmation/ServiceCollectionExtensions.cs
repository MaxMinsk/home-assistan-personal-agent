using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: DI-регистрация generic confirmation слоя.
/// Зачем: Program.cs должен подключать approve/reject orchestration один раз, а конкретные домены добавлять только IConfirmationActionExecutor.
/// Как: регистрирует ConfirmationService как singleton; executors подхватываются через IEnumerable&lt;IConfirmationActionExecutor&gt;.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConfirmationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ConfirmationResultFormatter>();
        services.AddSingleton<IConfirmationService, ConfirmationService>();

        return services;
    }
}
