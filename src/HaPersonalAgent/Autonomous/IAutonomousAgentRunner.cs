namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: контракт исполнителя одного запуска автономного агента.
/// Зачем: планировщик (HPA-029) отвечает только за "когда", а "что именно делать" реализует research-исполнитель (HPA-030) — это позволяет тестировать планировщик с фейком.
/// Как: планировщик вызывает RunAsync для наступившего срока; исполнитель сам ведёт запись запуска, память и доставку сводки.
/// </summary>
public interface IAutonomousAgentRunner
{
    /// <summary>
    /// Выполняет один запуск. При deliverIndividually доставляет бриф сразу (как раньше); иначе доставку подавляет
    /// и возвращает payload — планировщик соберёт несколько и отправит один сводный дайджест (HPA-039).
    /// Возвращает null, если запуск не дал доставляемого результата (не настроен рантайм или ошибка).
    /// </summary>
    Task<AutonomousRunDelivery?> RunAsync(
        AutonomousAgentDefinition definition,
        bool deliverIndividually,
        CancellationToken cancellationToken);
}
