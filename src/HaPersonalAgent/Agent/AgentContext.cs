namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: контекст одного вызова agent runtime.
/// Зачем: даже в первом spike нам нужен correlation id, чтобы связывать Telegram/update/log/model call в одну трассу.
/// Как: immutable record передается в SendAsync, а Create генерирует новый id и принимает уже отобранную историю диалога.
/// </summary>
public sealed record AgentContext(
    string CorrelationId,
    IReadOnlyList<AgentConversationMessage> ConversationMessages)
{
    public static AgentContext Create(
        string? correlationId = null,
        IReadOnlyList<AgentConversationMessage>? conversationMessages = null) =>
        new(
            string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            conversationMessages ?? Array.Empty<AgentConversationMessage>());
}
