namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: нормализованный ответ нашего runtime, отделенный от типов Microsoft Agent Framework.
/// Зачем: Telegram gateway и будущие adapters не должны зависеть от provider-specific response types.
/// Как: хранит текст ответа, correlation id и health snapshot, чтобы caller мог показать понятную ошибку при not configured.
/// </summary>
public sealed record AgentRuntimeResponse(
    string CorrelationId,
    bool IsConfigured,
    string Text,
    AgentRuntimeHealth Health,
    string? PersistedSummaryCandidate = null);
