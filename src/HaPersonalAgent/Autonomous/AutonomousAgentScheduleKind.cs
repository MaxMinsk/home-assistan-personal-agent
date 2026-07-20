namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: способ задания расписания пробуждения автономного агента.
/// Зачем: пользователь описывает каденцию простыми пресетами, но нужен и полный cron для нестандартных случаев.
/// Как: Manual — только ручной запуск; Hourly/Daily/Weekly — пресеты; Cron — выражение в ScheduleExpression. Нормализацию в момент времени делает планировщик (HPA-029).
/// </summary>
public enum AutonomousAgentScheduleKind
{
    Manual,
    Hourly,
    Daily,
    Weekly,
    Cron,
}
