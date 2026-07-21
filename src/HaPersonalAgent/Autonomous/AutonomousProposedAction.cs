namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: нейтральная проекция предложенного (но не исполненного) действия для доставки в бриф (HPA-035).
/// Зачем: порт нотификатора не должен знать про подсистему Confirmation — ему достаточно id (для кнопок),
/// короткого описания и риска. Само действие живёт как PendingConfirmation и выполнится лишь после одобрения владельца.
/// Как: раннер собирает предложения этого прогона через IConfirmationService и передаёт их списком в доставку.
/// </summary>
public sealed record AutonomousProposedAction(
    string Id,
    string Summary,
    string Risk);
