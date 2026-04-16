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

    public bool UsesSmallModelTarget =>
        string.Equals(ModelTarget, ModelTargetSmallContextFast, StringComparison.Ordinal);
}
