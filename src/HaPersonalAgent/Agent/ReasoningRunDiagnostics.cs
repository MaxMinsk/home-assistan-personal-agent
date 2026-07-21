using System.Threading;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: per-run агрегатор reasoning/thinking диагностики.
/// Зачем: разрозненные логи policy сложно интерпретировать; нужен единый итоговый снимок по одному agent run.
/// Как: request policy пишет счетчики в этот объект, а AgentRuntime в конце run логирует summary.
/// HPA-041: replay-счётчики удалены вместе с инертным ReasoningContentReplayChatClient — источник истины один,
/// это raw-JSON решение политики, поэтому противоречивых диагностик больше нет.
/// </summary>
public sealed class ReasoningRunDiagnostics
{
    private int _policyObservedRequests;
    private int _policyForcedDisablePatches;
    private int _policyForcedEnablePatches;
    private int _policyAutoSafetyDisablePatches;
    private int _policyReasoningReplayPatches;
    private int _policyNoPatchRequests;

    internal void RecordPolicyPatchDecision(LlmChatCompletionRequestPolicy.LlmRequestPatchKind patchKind)
    {
        Interlocked.Increment(ref _policyObservedRequests);

        switch (patchKind)
        {
            case LlmChatCompletionRequestPolicy.LlmRequestPatchKind.ForcedThinkingDisable:
                Interlocked.Increment(ref _policyForcedDisablePatches);
                return;
            case LlmChatCompletionRequestPolicy.LlmRequestPatchKind.ForcedThinkingEnable:
                Interlocked.Increment(ref _policyForcedEnablePatches);
                return;
            case LlmChatCompletionRequestPolicy.LlmRequestPatchKind.AutoToolStepSafetyDisable:
                Interlocked.Increment(ref _policyAutoSafetyDisablePatches);
                return;
            case LlmChatCompletionRequestPolicy.LlmRequestPatchKind.ReasoningContentReplayed:
                Interlocked.Increment(ref _policyReasoningReplayPatches);
                return;
            default:
                Interlocked.Increment(ref _policyNoPatchRequests);
                return;
        }
    }

    public ReasoningRunDiagnosticsSnapshot Snapshot() =>
        new(
            PolicyObservedRequests: Volatile.Read(ref _policyObservedRequests),
            PolicyForcedDisablePatches: Volatile.Read(ref _policyForcedDisablePatches),
            PolicyForcedEnablePatches: Volatile.Read(ref _policyForcedEnablePatches),
            PolicyAutoSafetyDisablePatches: Volatile.Read(ref _policyAutoSafetyDisablePatches),
            PolicyReasoningReplayPatches: Volatile.Read(ref _policyReasoningReplayPatches),
            PolicyNoPatchRequests: Volatile.Read(ref _policyNoPatchRequests));
}

/// <summary>
/// Что: immutable snapshot reasoning диагностики за один run.
/// Зачем: runtime должен логировать стабильное состояние счетчиков в конце выполнения.
/// Как: формируется из ReasoningRunDiagnostics.Snapshot и используется только для логов/диагностики.
/// </summary>
public sealed record ReasoningRunDiagnosticsSnapshot(
    int PolicyObservedRequests,
    int PolicyForcedDisablePatches,
    int PolicyForcedEnablePatches,
    int PolicyAutoSafetyDisablePatches,
    int PolicyReasoningReplayPatches,
    int PolicyNoPatchRequests)
{
    /// <summary>
    /// Thinking был снят на tool-шаге, потому что для assistant tool-call сообщения не нашлось захваченного
    /// reasoning_content (например, стриминговый шаг). Это ожидаемый предохранитель от 400, не сбой.
    /// </summary>
    public bool SafetyFallbackApplied => PolicyAutoSafetyDisablePatches > 0;

    /// <summary>
    /// Захваченный reasoning_content был вписан обратно в исходящий tool-шаг (HPA-041 follow-up),
    /// то есть модель продолжала думать во время работы с инструментами.
    /// </summary>
    public bool ReasoningReplayedToWire => PolicyReasoningReplayPatches > 0;
}
