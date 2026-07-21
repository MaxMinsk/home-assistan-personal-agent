namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: reply-якорь одного прогона после доставки сводного дайджеста (HPA-039).
/// Зачем: даже в дайджесте у каждого агента с вопросами своё сообщение-с-кнопками — по его id матчатся reply
/// пользователя (HPA-032). Планировщик сохраняет этот id в запуск, чтобы ответ доехал до нужного агента.
/// Как: пара (runId, deliveredMessageId); messageId == null, если Telegram-доставки не было (только панель).
/// </summary>
public sealed record AutonomousDigestAnchor(
    string RunId,
    string? DeliveredMessageId);
