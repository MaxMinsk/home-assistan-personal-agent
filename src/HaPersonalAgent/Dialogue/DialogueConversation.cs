namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: transport-agnostic ссылка на диалог пользователя с агентом.
/// Зачем: Telegram, будущий Web UI и другие adapters должны использовать одну модель диалога и памяти, не изобретая свои ключи хранения.
/// Как: adapter передает transport, conversation id и participant id, а DialogueService сам переводит их в storage key.
/// </summary>
public sealed record DialogueConversation(
    string Transport,
    string ConversationId,
    string ParticipantId)
{
    public static DialogueConversation Create(
        string transport,
        string conversationId,
        string participantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);

        return new DialogueConversation(
            transport.Trim(),
            conversationId.Trim(),
            participantId.Trim());
    }
}
