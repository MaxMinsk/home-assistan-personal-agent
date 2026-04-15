using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты сервиса project capsules поверх raw events.
/// Зачем: нужно зафиксировать идемпотентность refresh и корректную трассировку source_event_id, чтобы derived memory не дрейфовала.
/// Как: использует временный SQLite state store и fake runtime без сетевых вызовов к LLM.
/// </summary>
public class ProjectCapsuleServiceTests
{
    [Fact]
    public async Task Refresh_is_idempotent_when_no_new_raw_events_and_tracks_source_event_id()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create(
                        "telegram:200:100",
                        "telegram",
                        "200",
                        "100",
                        DialogueRawEventKinds.UserMessage,
                        "Мы строим сарай и обсуждаем доски."),
                },
                CancellationToken.None);
            var runtime = new FakeAgentRuntime(
                """
                {
                  "capsules": [
                    {
                      "key": "construction",
                      "title": "Стройка",
                      "contentMarkdown": "## Факты\n- Идет выбор сечения досок.",
                      "scope": "conversation",
                      "confidence": 0.88,
                      "sourceEventId": 1
                    }
                  ]
                }
                """);
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            var first = await service.RefreshAsync(
                conversation,
                correlationId: "capsules-1",
                force: false,
                CancellationToken.None);
            var second = await service.RefreshAsync(
                conversation,
                correlationId: "capsules-2",
                force: false,
                CancellationToken.None);

            Assert.True(first.IsConfigured);
            Assert.True(first.IsUpdated);
            Assert.True(first.CapsuleCount > 0);
            Assert.True(second.IsConfigured);
            Assert.False(second.IsUpdated);
            Assert.Single(runtime.Calls);

            var capsules = await repository.GetProjectCapsulesAsync(
                "telegram:200:100",
                10,
                CancellationToken.None);
            Assert.Single(capsules);
            Assert.Equal("construction", capsules[0].CapsuleKey);
            Assert.Equal(1, capsules[0].SourceEventId);
            Assert.Equal(1, capsules[0].Version);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Auto_batched_mode_uses_raw_event_threshold()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create("telegram:200:100", "telegram", "200", "100", DialogueRawEventKinds.UserMessage, "m1"),
                },
                CancellationToken.None);
            var runtime = new FakeAgentRuntime("{\"capsules\":[]}");
            var service = CreateService(
                repository,
                runtime,
                new AgentOptions
                {
                    StateDatabasePath = databasePath,
                    CapsuleExtractionMode = AgentOptions.CapsuleExtractionModeAutoBatched,
                    CapsuleAutoBatchRawEventThreshold = 4,
                });

            var firstCheck = await service.ShouldAutoRefreshAsync("telegram:200:100", CancellationToken.None);
            await repository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create("telegram:200:100", "telegram", "200", "100", DialogueRawEventKinds.AssistantMessage, "m2"),
                    RawEventEntry.Create("telegram:200:100", "telegram", "200", "100", DialogueRawEventKinds.UserMessage, "m3"),
                    RawEventEntry.Create("telegram:200:100", "telegram", "200", "100", DialogueRawEventKinds.AssistantMessage, "m4"),
                },
                CancellationToken.None);
            var secondCheck = await service.ShouldAutoRefreshAsync("telegram:200:100", CancellationToken.None);

            Assert.False(firstCheck);
            Assert.True(secondCheck);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    private static ProjectCapsuleService CreateService(
        AgentStateRepository repository,
        FakeAgentRuntime runtime,
        AgentOptions? agentOptions = null)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(agentOptions ?? new AgentOptions());

        return new ProjectCapsuleService(
            runtime,
            options,
            repository,
            loggerFactory.CreateLogger<ProjectCapsuleService>());
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

    private sealed class FakeAgentRuntime : IAgentRuntime
    {
        private readonly Queue<string> _responses;

        public FakeAgentRuntime(string responseText)
        {
            _responses = new Queue<string>(new[] { responseText });
        }

        public List<(string Message, AgentContext Context)> Calls { get; } = new();

        public AgentRuntimeHealth GetHealth() => AgentRuntimeHealth.Configured(new LlmOptions());

        public Task<AgentRuntimeResponse> SendAsync(
            string message,
            AgentContext context,
            Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
            CancellationToken cancellationToken)
        {
            Calls.Add((message, context));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake runtime responses left.");
            }

            return Task.FromResult(new AgentRuntimeResponse(
                context.CorrelationId,
                IsConfigured: true,
                _responses.Dequeue(),
                GetHealth()));
        }
    }
}
