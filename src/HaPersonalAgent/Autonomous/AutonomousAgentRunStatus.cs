namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: статус одного запуска автономного агента.
/// Зачем: UI показывает историю запусков, а планировщик не должен стартовать новый запуск поверх незавершённого.
/// Как: Running — идёт сейчас; Completed — завершён со сводкой; Failed — упал с ошибкой.
/// </summary>
public enum AutonomousAgentRunStatus
{
    Running,
    Completed,
    Failed,
}
