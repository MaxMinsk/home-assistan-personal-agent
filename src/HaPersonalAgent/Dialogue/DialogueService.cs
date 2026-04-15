using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: transport-agnostic сервис диалога с агентом.
/// Зачем: Telegram и будущий Web UI должны переиспользовать одну логику загрузки истории, вызова IAgentRuntime, сохранения turns и reset.
/// Как: получает DialogueRequest, строит storage key через DialogueConversationKey, загружает bounded history + vector recall, вызывает runtime и сохраняет только user/assistant turns.
/// </summary>
public sealed class DialogueService
{
    private const int MaxPersistedSummaryLength = 4_000;
    private const int PersistedSummaryRefreshMessageThreshold = 12;

    private readonly IAgentRuntime _agentRuntime;
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly BoundedChatHistoryProvider _boundedChatHistoryProvider;
    private readonly ProjectCapsuleService _projectCapsuleService;
    private readonly ILogger<DialogueService> _logger;
    private readonly AgentStateRepository _stateRepository;

    public DialogueService(
        IAgentRuntime agentRuntime,
        IOptions<AgentOptions> agentOptions,
        BoundedChatHistoryProvider boundedChatHistoryProvider,
        ProjectCapsuleService projectCapsuleService,
        AgentStateRepository stateRepository,
        ILogger<DialogueService> logger)
    {
        _agentRuntime = agentRuntime;
        _agentOptions = agentOptions;
        _boundedChatHistoryProvider = boundedChatHistoryProvider;
        _projectCapsuleService = projectCapsuleService;
        _stateRepository = stateRepository;
        _logger = logger;
    }

