namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: capability profile LLM provider для agent runtime.
/// Зачем: reasoning/tools поведение должно зависеть от возможностей provider, а не от разбросанных проверок `provider == moonshot`.
/// Как: resolver строит immutable record по LlmOptions, а planner использует flags для выбора execution plan.
/// </summary>
public sealed record LlmProviderCapabilities(
    string ProviderKey,
    bool SupportsTools,
    bool SupportsStreaming,
    bool SupportsReasoning,
    bool RequiresReasoningContentRoundTripForToolCalls,
    bool SupportsReasoningContentRoundTrip,
    bool SupportsExplicitThinkingEnable,
    LlmThinkingControlStyle ThinkingControlStyle);
