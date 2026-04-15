using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.HomeAssistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private const int DefaultProjectCapsuleLimit = 6;
    private const int MaxProjectCapsuleLimit = 12;
    private const string TelegramTransportName = "telegram";
    private static readonly TimeSpan TypingIndicatorInterval = TimeSpan.FromSeconds(4);

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
                "Привет. Пиши обычным текстом, я отвечу через агента. /think <вопрос> запускает deep reasoning без tools. /status покажет статус, /resetContext очистит контекст этого чата, /showSummary покажет persisted summary, /refreshSummary принудительно пересоберет persisted summary, /showRawEvents [N] покажет последние сырые события памяти, /showCapsules [N] покажет project capsules, /refreshCapsules обновит project capsules из raw events. Для действий с домом используй /approve <id> или /reject <id>, когда агент попросит подтверждение.",
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
            LlmExecutionProfile.ToolEnabled,
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
        LlmExecutionProfile executionProfile,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Dialogue request telegram-{TelegramUpdateId} started for chat {TelegramChatId} with profile {ExecutionProfile}; text length {TextLength}.",
            updateId,
            chatId,
            executionProfile,
            text.Length);
        var response = await ExecuteWithTypingIndicatorAsync(
            client,
            chatId,
            ct => _dialogueService.SendUserMessageAsync(
                DialogueRequest.Create(
                    conversation,
                    text,
                    correlationId: $"telegram-{updateId}",
                    executionProfile: executionProfile),
                ct),
            cancellationToken);
        _logger.LogInformation(
            "Dialogue request telegram-{TelegramUpdateId} completed for chat {TelegramChatId}; configured {IsConfigured}; response length {ResponseLength}.",
            updateId,
            chatId,
            response.IsConfigured,
            response.Text.Length);

        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(response.Text),
            cancellationToken);
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
            $"Uptime: {status.Uptime}",
            $"Configuration: {status.ConfigurationMode}",
            $"HA MCP: {FormatMcpStatus(mcpDiscovery)}",
            $"Context(stored): {contextSnapshot.StoredMessageCount} messages",
            $"RawEvents(stored): {contextSnapshot.RawEventCount} events",
            $"VectorMemory(stored): {contextSnapshot.VectorMemoryCount} entries",
            $"ProjectCapsules(stored): {contextSnapshot.ProjectCapsuleCount} entries",
            $"ProjectCapsules(lastSourceEventId): {contextSnapshot.ProjectCapsuleLatestSourceEventId}",
            $"ProjectCapsules(lastUpdatedUtc): {FormatUtc(contextSnapshot.ProjectCapsuleLastUpdatedAtUtc)}",
            $"ProjectCapsules(extraction): lastRawEventId {contextSnapshot.ProjectCapsuleLastProcessedRawEventId}, lastExtractionUtc {FormatUtc(contextSnapshot.ProjectCapsuleLastExtractionAtUtc)}, runs {contextSnapshot.ProjectCapsuleExtractionRunsCount}",
            $"Context(loaded): {contextSnapshot.LoadedHistoryMessageCount} / {contextSnapshot.MaxContextMessages} messages",
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
