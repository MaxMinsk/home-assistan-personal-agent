using HaPersonalAgent.Confirmation;
using Microsoft.Extensions.DependencyInjection;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: DI-регистрация transport-agnostic dialogue слоя.
/// Зачем: Program.cs должен подключать общую диалоговую логику отдельно от Telegram adapter.
/// Как: регистрирует DialogueService как singleton, потому что он не держит открытых соединений и использует thread-safe repository/runtime зависимости.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDialogueServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<BoundedChatHistoryProvider>();
        services.AddSingleton<ProjectCapsuleService>();
        services.AddSingleton<IConfirmationActionExecutor, ProjectCapsuleUpsertActionExecutor>();
        services.AddSingleton<DialogueService>();

        return services;
    }
}
