using System.Threading;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: per-run агрегатор reasoning/thinking диагностики.
/// Зачем: разрозненные логи policy/replay сложно интерпретировать; нужен единый итоговый снимок по одному agent run.
/// Как: policy и replay middleware пишут счетчики в этот объект, а AgentRuntime в конце run логирует summary.
/// </summary>
public sealed class ReasoningRunDiagnostics
{
    private int _policyObservedRequests;
    private int _policyForcedDisablePatches;
    private int _policyForcedEnablePatches;
    private int _policyAutoSafetyDisablePatches;
    private int _policyNoPatchRequests;

    private int _replayRequestsObserved;
    private int _replayRequestToolCallMessages;
    private int _replayRequestMissingToolCallReasoningMessages;
    private int _replayInjectedMessages;

    private int _replayResponsesObserved;
    private int _replayResponseToolCallMessages;
    private int _replayResponseMissingToolCallReasoningMessages;
    private int _replayCapturedMessages;
    private int _replayResponsesWithAssistantReasoning;

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
            default:
                Interlocked.Increment(ref _policyNoPatchRequests);
                return;
        }
    }

    public void RecordReplayRequest(
        int toolCallMessageCount,
        int missingToolCallReasoningMessageCount)
    {
        Interlocked.Increment(ref _replayRequestsObserved);
        Interlocked.Add(ref _replayRequestToolCallMessages, toolCallMessageCount);
        Interlocked.Add(ref _replayRequestMissingToolCallReasoningMessages, missingToolCallReasoningMessageCount);
    }

    public void RecordReplayInjection(int injectedMessageCount)
    {
        if (injectedMessageCount <= 0)
        {
            return;
        }

        Interlocked.Add(ref _replayInjectedMessages, injectedMessageCount);
    }

    public void RecordReplayResponse(
        int toolCallMessageCount,
        int missingToolCallReasoningMessageCount,
        int capturedMessageCount,
        bool hasAssistantReasoning)
    {
        Interlocked.Increment(ref _replayResponsesObserved);
        Interlocked.Add(ref _replayResponseToolCallMessages, toolCallMessageCount);
        Interlocked.Add(ref _replayResponseMissingToolCallReasoningMessages, missingToolCallReasoningMessageCount);
        Interlocked.Add(ref _replayCapturedMessages, capturedMessageCount);

        if (hasAssistantReasoning)
        {
            Interlocked.Increment(ref _replayResponsesWithAssistantReasoning);
        }
    }

    public ReasoningRunDiagnosticsSnapshot Snapshot() =>
        new(
            PolicyObservedRequests: Volatile.Read(ref _policyObservedRequests),
            PolicyForcedDisablePatches: Volatile.Read(ref _policyForcedDisablePatches),
            PolicyForcedEnablePatches: Volatile.Read(ref _policyForcedEnablePatches),
            PolicyAutoSafetyDisablePatches: Volatile.Read(ref _policyAutoSafetyDisablePatches),
            PolicyNoPatchRequests: Volatile.Read(ref _policyNoPatchRequests),
            ReplayRequestsObserved: Volatile.Read(ref _replayRequestsObserved),
            ReplayRequestToolCallMessages: Volatile.Read(ref _replayRequestToolCallMessages),
            ReplayRequestMissingToolCallReasoningMessages: Volatile.Read(ref _replayRequestMissingToolCallReasoningMessages),
            ReplayInjectedMessages: Volatile.Read(ref _replayInjectedMessages),
            ReplayResponsesObserved: Volatile.Read(ref _replayResponsesObserved),
            ReplayResponseToolCallMessages: Volatile.Read(ref _replayResponseToolCallMessages),
            ReplayResponseMissingToolCallReasoningMessages: Volatile.Read(ref _replayResponseMissingToolCallReasoningMessages),
            ReplayCapturedMessages: Volatile.Read(ref _replayCapturedMessages),
            ReplayResponsesWithAssistantReasoning: Volatile.Read(ref _replayResponsesWithAssistantReasoning));
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
    int PolicyNoPatchRequests,
    int ReplayRequestsObserved,
    int ReplayRequestToolCallMessages,
    int ReplayRequestMissingToolCallReasoningMessages,
    int ReplayInjectedMessages,
    int ReplayResponsesObserved,
    int ReplayResponseToolCallMessages,
    int ReplayResponseMissingToolCallReasoningMessages,
    int ReplayCapturedMessages,
    int ReplayResponsesWithAssistantReasoning)
{
    public bool ProviderReasoningObserved => ReplayResponsesWithAssistantReasoning > 0;

    public bool ReplayWasNeeded => ReplayRequestToolCallMessages > 0 || ReplayResponseToolCallMessages > 0;

    public bool SafetyFallbackApplied => PolicyAutoSafetyDisablePatches > 0;
}
