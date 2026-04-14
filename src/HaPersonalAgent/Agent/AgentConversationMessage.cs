namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: одно сохраненное сообщение из диалога с агентом.
/// Зачем: Telegram gateway должен передавать в MAF runtime краткосрочную историю беседы без привязки к Telegram DTO.
/// Как: role показывает автора сообщения, text хранит пользовательский или assistant-текст, а CreatedAtUtc нужен для будущей сортировки и диагностики.
/// </summary>
public sealed record AgentConversationMessage(
    AgentConversationRole Role,
    string Text,
    DateTimeOffset CreatedAtUtc);
