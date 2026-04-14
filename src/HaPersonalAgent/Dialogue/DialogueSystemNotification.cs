namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: системное уведомление, которое может быть доставлено в канал пользователя.
/// Зачем: будущие тревоги камер и события Home Assistant не должны выглядеть как user-assistant реплики и загрязнять историю диалога.
/// Как: хранит отдельный notification kind/source id; текущий DialogueService явно не добавляет такие записи в conversation_messages.
/// </summary>
public sealed record DialogueSystemNotification(
    DialogueConversation Conversation,
    string Kind,
    string Text,
    string? SourceId,
    DateTimeOffset CreatedAtUtc)
{
    public static DialogueSystemNotification Create(
        DialogueConversation conversation,
        string kind,
        string text,
        string? sourceId = null,
        DateTimeOffset? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return new DialogueSystemNotification(
            conversation,
            kind.Trim(),
            text.Trim(),
            string.IsNullOrWhiteSpace(sourceId) ? null : sourceId.Trim(),
            createdAtUtc ?? DateTimeOffset.UtcNow);
    }
}
