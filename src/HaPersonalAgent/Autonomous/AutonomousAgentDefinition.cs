namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: определение автономного агента — что он исследует, как часто просыпается и что ему позволено.
/// Зачем: это пользовательская сущность, которую создают из Web UI; планировщик (HPA-029) и исполнитель (HPA-030) читают её как контракт запуска.
/// Как: хранится в локальном SQLite (операционные данные, не Memory MCP); NextRunUtc/LastRunUtc ведёт планировщик, остальное — пользователь.
/// </summary>
public sealed record AutonomousAgentDefinition(
    string Id,
    string Name,
    string Mission,
    AutonomousAgentScheduleKind ScheduleKind,
    string? ScheduleExpression,
    AutonomousAgentStatus Status,
    AutonomousAgentToolScope ToolScope,
    long? DeliveryTelegramChatId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? NextRunUtc,
    DateTimeOffset? LastRunUtc)
{
    public const int MaxNameLength = 120;
    public const int MaxMissionLength = 4_000;

    /// <summary>Создаёт новое определение: валидирует и нормализует пользовательский ввод, проставляет id и метки времени.</summary>
    public static AutonomousAgentDefinition Create(
        string name,
        string mission,
        AutonomousAgentScheduleKind scheduleKind,
        string? scheduleExpression = null,
        AutonomousAgentToolScope? toolScope = null,
        long? deliveryTelegramChatId = null,
        DateTimeOffset? createdUtc = null,
        string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(mission);

        var now = createdUtc ?? DateTimeOffset.UtcNow;
        var normalizedExpression = NormalizeScheduleExpression(scheduleKind, scheduleExpression);

        return new AutonomousAgentDefinition(
            string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim(),
            Truncate(name.Trim(), MaxNameLength),
            Truncate(mission.Trim(), MaxMissionLength),
            scheduleKind,
            normalizedExpression,
            AutonomousAgentStatus.Active,
            toolScope ?? AutonomousAgentToolScope.ResearchDefault,
            deliveryTelegramChatId,
            now,
            now,
            NextRunUtc: null,
            LastRunUtc: null);
    }

    /// <summary>Применяет пользовательские правки к существующему определению, сохраняя id, историю расписания и метку создания.</summary>
    public AutonomousAgentDefinition WithEdits(
        string name,
        string mission,
        AutonomousAgentScheduleKind scheduleKind,
        string? scheduleExpression,
        AutonomousAgentToolScope toolScope,
        long? deliveryTelegramChatId,
        DateTimeOffset? updatedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(mission);
        ArgumentNullException.ThrowIfNull(toolScope);

        return this with
        {
            Name = Truncate(name.Trim(), MaxNameLength),
            Mission = Truncate(mission.Trim(), MaxMissionLength),
            ScheduleKind = scheduleKind,
            ScheduleExpression = NormalizeScheduleExpression(scheduleKind, scheduleExpression),
            ToolScope = toolScope,
            DeliveryTelegramChatId = deliveryTelegramChatId,
            UpdatedUtc = updatedUtc ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Cron-выражение имеет смысл только для Cron-расписания; для пресетов и ручного запуска оно очищается.</summary>
    private static string? NormalizeScheduleExpression(
        AutonomousAgentScheduleKind scheduleKind,
        string? scheduleExpression)
    {
        if (scheduleKind != AutonomousAgentScheduleKind.Cron)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleExpression);
        return scheduleExpression.Trim();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
