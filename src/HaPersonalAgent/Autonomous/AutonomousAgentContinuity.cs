namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: непрерывное состояние автономного агента между запусками (на чём остановился, что осталось выяснить).
/// Зачем: агент ведёт длинное исследование неделями — без переносимого фокуса и открытых вопросов каждый запуск начинался бы заново.
/// Как: одна строка на агента в локальном SQLite; CapsuleNoteKey указывает на единственную идемпотентную research-капсулу в Memory MCP (HPA-031).
/// </summary>
public sealed record AutonomousAgentContinuity(
    string AgentId,
    string? Focus,
    string? OpenQuestions,
    string? CapsuleNoteKey,
    DateTimeOffset? CapsuleUpdatedUtc,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>Пустое состояние для агента, который ещё ни разу не запускался.</summary>
    public static AutonomousAgentContinuity Empty(string agentId, DateTimeOffset? updatedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        return new AutonomousAgentContinuity(
            agentId.Trim(),
            Focus: null,
            OpenQuestions: null,
            CapsuleNoteKey: null,
            CapsuleUpdatedUtc: null,
            updatedUtc ?? DateTimeOffset.UtcNow);
    }
}
