namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: фактический reasoning/thinking режим, выбранный для конкретного LLM request.
/// Зачем: requested config может быть `auto`, а runtime должен логировать и применять конкретное поведение без provider-specific деталей в transport layer.
/// Как: planner возвращает Disabled/Enabled, когда можно явно управлять request body, или ProviderDefault, когда provider сам выбирает режим.
/// </summary>
public enum LlmEffectiveThinkingMode
{
    ProviderDefault,
    Disabled,
    Enabled,
}
