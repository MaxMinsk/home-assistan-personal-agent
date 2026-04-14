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

            await service.ResetAsync(conversation, CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);

            Assert.Empty(stored);
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
        public FakeAgentRuntime(string responseText)
        {
            ResponseText = responseText;
        }

        public string ResponseText { get; }

        public List<(string Message, AgentContext Context)> Calls { get; } = new();

        public AgentRuntimeHealth GetHealth() =>
            AgentRuntimeHealth.Configured(new LlmOptions());

        public Task<AgentRuntimeResponse> SendAsync(
            string message,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            Calls.Add((message, context));

            return Task.FromResult(new AgentRuntimeResponse(
                context.CorrelationId,
                IsConfigured: true,
                ResponseText,
                GetHealth()));
        }
    }
}
