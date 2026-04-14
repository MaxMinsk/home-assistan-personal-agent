namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: lifecycle status любого действия, ожидающего пользовательского подтверждения.
/// Зачем: Home Assistant, файловые операции и будущие risky tools должны одинаково защищаться от повторного выполнения.
/// Как: repository хранит статус строкой, а ConfirmationService переводит Pending через Executing в terminal state.
/// </summary>
public enum ConfirmationActionStatus
{
    Pending,
    Executing,
    Completed,
    Rejected,
    Expired,
    Failed,
}
