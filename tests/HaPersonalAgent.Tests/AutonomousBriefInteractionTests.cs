using HaPersonalAgent.Agent;
using HaPersonalAgent.Autonomous;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.Storage;
using HaPersonalAgent.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты интерактивного брифа (HPA-038): findings в контракте/брифе, кнопки в Telegram и распространение «Не актуально».
/// Зачем: бриф должен быть сканируемым (тезисы + вопросы), а решения «отложить/не актуально» — реально доезжать до следующего запуска.
/// Как: чистые проверки парсера/форматтера + фейковый Telegram-адаптер для нотификатора + реальный repo/сервис для dismiss.
/// </summary>
public class AutonomousBriefInteractionTests
{
    [Fact]
    public void Parser_reads_findings()
    {
        const string response = """
            {
              "summary": "рамка",
              "findings": ["тезис 1", "тезис 2", "тезис 3"],
              "questions": ["вопрос?"]
            }
            """;

        var output = AutonomousRunOutputParser.Parse(response, maxDurableFacts: 3);

        Assert.Equal("рамка", output.Summary);
        Assert.Equal(3, output.Findings.Count);
        Assert.Equal("тезис 1", output.Findings[0]);
    }

    [Fact]
    public void Brief_renders_findings_as_bullets_and_numbers_questions()
    {
        var definition = AutonomousAgentDefinition.Create("Агент", "миссия", AutonomousAgentScheduleKind.Weekly);
        var output = new AutonomousRunOutput(
            "Итоги за неделю",
            new[] { "Находка A", "Находка B" },
            new[] { "Уточнить бюджет?" },
            Array.Empty<string>(),
            "дальше");

        var brief = AutonomousAgentBriefFormatter.BuildBrief(definition, output);

        Assert.Contains("• Находка A", brief, StringComparison.Ordinal);
        Assert.Contains("• Находка B", brief, StringComparison.Ordinal);
        Assert.Contains("1. Уточнить бюджет?", brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Brief_with_questions_gets_snooze_and_dismiss_buttons_anchored_to_the_run()
    {
        var adapter = new FakeAdapter();
        var (notifier, _) = await CreateNotifierAsync(adapter);
        var definition = AutonomousAgentDefinition.Create(
            "Агент",
            "миссия",
            AutonomousAgentScheduleKind.Weekly,
            deliveryTelegramChatId: 555);
        var run = AutonomousAgentRun.Start(definition.Id);
        var output = new AutonomousRunOutput(
            "рамка",
            new[] { "находка" },
            new[] { "вопрос?" },
            Array.Empty<string>(),
            "дальше");

        var deliveredMessageId = await notifier.DeliverAsync(
            definition,
            run,
            output,
            Array.Empty<AutonomousProposedAction>(),
            CancellationToken.None);

        var buttonMessage = Assert.Single(adapter.SentButtonMessages);
        Assert.Equal(2, buttonMessage.Buttons.Count);
        Assert.Contains(buttonMessage.Buttons, b => b.CallbackData == $"{TelegramAutonomousAgentNotifier.SnoozeCallbackPrefix}{run.Id}");
        Assert.Contains(buttonMessage.Buttons, b => b.CallbackData == $"{TelegramAutonomousAgentNotifier.DismissCallbackPrefix}{run.Id}");
        // Якорь reply — это сообщение с кнопками (там же вопросы).
        Assert.Equal(buttonMessage.MessageId.ToString(System.Globalization.CultureInfo.InvariantCulture), deliveredMessageId);
    }

    [Fact]
    public async Task Brief_without_questions_has_no_buttons()
    {
        var adapter = new FakeAdapter();
        var (notifier, _) = await CreateNotifierAsync(adapter);
        var definition = AutonomousAgentDefinition.Create(
            "Агент",
            "миссия",
            AutonomousAgentScheduleKind.Weekly,
            deliveryTelegramChatId: 555);

        await notifier.DeliverAsync(
            definition,
            AutonomousAgentRun.Start(definition.Id),
            new AutonomousRunOutput("рамка", new[] { "находка" }, Array.Empty<string>(), Array.Empty<string>(), null),
            Array.Empty<AutonomousProposedAction>(),
            CancellationToken.None);

        Assert.Empty(adapter.SentButtonMessages);
        Assert.NotEmpty(adapter.PlainMessages);
    }

    [Fact]
    public async Task Dismiss_clears_open_questions_and_queues_a_move_on_note_for_the_next_run()
    {
        var databasePath = TempDbPath();
        try
        {
            var repository = new SqliteAutonomousAgentRepository(new SqliteConnectionFactory(
                Options.Create(new AgentOptions { StateDatabasePath = databasePath })));
            var service = new AutonomousAgentService(repository, NullLogger<AutonomousAgentService>.Instance);

            var definition = AutonomousAgentDefinition.Create("Агент", "миссия", AutonomousAgentScheduleKind.Weekly);
            await repository.UpsertDefinitionAsync(definition, CancellationToken.None);
            await repository.UpsertContinuityAsync(
                AutonomousAgentContinuity.Empty(definition.Id) with { OpenQuestions = "старый вопрос?" },
                CancellationToken.None);
            var run = AutonomousAgentRun.Start(definition.Id);
            await repository.AppendRunAsync(run, CancellationToken.None);

            var result = await service.DismissBriefThreadsAsync(
                definition.Id,
                run.Id,
                AutonomousAgentReplySource.Telegram,
                CancellationToken.None);

            Assert.NotNull(result);

            // Открытые вопросы сняты, чтобы агент не поднимал их снова.
            var continuity = await repository.GetContinuityAsync(definition.Id, CancellationToken.None);
            Assert.Null(continuity!.OpenQuestions);

            // В очередь легла инструкция двигаться дальше — она попадёт в контекст следующего запуска.
            var pending = await repository.GetPendingRepliesAsync(definition.Id, CancellationToken.None);
            var entry = Assert.Single(pending);
            Assert.Contains("неактуальны", entry.Text, StringComparison.Ordinal);
        }
        finally
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Proposed_actions_are_delivered_as_messages_with_approve_and_reject_buttons()
    {
        // HPA-035: каждое предложенное действие идёт отдельным сообщением с кнопками одобрить/отклонить по его id.
        var adapter = new FakeAdapter();
        var (notifier, _) = await CreateNotifierAsync(adapter);
        var definition = AutonomousAgentDefinition.Create(
            "Агент",
            "миссия",
            AutonomousAgentScheduleKind.Weekly,
            deliveryTelegramChatId: 555);
        var output = new AutonomousRunOutput(
            "рамка",
            new[] { "находка" },
            Array.Empty<string>(),
            Array.Empty<string>(),
            null);

        await notifier.DeliverAsync(
            definition,
            AutonomousAgentRun.Start(definition.Id),
            output,
            new[] { new AutonomousProposedAction("cid-1", "Включить свет в гостиной", "Изменит состояние устройства") },
            CancellationToken.None);

        var actionMessage = Assert.Single(adapter.SentButtonMessages);
        Assert.Contains("Включить свет в гостиной", actionMessage.Text, StringComparison.Ordinal);
        Assert.Contains(actionMessage.Buttons, b => b.CallbackData == $"{TelegramAutonomousAgentNotifier.ApproveActionCallbackPrefix}cid-1");
        Assert.Contains(actionMessage.Buttons, b => b.CallbackData == $"{TelegramAutonomousAgentNotifier.RejectActionCallbackPrefix}cid-1");
    }

    [Fact]
    public async Task Digest_sends_one_overview_plus_per_agent_question_tails_with_anchors()
    {
        // HPA-039: несколько агентов -> ОДИН обзор (без кнопок) + по-агентные вопросы с кнопками и reply-якорем.
        var adapter = new FakeAdapter();
        var (notifier, _) = await CreateNotifierAsync(adapter);
        var withQuestions = DigestDelivery("Бизнес", chatId: 555, questions: new[] { "Какой бюджет?" });
        var withoutQuestions = DigestDelivery("Сканер", chatId: 555, questions: Array.Empty<string>());

        var anchors = await notifier.DeliverDigestAsync(
            new[] { withQuestions, withoutQuestions },
            new[] { "«Бизнес» и «Сканер» пересекаются по офису" },
            CancellationToken.None);

        // Обзор — обычное сообщение (без кнопок), содержит обоих агентов и блок связей.
        Assert.Contains(
            adapter.PlainMessages,
            m => m.Text.Contains("Сводка автономных агентов", StringComparison.Ordinal)
                && m.Text.Contains("Бизнес", StringComparison.Ordinal)
                && m.Text.Contains("Сканер", StringComparison.Ordinal)
                && m.Text.Contains("Связи", StringComparison.Ordinal));

        // У агента с вопросами — сообщение с кнопками Отложить/Не актуально; якорь возвращён.
        var buttonMessage = Assert.Single(adapter.SentButtonMessages);
        Assert.Contains("Какой бюджет?", buttonMessage.Text, StringComparison.Ordinal);
        Assert.Contains(
            buttonMessage.Buttons,
            b => b.CallbackData == $"{TelegramAutonomousAgentNotifier.SnoozeCallbackPrefix}{withQuestions.Run.Id}");
        Assert.Equal(
            buttonMessage.MessageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            anchors.Single(a => a.RunId == withQuestions.Run.Id).DeliveredMessageId);

        // Агент без вопросов — reply-якоря нет.
        Assert.Null(anchors.Single(a => a.RunId == withoutQuestions.Run.Id).DeliveredMessageId);
    }

    private static AutonomousRunDelivery DigestDelivery(string name, long chatId, string[] questions)
    {
        var definition = AutonomousAgentDefinition.Create(
            name,
            "миссия",
            AutonomousAgentScheduleKind.Weekly,
            deliveryTelegramChatId: chatId);
        var output = new AutonomousRunOutput(
            $"сводка {name}",
            new[] { $"находка {name}" },
            questions,
            Array.Empty<string>(),
            null);
        return new AutonomousRunDelivery(
            definition,
            AutonomousAgentRun.Start(definition.Id),
            output,
            Array.Empty<AutonomousProposedAction>());
    }

    private static async Task<(TelegramAutonomousAgentNotifier Notifier, DialogueService Dialogue)> CreateNotifierAsync(
        FakeAdapter adapter)
    {
        var databasePath = TempDbPath();
        var repository = new AgentStateRepository(new SqliteConnectionFactory(
            Options.Create(new AgentOptions { StateDatabasePath = databasePath })));
        await repository.InitializeAsync(CancellationToken.None);
        var memoryStore = new SqliteConversationMemoryStore(repository);
        var boundedProvider = new BoundedChatHistoryProvider(
            memoryStore,
            NullLogger<BoundedChatHistoryProvider>.Instance);
        var dialogue = new DialogueService(
            new NoopAgentRuntime(),
            Options.Create(new AgentOptions()),
            boundedProvider,
            memoryStore,
            NullLogger<DialogueService>.Instance);

        var notifier = new TelegramAutonomousAgentNotifier(
            new FakeFactory(adapter),
            Options.Create(new TelegramOptions { BotToken = "token" }),
            dialogue,
            NullLogger<TelegramAutonomousAgentNotifier>.Instance);

        return (notifier, dialogue);
    }

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), "ha-personal-agent-tests", Guid.NewGuid().ToString("N"), "state.sqlite");

    private sealed class FakeFactory : ITelegramBotClientAdapterFactory
    {
        private readonly ITelegramBotClientAdapter _adapter;

        public FakeFactory(ITelegramBotClientAdapter adapter) => _adapter = adapter;

        public ITelegramBotClientAdapter Create(string botToken) => _adapter;
    }

    private sealed class NoopAgentRuntime : IAgentRuntime
    {
        public AgentRuntimeHealth GetHealth() => AgentRuntimeHealth.Configured(new LlmOptions());

        public Task<AgentRuntimeResponse> SendAsync(
            string message,
            AgentContext context,
            Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The notifier never invokes the runtime.");
    }

    /// <summary>Фейк Telegram-адаптера: нужны только отправка текста и сообщения с кнопками.</summary>
    private sealed class FakeAdapter : ITelegramBotClientAdapter
    {
        private int _nextMessageId;

        public List<(long ChatId, int MessageId, string Text)> PlainMessages { get; } = new();

        public List<(long ChatId, int MessageId, string Text, IReadOnlyList<(string Label, string CallbackData)> Buttons)> SentButtonMessages { get; } = new();

        public Task<int> SendMessageWithIdAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            var id = ++_nextMessageId;
            PlainMessages.Add((chatId, id, text));
            return Task.FromResult(id);
        }

        public Task<int> SendMessageWithButtonsAsync(
            long chatId,
            string text,
            IReadOnlyList<(string Label, string CallbackData)> buttons,
            CancellationToken cancellationToken)
        {
            var id = ++_nextMessageId;
            SentButtonMessages.Add((chatId, id, text, buttons));
            return Task.FromResult(id);
        }

        public Task DeleteWebhookAsync(bool dropPendingUpdates, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<Update>> GetUpdatesAsync(
            int? offset,
            int limit,
            int timeoutSeconds,
            IReadOnlyList<UpdateType> allowedUpdates,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Update>>(Array.Empty<Update>());

        public Task SendTypingAsync(long chatId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetCommandsAsync(
            IReadOnlyList<(string Command, string Description)> commands,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendConfirmationMessageAsync(long chatId, string text, string confirmationId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearInlineKeyboardAsync(long chatId, int messageId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, bool showAlert, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
