namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: числовой срез состояния памяти конкретного диалога.
/// Зачем: /status в Telegram и будущий Web UI должны видеть прозрачные метрики контекста, а не только общий runtime health.
/// Как: собирается в DialogueService из conversation_messages и conversation_summary с учетом текущего лимита контекстного окна.
/// </summary>
public sealed record DialogueContextSnapshot(
    string ConversationKey,
    int StoredMessageCount,
    int RawEventCount,
    int MaxContextMessages,
    int LoadedHistoryMessageCount,
    int MessagesSincePersistedSummary,
    bool PersistedSummaryPresent,
    int PersistedSummaryLength,
    int PersistedSummaryVersion,
    long PersistedSummarySourceLastMessageId);
