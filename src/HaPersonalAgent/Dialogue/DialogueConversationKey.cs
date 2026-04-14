namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: единое место генерации storage key для диалога.
/// Зачем: adapters не должны знать формат ключа в SQLite, иначе Web UI и Telegram начнут дублировать memory logic.
/// Как: строит стабильную строку из transport/conversation/participant частей и нормализует разделитель.
/// </summary>
public static class DialogueConversationKey
{
    private const string Separator = ":";
    private const string EscapedSeparator = "_";

    public static string Create(DialogueConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        return string.Join(
            Separator,
            Normalize(conversation.Transport),
            Normalize(conversation.ConversationId),
            Normalize(conversation.ParticipantId));
    }

    private static string Normalize(string value) =>
        value.Trim().Replace(Separator, EscapedSeparator, StringComparison.Ordinal);
}
