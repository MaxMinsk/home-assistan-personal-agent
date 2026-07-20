using Cronos;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: чистый расчёт следующего времени пробуждения агента по его расписанию.
/// Зачем: планировщик должен быть детерминированно тестируемым — время передаётся аргументом, а не берётся из системных часов.
/// Как: пресеты считаются как интервал от точки отсчёта (без привязки к стенным часам и таймзонам), Cron — через Cronos; Manual не планируется вовсе.
/// </summary>
public static class AutonomousAgentScheduleCalculator
{
    public static DateTimeOffset? ComputeNextRun(
        AutonomousAgentScheduleKind scheduleKind,
        string? scheduleExpression,
        DateTimeOffset fromUtc) =>
        scheduleKind switch
        {
            // Ручной агент просыпается только по кнопке "Запустить сейчас".
            AutonomousAgentScheduleKind.Manual => null,
            AutonomousAgentScheduleKind.Hourly => fromUtc.AddHours(1),
            AutonomousAgentScheduleKind.Daily => fromUtc.AddDays(1),
            AutonomousAgentScheduleKind.Weekly => fromUtc.AddDays(7),
            AutonomousAgentScheduleKind.Cron => ComputeCronNextRun(scheduleExpression, fromUtc),
            _ => null,
        };

    /// <summary>Проверяет, что cron-выражение разбирается — используется при валидации пользовательского ввода.</summary>
    public static bool IsValidCronExpression(string? scheduleExpression) =>
        TryParseCron(scheduleExpression, out _);

    private static DateTimeOffset? ComputeCronNextRun(string? scheduleExpression, DateTimeOffset fromUtc)
    {
        if (!TryParseCron(scheduleExpression, out var expression))
        {
            return null;
        }

        var next = expression!.GetNextOccurrence(fromUtc.UtcDateTime, inclusive: false);
        return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : null;
    }

    private static bool TryParseCron(string? scheduleExpression, out CronExpression? expression)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(scheduleExpression))
        {
            return false;
        }

        try
        {
            // Поддерживаем классические 5 полей (мин час день месяц день-недели); секунды намеренно не даём —
            // фоновый исследовательский агент не должен просыпаться чаще, чем раз в минуту.
            expression = CronExpression.Parse(scheduleExpression.Trim(), CronFormat.Standard);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }
}
