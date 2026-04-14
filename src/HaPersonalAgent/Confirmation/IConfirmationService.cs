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
}
