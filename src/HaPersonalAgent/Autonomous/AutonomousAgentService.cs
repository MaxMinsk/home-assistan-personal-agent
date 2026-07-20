using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: прикладной сервис управления автономными агентами (создать/изменить/пауза/удалить, поставить ответ в очередь).
/// Зачем: Web UI (HPA-033) и Telegram (HPA-032) должны работать с валидированными операциями, а не дёргать репозиторий напрямую.
/// Как: тонкий слой над IAutonomousAgentRepository — валидация и нормализация ввода, генерация id и меток времени, логирование; планирование и исполнение живут в HPA-029/030.
/// </summary>
public sealed class AutonomousAgentService
{
    private const int DefaultRunHistoryLimit = 25;

    private readonly IAutonomousAgentRepository _repository;
    private readonly ILogger<AutonomousAgentService> _logger;

    public AutonomousAgentService(
        IAutonomousAgentRepository repository,
        ILogger<AutonomousAgentService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<AutonomousAgentDefinition> CreateAsync(
        string name,
        string mission,
        AutonomousAgentScheduleKind scheduleKind,
        string? scheduleExpression,
        AutonomousAgentToolScope? toolScope,
        long? deliveryTelegramChatId,
        CancellationToken cancellationToken)
    {
        ValidateSchedule(scheduleKind, scheduleExpression);

        var definition = AutonomousAgentDefinition.Create(
            name,
            mission,
            scheduleKind,
            scheduleExpression,
            toolScope,
            deliveryTelegramChatId);

        await _repository.UpsertDefinitionAsync(definition, cancellationToken);
        await _repository.UpsertContinuityAsync(
            AutonomousAgentContinuity.Empty(definition.Id),
            cancellationToken);

        _logger.LogInformation(
            "Autonomous agent {AgentId} created: name '{AgentName}', schedule {ScheduleKind}, web search {AllowWebSearch}, memory write {AllowMemoryWrite} (max {MaxDurableFacts} facts/run).",
            definition.Id,
            definition.Name,
            definition.ScheduleKind,
            definition.ToolScope.AllowWebSearch,
            definition.ToolScope.AllowMemoryWrite,
            definition.ToolScope.MaxDurableFactsPerRun);

        return definition;
    }

    public async Task<AutonomousAgentDefinition?> UpdateAsync(
        string agentId,
        string name,
        string mission,
        AutonomousAgentScheduleKind scheduleKind,
        string? scheduleExpression,
        AutonomousAgentToolScope toolScope,
        long? deliveryTelegramChatId,
        CancellationToken cancellationToken)
    {
        ValidateSchedule(scheduleKind, scheduleExpression);

        var existing = await _repository.GetDefinitionAsync(agentId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var updated = existing.WithEdits(
            name,
            mission,
            scheduleKind,
            scheduleExpression,
            toolScope,
            deliveryTelegramChatId);

        await _repository.UpsertDefinitionAsync(updated, cancellationToken);
        _logger.LogInformation("Autonomous agent {AgentId} updated.", updated.Id);

        return updated;
    }

    public async Task<AutonomousAgentDefinition?> SetStatusAsync(
        string agentId,
        AutonomousAgentStatus status,
        CancellationToken cancellationToken)
    {
        var existing = await _repository.GetDefinitionAsync(agentId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var updated = existing with
        {
            Status = status,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        await _repository.UpsertDefinitionAsync(updated, cancellationToken);
        _logger.LogInformation("Autonomous agent {AgentId} status set to {Status}.", agentId, status);

        return updated;
    }

    public Task<AutonomousAgentDefinition?> GetAsync(string agentId, CancellationToken cancellationToken) =>
        _repository.GetDefinitionAsync(agentId, cancellationToken);

    public Task<IReadOnlyList<AutonomousAgentDefinition>> ListAsync(CancellationToken cancellationToken) =>
        _repository.ListDefinitionsAsync(cancellationToken);

    public async Task<bool> DeleteAsync(string agentId, CancellationToken cancellationToken)
    {
        var deleted = await _repository.DeleteDefinitionAsync(agentId, cancellationToken);
        if (deleted)
        {
            _logger.LogInformation("Autonomous agent {AgentId} deleted with its runs, inbox and continuity.", agentId);
        }

        return deleted;
    }

    public Task<IReadOnlyList<AutonomousAgentRun>> ListRunsAsync(
        string agentId,
        int limit,
        CancellationToken cancellationToken) =>
        _repository.ListRunsAsync(agentId, limit <= 0 ? DefaultRunHistoryLimit : limit, cancellationToken);

    /// <summary>
    /// Ставит ответ пользователя в очередь агента. Осознанно НЕ запускает агента:
    /// по решению эпика ответ попадает в контекст следующего планового запуска, чтобы не дёргать пользователя между запусками.
    /// </summary>
    public async Task<AutonomousAgentInboxEntry?> RecordReplyAsync(
        string agentId,
        string text,
        AutonomousAgentReplySource source,
        string? runId,
        CancellationToken cancellationToken)
    {
        var existing = await _repository.GetDefinitionAsync(agentId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var entry = AutonomousAgentInboxEntry.Create(agentId, text, source, runId);
        await _repository.EnqueueReplyAsync(entry, cancellationToken);

        _logger.LogInformation(
            "Autonomous agent {AgentId} queued a user reply from {Source} (entry {EntryId}); it will be consumed by the next scheduled run.",
            agentId,
            source,
            entry.Id);

        return entry;
    }

    public Task<IReadOnlyList<AutonomousAgentInboxEntry>> GetPendingRepliesAsync(
        string agentId,
        CancellationToken cancellationToken) =>
        _repository.GetPendingRepliesAsync(agentId, cancellationToken);

    public Task<AutonomousAgentContinuity?> GetContinuityAsync(
        string agentId,
        CancellationToken cancellationToken) =>
        _repository.GetContinuityAsync(agentId, cancellationToken);

    /// <summary>
    /// Просит запустить агента как можно скорее: сдвигает срок на "сейчас", а сам запуск делает планировщик
    /// на ближайшем тике. Так не появляется второго пути исполнения со своими гонками и правилами non-overlap.
    /// </summary>
    public async Task<bool> RequestRunNowAsync(string agentId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetDefinitionAsync(agentId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        await _repository.UpdateScheduleStateAsync(
            agentId,
            DateTimeOffset.UtcNow,
            existing.LastRunUtc,
            cancellationToken);
        _logger.LogInformation("Autonomous agent {AgentId} was requested to run as soon as possible.", agentId);

        return true;
    }

    /// <summary>Cron проверяем здесь, а не в домене: разбор выражения — инфраструктурная деталь планировщика.</summary>
    private static void ValidateSchedule(AutonomousAgentScheduleKind scheduleKind, string? scheduleExpression)
    {
        if (scheduleKind != AutonomousAgentScheduleKind.Cron)
        {
            return;
        }

        if (!AutonomousAgentScheduleCalculator.IsValidCronExpression(scheduleExpression))
        {
            throw new ArgumentException(
                $"Cron expression '{scheduleExpression}' is not a valid 5-field cron schedule.",
                nameof(scheduleExpression));
        }
    }
}
