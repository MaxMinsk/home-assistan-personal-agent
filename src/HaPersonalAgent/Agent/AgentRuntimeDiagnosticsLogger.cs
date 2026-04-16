using HaPersonalAgent.HomeAssistant;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: выделенный logger runtime-диагностики (start/finish/reasoning/compaction).
/// Зачем: структурные логи остаются единообразными, но orchestration-код не захламляется форматированием длинных logging шаблонов.
/// Как: принимает runtime logger и предоставляет методы журналирования по фазам run lifecycle.
/// </summary>
public sealed class AgentRuntimeDiagnosticsLogger
{
    private readonly ILogger<AgentRuntime> _logger;

    public AgentRuntimeDiagnosticsLogger(ILogger<AgentRuntime> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void LogRunStart(
        AgentContext context,
        AgentRuntimeHealth health,
        AgentExecutionDecision decision,
        HomeAssistantMcpAgentToolSet homeAssistantMcpTools)
    {
        _logger.LogInformation(
            "Agent run {CorrelationId} starting with provider {Provider}, default model {DefaultModel}, selected model {SelectedModel}, profile {ExecutionProfile}, provider profile {ProviderProfile}, thinking requested {RequestedThinkingMode}, thinking effective {EffectiveThinkingMode}, thinking reason {ThinkingReason}, router mode {RouterMode}, router applied {RouterApplied}, router model target {RouterModelTarget}, router reasoning target {RouterReasoningTarget}, router decision bucket {RouterDecisionBucket}, router reason {RouterReason}, history messages {HistoryMessageCount}, memory retrieval mode {MemoryRetrievalMode}, persisted summary present {PersistedSummaryPresent}, persisted summary length {PersistedSummaryLength}, retrieved memories {RetrievedMemoryCount}, retrieved memory text length {RetrievedMemoryLength}, messages since persisted summary {MessagesSincePersistedSummary}, persisted summary refresh requested {ShouldRefreshPersistedSummary}, persisted summary refresh reason {PersistedSummaryRefreshReason}, persisted summary refresh forced {ForcePersistedSummaryRefresh}, MCP status {McpStatus}, read-only MCP tools {ReadOnlyToolCount}, confirmation MCP tools {ConfirmationToolCount}.",
            context.CorrelationId,
            health.Provider,
            decision.DefaultModel,
            decision.SelectedModel,
            decision.SelectedPlan.Profile,
            decision.SelectedPlan.Capabilities.ProviderKey,
            decision.SelectedPlan.RequestedThinkingMode,
            decision.SelectedPlan.EffectiveThinkingMode,
            decision.SelectedPlan.Reason,
            decision.RoutingDecision.RouterMode,
            decision.RoutingDecision.IsApplied,
            decision.RoutingDecision.ModelTarget,
            decision.RoutingDecision.ReasoningTarget,
            decision.RoutingDecision.DecisionBucket,
            decision.RoutingDecision.Reason,
            context.ConversationMessages.Count,
            context.MemoryRetrievalMode,
            !string.IsNullOrWhiteSpace(context.PersistedSummary),
            context.PersistedSummary?.Length ?? 0,
            context.RetrievedMemoryCount,
            context.RetrievedMemoryContext?.Length ?? 0,
            context.MessagesSincePersistedSummary,
            context.ShouldRefreshPersistedSummary,
            context.PersistedSummaryRefreshReason,
            context.ForcePersistedSummaryRefresh,
            homeAssistantMcpTools.Status,
            homeAssistantMcpTools.ExposedToolCount,
            homeAssistantMcpTools.ConfirmationRequiredTools.Count);
    }

    public void LogRunCompleted(
        string correlationId,
        string responseText,
        string selectedModel,
        bool routerApplied,
        bool fallbackApplied,
        string executedBucket)
    {
        _logger.LogInformation(
            "Agent run {CorrelationId} completed with response length {ResponseLength}; selected model {SelectedModel}; router applied {RouterApplied}; fallback applied {FallbackApplied}; executed bucket {ExecutedBucket}.",
            correlationId,
            responseText.Length,
            selectedModel,
            routerApplied,
            fallbackApplied,
            executedBucket);
    }

    public void LogReasoningDiagnostics(
        string correlationId,
        LlmExecutionPlan executionPlan,
        ReasoningRunDiagnostics diagnostics,
        bool success)
    {
        var snapshot = diagnostics.Snapshot();

        _logger.LogInformation(
            "Agent run {CorrelationId} reasoning diagnostics: success {Success}, requested {RequestedThinkingMode}, effective {EffectiveThinkingMode}, patch pipeline enabled {PatchPipelineEnabled}, provider reasoning observed {ProviderReasoningObserved}, replay needed {ReplayNeeded}, safety fallback applied {SafetyFallbackApplied}; policy requests {PolicyRequests}, policy no-patch {PolicyNoPatch}, policy forced disable {PolicyForcedDisable}, policy forced enable {PolicyForcedEnable}, policy auto safety disable {PolicyAutoSafetyDisable}; replay requests {ReplayRequests}, replay request tool-call messages {ReplayRequestToolCalls}, replay request missing reasoning {ReplayRequestMissingReasoning}, replay injected {ReplayInjected}, replay responses {ReplayResponses}, replay response tool-call messages {ReplayResponseToolCalls}, replay response missing reasoning {ReplayResponseMissingReasoning}, replay captured {ReplayCaptured}.",
            correlationId,
            success,
            executionPlan.RequestedThinkingMode,
            executionPlan.EffectiveThinkingMode,
            executionPlan.ShouldPatchChatCompletionRequest,
            snapshot.ProviderReasoningObserved,
            snapshot.ReplayWasNeeded,
            snapshot.SafetyFallbackApplied,
            snapshot.PolicyObservedRequests,
            snapshot.PolicyNoPatchRequests,
            snapshot.PolicyForcedDisablePatches,
            snapshot.PolicyForcedEnablePatches,
            snapshot.PolicyAutoSafetyDisablePatches,
            snapshot.ReplayRequestsObserved,
            snapshot.ReplayRequestToolCallMessages,
            snapshot.ReplayRequestMissingToolCallReasoningMessages,
            snapshot.ReplayInjectedMessages,
            snapshot.ReplayResponsesObserved,
            snapshot.ReplayResponseToolCallMessages,
            snapshot.ReplayResponseMissingToolCallReasoningMessages,
            snapshot.ReplayCapturedMessages);
    }

    public void LogCompactionDiagnostics(
        string correlationId,
        CompactionRunDiagnostics diagnostics,
        bool success)
    {
        var snapshot = diagnostics.Snapshot();

        _logger.LogInformation(
            "Agent run {CorrelationId} compaction diagnostics: success {Success}, summarization requests {SummarizationRequests}, summarization responses {SummarizationResponses}, summarization triggered {SummarizationTriggered}, summary text length {SummaryTextLength}.",
            correlationId,
            success,
            snapshot.SummarizationRequests,
            snapshot.SummarizationResponses,
            snapshot.SummarizationTriggered,
            snapshot.LatestSummaryText?.Length ?? 0);
    }
}
