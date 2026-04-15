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
        var history = await _stateRepository.GetConversationMessagesAsync(
            conversationKey,
            maxMessages,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        AgentRuntimeResponse response;
        try
        {
            response = await _agentRuntime.SendAsync(
                request.Text,
                AgentContext.Create(
                    correlationId: request.CorrelationId,
                    conversationMessages: history,
                    conversationKey: conversationKey,
                    transport: request.Conversation.Transport,
                    conversationId: request.Conversation.ConversationId,
                    participantId: request.Conversation.ParticipantId),
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
}
