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
        IReadOnlyList<AutonomousProposedAction> proposedActions,
        CancellationToken cancellationToken);

    /// <summary>
    /// HPA-039: доставляет результаты НЕСКОЛЬКИХ агентов одним сводным дайджестом (обзор по агентам + блок «Связи»),
    /// а интерактив (вопросы с кнопками, предложения) — по-агентно, чтобы reply-якоря и одобрения работали.
    /// Возвращает reply-якорь на каждый прогон, чтобы вызывающий сохранил его в запуск.
    /// </summary>
    Task<IReadOnlyList<AutonomousDigestAnchor>> DeliverDigestAsync(
        IReadOnlyList<AutonomousRunDelivery> deliveries,
        IReadOnlyList<string> connections,
        CancellationToken cancellationToken);
}
