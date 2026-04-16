namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: числовой срез состояния памяти конкретного диалога.
/// Зачем: /status в Telegram и будущий Web UI должны видеть прозрачные метрики контекста, а не только общий runtime health.
/// Как: собирается в DialogueService из conversation_messages, conversation_summary, conversation_vector_memory, project_capsules и extraction state с учетом текущего лимита контекстного окна.
/// </summary>
public sealed record DialogueContextSnapshot(
    string ConversationKey,
    int StoredMessageCount,
    int RawEventCount,
    int VectorMemoryCount,
    int ProjectCapsuleCount,
    long ProjectCapsuleLatestSourceEventId,
    DateTimeOffset? ProjectCapsuleLastUpdatedAtUtc,
    long ProjectCapsuleLastProcessedRawEventId,
    DateTimeOffset? ProjectCapsuleLastExtractionAtUtc,
    int ProjectCapsuleExtractionRunsCount,
    string MemoryRetrievalMode,
    bool MemoryRetrievalBeforeInvokeEnabled,
    bool MemoryRetrievalOnDemandToolEnabled,
    int MaxContextMessages,
    int LoadedHistoryMessageCount,
    int MessagesSincePersistedSummary,
    int PersistedSummaryRefreshThreshold,
    bool PersistedSummaryRefreshSuggested,
    string PersistedSummaryRefreshReason,
    bool PersistedSummaryPresent,
    int PersistedSummaryLength,
    int PersistedSummaryVersion,
    long PersistedSummarySourceLastMessageId,
    bool PersistedSummaryStructuredContract,
    int PersistedSummaryFactsCount,
    int PersistedSummaryConflictsCount,
    double PersistedSummaryHistoryToSummaryCompressionRatio,
    int EstimatedContextTokenCount,
    int EstimatedHistoryTokenCount,
    int EstimatedPersistedSummaryTokenCount,
    int EstimatedProjectCapsuleTokenCount,
    int EstimatedMessageScaffoldingTokenCount);
