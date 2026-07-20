namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: запись об одном запуске автономного агента (лента запусков).
/// Зачем: пользователь читает историю сводок в UI, а Telegram-петля (HPA-032) сопоставляет свой reply с конкретным запуском через DeliveredMessageId.
/// Как: живёт в локальном SQLite как операционный журнал; в Memory MCP уходит только курируемая капсула (HPA-031), а не каждый запуск.
/// </summary>
public sealed record AutonomousAgentRun(
    string Id,
    string AgentId,
    AutonomousAgentRunStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string? Summary,
    string? QuestionsJson,
    string? Diagnostics,
    string? Error,
    int ToolCallCount,
    string CorrelationId,
    string? DeliveredMessageId)
{
    /// <summary>Начинает новый запуск в статусе Running.</summary>
    public static AutonomousAgentRun Start(
        string agentId,
        DateTimeOffset? startedUtc = null,
        string? id = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var runId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();

        return new AutonomousAgentRun(
            runId,
            agentId.Trim(),
            AutonomousAgentRunStatus.Running,
            startedUtc ?? DateTimeOffset.UtcNow,
            FinishedUtc: null,
            Summary: null,
            QuestionsJson: null,
            Diagnostics: null,
            Error: null,
            ToolCallCount: 0,
            string.IsNullOrWhiteSpace(correlationId) ? $"autonomous-{runId}" : correlationId.Trim(),
            DeliveredMessageId: null);
    }

    public AutonomousAgentRun Complete(
        string? summary,
        string? questionsJson,
        string? diagnostics,
        int toolCallCount,
        DateTimeOffset? finishedUtc = null) =>
        this with
        {
            Status = AutonomousAgentRunStatus.Completed,
            FinishedUtc = finishedUtc ?? DateTimeOffset.UtcNow,
            Summary = summary,
            QuestionsJson = questionsJson,
            Diagnostics = diagnostics,
            ToolCallCount = toolCallCount,
            Error = null,
        };

    public AutonomousAgentRun Fail(string error, DateTimeOffset? finishedUtc = null) =>
        this with
        {
            Status = AutonomousAgentRunStatus.Failed,
            FinishedUtc = finishedUtc ?? DateTimeOffset.UtcNow,
            Error = error,
        };
}
