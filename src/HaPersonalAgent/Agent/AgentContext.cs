namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: контекст одного вызова agent runtime.
/// Зачем: runtime должен связывать Telegram/update/log/model call в одну трассу и знать scope диалога для confirmation actions.
/// Как: immutable record передается в SendAsync, а Create генерирует новый id и принимает уже отобранную историю диалога и transport identity.
/// </summary>
public sealed record AgentContext(
    string CorrelationId,
    IReadOnlyList<AgentConversationMessage> ConversationMessages,
    string? ConversationKey = null,
    string? Transport = null,
    string? ConversationId = null,
    string? ParticipantId = null)
{
    public static AgentContext Create(
        string? correlationId = null,
        IReadOnlyList<AgentConversationMessage>? conversationMessages = null,
        string? conversationKey = null,
        string? transport = null,
        string? conversationId = null,
        string? participantId = null) =>
        new(
            string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            conversationMessages ?? Array.Empty<AgentConversationMessage>(),
            string.IsNullOrWhiteSpace(conversationKey) ? null : conversationKey.Trim(),
            string.IsNullOrWhiteSpace(transport) ? null : transport.Trim(),
            string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim(),
            string.IsNullOrWhiteSpace(participantId) ? null : participantId.Trim());
}
