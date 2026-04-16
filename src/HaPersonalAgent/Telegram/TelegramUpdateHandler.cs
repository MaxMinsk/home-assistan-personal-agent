using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.HomeAssistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot.Types;

namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: обработчик одного Telegram update.
/// Зачем: long polling gateway должен оставаться транспортным циклом, а команды, allowlist и agent invocation жить в тестируемом классе.
/// Как: проверяет автора по allowlist, маршрутизирует команды включая approve/reject, а обычный текст передает в transport-agnostic DialogueService.
/// </summary>
public sealed class TelegramUpdateHandler
{
    private const int MaxTelegramMessageLength = 4096;
    private const int MaxRawEventPayloadPreviewLength = 180;
    private const int DefaultRawEventLimit = 10;
    private const int MaxRawEventLimit = 25;
    private const int DefaultVectorMemoryLimit = 10;
    private const int MaxVectorMemoryLimit = 25;
    private const int DefaultProjectCapsuleLimit = 6;
    private const int MaxProjectCapsuleLimit = 12;
    private const int MaxReasoningPreviewLength = 1_500;
    private const string ConfirmationCallbackApprovePrefix = "confirm:approve:";
    private const string ConfirmationCallbackRejectPrefix = "confirm:reject:";
    private const string TelegramTransportName = "telegram";
    private static readonly TimeSpan TypingIndicatorInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ReasoningPreviewEditInterval = TimeSpan.FromSeconds(2);
    private static readonly Regex ApproveCommandRegex = new(
        "/approve\\s+([A-Za-z0-9_-]{6,64})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RejectCommandRegex = new(
        "/reject\\s+([A-Za-z0-9_-]{6,64})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly DialogueService _dialogueService;
    private readonly IHomeAssistantMcpClient _homeAssistantMcpClient;
    private readonly IConfirmationService? _confirmationService;
    private readonly AgentStatusTool _statusTool;
    private readonly IAgentRuntime _agentRuntime;
    private readonly LlmExecutionPlanner _executionPlanner;
    private readonly IOptions<LlmOptions> _llmOptions;
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(
        DialogueService dialogueService,
        IHomeAssistantMcpClient homeAssistantMcpClient,
        AgentStatusTool statusTool,
        IAgentRuntime agentRuntime,
        LlmExecutionPlanner executionPlanner,
        IOptions<LlmOptions> llmOptions,
        ILogger<TelegramUpdateHandler> logger,
        IConfirmationService? confirmationService = null)
    {
        _dialogueService = dialogueService;
        _homeAssistantMcpClient = homeAssistantMcpClient;
        _statusTool = statusTool;
        _agentRuntime = agentRuntime;
        _executionPlanner = executionPlanner;
        _llmOptions = llmOptions;
        _logger = logger;
        _confirmationService = confirmationService;
    }

    public async Task HandleAsync(
        ITelegramBotClientAdapter client,
        Update update,
        TelegramOptions telegramOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(telegramOptions);

        if (update.CallbackQuery is { From: not null } callbackQuery)
        {
            await HandleCallbackQueryAsync(
                client,
                update.Id,
                callbackQuery,
                telegramOptions,
                cancellationToken);
            return;
        }

        var message = update.Message;
        if (message?.From is null || string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        var userId = message.From.Id;
        var chatId = message.Chat.Id;
        var text = message.Text.Trim();
        _logger.LogInformation(
            "Telegram update {TelegramUpdateId} received from user {TelegramUserId} in chat {TelegramChatId}; text length {TextLength}.",
            update.Id,
            userId,
            chatId,
            text.Length);

        if (!IsAllowed(telegramOptions, userId))
        {
            _logger.LogWarning(
                "Ignoring Telegram update {TelegramUpdateId} from non-allowlisted user {TelegramUserId}",
                update.Id,
                userId);
            return;
        }

        var conversation = CreateConversation(chatId, userId);

        if (IsCommand(text, "/start"))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /start command.",
                update.Id);
            await client.SendMessageAsync(
                chatId,
                "Привет. Пиши обычным текстом, я отвечу через агента. /think <вопрос> запускает deep reasoning без tools. /status покажет статус, /resetContext очистит контекст этого чата, /showSummary покажет persisted summary, /refreshSummary принудительно пересоберет persisted summary, /showRawEvents [N] покажет последние сырые события памяти, /showVector [N] покажет записи vector memory, /showCapsules [N] покажет project capsules, /refreshCapsules обновит project capsules из raw events. Для действий с подтверждением можно нажать кнопки Подтвердить/Отклонить, а также доступны команды /approve <id> и /reject <id>.",
                cancellationToken);
            return;
        }

        if (IsCommand(text, "/status"))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /status command.",
                update.Id);
            await client.SendMessageAsync(
                chatId,
                await FormatStatusAsync(conversation, cancellationToken),
                cancellationToken);
            return;
        }

        if (IsCommand(text, "/resetContext"))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /resetContext command for conversation {ConversationKey}.",
                update.Id,
                DialogueConversationKey.Create(conversation));
            await _dialogueService.ResetAsync(conversation, cancellationToken);
            await client.SendMessageAsync(
                chatId,
                "Контекст этого чата очищен.",
                cancellationToken);
            return;
        }

        if (IsCommand(text, "/showSummary"))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /showSummary command for conversation {ConversationKey}.",
                update.Id,
                DialogueConversationKey.Create(conversation));
            await HandleShowSummaryCommandAsync(
                client,
                chatId,
                conversation,
                cancellationToken);
            return;
        }

        if (TryReadCommandArgument(text, "/showRawEvents", out var rawEventsLimitArgument))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /showRawEvents command for conversation {ConversationKey}.",
                update.Id,
                DialogueConversationKey.Create(conversation));
            await HandleShowRawEventsCommandAsync(
                client,
                chatId,
                conversation,
                rawEventsLimitArgument,
                cancellationToken);
            return;
        }

        if (TryReadCommandArgument(text, "/showVector", out var vectorMemoryLimitArgument))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /showVector command for conversation {ConversationKey}.",
                update.Id,
                DialogueConversationKey.Create(conversation));
            await HandleShowVectorMemoryCommandAsync(
                client,
                chatId,
                conversation,
                vectorMemoryLimitArgument,
                cancellationToken);
            return;
        }

        if (IsCommand(text, "/refreshSummary"))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /refreshSummary command for conversation {ConversationKey}.",
                update.Id,
                DialogueConversationKey.Create(conversation));
            await HandleRefreshSummaryCommandAsync(
                client,
                update.Id,
                chatId,
                conversation,
                cancellationToken);
            return;
        }

        if (TryReadCommandArgument(text, "/showCapsules", out var projectCapsuleLimitArgument))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /showCapsules command for conversation {ConversationKey}.",
                update.Id,
                DialogueConversationKey.Create(conversation));
            await HandleShowProjectCapsulesCommandAsync(
                client,
                chatId,
                conversation,
                projectCapsuleLimitArgument,
                cancellationToken);
            return;
        }

        if (IsCommand(text, "/refreshCapsules"))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /refreshCapsules command for conversation {ConversationKey}.",
                update.Id,
                DialogueConversationKey.Create(conversation));
            await HandleRefreshProjectCapsulesCommandAsync(
                client,
                update.Id,
                chatId,
                conversation,
                cancellationToken);
            return;
        }

        if (TryReadCommandArgument(text, "/approve", out var approveActionId))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /approve command with action id {ConfirmationId}.",
                update.Id,
                approveActionId);
            await HandleConfirmationCommandAsync(
                client,
                chatId,
                conversation,
                approveActionId,
                approve: true,
                cancellationToken);
            return;
        }

        if (TryReadCommandArgument(text, "/reject", out var rejectActionId))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /reject command with action id {ConfirmationId}.",
                update.Id,
                rejectActionId);
            await HandleConfirmationCommandAsync(
                client,
                chatId,
                conversation,
                rejectActionId,
                approve: false,
                cancellationToken);
            return;
        }

        if (TryReadCommandArgument(text, "/think", out var deepReasoningText))
        {
            if (string.IsNullOrWhiteSpace(deepReasoningText))
            {
                _logger.LogInformation(
                    "Telegram update {TelegramUpdateId} routed to /think command without payload.",
                    update.Id);
                await client.SendMessageAsync(
                    chatId,
                    "Укажи вопрос: /think <вопрос>. В этом режиме tools отключены.",
                    cancellationToken);
                return;
            }

            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to deep reasoning profile; text length {TextLength}.",
                update.Id,
                deepReasoningText.Length);
            await HandleAgentMessageAsync(
                client,
                update.Id,
                chatId,
                conversation,
                deepReasoningText,
                telegramOptions,
                LlmExecutionProfile.DeepReasoning,
                cancellationToken);
            return;
        }

        _logger.LogInformation(
            "Telegram update {TelegramUpdateId} routed to tool-enabled profile; text length {TextLength}.",
            update.Id,
            text.Length);
        await HandleAgentMessageAsync(
            client,
            update.Id,
            chatId,
            conversation,
            text,
            telegramOptions,
            LlmExecutionProfile.ToolEnabled,
            cancellationToken);
    }

    private async Task HandleCallbackQueryAsync(
        ITelegramBotClientAdapter client,
        int updateId,
        CallbackQuery callbackQuery,
        TelegramOptions telegramOptions,
        CancellationToken cancellationToken)
    {
        var userId = callbackQuery.From.Id;
        var callbackData = callbackQuery.Data?.Trim();
        _logger.LogInformation(
            "Telegram callback query {TelegramUpdateId}/{CallbackQueryId} received from user {TelegramUserId}; data '{CallbackData}'.",
            updateId,
            callbackQuery.Id,
            userId,
            callbackData ?? "<empty>");

        if (!IsAllowed(telegramOptions, userId))
        {
            _logger.LogWarning(
                "Ignoring Telegram callback query {CallbackQueryId} from non-allowlisted user {TelegramUserId}.",
                callbackQuery.Id,
                userId);
            await client.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                "Недостаточно прав для этого действия.",
                showAlert: true,
                cancellationToken);
            return;
        }

        if (callbackQuery.Message?.Chat is null)
        {
            await client.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                "Не удалось определить чат для подтверждения.",
                showAlert: true,
                cancellationToken);
            return;
        }

        if (!TryParseConfirmationCallbackData(callbackData, out var actionId, out var approve))
        {
            await client.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                "Неизвестное действие кнопки.",
                showAlert: true,
                cancellationToken);
            return;
        }

        await client.AnswerCallbackQueryAsync(
            callbackQuery.Id,
            approve ? "Подтверждаю действие..." : "Отклоняю действие...",
            showAlert: false,
            cancellationToken);

        var chatId = callbackQuery.Message.Chat.Id;
        try
        {
            await client.ClearInlineKeyboardAsync(
                chatId,
                callbackQuery.Message.Id,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(
                exception,
                "Failed to clear inline keyboard for callback query {CallbackQueryId} in chat {TelegramChatId}.",
                callbackQuery.Id,
                chatId);
        }

        var conversation = CreateConversation(chatId, userId);
        await HandleConfirmationCommandAsync(
            client,
            chatId,
            conversation,
            actionId,
            approve,
            cancellationToken);
    }

    private async Task HandleConfirmationCommandAsync(
        ITelegramBotClientAdapter client,
        long chatId,
        DialogueConversation conversation,
        string? actionId,
        bool approve,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            await client.SendMessageAsync(
                chatId,
                approve
                    ? "Укажи id действия: /approve <id>."
                    : "Укажи id действия: /reject <id>.",
                cancellationToken);
            return;
        }

        if (_confirmationService is null)
        {
            await client.SendMessageAsync(
                chatId,
                "Confirmation service сейчас недоступен.",
                cancellationToken);
            return;
        }

        var result = approve
            ? await _confirmationService.ApproveAsync(conversation, actionId, cancellationToken)
            : await _confirmationService.RejectAsync(conversation, actionId, cancellationToken);
        _logger.LogInformation(
            "Confirmation command {Command} for action {ConfirmationId} completed with outcome {Outcome} and success {IsSuccess}.",
            approve ? "/approve" : "/reject",
            actionId,
            result.Outcome,
            result.IsSuccess);

        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(result.Message),
            cancellationToken);
    }

    private async Task HandleShowSummaryCommandAsync(
        ITelegramBotClientAdapter client,
        long chatId,
        DialogueConversation conversation,
        CancellationToken cancellationToken)
    {
        var summary = await _dialogueService.GetPersistedSummaryAsync(conversation, cancellationToken);
        if (summary is null)
        {
            await client.SendMessageAsync(
                chatId,
                "Persisted summary для этого чата пока отсутствует.",
                cancellationToken);
            return;
        }

        var response = FormatPersistedSummary(summary);
        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(response),
            cancellationToken);
    }

    private async Task HandleRefreshSummaryCommandAsync(
        ITelegramBotClientAdapter client,
        int updateId,
        long chatId,
        DialogueConversation conversation,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteWithTypingIndicatorAsync(
            client,
            chatId,
            ct => _dialogueService.RefreshPersistedSummaryAsync(
                conversation,
                correlationId: $"telegram-{updateId}-refresh-summary",
                ct),
            cancellationToken);
        var response = result.Summary is null
            ? result.Message
            : string.Join(
                Environment.NewLine,
                result.Message,
                string.Empty,
                FormatPersistedSummary(result.Summary));
        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(response),
            cancellationToken);
    }

    private async Task HandleShowRawEventsCommandAsync(
        ITelegramBotClientAdapter client,
        long chatId,
        DialogueConversation conversation,
        string? limitArgument,
        CancellationToken cancellationToken)
    {
        var rawEventLimit = ParseRawEventLimit(limitArgument);
        var rawEvents = await _dialogueService.GetRawEventsAsync(
            conversation,
            rawEventLimit,
            cancellationToken);

        if (rawEvents.Count == 0)
        {
            await client.SendMessageAsync(
                chatId,
                "Raw events для этого чата пока отсутствуют.",
                cancellationToken);
            return;
        }

        var lines = new List<string>
        {
            $"Raw events: последние {rawEvents.Count} (из запрошенных {rawEventLimit})",
        };

        foreach (var rawEvent in rawEvents)
        {
            var details = $"#{rawEvent.Id} {rawEvent.CreatedAtUtc:O} {rawEvent.EventKind}";
            if (!string.IsNullOrWhiteSpace(rawEvent.SourceId))
            {
                details += $" source={rawEvent.SourceId}";
            }

            if (!string.IsNullOrWhiteSpace(rawEvent.CorrelationId))
            {
                details += $" correlation={rawEvent.CorrelationId}";
            }

            lines.Add(details);
            lines.Add($"  {FormatRawEventPayload(rawEvent.Payload)}");
        }

        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(string.Join(Environment.NewLine, lines)),
            cancellationToken);
    }

    private async Task HandleShowVectorMemoryCommandAsync(
        ITelegramBotClientAdapter client,
        long chatId,
        DialogueConversation conversation,
        string? limitArgument,
        CancellationToken cancellationToken)
    {
        var vectorLimit = ParseVectorMemoryLimit(limitArgument);
        var vectorEntries = await _dialogueService.GetVectorMemoryAsync(
            conversation,
            vectorLimit,
            cancellationToken);

        if (vectorEntries.Count == 0)
        {
            await client.SendMessageAsync(
                chatId,
                "Vector memory для этого чата пока отсутствует.",
                cancellationToken);
            return;
        }

        var lines = new List<string>
        {
            $"Vector memory: последние {vectorEntries.Count} (из запрошенных {vectorLimit})",
        };

        foreach (var entry in vectorEntries)
        {
            lines.Add(
                $"#{entry.Id} sourceMessageId={entry.SourceMessageId} role={entry.Role} created={entry.CreatedAtUtc:O} embeddingDims={CountEmbeddingDimensions(entry.Embedding)}");
            lines.Add($"  {FormatRawEventPayload(entry.Content)}");
        }

        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(string.Join(Environment.NewLine, lines)),
            cancellationToken);
    }

    private async Task HandleShowProjectCapsulesCommandAsync(
        ITelegramBotClientAdapter client,
        long chatId,
        DialogueConversation conversation,
        string? limitArgument,
        CancellationToken cancellationToken)
    {
        var limit = ParseProjectCapsuleLimit(limitArgument);
        var capsules = await _dialogueService.GetProjectCapsulesAsync(
            conversation,
            limit,
            cancellationToken);
        if (capsules.Count == 0)
        {
            await client.SendMessageAsync(
                chatId,
                "Project capsules для этого чата пока отсутствуют.",
                cancellationToken);
            return;
        }

        var lines = new List<string>
        {
            $"Project capsules: {capsules.Count} (из запрошенных {limit})",
        };

        foreach (var capsule in capsules)
        {
            lines.Add(
                $"[{capsule.CapsuleKey}] {capsule.Title} | scope={capsule.Scope} | confidence={capsule.Confidence:0.00} | sourceEventId={capsule.SourceEventId} | version={capsule.Version}");
            lines.Add(capsule.ContentMarkdown);
        }

        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(string.Join(Environment.NewLine + Environment.NewLine, lines)),
            cancellationToken);
    }

    private async Task HandleRefreshProjectCapsulesCommandAsync(
        ITelegramBotClientAdapter client,
        int updateId,
        long chatId,
        DialogueConversation conversation,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteWithTypingIndicatorAsync(
            client,
            chatId,
            ct => _dialogueService.RefreshProjectCapsulesAsync(
                conversation,
                correlationId: $"telegram-{updateId}-refresh-capsules",
                force: false,
                ct),
            cancellationToken);
        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(
                string.Join(
                    Environment.NewLine,
                    result.Message,
                    $"Capsules total: {result.CapsuleCount}",
                    $"Last processed raw event id: {result.LastProcessedRawEventId}")),
            cancellationToken);
    }

    private async Task HandleAgentMessageAsync(
        ITelegramBotClientAdapter client,
        int updateId,
        long chatId,
        DialogueConversation conversation,
        string text,
        TelegramOptions telegramOptions,
        LlmExecutionProfile executionProfile,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Dialogue request telegram-{TelegramUpdateId} started for chat {TelegramChatId} with profile {ExecutionProfile}; text length {TextLength}.",
            updateId,
            chatId,
            executionProfile,
            text.Length);
        var reasoningPreviewSession = new TelegramReasoningPreviewSession(
            client,
            chatId,
            _logger,
            telegramOptions,
            cancellationToken);
        _logger.LogInformation(
            "Dialogue request telegram-{TelegramUpdateId} reasoning preview configured: enabled {ReasoningPreviewEnabled}, delay seconds {ReasoningPreviewDelaySeconds}.",
            updateId,
            reasoningPreviewSession.IsEnabled,
            reasoningPreviewSession.DelaySeconds);
        AgentRuntimeResponse response;
        try
        {
            response = await ExecuteWithTypingIndicatorAsync(
                client,
                chatId,
                ct => _dialogueService.SendUserMessageAsync(
                    DialogueRequest.Create(
                        conversation,
                        text,
                        correlationId: $"telegram-{updateId}",
                        executionProfile: executionProfile,
                        onReasoningUpdate: reasoningPreviewSession.IsEnabled
                            ? reasoningPreviewSession.OnReasoningUpdateAsync
                            : null),
                    ct),
                cancellationToken);
        }
        finally
        {
            try
            {
                await reasoningPreviewSession.CompleteAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
        _logger.LogInformation(
            "Dialogue request telegram-{TelegramUpdateId} completed for chat {TelegramChatId}; configured {IsConfigured}; response length {ResponseLength}.",
            updateId,
            chatId,
            response.IsConfigured,
            response.Text.Length);

        var normalizedResponse = NormalizeTelegramText(response.Text);
        if (TryExtractConfirmationPromptId(normalizedResponse, out var confirmationId)
            && !string.IsNullOrWhiteSpace(confirmationId))
        {
            _logger.LogInformation(
                "Dialogue request telegram-{TelegramUpdateId} produced confirmation prompt {ConfirmationId}; sending Telegram inline buttons.",
                updateId,
                confirmationId);
            await client.SendConfirmationMessageAsync(
                chatId,
                normalizedResponse,
                confirmationId,
                cancellationToken);
            return;
        }

        await client.SendMessageAsync(chatId, normalizedResponse, cancellationToken);
    }

    private async Task<string> FormatStatusAsync(
        DialogueConversation conversation,
        CancellationToken cancellationToken)
    {
        var status = _statusTool.GetStatus();
        var runtimeHealth = _agentRuntime.GetHealth();
        var toolEnabledPlan = _executionPlanner.CreatePlan(_llmOptions.Value, LlmExecutionProfile.ToolEnabled);
        var deepReasoningPlan = _executionPlanner.CreatePlan(_llmOptions.Value, LlmExecutionProfile.DeepReasoning);
        var mcpDiscovery = await _homeAssistantMcpClient.DiscoverAsync(cancellationToken);
        var contextSnapshot = await _dialogueService.GetContextSnapshotAsync(conversation, cancellationToken);
        var runtimeText = runtimeHealth.IsConfigured
            ? "configured"
            : $"not configured ({runtimeHealth.Reason})";
        var toolReasoningActive = IsReasoningActive(toolEnabledPlan);
        var deepReasoningActive = IsReasoningActive(deepReasoningPlan);

        return string.Join(
            Environment.NewLine,
            $"{status.ApplicationName} {status.Version}",
            $"Runtime: {runtimeText}",
            $"LLM: {runtimeHealth.Provider} / {runtimeHealth.Model} / thinking {runtimeHealth.ThinkingMode}",
            $"ReasoningActive(tool-enabled): {toolReasoningActive}",
            $"ReasoningPlan(tool-enabled): requested {toolEnabledPlan.RequestedThinkingMode}, effective {FormatThinkingMode(toolEnabledPlan.EffectiveThinkingMode)}, patch {toolEnabledPlan.ShouldPatchChatCompletionRequest}",
            $"ReasoningSafetyFallback(tool-enabled): {ShouldUseReasoningSafetyFallback(toolEnabledPlan)}",
            $"ReasoningActive(deep): {deepReasoningActive}",
            $"ReasoningPlan(deep): requested {deepReasoningPlan.RequestedThinkingMode}, effective {FormatThinkingMode(deepReasoningPlan.EffectiveThinkingMode)}, patch {deepReasoningPlan.ShouldPatchChatCompletionRequest}",
            $"ReasoningPreview(Telegram): enabled {status.Configuration.TelegramReasoningPreviewEnabled}, delay {status.Configuration.TelegramReasoningPreviewDelaySeconds}s",
            $"Uptime: {status.Uptime}",
            $"Configuration: {status.ConfigurationMode}",
            $"HA MCP: {FormatMcpStatus(mcpDiscovery)}",
            $"Context(stored): {contextSnapshot.StoredMessageCount} messages",
            $"RawEvents(stored): {contextSnapshot.RawEventCount} events",
            $"VectorMemory(stored): {contextSnapshot.VectorMemoryCount} entries",
            $"MemoryRetrieval: mode {contextSnapshot.MemoryRetrievalMode}, before-invoke {contextSnapshot.MemoryRetrievalBeforeInvokeEnabled}, on-demand-tool {contextSnapshot.MemoryRetrievalOnDemandToolEnabled}",
            $"ProjectCapsules(stored): {contextSnapshot.ProjectCapsuleCount} entries",
            $"ProjectCapsules(lastSourceEventId): {contextSnapshot.ProjectCapsuleLatestSourceEventId}",
            $"ProjectCapsules(lastUpdatedUtc): {FormatUtc(contextSnapshot.ProjectCapsuleLastUpdatedAtUtc)}",
            $"ProjectCapsules(extraction): lastRawEventId {contextSnapshot.ProjectCapsuleLastProcessedRawEventId}, lastExtractionUtc {FormatUtc(contextSnapshot.ProjectCapsuleLastExtractionAtUtc)}, runs {contextSnapshot.ProjectCapsuleExtractionRunsCount}",
            $"Context(loaded): {contextSnapshot.LoadedHistoryMessageCount} / {contextSnapshot.MaxContextMessages} messages",
            $"Context(tokens~): {contextSnapshot.EstimatedContextTokenCount} (history {contextSnapshot.EstimatedHistoryTokenCount}, summary {contextSnapshot.EstimatedPersistedSummaryTokenCount}, capsules {contextSnapshot.EstimatedProjectCapsuleTokenCount}, scaffolding {contextSnapshot.EstimatedMessageScaffoldingTokenCount}; heuristic UTF8 bytes/4)",
            $"PersistedSummary: present {contextSnapshot.PersistedSummaryPresent}, version {contextSnapshot.PersistedSummaryVersion}, length {contextSnapshot.PersistedSummaryLength}, sourceLastMessageId {contextSnapshot.PersistedSummarySourceLastMessageId}, messagesSinceSummary {contextSnapshot.MessagesSincePersistedSummary}",
            $"Telegram allowlist users: {status.Configuration.AllowedTelegramUserCount}");
    }

    private static string FormatMcpStatus(HomeAssistantMcpDiscoveryResult discovery) =>
        discovery.Status switch
        {
            HomeAssistantMcpStatus.Reachable => $"reachable ({discovery.ToolCount} tools, {discovery.PromptCount} prompts)",
            HomeAssistantMcpStatus.NotConfigured => $"not configured ({discovery.Reason})",
            HomeAssistantMcpStatus.InvalidConfiguration => $"invalid configuration ({discovery.Reason})",
            HomeAssistantMcpStatus.AuthFailed => "auth failed",
            HomeAssistantMcpStatus.IntegrationMissing => "integration missing",
            HomeAssistantMcpStatus.Unreachable => $"unreachable ({discovery.Reason})",
            _ => $"error ({discovery.Reason})",
        };

    private static bool IsAllowed(TelegramOptions telegramOptions, long userId) =>
        telegramOptions.AllowedUserIds.Contains(userId);

    private static DialogueConversation CreateConversation(long chatId, long userId) =>
        DialogueConversation.Create(
            TelegramTransportName,
            chatId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            userId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static bool IsCommand(string text, string command)
    {
        return TryReadCommandArgument(text, command, out _);
    }

    private static bool TryReadCommandArgument(
        string text,
        string command,
        out string? argument)
    {
        var commandToken = text.Split(' ', 2, StringSplitOptions.TrimEntries)[0];
        var commandWithoutBotSuffix = commandToken.Split('@', 2, StringSplitOptions.TrimEntries)[0];

        if (!string.Equals(commandWithoutBotSuffix, command, StringComparison.OrdinalIgnoreCase))
        {
            argument = null;
            return false;
        }

        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        argument = parts.Length == 2 ? parts[1].Trim() : null;
        return true;
    }

    private static bool TryParseConfirmationCallbackData(
        string? callbackData,
        out string? actionId,
        out bool approve)
    {
        if (string.IsNullOrWhiteSpace(callbackData))
        {
            actionId = null;
            approve = false;
            return false;
        }

        if (callbackData.StartsWith(ConfirmationCallbackApprovePrefix, StringComparison.Ordinal))
        {
            actionId = callbackData[ConfirmationCallbackApprovePrefix.Length..].Trim();
            approve = true;
            return !string.IsNullOrWhiteSpace(actionId);
        }

        if (callbackData.StartsWith(ConfirmationCallbackRejectPrefix, StringComparison.Ordinal))
        {
            actionId = callbackData[ConfirmationCallbackRejectPrefix.Length..].Trim();
            approve = false;
            return !string.IsNullOrWhiteSpace(actionId);
        }

        actionId = null;
        approve = false;
        return false;
    }

    private static bool TryExtractConfirmationPromptId(
        string text,
        out string? confirmationId)
    {
        confirmationId = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var approveMatch = ApproveCommandRegex.Match(text);
        var rejectMatch = RejectCommandRegex.Match(text);
        if (!approveMatch.Success && !rejectMatch.Success)
        {
            return false;
        }

        if (approveMatch.Success && rejectMatch.Success)
        {
            var approveId = approveMatch.Groups[1].Value;
            var rejectId = rejectMatch.Groups[1].Value;
            if (!string.Equals(approveId, rejectId, StringComparison.Ordinal))
            {
                return false;
            }

            confirmationId = approveId;
            return !string.IsNullOrWhiteSpace(confirmationId);
        }

        confirmationId = approveMatch.Success
            ? approveMatch.Groups[1].Value
            : rejectMatch.Groups[1].Value;
        return !string.IsNullOrWhiteSpace(confirmationId);
    }

    private static string NormalizeTelegramText(string text)
    {
        var normalized = string.IsNullOrWhiteSpace(text)
            ? "Агент вернул пустой ответ."
            : text.Trim();

        if (normalized.Length <= MaxTelegramMessageLength)
        {
            return normalized;
        }

        return normalized[..(MaxTelegramMessageLength - 32)] + "\n\n[ответ обрезан]";
    }

    private static int ParseRawEventLimit(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return DefaultRawEventLimit;
        }

        return int.TryParse(argument, out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, MaxRawEventLimit)
            : DefaultRawEventLimit;
    }

    private static int ParseProjectCapsuleLimit(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return DefaultProjectCapsuleLimit;
        }

        return int.TryParse(argument, out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, MaxProjectCapsuleLimit)
            : DefaultProjectCapsuleLimit;
    }

    private static int ParseVectorMemoryLimit(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return DefaultVectorMemoryLimit;
        }

        return int.TryParse(argument, out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, MaxVectorMemoryLimit)
            : DefaultVectorMemoryLimit;
    }

    private static string FormatRawEventPayload(string payload)
    {
        var normalizedPayload = payload
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Trim();
        if (normalizedPayload.Length <= MaxRawEventPayloadPreviewLength)
        {
            return normalizedPayload;
        }

        return normalizedPayload[..MaxRawEventPayloadPreviewLength] + "...";
    }

    private static int CountEmbeddingDimensions(string embedding)
    {
        if (string.IsNullOrWhiteSpace(embedding))
        {
            return 0;
        }

        return embedding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static bool IsReasoningActive(LlmExecutionPlan plan) =>
        plan.EffectiveThinkingMode != LlmEffectiveThinkingMode.Disabled;

    private static string FormatUtc(DateTimeOffset? value) =>
        value?.ToString("O") ?? "n/a";

    private static bool ShouldUseReasoningSafetyFallback(LlmExecutionPlan plan) =>
        string.Equals(plan.RequestedThinkingMode, LlmThinkingModes.Auto, StringComparison.Ordinal)
        && plan.UsesTools
        && plan.Capabilities.RequiresReasoningContentRoundTripForToolCalls
        && plan.Capabilities.ThinkingControlStyle == LlmThinkingControlStyle.OpenAiCompatibleThinkingObject;

    private static string FormatThinkingMode(LlmEffectiveThinkingMode mode) =>
        mode switch
        {
            LlmEffectiveThinkingMode.Disabled => "disabled",
            LlmEffectiveThinkingMode.Enabled => "enabled",
            _ => "provider-default",
        };

    /// <summary>
    /// Что: ephemeral preview reasoning в Telegram во время длинного ответа.
    /// Зачем: пользователь должен видеть прогресс, если модель думает заметно дольше обычного.
    /// Как: получает streaming reasoning delta, по таймауту создает временное сообщение, периодически редактирует и удаляет после финального ответа.
    /// </summary>
    private sealed class TelegramReasoningPreviewSession
    {
        private readonly ITelegramBotClientAdapter _client;
        private readonly TimeSpan _delay;
        private readonly long _chatId;
        private readonly ILogger _logger;
        private readonly StringBuilder _reasoningBuffer = new();
        private readonly SemaphoreSlim _sync = new(1, 1);
        private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

        private DateTimeOffset _lastEditedAtUtc = DateTimeOffset.MinValue;
        private int? _messageId;
        private string _lastPreviewText = string.Empty;
        private bool _isCompleted;

        public TelegramReasoningPreviewSession(
            ITelegramBotClientAdapter client,
            long chatId,
            ILogger logger,
            TelegramOptions telegramOptions,
            CancellationToken requestCancellationToken)
        {
            _client = client;
            _chatId = chatId;
            _logger = logger;
            var delaySeconds = Math.Clamp(telegramOptions.ReasoningPreviewDelaySeconds, 1, 30);
            _delay = TimeSpan.FromSeconds(delaySeconds);
            IsEnabled = telegramOptions.ReasoningPreviewEnabled;
            DelaySeconds = delaySeconds;
            if (IsEnabled)
            {
                _ = EnsurePlaceholderAfterDelayAsync(requestCancellationToken);
            }
        }

        public bool IsEnabled { get; }

        public int DelaySeconds { get; }

        public async Task OnReasoningUpdateAsync(
            AgentRuntimeReasoningUpdate update,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(update.TextDelta))
            {
                return;
            }

            await _sync.WaitAsync(cancellationToken);
            try
            {
                if (_isCompleted)
                {
                    return;
                }

                _reasoningBuffer.Append(update.TextDelta);

                var now = DateTimeOffset.UtcNow;
                if (now - _startedAtUtc < _delay)
                {
                    return;
                }

                var previewText = BuildPreviewText(_reasoningBuffer.ToString());
                if (string.Equals(previewText, _lastPreviewText, StringComparison.Ordinal))
                {
                    return;
                }

                if (_messageId is null)
                {
                    _messageId = await _client.SendMessageWithIdAsync(
                        _chatId,
                        previewText,
                        cancellationToken);
                    _lastPreviewText = previewText;
                    _lastEditedAtUtc = now;
                    _logger.LogInformation(
                        "Telegram reasoning preview created for chat {TelegramChatId}.",
                        _chatId);
                    return;
                }

                if (now - _lastEditedAtUtc < ReasoningPreviewEditInterval)
                {
                    return;
                }

                await _client.EditMessageTextAsync(
                    _chatId,
                    _messageId.Value,
                    previewText,
                    cancellationToken);
                _lastPreviewText = previewText;
                _lastEditedAtUtc = now;
                _logger.LogDebug(
                    "Telegram reasoning preview updated for chat {TelegramChatId}; text length {PreviewLength}.",
                    _chatId,
                    previewText.Length);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Failed to update Telegram reasoning preview for chat {TelegramChatId}.",
                    _chatId);
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (!IsEnabled)
            {
                return;
            }

            await _sync.WaitAsync(cancellationToken);
            try
            {
                if (_isCompleted)
                {
                    return;
                }

                _isCompleted = true;

                if (_messageId is null)
                {
                    return;
                }

                await _client.DeleteMessageAsync(
                    _chatId,
                    _messageId.Value,
                    cancellationToken);
                _logger.LogInformation(
                    "Telegram reasoning preview removed for chat {TelegramChatId}.",
                    _chatId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Failed to remove Telegram reasoning preview for chat {TelegramChatId}.",
                    _chatId);
            }
            finally
            {
                _sync.Release();
            }
        }

        private async Task EnsurePlaceholderAfterDelayAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_delay, cancellationToken);
                await _sync.WaitAsync(cancellationToken);
                try
                {
                    if (_isCompleted || _messageId is not null)
                    {
                        return;
                    }

                    var previewText = BuildPreviewText(string.Empty);
                    _messageId = await _client.SendMessageWithIdAsync(
                        _chatId,
                        previewText,
                        cancellationToken);
                    _lastPreviewText = previewText;
                    _lastEditedAtUtc = DateTimeOffset.UtcNow;
                    _logger.LogInformation(
                        "Telegram reasoning preview fallback created for chat {TelegramChatId} after delay {DelaySeconds}s.",
                        _chatId,
                        DelaySeconds);
                }
                finally
                {
                    _sync.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Failed to create Telegram reasoning preview fallback for chat {TelegramChatId}.",
                    _chatId);
            }
        }

        private static string BuildPreviewText(string rawReasoning)
        {
            var normalized = rawReasoning
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "Думаю над ответом...";
            }

            if (normalized.Length > MaxReasoningPreviewLength)
            {
                normalized = normalized[..MaxReasoningPreviewLength] + "...";
            }

            return string.Join(
                Environment.NewLine,
                "Промежуточные рассуждения:",
                normalized);
        }
    }

    private async Task<T> ExecuteWithTypingIndicatorAsync<T>(
        ITelegramBotClientAdapter client,
        long chatId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var typingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var typingTask = SendTypingLoopAsync(client, chatId, typingCancellation.Token);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            typingCancellation.Cancel();
            try
            {
                await typingTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task SendTypingLoopAsync(
        ITelegramBotClientAdapter client,
        long chatId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await client.SendTypingAsync(chatId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Failed to send Telegram typing indicator for chat {TelegramChatId}.",
                    chatId);
            }

            try
            {
                await Task.Delay(TypingIndicatorInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static string FormatPersistedSummary(ConversationSummaryMemory summary) =>
        string.Join(
            Environment.NewLine,
            $"Summary version: {summary.SummaryVersion}",
            $"Updated (UTC): {summary.UpdatedAtUtc:O}",
            $"Source last message id: {summary.SourceLastMessageId}",
            string.Empty,
            summary.Summary);

}
