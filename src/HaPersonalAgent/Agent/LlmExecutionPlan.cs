namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: resolved execution strategy для одного LLM вызова.
/// Зачем: runtime должен явно знать, включены ли tools, какой reasoning режим применен и почему принято такое решение.
/// Как: LlmExecutionPlanner собирает plan из provider capabilities, requested thinking mode и LlmExecutionProfile.
/// </summary>
public sealed record LlmExecutionPlan(
    LlmExecutionProfile Profile,
    LlmProviderCapabilities Capabilities,
    string RequestedThinkingMode,
    LlmEffectiveThinkingMode EffectiveThinkingMode,
    string Reason)
{
    public bool UsesTools => Profile == LlmExecutionProfile.ToolEnabled;

    public bool ShouldPatchChatCompletionRequest =>
        Capabilities.ThinkingControlStyle != LlmThinkingControlStyle.None
        && (EffectiveThinkingMode == LlmEffectiveThinkingMode.Disabled
            || EffectiveThinkingMode == LlmEffectiveThinkingMode.Enabled
                && Capabilities.SupportsExplicitThinkingEnable);
}
