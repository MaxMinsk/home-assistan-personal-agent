using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.HomeAssistant;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(
        DialogueService dialogueService,
        IHomeAssistantMcpClient homeAssistantMcpClient,
        AgentStatusTool statusTool,
        IAgentRuntime agentRuntime,
        ILogger<TelegramUpdateHandler> logger,
        IConfirmationService? confirmationService = null)
    {
        _dialogueService = dialogueService;
        _homeAssistantMcpClient = homeAssistantMcpClient;
        _statusTool = statusTool;
        _agentRuntime = agentRuntime;
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
        if (!IsAllowed(telegramOptions, userId))
        {
            _logger.LogWarning(
                "Ignoring Telegram update {TelegramUpdateId} from non-allowlisted user {TelegramUserId}",
                update.Id,
                userId);
            return;
        }

        var chatId = message.Chat.Id;
        var conversation = CreateConversation(chatId, userId);
        var text = message.Text.Trim();

        if (IsCommand(text, "/start"))
        {
            await client.SendMessageAsync(
                chatId,
                "Привет. Пиши обычным текстом, я отвечу через агента. /status покажет статус, /resetContext очистит контекст этого чата. Для действий с домом используй /approve <id> или /reject <id>, когда агент попросит подтверждение.",
                cancellationToken);
            return;
        }

        if (IsCommand(text, "/status"))
        {
            await client.SendMessageAsync(
                chatId,
                await FormatStatusAsync(cancellationToken),
                cancellationToken);
            return;
        }

        if (IsCommand(text, "/resetContext"))
        {
            await _dialogueService.ResetAsync(conversation, cancellationToken);
            await client.SendMessageAsync(
                chatId,
                "Контекст этого чата очищен.",
                cancellationToken);
            return;
        }

        if (TryReadCommandArgument(text, "/approve", out var approveActionId))
        {
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
            await HandleConfirmationCommandAsync(
                client,
                chatId,
                conversation,
                rejectActionId,
                approve: false,
                cancellationToken);
            return;
        }

        await HandleAgentMessageAsync(
            client,
            update.Id,
            chatId,
            conversation,
            text,
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

        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(result.Message),
            cancellationToken);
    }

    private async Task HandleAgentMessageAsync(
        ITelegramBotClientAdapter client,
        int updateId,
        long chatId,
        DialogueConversation conversation,
        string text,
        CancellationToken cancellationToken)
    {
        var response = await _dialogueService.SendUserMessageAsync(
            DialogueRequest.Create(
                conversation,
                text,
                correlationId: $"telegram-{updateId}"),
            cancellationToken);

        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(response.Text),
            cancellationToken);
    }

    private async Task<string> FormatStatusAsync(CancellationToken cancellationToken)
    {
        var status = _statusTool.GetStatus();
        var runtimeHealth = _agentRuntime.GetHealth();
        var mcpDiscovery = await _homeAssistantMcpClient.DiscoverAsync(cancellationToken);
        var runtimeText = runtimeHealth.IsConfigured
            ? "configured"
            : $"not configured ({runtimeHealth.Reason})";

        return string.Join(
            Environment.NewLine,
            $"{status.ApplicationName} {status.Version}",
            $"Runtime: {runtimeText}",
            $"LLM: {runtimeHealth.Provider} / {runtimeHealth.Model}",
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
}
