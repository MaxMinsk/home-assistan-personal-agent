namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: входящий пользовательский запрос к диалогу с агентом.
/// Зачем: transport adapters должны передавать в core слой не Telegram DTO, а общий request с conversation reference и correlation id.
/// Как: Create валидирует текст, создает correlation id при необходимости и оставляет transport-specific детали внутри DialogueConversation.
/// </summary>
public sealed record DialogueRequest(
    DialogueConversation Conversation,
    string Text,
    string CorrelationId)
{
    public static DialogueRequest Create(
        DialogueConversation conversation,
        string text,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return new DialogueRequest(
            conversation,
            text.Trim(),
            string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId.Trim());
    }
}