    public async Task<AgentRuntimeResponse> SendUserMessageAsync(
        DialogueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conversationKey = DialogueConversationKey.Create(request.Conversation);
        var maxMessages = GetMaxContextMessages();
        var persistedSummary = await _stateRepository.GetConversationSummaryAsync(
            conversationKey,
            cancellationToken);
        var latestMessageId = await _stateRepository.GetLatestConversationMessageIdAsync(
            conversationKey,
            cancellationToken);
        var messagesSincePersistedSummary = CountMessagesSinceSummary(
            persistedSummary,
            latestMessageId);
        var memoryRetrievalMode = AgentOptions.NormalizeMemoryRetrievalMode(_agentOptions.Value.MemoryRetrievalMode);
        var autoMemoryRetrievalEnabled = AgentOptions.IsBeforeInvokeRetrieval(memoryRetrievalMode);
        var shouldRefreshPersistedSummary = persistedSummary is null
            || messagesSincePersistedSummary >= PersistedSummaryRefreshMessageThreshold;
        var boundedHistory = await _boundedChatHistoryProvider.LoadAsync(
            conversationKey,
            request.Text,
            maxMessages,
            includeRetrievedMemory: autoMemoryRetrievalEnabled,
            cancellationToken);
        var capsulePromptContext = await _projectCapsuleService.BuildPromptContextAsync(
            conversationKey,
            cancellationToken);
        var vectorRetrievedMemoryCount = boundedHistory.RetrievedMemoryCount;
        var combinedMemoryContext = CombineContextBlocks(
            capsulePromptContext.PromptText,
            boundedHistory.RetrievedMemoryContext);
        var combinedMemoryCount = capsulePromptContext.CapsuleCount + vectorRetrievedMemoryCount;
        var history = boundedHistory.RecentMessages;
        _logger.LogInformation(
            "Dialogue request {CorrelationId} started for {ConversationKey} ({Transport}/{ConversationId}, participant {ParticipantId}) with profile {ExecutionProfile}, text length {TextLength}, history messages {HistoryMessageCount}, memory retrieval mode {MemoryRetrievalMode}, auto retrieval {AutoMemoryRetrievalEnabled}, vector retrieved memories {VectorRetrievedMemoryCount}, project capsules in prompt {ProjectCapsuleCount}, combined retrieved memories {RetrievedMemoryCount}, persisted summary present {PersistedSummaryPresent}, persisted summary version {PersistedSummaryVersion}, messages since persisted summary {MessagesSincePersistedSummary}, persisted summary refresh requested {ShouldRefreshPersistedSummary}.",
            request.CorrelationId,
            conversationKey,
            request.Conversation.Transport,
            request.Conversation.ConversationId,
            request.Conversation.ParticipantId,
            request.ExecutionProfile,
            request.Text.Length,
            history.Count,
            memoryRetrievalMode,
            autoMemoryRetrievalEnabled,
            vectorRetrievedMemoryCount,
            capsulePromptContext.CapsuleCount,
            combinedMemoryCount,
            persistedSummary is not null,
            persistedSummary?.SummaryVersion ?? 0,
            messagesSincePersistedSummary,
            shouldRefreshPersistedSummary);

        var now = DateTimeOffset.UtcNow;
        await _stateRepository.AppendRawEventsAsync(
            new[]
            {
                RawEventEntry.Create(
                    conversationKey,
                    request.Conversation.Transport,
                    request.Conversation.ConversationId,
                    request.Conversation.ParticipantId,
                    DialogueRawEventKinds.UserMessage,
                    request.Text,
                    correlationId: request.CorrelationId,
                    createdAtUtc: now),
            },
            cancellationToken);

        AgentRuntimeResponse response;
        try
        {
            response = await _agentRuntime.SendAsync(
                request.Text,
                AgentContext.Create(
                    correlationId: request.CorrelationId,
                    conversationMessages: history,
                    persistedSummary: persistedSummary?.Summary,
                    retrievedMemoryContext: combinedMemoryContext,
                    retrievedMemoryCount: combinedMemoryCount,
                    shouldRefreshPersistedSummary: shouldRefreshPersistedSummary,
                    messagesSincePersistedSummary: messagesSincePersistedSummary,
                    memoryRetrievalMode: memoryRetrievalMode,
                    conversationKey: conversationKey,
                    transport: request.Conversation.Transport,
                    conversationId: request.Conversation.ConversationId,
                    participantId: request.Conversation.ParticipantId,
                    executionProfile: request.ExecutionProfile),
                request.OnReasoningUpdate,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            const string fallbackText = "Не смог обработать сообщение из-за внутренней ошибки агента. Запрос не сохранен в историю диалога.";
            await _stateRepository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create(
                        conversationKey,
                        request.Conversation.Transport,
                        request.Conversation.ConversationId,
                        request.Conversation.ParticipantId,
                        DialogueRawEventKinds.AssistantMessage,
                        fallbackText,
                        sourceId: "runtime-error",
                        correlationId: request.CorrelationId),
                },
                cancellationToken);
            _logger.LogWarning(
                exception,
                "Agent runtime failed for dialogue request {CorrelationId}.",
                request.CorrelationId);

            return new AgentRuntimeResponse(
                request.CorrelationId,
                IsConfigured: false,
                fallbackText,
                _agentRuntime.GetHealth());
        }

