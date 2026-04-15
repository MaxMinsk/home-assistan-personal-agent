namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: persisted summary памяти по одному conversation scope.
/// Зачем: summary должен жить между run-ами и рестартами, чтобы не пересчитываться каждый ход и не засорять таблицу обычных turns.
/// Как: хранит текст summary, версию и id последнего сообщения, до которого summary считается актуальным.
/// </summary>
public sealed record ConversationSummaryMemory(
    string ConversationKey,
    string Summary,
    DateTimeOffset UpdatedAtUtc,
    long SourceLastMessageId,
    int SummaryVersion);
