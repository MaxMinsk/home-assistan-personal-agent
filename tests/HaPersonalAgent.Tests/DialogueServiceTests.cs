using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты transport-agnostic dialogue слоя.
/// Зачем: память диалога должна работать одинаково для Telegram, будущего Web UI и системных notifications.
/// Как: использует временную SQLite базу и fake IAgentRuntime без реальных Telegram или LLM сетевых вызовов.
/// </summary>
public class DialogueServiceTests
{
    [Fact]
    public async Task User_message_is_stored_under_transport_agnostic_conversation_key()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("hello back");
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("web-ui", "session-1", "user-1");

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "hello", "web-test"),
                CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);

            Assert.Equal("web-test", runtime.Calls.Single().Context.CorrelationId);
            Assert.Equal("web-ui:session-1:user-1", runtime.Calls.Single().Context.ConversationKey);
            Assert.Equal("user-1", runtime.Calls.Single().Context.ParticipantId);
            Assert.Equal(new[] { "hello", "hello back" }, stored.Select(message => message.Text));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Reset_clears_context_through_conversation_abstraction()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var service = CreateService(repository, new FakeAgentRuntime("unused"));
            var conversation = DialogueConversation.Create("web-ui", "session-1", "user-1");
            await repository.AppendConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                new[] { new AgentConversationMessage(AgentConversationRole.User, "clear me", DateTimeOffset.UtcNow) },
                CancellationToken.None);
            var latestMessageId = await repository.GetLatestConversationMessageIdAsync(
                DialogueConversationKey.Create(conversation),
                CancellationToken.None);
            Assert.True(latestMessageId.HasValue);
            await repository.UpsertConversationSummaryAsync(
                new ConversationSummaryMemory(
                    DialogueConversationKey.Create(conversation),
                    "summary-to-clear",
                    DateTimeOffset.UtcNow,
                    latestMessageId!.Value,
                    SummaryVersion: 1),
                CancellationToken.None);

            await service.ResetAsync(conversation, CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);
            var summary = await repository.GetConversationSummaryAsync(
                DialogueConversationKey.Create(conversation),
                CancellationToken.None);

            Assert.Empty(stored);
            Assert.Null(summary);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task System_notification_is_not_stored_as_dialogue_turn()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("unused");
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            await service.RecordSystemNotificationAsync(
                DialogueSystemNotification.Create(
                    conversation,
                    kind: "camera-alert",
                    text: "Motion detected near the garage.",
                    sourceId: "camera.garage"),
                CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);

            Assert.Empty(stored);
            Assert.Empty(runtime.Calls);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Compaction_notice_is_visible_to_user_but_not_persisted_in_sql_history()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime(new[]
            {
                "[context-summary] Чтобы удержать бюджет контекста, я сжал раннюю часть диалога (1 summarize step)." + Environment.NewLine + Environment.NewLine + "Текущий ответ.",
                "Продолжаем диалог.",
            });
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            var firstResponse = await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Привет", "run-1"),
                CancellationToken.None);

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Что дальше?", "run-2"),
                CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);

            Assert.Equal(4, stored.Count);
            Assert.Equal("Привет", stored[0].Text);
            Assert.Equal(AgentConversationRole.User, stored[0].Role);
            Assert.Equal("Текущий ответ.", stored[1].Text);
            Assert.Equal(AgentConversationRole.Assistant, stored[1].Role);
            Assert.Equal("Что дальше?", stored[2].Text);
            Assert.Equal(AgentConversationRole.User, stored[2].Role);
            Assert.Equal("Продолжаем диалог.", stored[3].Text);
            Assert.Equal(AgentConversationRole.Assistant, stored[3].Role);
            Assert.StartsWith("[context-summary]", firstResponse.Text, StringComparison.Ordinal);

            Assert.Equal(2, runtime.Calls.Count);
            Assert.Equal(2, runtime.Calls[1].Context.ConversationMessages.Count);
            Assert.Equal("Привет", runtime.Calls[1].Context.ConversationMessages[0].Text);
            Assert.Equal("Текущий ответ.", runtime.Calls[1].Context.ConversationMessages[1].Text);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Persisted_summary_candidate_is_saved_and_reused_in_next_runtime_context()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime(new (string Text, string? SummaryCandidate)[]
            {
                ("Первый ответ.", "Persisted summary v1"),
                ("Второй ответ.", null),
            });
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var conversationKey = DialogueConversationKey.Create(conversation);

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Первое сообщение", "run-1"),
                CancellationToken.None);

            var summaryAfterFirstRun = await repository.GetConversationSummaryAsync(
                conversationKey,
                CancellationToken.None);
            Assert.NotNull(summaryAfterFirstRun);
            Assert.Equal("Persisted summary v1", summaryAfterFirstRun.Summary);
            Assert.Equal(1, summaryAfterFirstRun.SummaryVersion);

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Второе сообщение", "run-2"),
                CancellationToken.None);

            Assert.Equal(2, runtime.Calls.Count);
            Assert.Equal("Persisted summary v1", runtime.Calls[1].Context.PersistedSummary);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    private static DialogueService CreateService(
        AgentStateRepository repository,
        FakeAgentRuntime runtime)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });

        return new DialogueService(
            runtime,
            Options.Create(new AgentOptions()),
            repository,
            loggerFactory.CreateLogger<DialogueService>());
    }

    private static AgentStateRepository CreateRepository(string databasePath)
    {
        var options = Options.Create(new AgentOptions
        {
            StateDatabasePath = databasePath,
        });
        var connectionFactory = new SqliteConnectionFactory(options);

        return new AgentStateRepository(connectionFactory);
    }

    private static string CreateTemporaryDatabasePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "ha-personal-agent-tests",
            Guid.NewGuid().ToString("N"),
            "state.sqlite");
    }

    private static void DeleteTemporaryDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Что: fake agent runtime для тестов DialogueService.
    /// Зачем: тесты проверяют memory orchestration без сетевых вызовов к LLM provider.
    /// Как: сохраняет вызовы SendAsync и возвращает заранее заданный ответ.
    /// </summary>
    private sealed class FakeAgentRuntime : IAgentRuntime
    {
        private readonly Queue<(string Text, string? SummaryCandidate)> _responseQueue;

        public FakeAgentRuntime(string responseText)
            : this(new[] { responseText })
        {
        }

        public FakeAgentRuntime(IEnumerable<string> responseTexts)
            : this(responseTexts.Select(text => (Text: text, SummaryCandidate: (string?)null)))
        {
        }

        public FakeAgentRuntime(IEnumerable<(string Text, string? SummaryCandidate)> responseEntries)
        {
            _responseQueue = new Queue<(string Text, string? SummaryCandidate)>(responseEntries);
        }

        public List<(string Message, AgentContext Context)> Calls { get; } = new();

        public AgentRuntimeHealth GetHealth() =>
            AgentRuntimeHealth.Configured(new LlmOptions());

        public Task<AgentRuntimeResponse> SendAsync(
            string message,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add((message, context));
            if (_responseQueue.Count == 0)
            {
                throw new InvalidOperationException("No fake runtime responses left in queue.");
            }
            var responseEntry = _responseQueue.Dequeue();

            return Task.FromResult(new AgentRuntimeResponse(
                context.CorrelationId,
                IsConfigured: true,
                responseEntry.Text,
                GetHealth(),
                responseEntry.SummaryCandidate));
        }
    }
}
