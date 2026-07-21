using HaPersonalAgent.Dialogue;

namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: универсальный orchestration service для подтверждаемых действий.
/// Зачем: transport adapters и agent tools должны использовать один поток propose/approve/reject независимо от домена действия.
/// Как: proposal сохраняет PendingConfirmation, а approve/reject вызываются из Telegram или будущего Web UI.
/// </summary>
public interface IConfirmationService
{
    Task<ConfirmationProposalResult> ProposeAsync(
        ConfirmationProposalRequest request,
        CancellationToken cancellationToken);

    Task<ConfirmationDecisionResult> ApproveAsync(
        DialogueConversation conversation,
        string confirmationId,
        CancellationToken cancellationToken);

    Task<ConfirmationDecisionResult> RejectAsync(
        DialogueConversation conversation,
        string confirmationId,
        CancellationToken cancellationToken);

    Task<string?> GetLatestPendingConfirmationIdAsync(
        DialogueConversation conversation,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// HPA-035: ещё ожидающие подтверждения одного участника (для автономного агента participant == agentId),
    /// опционально суженные до одного прогона. Истёкшие отфильтрованы. Используется брифом и вкладкой «Действия».
    /// </summary>
    Task<IReadOnlyList<PendingConfirmation>> ListPendingForParticipantAsync(
        string participantId,
        string? correlationId,
        CancellationToken cancellationToken);

    /// <summary>Одобрить действие, зная участника-владельца (например, agentId) — минуя привязку к чату-инициатору.</summary>
    Task<ConfirmationDecisionResult> ApproveForParticipantAsync(
        string participantId,
        string confirmationId,
        CancellationToken cancellationToken);

    /// <summary>Отклонить действие, зная участника-владельца.</summary>
    Task<ConfirmationDecisionResult> RejectForParticipantAsync(
        string participantId,
        string confirmationId,
        CancellationToken cancellationToken);

    /// <summary>Одобрить действие только по его id: участник-владелец резолвится из самого подтверждения (для компактных Telegram-кнопок).</summary>
    Task<ConfirmationDecisionResult> ApproveByConfirmationIdAsync(
        string confirmationId,
        CancellationToken cancellationToken);

    /// <summary>Отклонить действие только по его id.</summary>
    Task<ConfirmationDecisionResult> RejectByConfirmationIdAsync(
        string confirmationId,
        CancellationToken cancellationToken);
}
