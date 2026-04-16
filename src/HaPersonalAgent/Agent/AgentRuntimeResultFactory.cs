using Microsoft.Agents.AI;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: фабрика нормализованных runtime-результатов и execution bucket resolution.
/// Зачем: чтобы AgentRuntime orchestration не держал в себе форматирование user-facing ошибок/ответов и mapping bucket логики.
/// Как: строит AgentRuntimeResponse для success/failure и вычисляет execution bucket на основе routing+plan.
/// </summary>
public static class AgentRuntimeResultFactory
{
    public static AgentRuntimeResponse CreateProviderFailureResponse(
        AgentContext context,
        AgentRuntimeHealth health,
        int? status)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(health);

        var statusText = status.HasValue
            ? $" HTTP {status.Value}"
            : string.Empty;

        return new AgentRuntimeResponse(
            context.CorrelationId,
            IsConfigured: false,
            $"Не смог получить ответ от LLM provider{statusText}. Запрос не сохранен в историю диалога. Повтори запрос позже; если проверяешь Home Assistant MCP, можно также выполнить /status.",
            health);
    }

    public static AgentRuntimeResponse CreateSuccessResponse(
        AgentContext context,
        AgentRuntimeHealth health,
        AgentResponse response,
        CompactionRunDiagnosticsSnapshot compactionSnapshot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(compactionSnapshot);

        var responseText = compactionSnapshot.SummarizationTriggered
            ? BuildSummarizationNotice(compactionSnapshot) + Environment.NewLine + Environment.NewLine + response.Text
            : response.Text;
        var persistedSummaryCandidate = string.IsNullOrWhiteSpace(compactionSnapshot.LatestSummaryText)
            ? null
            : compactionSnapshot.LatestSummaryText;

        return new AgentRuntimeResponse(
            context.CorrelationId,
            IsConfigured: true,
            responseText,
            health,
            persistedSummaryCandidate);
    }

    public static string ResolveExecutionBucket(
        LlmRoutingDecision routingDecision,
        LlmExecutionPlan executionPlan)
    {
        ArgumentNullException.ThrowIfNull(routingDecision);
        ArgumentNullException.ThrowIfNull(executionPlan);

        if (routingDecision.IsApplied)
        {
            return routingDecision.DecisionBucket;
        }

        // Extension point: когда добавим больше routing bucket'ов (например default+disabled или tool-heavy),
        // здесь можно вычислять bucket по фактическому executionPlan/profile, а не сводить всё к двум default веткам.
        return executionPlan.Profile == LlmExecutionProfile.DeepReasoning
            ? LlmRoutingDecision.DecisionBucketDefaultDeep
            : LlmRoutingDecision.DecisionBucketDefaultProviderDefault;
    }

    private static string BuildSummarizationNotice(CompactionRunDiagnosticsSnapshot snapshot) =>
        $"[context-summary] Чтобы удержать бюджет контекста, я сжал раннюю часть диалога ({snapshot.SummarizationRequests} summarize step).";
}
