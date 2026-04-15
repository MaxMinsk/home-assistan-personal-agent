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
    private const string TelegramTransportName = "telegram";

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
                "Привет. Пиши обычным текстом, я отвечу через агента. /think <вопрос> запускает deep reasoning без tools. /status покажет статус, /resetContext очистит контекст этого чата, /showSummarized покажет persisted summary. Для действий с домом используй /approve <id> или /reject <id>, когда агент попросит подтверждение.",
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
                await FormatStatusAsync(cancellationToken),
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

        if (IsCommand(text, "/showSummarized"))
        {
            _logger.LogInformation(
                "Telegram update {TelegramUpdateId} routed to /showSummarized command for conversation {ConversationKey}.",
                update.Id,
                DialogueConversationKey.Create(conversation));
            await HandleShowSummarizedCommandAsync(
                client,
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

    private async Task HandleShowSummarizedCommandAsync(
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

        var response = string.Join(
            Environment.NewLine,
            $"Summary version: {summary.SummaryVersion}",
            $"Updated (UTC): {summary.UpdatedAtUtc:O}",
            $"Source last message id: {summary.SourceLastMessageId}",
            string.Empty,
            summary.Summary);
        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(response),
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
        var response = await _dialogueService.SendUserMessageAsync(
            DialogueRequest.Create(
                conversation,
                text,
                correlationId: $"telegram-{updateId}",
                executionProfile: executionProfile),
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

    private async Task<string> FormatStatusAsync(CancellationToken cancellationToken)
    {
        var status = _statusTool.GetStatus();
        var runtimeHealth = _agentRuntime.GetHealth();
        var toolEnabledPlan = _executionPlanner.CreatePlan(_llmOptions.Value, LlmExecutionProfile.ToolEnabled);
        var deepReasoningPlan = _executionPlanner.CreatePlan(_llmOptions.Value, LlmExecutionProfile.DeepReasoning);
        var mcpDiscovery = await _homeAssistantMcpClient.DiscoverAsync(cancellationToken);
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

    private static bool IsReasoningActive(LlmExecutionPlan plan) =>
        plan.EffectiveThinkingMode != LlmEffectiveThinkingMode.Disabled;

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

}