        if (!response.IsConfigured)
        {
            await _stateRepository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create(
                        conversationKey,
                        request.Conversation.Transport,
                        request.Conversation.ConversationId,
                        request.Conversation.ParticipantId,
                        DialogueRawEventKinds.AssistantMessage,
                        NormalizeRawEventPayload(response.Text),
                        sourceId: "runtime-not-configured",
                        correlationId: request.CorrelationId),
                },
                cancellationToken);
            _logger.LogInformation(
                "Dialogue request {CorrelationId} completed without persisted turn because runtime is not configured or provider call failed.",
                request.CorrelationId);
            return response;
        }

        var assistantTextForPersistence = GetAssistantTextForPersistence(response.Text);
        await _stateRepository.AppendConversationMessagesAsync(
            conversationKey,
            new[]
            {
                new AgentConversationMessage(AgentConversationRole.User, request.Text, now),
                new AgentConversationMessage(AgentConversationRole.Assistant, assistantTextForPersistence, DateTimeOffset.UtcNow),
            },
            cancellationToken);
        await _stateRepository.AppendRawEventsAsync(
            new[]
            {
                RawEventEntry.Create(
                    conversationKey,
                    request.Conversation.Transport,
                    request.Conversation.ConversationId,
                    request.Conversation.ParticipantId,
                    DialogueRawEventKinds.AssistantMessage,
                    NormalizeRawEventPayload(assistantTextForPersistence),
                    correlationId: request.CorrelationId),
            },
            cancellationToken);

        await _boundedChatHistoryProvider.ArchiveOverflowAndTrimAsync(
            conversationKey,
            maxMessages,
            cancellationToken);
        await PersistSummaryCandidateIfNeededAsync(
            conversationKey,
            response.PersistedSummaryCandidate,
            persistedSummary,
            cancellationToken);
        await TryAutoRefreshProjectCapsulesAsync(
            request.Conversation,
            conversationKey,
            request.CorrelationId,
            cancellationToken);
        _logger.LogInformation(
            "Dialogue request {CorrelationId} completed and persisted user/assistant turns for {ConversationKey}; response length {ResponseLength}.",
            request.CorrelationId,
            conversationKey,
            response.Text.Length);

        return response;
    }

    public async Task ResetAsync(
        DialogueConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var conversationKey = DialogueConversationKey.Create(conversation);
        await _stateRepository.ClearConversationMessagesAsync(
            conversationKey,
            cancellationToken);
        await _stateRepository.ClearConversationSummaryAsync(
            conversationKey,
            cancellationToken);
        await _stateRepository.ClearConversationVectorMemoryAsync(
            conversationKey,
            cancellationToken);
        await _stateRepository.ClearProjectCapsulesAsync(
            conversationKey,
            cancellationToken);
        await _stateRepository.ClearProjectCapsuleExtractionStateAsync(
            conversationKey,
            cancellationToken);
        await _stateRepository.AppendRawEventsAsync(
            new[]
            {
                RawEventEntry.Create(
                    conversationKey,
                    conversation.Transport,
                    conversation.ConversationId,
                    conversation.ParticipantId,
                    DialogueRawEventKinds.ContextReset,
                    "User requested context reset."),
            },
            cancellationToken);
        _logger.LogInformation(
            "Dialogue context reset for {ConversationKey}.",
            conversationKey);
    }

    public async Task<ConversationSummaryMemory?> GetPersistedSummaryAsync(
        DialogueConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var conversationKey = DialogueConversationKey.Create(conversation);
        return await _stateRepository.GetConversationSummaryAsync(
            conversationKey,
            cancellationToken);
    }

    public async Task<IReadOnlyList<RawEventRecord>> GetRawEventsAsync(
        DialogueConversation conversation,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var conversationKey = DialogueConversationKey.Create(conversation);
        return await _stateRepository.GetRawEventsAsync(
            conversationKey,
            limit,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectCapsuleMemory>> GetProjectCapsulesAsync(
        DialogueConversation conversation,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var conversationKey = DialogueConversationKey.Create(conversation);
        return await _stateRepository.GetProjectCapsulesAsync(
            conversationKey,
            limit,
            cancellationToken);
    }

    public Task<ProjectCapsuleRefreshResult> RefreshProjectCapsulesAsync(
        DialogueConversation conversation,
        string correlationId,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return _projectCapsuleService.RefreshAsync(
            conversation,
            correlationId,
            force,
            cancellationToken);
    }

    public async Task<PersistedSummaryRefreshResult> RefreshPersistedSummaryAsync(
        DialogueConversation conversation,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var conversationKey = DialogueConversationKey.Create(conversation);
        var maxMessages = GetMaxContextMessages();
        var persistedSummary = await _stateRepository.GetConversationSummaryAsync(
            conversationKey,
            cancellationToken);
        var latestMessageId = await _stateRepository.GetLatestConversationMessageIdAsync(
            conversationKey,
            cancellationToken);
        var messagesSincePersistedSummary = CountMessagesSinceSummary(
            persistedSummary,
            latestMessageId);
        var history = await _stateRepository.GetConversationMessagesAsync(
            conversationKey,
            maxMessages,
            cancellationToken);
        if (history.Count == 0)
        {
            return new PersistedSummaryRefreshResult(
                IsConfigured: true,
                IsUpdated: false,
                "В этом чате пока нет истории для пересборки summary.",
                persistedSummary);
        }

        _logger.LogInformation(
            "Persisted summary refresh {CorrelationId} started for {ConversationKey}; history messages {HistoryMessageCount}, persisted summary present {PersistedSummaryPresent}, persisted summary version {PersistedSummaryVersion}, messages since persisted summary {MessagesSincePersistedSummary}.",
            correlationId,
            conversationKey,
            history.Count,
            persistedSummary is not null,
            persistedSummary?.SummaryVersion ?? 0,
            messagesSincePersistedSummary);

        AgentRuntimeResponse response;
        try
        {
            response = await _agentRuntime.SendAsync(
                "Service request: refresh persisted conversation summary memory for this dialogue context.",
                AgentContext.Create(
                    correlationId: correlationId,
                    conversationMessages: history,
                    persistedSummary: persistedSummary?.Summary,
                    shouldRefreshPersistedSummary: true,
                    forcePersistedSummaryRefresh: true,
                    messagesSincePersistedSummary: Math.Max(
                        messagesSincePersistedSummary,
                        PersistedSummaryRefreshMessageThreshold),
                    conversationKey: conversationKey,
                    transport: conversation.Transport,
                    conversationId: conversation.ConversationId,
                    participantId: conversation.ParticipantId,
                    executionProfile: LlmExecutionProfile.Summarization),
                onReasoningUpdate: null,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Persisted summary refresh {CorrelationId} failed for {ConversationKey}.",
                correlationId,
                conversationKey);
            return new PersistedSummaryRefreshResult(
                IsConfigured: false,
                IsUpdated: false,
                "Не удалось пересобрать persisted summary из-за внутренней ошибки агента.",
                persistedSummary);
        }

        if (!response.IsConfigured)
        {
            _logger.LogInformation(
                "Persisted summary refresh {CorrelationId} finished without update because runtime is not configured.",
                correlationId);
            return new PersistedSummaryRefreshResult(
                IsConfigured: false,
                IsUpdated: false,
                response.Text,
                persistedSummary);
        }

        if (string.IsNullOrWhiteSpace(response.PersistedSummaryCandidate))
        {
            _logger.LogInformation(
                "Persisted summary refresh {CorrelationId} returned no summary candidate for {ConversationKey}.",
                correlationId,
                conversationKey);
            return new PersistedSummaryRefreshResult(
                IsConfigured: true,
                IsUpdated: false,
                "Не удалось получить новый persisted summary. Попробуй позже.",
                persistedSummary);
        }

        await PersistSummaryCandidateIfNeededAsync(
            conversationKey,
            response.PersistedSummaryCandidate,
            persistedSummary,
            cancellationToken);
        var refreshedSummary = await _stateRepository.GetConversationSummaryAsync(
            conversationKey,
            cancellationToken);
        if (refreshedSummary is null)
        {
            return new PersistedSummaryRefreshResult(
                IsConfigured: true,
                IsUpdated: false,
                "Persisted summary не появился после refresh. Попробуй позже.");
        }

        var isUpdated = refreshedSummary.SummaryVersion > (persistedSummary?.SummaryVersion ?? 0);
        _logger.LogInformation(
            "Persisted summary refresh {CorrelationId} completed for {ConversationKey}; updated {IsUpdated}, version {SummaryVersion}, source last message id {SourceLastMessageId}.",
            correlationId,
            conversationKey,
            isUpdated,
            refreshedSummary.SummaryVersion,
            refreshedSummary.SourceLastMessageId);

        return new PersistedSummaryRefreshResult(
            IsConfigured: true,
            IsUpdated: isUpdated,
            Message: isUpdated
                ? "Persisted summary пересобран."
                : "Persisted summary уже актуален, изменений нет.",
            refreshedSummary);
    }

    public async Task<DialogueContextSnapshot> GetContextSnapshotAsync(
        DialogueConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var conversationKey = DialogueConversationKey.Create(conversation);
        var maxMessages = GetMaxContextMessages();
        var storedMessageCount = await _stateRepository.GetConversationMessageCountAsync(
            conversationKey,
            cancellationToken);
        var rawEventCount = await _stateRepository.GetRawEventCountAsync(
            conversationKey,
            cancellationToken);
        var vectorMemoryCount = await _stateRepository.GetConversationVectorMemoryCountAsync(
            conversationKey,
            cancellationToken);
        var projectCapsuleCount = await _stateRepository.GetProjectCapsuleCountAsync(
            conversationKey,
            cancellationToken);
        var projectCapsuleLatestSourceEventId = await _stateRepository.GetProjectCapsuleLatestSourceEventIdAsync(
            conversationKey,
            cancellationToken);
        var projectCapsuleLastUpdatedAtUtc = await _stateRepository.GetProjectCapsuleLastUpdatedAtUtcAsync(
            conversationKey,
            cancellationToken);
        var projectCapsuleExtractionState = await _stateRepository.GetProjectCapsuleExtractionStateAsync(
            conversationKey,
            cancellationToken);
        var memoryRetrievalMode = AgentOptions.NormalizeMemoryRetrievalMode(_agentOptions.Value.MemoryRetrievalMode);
        var autoMemoryRetrievalEnabled = AgentOptions.IsBeforeInvokeRetrieval(memoryRetrievalMode);
        var summary = await _stateRepository.GetConversationSummaryAsync(
            conversationKey,
            cancellationToken);
        var latestMessageId = await _stateRepository.GetLatestConversationMessageIdAsync(
            conversationKey,
            cancellationToken);
        var messagesSinceSummary = CountMessagesSinceSummary(summary, latestMessageId);

        return new DialogueContextSnapshot(
            conversationKey,
            StoredMessageCount: storedMessageCount,
            RawEventCount: rawEventCount,
            VectorMemoryCount: vectorMemoryCount,
            ProjectCapsuleCount: projectCapsuleCount,
            ProjectCapsuleLatestSourceEventId: projectCapsuleLatestSourceEventId ?? 0,
            ProjectCapsuleLastUpdatedAtUtc: projectCapsuleLastUpdatedAtUtc,
            ProjectCapsuleLastProcessedRawEventId: projectCapsuleExtractionState?.LastRawEventId ?? 0,
            ProjectCapsuleLastExtractionAtUtc: projectCapsuleExtractionState?.UpdatedAtUtc,
            ProjectCapsuleExtractionRunsCount: projectCapsuleExtractionState?.RunsCount ?? 0,
            MemoryRetrievalMode: memoryRetrievalMode,
            MemoryRetrievalBeforeInvokeEnabled: autoMemoryRetrievalEnabled,
            MemoryRetrievalOnDemandToolEnabled: !autoMemoryRetrievalEnabled,
            MaxContextMessages: maxMessages,
            LoadedHistoryMessageCount: Math.Min(storedMessageCount, maxMessages),
            MessagesSincePersistedSummary: messagesSinceSummary,
            PersistedSummaryPresent: summary is not null,
            PersistedSummaryLength: summary?.Summary.Length ?? 0,
            PersistedSummaryVersion: summary?.SummaryVersion ?? 0,
            PersistedSummarySourceLastMessageId: summary?.SourceLastMessageId ?? 0);
    }

    public Task RecordSystemNotificationAsync(
        DialogueSystemNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var conversation = notification.Conversation;
        var conversationKey = DialogueConversationKey.Create(conversation);

        _logger.LogInformation(
            "System notification {NotificationKind} for {ConversationKey} will be appended to raw event store and excluded from dialogue memory.",
            notification.Kind,
            conversationKey);

        return _stateRepository.AppendRawEventsAsync(
            new[]
            {
                RawEventEntry.Create(
                    conversationKey,
                    conversation.Transport,
                    conversation.ConversationId,
                    conversation.ParticipantId,
                    DialogueRawEventKinds.SystemNotification,
                    notification.Text,
                    sourceId: notification.SourceId,
                    createdAtUtc: notification.CreatedAtUtc),
            },
            cancellationToken);
    }

    private int GetMaxContextMessages()
    {
        var maxTurns = Math.Clamp(_agentOptions.Value.ConversationContextMaxTurns, 0, 50);
        return maxTurns * 2;
    }

    private async Task TryAutoRefreshProjectCapsulesAsync(
        DialogueConversation conversation,
        string conversationKey,
        string parentCorrelationId,
        CancellationToken cancellationToken)
    {
        if (!await _projectCapsuleService.ShouldAutoRefreshAsync(conversationKey, cancellationToken))
        {
            return;
        }

        var autoCorrelationId = $"{parentCorrelationId}-capsules";
        try
        {
            var result = await _projectCapsuleService.RefreshAsync(
                conversation,
                autoCorrelationId,
                force: false,
                cancellationToken);
            _logger.LogInformation(
                "Auto-batched project capsules refresh {CorrelationId} completed for {ConversationKey}; configured {IsConfigured}, updated {IsUpdated}, capsule count {CapsuleCount}, last raw event id {LastProcessedRawEventId}.",
                autoCorrelationId,
                conversationKey,
                result.IsConfigured,
                result.IsUpdated,
                result.CapsuleCount,
                result.LastProcessedRawEventId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Auto-batched project capsules refresh {CorrelationId} failed for {ConversationKey}.",
                autoCorrelationId,
                conversationKey);
        }
    }

    private async Task PersistSummaryCandidateIfNeededAsync(
        string conversationKey,
        string? summaryCandidate,
        ConversationSummaryMemory? currentSummary,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(summaryCandidate))
        {
            return;
        }

        var normalizedSummary = NormalizeSummaryCandidate(summaryCandidate);
        if (normalizedSummary.Length == 0)
        {
            return;
        }

        if (currentSummary is not null
            && string.Equals(currentSummary.Summary, normalizedSummary, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Persisted summary update skipped for {ConversationKey}: summary text is unchanged.",
                conversationKey);
            return;
        }

        var latestMessageId = await _stateRepository.GetLatestConversationMessageIdAsync(
            conversationKey,
            cancellationToken);
        if (!latestMessageId.HasValue)
        {
            return;
        }

        var nextVersion = (currentSummary?.SummaryVersion ?? 0) + 1;
        await _stateRepository.UpsertConversationSummaryAsync(
            new ConversationSummaryMemory(
                conversationKey,
                normalizedSummary,
                DateTimeOffset.UtcNow,
                latestMessageId.Value,
                nextVersion),
            cancellationToken);
        _logger.LogInformation(
            "Persisted conversation summary updated for {ConversationKey}; version {SummaryVersion}, source last message id {SourceLastMessageId}, length {SummaryLength}.",
            conversationKey,
            nextVersion,
            latestMessageId.Value,
            normalizedSummary.Length);
    }

    private static string NormalizeSummaryCandidate(string summaryCandidate)
    {
        var normalized = summaryCandidate
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        while (normalized.Contains("\n\n\n", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }

        return LimitSummaryLength(normalized);
    }

    private static string NormalizeRawEventPayload(string payload) =>
        string.IsNullOrWhiteSpace(payload)
            ? "[empty]"
            : payload;

    private static string? CombineContextBlocks(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second)
                ? null
                : second.Trim();
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first.Trim();
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            first.Trim(),
            second.Trim());
    }

    private static string LimitSummaryLength(string summary) =>
        summary.Length <= MaxPersistedSummaryLength
            ? summary
            : summary[..MaxPersistedSummaryLength];

    private static string GetAssistantTextForPersistence(string responseText)
    {
        if (!responseText.StartsWith("[context-summary]", StringComparison.Ordinal))
        {
            return responseText;
        }

        const string windowsSeparator = "\r\n\r\n";
        const string unixSeparator = "\n\n";

        var separatorIndex = responseText.IndexOf(windowsSeparator, StringComparison.Ordinal);
        var separatorLength = windowsSeparator.Length;
        if (separatorIndex < 0)
        {
            separatorIndex = responseText.IndexOf(unixSeparator, StringComparison.Ordinal);
            separatorLength = unixSeparator.Length;
        }

        if (separatorIndex < 0)
        {
            return responseText;
        }

        var content = responseText[(separatorIndex + separatorLength)..].TrimStart('\r', '\n');
        return string.IsNullOrWhiteSpace(content)
            ? responseText
            : content;
    }

    private static int CountMessagesSinceSummary(
        ConversationSummaryMemory? summary,
        long? latestMessageId)
    {
        if (summary is null || !latestMessageId.HasValue)
        {
            return 0;
        }

        var delta = latestMessageId.Value - summary.SourceLastMessageId;
        if (delta <= 0)
        {
            return 0;
        }

        return delta >= int.MaxValue
            ? int.MaxValue
            : (int)delta;
    }
}
