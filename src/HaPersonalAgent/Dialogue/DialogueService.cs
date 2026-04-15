using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: transport-agnostic сервис диалога с агентом.
/// Зачем: Telegram и будущий Web UI должны переиспользовать одну логику загрузки истории, вызова IAgentRuntime, сохранения turns и reset.
/// Как: получает DialogueRequest, строит storage key через DialogueConversationKey, читает SQLite history, вызывает runtime и сохраняет только user/assistant turns.
/// </summary>
public sealed class DialogueService
{
    private const int MaxPersistedSummaryLength = 4_000;
    private const int PersistedSummaryRefreshMessageThreshold = 12;

    private readonly IAgentRuntime _agentRuntime;
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly ILogger<DialogueService> _logger;
    private readonly AgentStateRepository _stateRepository;

    public DialogueService(
        IAgentRuntime agentRuntime,
        IOptions<AgentOptions> agentOptions,
        AgentStateRepository stateRepository,
        ILogger<DialogueService> logger)
    {
        _agentRuntime = agentRuntime;
        _agentOptions = agentOptions;
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
        var shouldRefreshPersistedSummary = persistedSummary is null
            || messagesSincePersistedSummary >= PersistedSummaryRefreshMessageThreshold;
        var history = await _stateRepository.GetConversationMessagesAsync(
            conversationKey,
            maxMessages,
            cancellationToken);
        _logger.LogInformation(
            "Dialogue request {CorrelationId} started for {ConversationKey} ({Transport}/{ConversationId}, participant {ParticipantId}) with profile {ExecutionProfile}, text length {TextLength}, history messages {HistoryMessageCount}, persisted summary present {PersistedSummaryPresent}, persisted summary version {PersistedSummaryVersion}, messages since persisted summary {MessagesSincePersistedSummary}, persisted summary refresh requested {ShouldRefreshPersistedSummary}.",
            request.CorrelationId,
            conversationKey,
            request.Conversation.Transport,
            request.Conversation.ConversationId,
            request.Conversation.ParticipantId,
            request.ExecutionProfile,
            request.Text.Length,
            history.Count,
            persistedSummary is not null,
            persistedSummary?.SummaryVersion ?? 0,
            messagesSincePersistedSummary,
            shouldRefreshPersistedSummary);

        var now = DateTimeOffset.UtcNow;
        AgentRuntimeResponse response;
        try
        {
            response = await _agentRuntime.SendAsync(
                request.Text,
                AgentContext.Create(
                    correlationId: request.CorrelationId,
                    conversationMessages: history,
                    persistedSummary: persistedSummary?.Summary,
                    shouldRefreshPersistedSummary: shouldRefreshPersistedSummary,
                    messagesSincePersistedSummary: messagesSincePersistedSummary,
                    conversationKey: conversationKey,
                    transport: request.Conversation.Transport,
                    conversationId: request.Conversation.ConversationId,
                    participantId: request.Conversation.ParticipantId,
                    executionProfile: request.ExecutionProfile),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Agent runtime failed for dialogue request {CorrelationId}.",
                request.CorrelationId);

            return new AgentRuntimeResponse(
                request.CorrelationId,
                IsConfigured: false,
                "Не смог обработать сообщение из-за внутренней ошибки агента. Запрос не сохранен в историю диалога.",
                _agentRuntime.GetHealth());
        }

        if (!response.IsConfigured)
        {
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

        await _stateRepository.TrimConversationMessagesAsync(
            conversationKey,
            maxMessages,
            cancellationToken);
        await PersistSummaryCandidateIfNeededAsync(
            conversationKey,
            response.PersistedSummaryCandidate,
            persistedSummary,
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

        await _stateRepository.ClearConversationMessagesAsync(
            DialogueConversationKey.Create(conversation),
            cancellationToken);
        await _stateRepository.ClearConversationSummaryAsync(
            DialogueConversationKey.Create(conversation),
            cancellationToken);
        _logger.LogInformation(
            "Dialogue context reset for {ConversationKey}.",
            DialogueConversationKey.Create(conversation));
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
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "System notification {NotificationKind} for {Transport} conversation is not stored as dialogue memory.",
            notification.Kind,
            notification.Conversation.Transport);

        return Task.CompletedTask;
    }

    private int GetMaxContextMessages()
    {
        var maxTurns = Math.Clamp(_agentOptions.Value.ConversationContextMaxTurns, 0, 50);
        return maxTurns * 2;
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
