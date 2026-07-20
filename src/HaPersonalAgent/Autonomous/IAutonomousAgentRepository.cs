namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: контракт хранилища автономных агентов (определения, запуски, inbox ответов, непрерывное состояние).
/// Зачем: планировщик, исполнитель, Telegram-петля и Web UI должны зависеть от абстракции, а не от SQLite — это же позволяет тестировать их с фейком.
/// Как: четыре независимые области за одним интерфейсом; всё это операционные данные и живёт локально, а не в Memory MCP.
/// </summary>
public interface IAutonomousAgentRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);

    // --- Определения ---

    Task UpsertDefinitionAsync(AutonomousAgentDefinition definition, CancellationToken cancellationToken);

    Task<AutonomousAgentDefinition?> GetDefinitionAsync(string agentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutonomousAgentDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken);

    /// <summary>Удаляет агента вместе с его запусками, inbox и состоянием непрерывности. Возвращает false, если агента не было.</summary>
    Task<bool> DeleteDefinitionAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>Обновляет только поля расписания — их ведёт планировщик, не пользователь.</summary>
    Task UpdateScheduleStateAsync(
        string agentId,
        DateTimeOffset? nextRunUtc,
        DateTimeOffset? lastRunUtc,
        CancellationToken cancellationToken);

    // --- Запуски ---

    Task AppendRunAsync(AutonomousAgentRun run, CancellationToken cancellationToken);

    Task UpdateRunAsync(AutonomousAgentRun run, CancellationToken cancellationToken);

    Task<AutonomousAgentRun?> GetRunAsync(string runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutonomousAgentRun>> ListRunsAsync(
        string agentId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Есть ли у агента незавершённый запуск — планировщик не должен стартовать поверх идущего.</summary>
    Task<bool> HasRunningRunAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>Ищет запуск по id доставленного сообщения — так Telegram-reply сопоставляется с агентом (HPA-032).</summary>
    Task<AutonomousAgentRun?> FindRunByDeliveredMessageAsync(
        string deliveredMessageId,
        CancellationToken cancellationToken);

    // --- Inbox ответов пользователя ---

    Task EnqueueReplyAsync(AutonomousAgentInboxEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutonomousAgentInboxEntry>> GetPendingRepliesAsync(
        string agentId,
        CancellationToken cancellationToken);

    Task MarkRepliesConsumedAsync(
        IEnumerable<string> entryIds,
        string consumedByRunId,
        DateTimeOffset consumedUtc,
        CancellationToken cancellationToken);

    // --- Непрерывное состояние ---

    Task<AutonomousAgentContinuity?> GetContinuityAsync(string agentId, CancellationToken cancellationToken);

    Task UpsertContinuityAsync(AutonomousAgentContinuity continuity, CancellationToken cancellationToken);
}
