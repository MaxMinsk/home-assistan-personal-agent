namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: ответ пользователя автономному агенту, поставленный в очередь до следующего планового запуска.
/// Зачем: ключевое требование эпика — агент не дёргает пользователя между запусками, а его ответ попадает в контекст СЛЕДУЮЩЕГО запуска, а не запускает новый.
/// Как: запись копится в локальном inbox; исполнитель (HPA-030) забирает непотреблённые записи и помечает их как использованные конкретным запуском.
/// </summary>
public sealed record AutonomousAgentInboxEntry(
    string Id,
    string AgentId,
    string? RunId,
    AutonomousAgentReplySource Source,
    string Text,
    DateTimeOffset ReceivedUtc,
    DateTimeOffset? ConsumedUtc,
    string? ConsumedByRunId)
{
    public const int MaxTextLength = 4_000;

    /// <summary>Создаёт непотреблённую запись inbox.</summary>
    public static AutonomousAgentInboxEntry Create(
        string agentId,
        string text,
        AutonomousAgentReplySource source,
        string? runId = null,
        DateTimeOffset? receivedUtc = null,
        string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var normalizedText = text.Trim();
        if (normalizedText.Length > MaxTextLength)
        {
            normalizedText = normalizedText[..MaxTextLength];
        }

        return new AutonomousAgentInboxEntry(
            string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim(),
            agentId.Trim(),
            string.IsNullOrWhiteSpace(runId) ? null : runId.Trim(),
            source,
            normalizedText,
            receivedUtc ?? DateTimeOffset.UtcNow,
            ConsumedUtc: null,
            ConsumedByRunId: null);
    }
}
