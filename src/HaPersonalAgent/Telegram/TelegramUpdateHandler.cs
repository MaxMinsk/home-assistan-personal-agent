using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;

namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: обработчик одного Telegram update.
/// Зачем: long polling gateway должен оставаться транспортным циклом, а команды, allowlist и agent invocation жить в тестируемом классе.
/// Как: проверяет автора по allowlist, маршрутизирует /start, /status, /resetContext, а обычный текст отправляет в IAgentRuntime с историей из SQLite.
/// </summary>
public sealed class TelegramUpdateHandler
{
    private const int MaxTelegramMessageLength = 4096;

    private readonly IAgentRuntime _agentRuntime;
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly AgentStateRepository _stateRepository;
    private readonly AgentStatusTool _statusTool;
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(
        IAgentRuntime agentRuntime,
        IOptions<AgentOptions> agentOptions,
        AgentStateRepository stateRepository,
        AgentStatusTool statusTool,
        ILogger<TelegramUpdateHandler> logger)
    {
        _agentRuntime = agentRuntime;
        _agentOptions = agentOptions;
        _stateRepository = stateRepository;
        _statusTool = statusTool;
        _logger = logger;
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
        var conversationKey = CreateConversationKey(chatId, userId);
        var text = message.Text.Trim();

        if (IsCommand(text, "/start"))
        {
            await client.SendMessageAsync(
                chatId,
                "Привет. Пиши обычным текстом, я отвечу через агента. /status покажет статус, /resetContext очистит контекст этого чата.",
                cancellationToken);
            return;
        }

        if (IsCommand(text, "/status"))
        {
            await client.SendMessageAsync(
                chatId,
                FormatStatus(),
                cancellationToken);
            return;
        }

        if (IsCommand(text, "/resetContext"))
        {
            await _stateRepository.ClearConversationMessagesAsync(conversationKey, cancellationToken);
            await client.SendMessageAsync(
                chatId,
                "Контекст этого чата очищен.",
                cancellationToken);
            return;
        }

        await HandleAgentMessageAsync(
            client,
            update.Id,
            chatId,
            conversationKey,
            text,
            cancellationToken);
    }

    private async Task HandleAgentMessageAsync(
        ITelegramBotClientAdapter client,
        int updateId,
        long chatId,
        string conversationKey,
        string text,
        CancellationToken cancellationToken)
    {
        var maxMessages = GetMaxContextMessages();
        var history = await _stateRepository.GetConversationMessagesAsync(
            conversationKey,
            maxMessages,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var response = await _agentRuntime.SendAsync(
            text,
            AgentContext.Create(
                correlationId: $"telegram-{updateId}",
                conversationMessages: history),
            cancellationToken);

        await client.SendMessageAsync(
            chatId,
            NormalizeTelegramText(response.Text),
            cancellationToken);

        if (!response.IsConfigured)
        {
            return;
        }

        await _stateRepository.AppendConversationMessagesAsync(
            conversationKey,
            new[]
            {
                new AgentConversationMessage(AgentConversationRole.User, text, now),
                new AgentConversationMessage(AgentConversationRole.Assistant, response.Text, DateTimeOffset.UtcNow),
            },
            cancellationToken);

        await _stateRepository.TrimConversationMessagesAsync(
            conversationKey,
            maxMessages,
            cancellationToken);
    }

    private int GetMaxContextMessages()
    {
        var maxTurns = Math.Clamp(_agentOptions.Value.ConversationContextMaxTurns, 0, 50);
        return maxTurns * 2;
    }

    private string FormatStatus()
    {
        var status = _statusTool.GetStatus();
        var runtimeHealth = _agentRuntime.GetHealth();
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
            $"Telegram allowlist users: {status.Configuration.AllowedTelegramUserCount}",
            $"Context window: {_agentOptions.Value.ConversationContextMaxTurns} turns");
    }

    private static bool IsAllowed(TelegramOptions telegramOptions, long userId) =>
        telegramOptions.AllowedUserIds.Contains(userId);

    private static string CreateConversationKey(long chatId, long userId) =>
        $"telegram:{chatId}:{userId}";

    private static bool IsCommand(string text, string command)
    {
        var commandToken = text.Split(' ', 2, StringSplitOptions.TrimEntries)[0];
        var commandWithoutBotSuffix = commandToken.Split('@', 2, StringSplitOptions.TrimEntries)[0];

        return string.Equals(commandWithoutBotSuffix, command, StringComparison.OrdinalIgnoreCase);
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
