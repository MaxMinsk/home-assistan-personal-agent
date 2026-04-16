namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: executor-обертка для fallback policy между routed и default model.
/// Зачем: runtime orchestration не должен знать детали retryability; решение о fallback инкапсулируется в одном месте.
/// Как: использует LlmRoutingFallbackPolicy и возвращает fallback-контекст выполнения, если retry допустим.
/// </summary>
public sealed class AgentFallbackExecutor
{
    public bool TryCreateFallback(
        AgentExecutionDecision decision,
        int? providerStatusCode,
        out AgentFallbackContext fallbackContext)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (!LlmRoutingFallbackPolicy.CanRetryWithDefaultModel(
                decision.RoutingDecision,
                decision.SelectedModel,
                decision.DefaultModel,
                providerStatusCode))
        {
            fallbackContext = AgentFallbackContext.None;
            return false;
        }

        // Extension point: multi-tier fallback (small -> medium -> default) добавляется здесь
        // без изменения orchestration-кода в AgentRuntime.
        fallbackContext = new AgentFallbackContext(
            IsEnabled: true,
            FallbackModel: decision.DefaultModel);
        return true;
    }
}

/// <summary>
/// Что: immutable snapshot fallback-ветки.
/// Зачем: orchestration и telemetry читают одинаковый контракт о том, применился ли fallback и какая модель выбрана для retry.
/// Как: создается AgentFallbackExecutor и передается в run flow как value object.
/// </summary>
public sealed record AgentFallbackContext(
    bool IsEnabled,
    string? FallbackModel)
{
    public static AgentFallbackContext None { get; } = new(IsEnabled: false, FallbackModel: null);
}
