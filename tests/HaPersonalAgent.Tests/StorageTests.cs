using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты SQLite state store.
/// Зачем: Telegram offset должен сохраняться на диск и корректно читаться после пересоздания repository.
/// Как: использует временную SQLite базу, инициализирует схему и удаляет test directory после каждого сценария.
/// </summary>
public class StorageTests
{
    [Fact]
    public async Task Initialize_creates_schema_on_empty_database()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);

            await repository.InitializeAsync(CancellationToken.None);

            Assert.True(File.Exists(databasePath));
            Assert.Equal(1L, await CountTablesAsync(databasePath, "agent_state"));
            Assert.Equal(1L, await CountTablesAsync(databasePath, "conversation_messages"));
            Assert.Equal(1L, await CountTablesAsync(databasePath, "conversation_summary"));
            Assert.Equal(1L, await CountTablesAsync(databasePath, "raw_events"));
            Assert.Equal(1L, await CountTablesAsync(databasePath, "pending_confirmations"));
            Assert.Equal(1L, await CountTablesAsync(databasePath, "confirmation_audit"));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Conversation_messages_persist_after_repository_recreated()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var firstRepository = CreateRepository(databasePath);
            await firstRepository.AppendConversationMessagesAsync(
                "telegram:1:2",
                new[]
                {
                    new AgentConversationMessage(AgentConversationRole.User, "hello", DateTimeOffset.UtcNow),
                    new AgentConversationMessage(AgentConversationRole.Assistant, "hi", DateTimeOffset.UtcNow),
                },
                CancellationToken.None);

            var secondRepository = CreateRepository(databasePath);
            var messages = await secondRepository.GetConversationMessagesAsync(
                "telegram:1:2",
                limit: 10,
                CancellationToken.None);

            Assert.Collection(
                messages,
                message =>
                {
                    Assert.Equal(AgentConversationRole.User, message.Role);
                    Assert.Equal("hello", message.Text);
                },
                message =>
                {
                    Assert.Equal(AgentConversationRole.Assistant, message.Role);
                    Assert.Equal("hi", message.Text);
                });
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Conversation_messages_returns_last_messages_in_chronological_order()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendConversationMessagesAsync(
                "telegram:1:2",
                new[]
                {
                    new AgentConversationMessage(AgentConversationRole.User, "one", DateTimeOffset.UtcNow),
                    new AgentConversationMessage(AgentConversationRole.Assistant, "two", DateTimeOffset.UtcNow),
                    new AgentConversationMessage(AgentConversationRole.User, "three", DateTimeOffset.UtcNow),
                    new AgentConversationMessage(AgentConversationRole.Assistant, "four", DateTimeOffset.UtcNow),
                },
                CancellationToken.None);

            var messages = await repository.GetConversationMessagesAsync(
                "telegram:1:2",
                limit: 3,
                CancellationToken.None);

            Assert.Equal(new[] { "two", "three", "four" }, messages.Select(message => message.Text));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Clear_conversation_messages_removes_only_selected_conversation()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendConversationMessagesAsync(
                "telegram:1:2",
                new[] { new AgentConversationMessage(AgentConversationRole.User, "clear me", DateTimeOffset.UtcNow) },
                CancellationToken.None);
            await repository.AppendConversationMessagesAsync(
                "telegram:3:4",
                new[] { new AgentConversationMessage(AgentConversationRole.User, "keep me", DateTimeOffset.UtcNow) },
                CancellationToken.None);

            await repository.ClearConversationMessagesAsync("telegram:1:2", CancellationToken.None);

            var cleared = await repository.GetConversationMessagesAsync("telegram:1:2", 10, CancellationToken.None);
            var kept = await repository.GetConversationMessagesAsync("telegram:3:4", 10, CancellationToken.None);

            Assert.Empty(cleared);
            Assert.Single(kept);
            Assert.Equal("keep me", kept[0].Text);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Conversation_messages_preserve_multiline_compaction_notice_content()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var notice = "[context-summary] Ранний контекст сжат." + Environment.NewLine + Environment.NewLine + "Ответ пользователю.";
            await repository.AppendConversationMessagesAsync(
                "telegram:1:2",
                new[]
                {
                    new AgentConversationMessage(AgentConversationRole.Assistant, notice, DateTimeOffset.UtcNow),
                },
                CancellationToken.None);

            var messages = await repository.GetConversationMessagesAsync(
                "telegram:1:2",
                limit: 10,
                CancellationToken.None);

            Assert.Single(messages);
            Assert.Equal(AgentConversationRole.Assistant, messages[0].Role);
            Assert.Equal(notice, messages[0].Text);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Raw_events_append_and_read_last_events_in_chronological_order()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create(
                        "telegram:1:2",
                        "telegram",
                        "1",
                        "2",
                        "dialogue.user_message",
                        "hello",
                        correlationId: "run-1",
                        createdAtUtc: DateTimeOffset.UtcNow.AddSeconds(-3)),
                    RawEventEntry.Create(
                        "telegram:1:2",
                        "telegram",
                        "1",
                        "2",
                        "dialogue.assistant_message",
                        "hi",
                        correlationId: "run-1",
                        createdAtUtc: DateTimeOffset.UtcNow.AddSeconds(-2)),
                    RawEventEntry.Create(
                        "telegram:1:2",
                        "telegram",
                        "1",
                        "2",
                        "dialogue.context_reset",
                        "context reset",
                        createdAtUtc: DateTimeOffset.UtcNow.AddSeconds(-1)),
                },
                CancellationToken.None);

            var count = await repository.GetRawEventCountAsync("telegram:1:2", CancellationToken.None);
            var events = await repository.GetRawEventsAsync("telegram:1:2", limit: 2, CancellationToken.None);

            Assert.Equal(3, count);
            Assert.Equal(new[] { "dialogue.assistant_message", "dialogue.context_reset" }, events.Select(rawEvent => rawEvent.EventKind));
            Assert.Equal(new[] { "hi", "context reset" }, events.Select(rawEvent => rawEvent.Payload));
            Assert.All(events, rawEvent => Assert.Equal("telegram:1:2", rawEvent.ConversationKey));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Conversation_summary_upsert_get_and_clear_roundtrip()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendConversationMessagesAsync(
                "telegram:1:2",
                new[]
                {
                    new AgentConversationMessage(AgentConversationRole.User, "hello", DateTimeOffset.UtcNow),
                    new AgentConversationMessage(AgentConversationRole.Assistant, "hi", DateTimeOffset.UtcNow),
                },
                CancellationToken.None);
            var latestMessageId = await repository.GetLatestConversationMessageIdAsync(
                "telegram:1:2",
                CancellationToken.None);
            Assert.True(latestMessageId.HasValue);

            await repository.UpsertConversationSummaryAsync(
                new ConversationSummaryMemory(
                    "telegram:1:2",
                    "summary-v1",
                    DateTimeOffset.UtcNow,
                    latestMessageId!.Value,
                    SummaryVersion: 1),
                CancellationToken.None);

            var summary = await repository.GetConversationSummaryAsync(
                "telegram:1:2",
                CancellationToken.None);
            Assert.NotNull(summary);
            Assert.Equal("summary-v1", summary.Summary);
            Assert.Equal(1, summary.SummaryVersion);

            await repository.UpsertConversationSummaryAsync(
                summary with
                {
                    Summary = "summary-v2",
                    SummaryVersion = 2,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                },
                CancellationToken.None);

            var updated = await repository.GetConversationSummaryAsync(
                "telegram:1:2",
                CancellationToken.None);
            Assert.NotNull(updated);
            Assert.Equal("summary-v2", updated.Summary);
            Assert.Equal(2, updated.SummaryVersion);

            await repository.ClearConversationSummaryAsync(
                "telegram:1:2",
                CancellationToken.None);

            var cleared = await repository.GetConversationSummaryAsync(
                "telegram:1:2",
                CancellationToken.None);
            Assert.Null(cleared);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Pending_confirmation_persists_and_status_updates_once()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var confirmation = new PendingConfirmation(
                "abc12345",
                "home_assistant_mcp",
                "telegram:200:100",
                "100",
                "HassCallService",
                "{\"domain\":\"light\"}",
                "Turn on kitchen light",
                "May change light state.",
                ConfirmationActionStatus.Pending,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(10),
                CompletedAtUtc: null,
                "test-correlation",
                ResultJson: null,
                Error: null);

            await repository.SavePendingConfirmationAsync(confirmation, CancellationToken.None);

            var stored = await repository.GetPendingConfirmationAsync(
                "telegram:200:100",
                "100",
                "abc12345",
                CancellationToken.None);
            var firstUpdate = await repository.TryUpdateConfirmationStatusAsync(
                "abc12345",
                ConfirmationActionStatus.Pending,
                ConfirmationActionStatus.Executing,
                completedAtUtc: null,
                resultJson: null,
                error: null,
                CancellationToken.None);
            var secondUpdate = await repository.TryUpdateConfirmationStatusAsync(
                "abc12345",
                ConfirmationActionStatus.Pending,
                ConfirmationActionStatus.Executing,
                completedAtUtc: null,
                resultJson: null,
                error: null,
                CancellationToken.None);

            Assert.NotNull(stored);
            Assert.Equal("home_assistant_mcp", stored.ActionKind);
            Assert.Equal("HassCallService", stored.OperationName);
            Assert.True(firstUpdate);
            Assert.False(secondUpdate);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Telegram_update_offset_persists_after_repository_recreated()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var firstRepository = CreateRepository(databasePath);
            await firstRepository.SaveTelegramUpdateOffsetAsync(42, CancellationToken.None);

            var secondRepository = CreateRepository(databasePath);
            var offset = await secondRepository.GetTelegramUpdateOffsetAsync(CancellationToken.None);

            Assert.Equal(42, offset);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
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

    private static async Task<long> CountTablesAsync(string databasePath, string tableName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $tableName;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0L);
    }
}
