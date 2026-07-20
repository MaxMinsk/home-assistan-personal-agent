namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: контракт исполнителя одного запуска автономного агента.
/// Зачем: планировщик (HPA-029) отвечает только за "когда", а "что именно делать" реализует research-исполнитель (HPA-030) — это позволяет тестировать планировщик с фейком.
/// Как: планировщик вызывает RunAsync для наступившего срока; исполнитель сам ведёт запись запуска, память и доставку сводки.
/// </summary>
public interface IAutonomousAgentRunner
{
    Task RunAsync(AutonomousAgentDefinition definition, CancellationToken cancellationToken);
}
