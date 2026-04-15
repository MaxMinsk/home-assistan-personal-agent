using System.Text.Json;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты executor для подтвержденного upsert project capsules.
/// Зачем: write-path капсул должен идти через generic confirmation и стабильно обновлять версию/контент без потери данных.
/// Как: использует временный SQLite store, формирует PendingConfirmation и вызывает executor напрямую.
/// </summary>
public class ProjectCapsuleUpsertActionExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_creates_capsule_from_confirmation_payload()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create("telegram:200:100", "telegram", "200", "100", DialogueRawEventKinds.UserMessage, "Сохрани прогресс по стройке."),
                },
                CancellationToken.None);
            var executor = CreateExecutor(repository);

            var result = await executor.ExecuteAsync(
                new PendingConfirmation(
                    "capsule-1",
                    ProjectCapsuleUpsertActionExecutor.ProjectCapsuleUpsertActionKind,
                    "telegram:200:100",
                    "100",
                    "upsert_project_capsule:construction",
                    """
                    {
                      "capsuleKey": "construction",
                      "title": "Стройка",
                      "contentMarkdown": "## Факты\n- Нужен расчет сечения досок.",
                      "scope": "conversation",
                      "confidence": 0.91
                    }
                    """,
                    "Обновить капсулу по стройке",
                    "Запись в долговременную память проекта.",
                    ConfirmationActionStatus.Pending,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    CompletedAtUtc: null,
                    "test-correlation",
                    ResultJson: null,
                    Error: null),
                CancellationToken.None);
            var stored = await repository.GetProjectCapsuleByKeyAsync(
                "telegram:200:100",
                "construction",
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.NotNull(stored);
            Assert.Equal("Стройка", stored!.Title);
            Assert.Equal(1, stored.Version);
            Assert.Equal(1, stored.SourceEventId);

            using var document = JsonDocument.Parse(result.ResultJson!);
            Assert.True(document.RootElement.GetProperty("changed").GetBoolean());
            Assert.Equal("construction", document.RootElement.GetProperty("capsuleKey").GetString());
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_keeps_version_for_same_payload_and_increments_for_changed_payload()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create("telegram:200:100", "telegram", "200", "100", DialogueRawEventKinds.UserMessage, "Первый факт."),
                },
                CancellationToken.None);
            var executor = CreateExecutor(repository);

            var first = await executor.ExecuteAsync(
                CreatePendingConfirmation(
                    "capsule-2",
                    """
                    {
                      "capsuleKey": "dog",
                      "title": "Щенок",
                      "contentMarkdown": "## Факты\n- Щенок освоился.",
                      "scope": "conversation",
                      "confidence": 0.80
                    }
                    """),
                CancellationToken.None);
            var second = await executor.ExecuteAsync(
                CreatePendingConfirmation(
                    "capsule-3",
                    """
                    {
                      "capsuleKey": "dog",
                      "title": "Щенок",
                      "contentMarkdown": "## Факты\n- Щенок освоился.",
                      "scope": "conversation",
                      "confidence": 0.80
                    }
                    """),
                CancellationToken.None);
            var third = await executor.ExecuteAsync(
                CreatePendingConfirmation(
                    "capsule-4",
                    """
                    {
                      "capsuleKey": "dog",
                      "title": "Щенок",
                      "contentMarkdown": "## Факты\n- Щенок освоился.\n- Любит вечерние прогулки.",
                      "scope": "conversation",
                      "confidence": 0.82
                    }
                    """),
                CancellationToken.None);
            var stored = await repository.GetProjectCapsuleByKeyAsync(
                "telegram:200:100",
                "dog",
                CancellationToken.None);

            Assert.True(first.IsSuccess);
            Assert.True(second.IsSuccess);
            Assert.True(third.IsSuccess);
            Assert.NotNull(stored);
            Assert.Equal(2, stored!.Version);

            using var firstJson = JsonDocument.Parse(first.ResultJson!);
            using var secondJson = JsonDocument.Parse(second.ResultJson!);
            using var thirdJson = JsonDocument.Parse(third.ResultJson!);
            Assert.True(firstJson.RootElement.GetProperty("changed").GetBoolean());
            Assert.False(secondJson.RootElement.GetProperty("changed").GetBoolean());
            Assert.True(thirdJson.RootElement.GetProperty("changed").GetBoolean());
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_returns_failure_for_invalid_payload()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var executor = CreateExecutor(repository);

            var result = await executor.ExecuteAsync(
                CreatePendingConfirmation("capsule-invalid", """{"capsuleKey":""}"""),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("payload", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    private static PendingConfirmation CreatePendingConfirmation(
        string id,
        string payloadJson) =>
        new(
            id,
            ProjectCapsuleUpsertActionExecutor.ProjectCapsuleUpsertActionKind,
            "telegram:200:100",
            "100",
            "upsert_project_capsule:dog",
            payloadJson,
            "Обновить капсулу",
            "Запись в память.",
            ConfirmationActionStatus.Pending,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(10),
            CompletedAtUtc: null,
            "test-correlation",
            ResultJson: null,
            Error: null);

    private static ProjectCapsuleUpsertActionExecutor CreateExecutor(AgentStateRepository repository)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });

        return new ProjectCapsuleUpsertActionExecutor(
            repository,
            loggerFactory.CreateLogger<ProjectCapsuleUpsertActionExecutor>());
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
}
