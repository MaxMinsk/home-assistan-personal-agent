namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: жизненный статус определения автономного агента.
/// Зачем: планировщик (HPA-029) должен будить только активных агентов, а пользователь — уметь поставить агента на паузу, не удаляя его.
/// Как: Active — участвует в расписании; Paused — сохранён, но не запускается.
/// </summary>
public enum AutonomousAgentStatus
{
    Active,
    Paused,
}
