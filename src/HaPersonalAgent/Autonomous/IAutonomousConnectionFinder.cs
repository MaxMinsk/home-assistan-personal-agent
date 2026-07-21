namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: поиск ЗАЗЕМЛЁННЫХ связей между находками нескольких агентов для блока «Связи» сводного дайджеста (HPA-039).
/// Зачем: ценность дайджеста — заметить, что находки разных агентов пересекаются; но по правилу проекта связь нельзя
/// выдумывать — только подтверждённую текстом находок. Реализация best-effort: при любой проблеме возвращает пусто.
/// Как: один заземлённый LLM-проход над сводками/находками; парсит строгий JSON-список связей.
/// </summary>
public interface IAutonomousConnectionFinder
{
    Task<IReadOnlyList<string>> FindConnectionsAsync(
        IReadOnlyList<AutonomousRunDelivery> deliveries,
        CancellationToken cancellationToken);
}
