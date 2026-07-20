namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: порт доставки сводки автономного агента пользователю.
/// Зачем: исполнитель не должен знать про Telegram — доставка это транспорт, и завтра рядом встанет Web/push, как того требует transport-agnostic принцип проекта.
/// Как: реализация отправляет бриф и возвращает id доставленного сообщения (якорь для reply), либо null, если доставлять некуда.
/// </summary>
public interface IAutonomousAgentNotifier
{
    Task<string?> DeliverAsync(
        AutonomousAgentDefinition definition,
        AutonomousAgentRun run,
        AutonomousRunOutput output,
        CancellationToken cancellationToken);
}
