namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: результат единого routing-решения перед LLM вызовом.
/// Зачем: HAAG-048 объединяет выбор модели и reasoning режима в детерминированный decision-объект с прозрачной диагностикой.
/// Как: LlmExecutionRouter заполняет record на основе message/context/features и режима router (off/shadow/enforced).
/// </summary>
public sealed record LlmRoutingDecision(
    string RouterMode,
    bool IsApplied,
    string ModelTarget,
    string SelectedModel,
    string ReasoningTarget,
    string? ThinkingModeOverride,
    string DecisionBucket,
    string IntentClass,
    string ContextProfile,
    string? ContextProfileBlockerReason,
    string Reason,
    int EstimatedInputChars,
    int HistoryMessageCount)
{
    public const string ModelTargetSmallContextFast = "small_context_fast";
    public const string ModelTargetDefault = "default_model";

    public const string ReasoningTargetDisabled = "disabled";
    public const string ReasoningTargetProviderDefault = "provider-default";
    public const string ReasoningTargetDeep = "deep";

    public const string DecisionBucketSmallDisabled = "small+disabled";
    public const string DecisionBucketDefaultProviderDefault = "default+provider-default";
    public const string DecisionBucketDefaultDeep = "default+deep";

    public const string IntentClassSimpleChat = "simple_chat";
    public const string IntentClassComplexAnalysis = "complex_analysis";
    public const string IntentClassToolHeavy = "tool_heavy";
    public const string IntentClassDeepReasoning = "deep_reasoning";

    public const string ContextProfileDefaultFull = "default_full";
    public const string ContextProfileSimplePacked = "simple_packed";

    public bool UsesSmallModelTarget =>
        string.Equals(ModelTarget, ModelTargetSmallContextFast, StringComparison.Ordinal);
}
