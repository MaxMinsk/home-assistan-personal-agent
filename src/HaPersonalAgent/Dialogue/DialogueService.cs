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
        var history = await _stateRepository.GetConversationMessagesAsync(
            conversationKey,
            maxMessages,
            cancellationToken);
        _logger.LogInformation(
            "Dialogue request {CorrelationId} started for {ConversationKey} ({Transport}/{ConversationId}, participant {ParticipantId}) with profile {ExecutionProfile}, text length {TextLength}, history messages {HistoryMessageCount}, persisted summary present {PersistedSummaryPresent}, persisted summary version {PersistedSummaryVersion}.",
            request.CorrelationId,
            conversationKey,
            request.Conversation.Transport,
            request.Conversation.ConversationId,
            request.Conversation.ParticipantId,
            request.ExecutionProfile,
            request.Text.Length,
            history.Count,
            persistedSummary is not null,
            persistedSummary?.SummaryVersion ?? 0);

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

        await _stateRepository.AppendConversationMessagesAsync(
            conversationKey,
            new[]
            {
                new AgentConversationMessage(AgentConversationRole.User, request.Text, now),
                new AgentConversationMessage(AgentConversationRole.Assistant, response.Text, DateTimeOffset.UtcNow),
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

        var normalizedSummary = summaryCandidate.Trim();
        if (normalizedSummary.Length > MaxPersistedSummaryLength)
        {
            normalizedSummary = normalizedSummary[..MaxPersistedSummaryLength];
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
}
