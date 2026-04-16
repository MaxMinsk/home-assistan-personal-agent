namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: policy fallback-ретрая c routed small model на default model.
/// Зачем: HAAG-048 требует сохранять стабильность при model/provider сбоях small-path без отключения adaptive routing.
/// Как: по routing decision и HTTP status определяет, нужно ли делать один retry на default model в рамках текущего run.
/// </summary>
public static class LlmRoutingFallbackPolicy
{
    public static bool CanRetryWithDefaultModel(
        LlmRoutingDecision routingDecision,
        string selectedModel,
        string defaultModel,
        int? providerStatusCode)
    {
        ArgumentNullException.ThrowIfNull(routingDecision);

        if (!routingDecision.IsApplied
            || !routingDecision.UsesSmallModelTarget)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(selectedModel)
            || string.IsNullOrWhiteSpace(defaultModel)
            || string.Equals(selectedModel.Trim(), defaultModel.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!providerStatusCode.HasValue)
        {
            return false;
        }

        var status = providerStatusCode.Value;
        if (status >= 500)
        {
            return true;
        }

        // Extension point: можно добавить provider-specific таблицу retryability,
        // если появятся детальные error-codes в body (например model_not_found/engine_overloaded/transient_upstream).
        return status is 400 or 404 or 408 or 409 or 429;
    }
}
