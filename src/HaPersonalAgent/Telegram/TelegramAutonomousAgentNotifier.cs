using System.Globalization;
using HaPersonalAgent.Autonomous;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Dialogue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: доставка брифа автономного агента в Telegram (адаптер порта IAutonomousAgentNotifier).
/// Зачем: Telegram — лишь один транспорт; исполнитель агента о нём не знает, поэтому реализация живёт здесь, а не в подсистеме агентов.
/// Как: форматирует бриф, нарезает под лимит Telegram и отправляет; id ПОСЛЕДНЕГО сообщения возвращается как якорь для reply
/// (именно там вопросы и подсказка «ответь реплаем»). Сам бриф записывается как системное уведомление в raw events — по правилу AGENTS.md
/// исходящие уведомления не должны попадать в диалоговую память как реплики user/assistant.
/// </summary>
public sealed class TelegramAutonomousAgentNotifier : IAutonomousAgentNotifier
{
    public const string BriefNotificationKind = "autonomous.agent_brief";

    /// <summary>Callback-префиксы кнопок брифа; по ним TelegramUpdateHandler маршрутизирует нажатия (HPA-038).</summary>
    public const string SnoozeCallbackPrefix = "agentbrief:snooze:";
    public const string DismissCallbackPrefix = "agentbrief:dismiss:";

    private const string TelegramTransportName = "telegram";

    private readonly ITelegramBotClientAdapterFactory _clientFactory;
    private readonly IOptions<TelegramOptions> _telegramOptions;
    private readonly DialogueService _dialogueService;
    private readonly ILogger<TelegramAutonomousAgentNotifier> _logger;

    public TelegramAutonomousAgentNotifier(
        ITelegramBotClientAdapterFactory clientFactory,
        IOptions<TelegramOptions> telegramOptions,
        DialogueService dialogueService,
        ILogger<TelegramAutonomousAgentNotifier> logger)
    {
        _clientFactory = clientFactory;
        _telegramOptions = telegramOptions;
        _dialogueService = dialogueService;
        _logger = logger;
    }

    public async Task<string?> DeliverAsync(
        AutonomousAgentDefinition definition,
        AutonomousAgentRun run,
        AutonomousRunOutput output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(output);

        if (definition.DeliveryTelegramChatId is not { } chatId)
        {
            _logger.LogInformation(
                "Autonomous agent {AgentId} has no Telegram target; its brief stays in the Web UI only.",
                definition.Id);
            return null;
        }

        var botToken = _telegramOptions.Value.BotToken;
        if (string.IsNullOrWhiteSpace(botToken))
        {
            _logger.LogWarning(
                "Autonomous agent {AgentId} targets Telegram chat {ChatId}, but no bot token is configured; brief not delivered.",
                definition.Id,
                chatId);
            return null;
        }

        var brief = AutonomousAgentBriefFormatter.BuildBrief(definition, output);
        var chunks = AutonomousAgentBriefFormatter.Chunk(brief);

        try
        {
            var client = _clientFactory.Create(botToken);
            int? lastMessageId = null;
            for (var index = 0; index < chunks.Count; index++)
            {
                var isLast = index == chunks.Count - 1;

                // К последнему чанку крепим кнопки только когда есть открытые вопросы: «Отложить» (ответлю позже)
                // и «Не актуально» (сними эту ветку). Обычный ответ по-прежнему делается реплаем (HPA-032).
                if (isLast && output.Questions.Count > 0)
                {
                    lastMessageId = await client.SendMessageWithButtonsAsync(
                        chatId,
                        chunks[index],
                        new[]
                        {
                            ("⏭ Отложить", $"{SnoozeCallbackPrefix}{run.Id}"),
                            ("🚫 Не актуально", $"{DismissCallbackPrefix}{run.Id}"),
                        },
                        cancellationToken);
                }
                else
                {
                    lastMessageId = await client.SendMessageWithIdAsync(chatId, chunks[index], cancellationToken);
                }
            }

            await RecordNotificationAsync(definition, run, chatId, brief, cancellationToken);

            _logger.LogInformation(
                "Autonomous agent {AgentId} delivered its brief to Telegram chat {ChatId} in {ChunkCount} message(s); reply anchor {MessageId}.",
                definition.Id,
                chatId,
                chunks.Count,
                lastMessageId);

            return lastMessageId?.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Недоставленный бриф не должен ронять запуск: сводка уже сохранена и видна в Web UI.
            _logger.LogWarning(
                exception,
                "Failed to deliver the brief of autonomous agent {AgentId} to Telegram chat {ChatId}.",
                definition.Id,
                chatId);
            return null;
        }
    }

    private async Task RecordNotificationAsync(
        AutonomousAgentDefinition definition,
        AutonomousAgentRun run,
        long chatId,
        string brief,
        CancellationToken cancellationToken)
    {
        try
        {
            var chatIdText = chatId.ToString(CultureInfo.InvariantCulture);
            await _dialogueService.RecordSystemNotificationAsync(
                DialogueSystemNotification.Create(
                    DialogueConversation.Create(TelegramTransportName, chatIdText, chatIdText),
                    BriefNotificationKind,
                    brief,
                    sourceId: $"autonomous:{definition.Id}:{run.Id}"),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(
                exception,
                "Failed to record the autonomous brief of agent {AgentId} as a system notification.",
                definition.Id);
        }
    }
}
