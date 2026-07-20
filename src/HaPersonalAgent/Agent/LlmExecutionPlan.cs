using HaPersonalAgent.Configuration;

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
    // HPA-037: глубина рассуждений и доступ к инструментам — независимые оси.
    // Раньше tools были только у ToolEnabled, из-за чего САМЫЙ думающий режим оставался единственным,
    // который не мог ни вспомнить память, ни посмотреть состояние дома — и потому склонял модель к выдумыванию.
    // PureChat и Summarization остаются без инструментов сознательно: это дешёвый маршрут и внутренняя суммаризация.
    public bool UsesTools =>
        Profile is LlmExecutionProfile.ToolEnabled or LlmExecutionProfile.DeepReasoning;

    public bool ShouldPatchChatCompletionRequest =>
        Capabilities.ThinkingControlStyle != LlmThinkingControlStyle.None
        && (
            EffectiveThinkingMode == LlmEffectiveThinkingMode.Disabled
            || (
                EffectiveThinkingMode == LlmEffectiveThinkingMode.Enabled
                && Capabilities.SupportsExplicitThinkingEnable
            )
            || (
                RequestedThinkingMode == LlmThinkingModes.Auto
                && UsesTools
                && Capabilities.RequiresReasoningContentRoundTripForToolCalls
            )
        );
}
