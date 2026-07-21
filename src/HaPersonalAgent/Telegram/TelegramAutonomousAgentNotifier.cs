using System.Globalization;
using System.Text;
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

    /// <summary>Callback-префиксы кнопок предложенных действий (HPA-035): одобрить/отклонить pending confirmation по его id.</summary>
    public const string ApproveActionCallbackPrefix = "agentaction:approve:";
    public const string RejectActionCallbackPrefix = "agentaction:reject:";

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
        IReadOnlyList<AutonomousProposedAction> proposedActions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(proposedActions);

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

            // HPA-035: предложенные действия идут ОТДЕЛЬНЫМИ сообщениями с кнопками Одобрить/Отклонить — так якорь
            // для reply остаётся на самом брифе, а каждое действие одобряется/отклоняется независимо по своему id.
            await SendProposedActionsAsync(client, chatId, proposedActions, cancellationToken);

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

    public async Task<IReadOnlyList<AutonomousDigestAnchor>> DeliverDigestAsync(
        IReadOnlyList<AutonomousRunDelivery> deliveries,
        IReadOnlyList<string> connections,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(connections);

        var anchors = new List<AutonomousDigestAnchor>();

        // Агенты без Telegram-цели видны только в панели — reply-якоря у них нет.
        foreach (var delivery in deliveries.Where(item => item.Definition.DeliveryTelegramChatId is null))
        {
            anchors.Add(new AutonomousDigestAnchor(delivery.Run.Id, null));
        }

        var byChat = deliveries
            .Where(item => item.Definition.DeliveryTelegramChatId is not null)
            .GroupBy(item => item.Definition.DeliveryTelegramChatId!.Value)
            .ToList();

        var botToken = _telegramOptions.Value.BotToken;
        if (byChat.Count == 0)
        {
            return anchors;
        }

        if (string.IsNullOrWhiteSpace(botToken))
        {
            _logger.LogWarning("Autonomous digest could not be delivered: no Telegram bot token is configured.");
            foreach (var delivery in byChat.SelectMany(group => group))
            {
                anchors.Add(new AutonomousDigestAnchor(delivery.Run.Id, null));
            }

            return anchors;
        }

        var client = _clientFactory.Create(botToken);
        foreach (var group in byChat)
        {
            var chatId = group.Key;
            var items = group.ToList();
            try
            {
                var overview = BuildDigestOverview(items, connections);
                foreach (var chunk in AutonomousAgentBriefFormatter.Chunk(overview))
                {
                    await client.SendMessageWithIdAsync(chatId, chunk, cancellationToken);
                }

                foreach (var delivery in items)
                {
                    var anchorMessageId = await SendQuestionsTailAsync(client, chatId, delivery, cancellationToken);
                    await SendProposedActionsAsync(client, chatId, delivery.ProposedActions, cancellationToken);
                    anchors.Add(new AutonomousDigestAnchor(delivery.Run.Id, anchorMessageId));
                }

                await RecordNotificationAsync(items[0].Definition, items[0].Run, chatId, overview, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Failed to deliver the autonomous digest to Telegram chat {ChatId}.", chatId);
                foreach (var delivery in items)
                {
                    anchors.Add(new AutonomousDigestAnchor(delivery.Run.Id, null));
                }
            }
        }

        return anchors;
    }

    private static string BuildDigestOverview(
        IReadOnlyList<AutonomousRunDelivery> items,
        IReadOnlyList<string> connections)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"📋 Сводка автономных агентов ({items.Count})");

        foreach (var delivery in items)
        {
            builder.AppendLine();
            builder.AppendLine($"▸ {delivery.Definition.Name}");
            if (!string.IsNullOrWhiteSpace(delivery.Output.Summary))
            {
                builder.AppendLine(delivery.Output.Summary.Trim());
            }

            foreach (var finding in delivery.Output.Findings)
            {
                builder.AppendLine($"• {finding}");
            }
        }

        if (connections.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("🔗 Связи между агентами:");
            foreach (var connection in connections)
            {
                builder.AppendLine($"— {connection}");
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Отправляет хвост с вопросами агента и кнопками Отложить/Не актуально, если вопросы есть; возвращает id
    /// этого сообщения как reply-якорь (по нему сопоставится ответ пользователя). Нет вопросов — null.
    /// </summary>
    private async Task<string?> SendQuestionsTailAsync(
        ITelegramBotClientAdapter client,
        long chatId,
        AutonomousRunDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.Output.Questions.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"❓ Вопросы агента «{delivery.Definition.Name}» (ответь реплаем на это сообщение):");
        var index = 1;
        foreach (var question in delivery.Output.Questions)
        {
            builder.AppendLine($"{index}. {question}");
            index++;
        }

        var messageId = await client.SendMessageWithButtonsAsync(
            chatId,
            builder.ToString().Trim(),
            new[]
            {
                ("⏭ Отложить", $"{SnoozeCallbackPrefix}{delivery.Run.Id}"),
                ("🚫 Не актуально", $"{DismissCallbackPrefix}{delivery.Run.Id}"),
            },
            cancellationToken);

        return messageId.ToString(CultureInfo.InvariantCulture);
    }

    private async Task SendProposedActionsAsync(
        ITelegramBotClientAdapter client,
        long chatId,
        IReadOnlyList<AutonomousProposedAction> proposedActions,
        CancellationToken cancellationToken)
    {
        foreach (var action in proposedActions)
        {
            var text = string.Join(
                Environment.NewLine,
                "Предлагаю действие (выполнится только после твоего одобрения):",
                action.Summary,
                $"Риск: {action.Risk}");

            await client.SendMessageWithButtonsAsync(
                chatId,
                text,
                new[]
                {
                    ("✅ Одобрить", $"{ApproveActionCallbackPrefix}{action.Id}"),
                    ("🚫 Отклонить", $"{RejectActionCallbackPrefix}{action.Id}"),
                },
                cancellationToken);
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
