using HaPersonalAgent.Autonomous;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты доменной модели и SQLite-хранилища автономных агентов (HPA-028).
/// Зачем: определения, запуски, очередь ответов и непрерывное состояние должны переживать рестарт add-on и не смешиваться между агентами.
/// Как: временная SQLite-база на сценарий, проверка CRUD, каскадного удаления, очереди inbox и правил нормализации домена.
/// </summary>
public class AutonomousAgentStorageTests
{
    [Fact]
    public async Task Initialize_creates_autonomous_agent_schema()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);

            await repository.InitializeAsync(CancellationToken.None);

            Assert.Equal(1L, await CountTablesAsync(databasePath, "autonomous_agents"));
            Assert.Equal(1L, await CountTablesAsync(databasePath, "autonomous_agent_runs"));
            Assert.Equal(1L, await CountTablesAsync(databasePath, "autonomous_agent_inbox"));
            Assert.Equal(1L, await CountTablesAsync(databasePath, "autonomous_agent_continuity"));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Definition_round_trips_after_repository_recreated()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var definition = AutonomousAgentDefinition.Create(
                "Еженедельный дайджест",
                "Тестовая миссия.",
                AutonomousAgentScheduleKind.Weekly,
                toolScope: AutonomousAgentToolScope.Create(true, true, true, true, 3),
                deliveryTelegramChatId: 4242);

            await CreateRepository(databasePath).UpsertDefinitionAsync(definition, CancellationToken.None);

            var loaded = await CreateRepository(databasePath)
                .GetDefinitionAsync(definition.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("Еженедельный дайджест", loaded!.Name);
            Assert.Equal(AutonomousAgentScheduleKind.Weekly, loaded.ScheduleKind);
            Assert.Equal(AutonomousAgentStatus.Active, loaded.Status);
            Assert.Equal(4242, loaded.DeliveryTelegramChatId);
            Assert.True(loaded.ToolScope.AllowWebSearch);
            Assert.Equal(3, loaded.ToolScope.MaxDurableFactsPerRun);
            Assert.Null(loaded.NextRunUtc);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Allow_propose_actions_flag_round_trips()
    {
        // HPA-035: галочка «может предлагать действия» должна честно сохраняться и читаться из /data.
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var definition = AutonomousAgentDefinition.Create(
                "Агент с предложениями",
                "Миссия.",
                AutonomousAgentScheduleKind.Weekly,
                toolScope: AutonomousAgentToolScope.Create(true, true, true, true, 3, allowProposeActions: true));

            await CreateRepository(databasePath).UpsertDefinitionAsync(definition, CancellationToken.None);
            var loaded = await CreateRepository(databasePath)
                .GetDefinitionAsync(definition.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.True(loaded!.ToolScope.AllowProposeActions);

            // По умолчанию — выключено (безопасный дефолт), и это тоже переживает roundtrip.
            var safe = AutonomousAgentDefinition.Create(
                "Обычный агент",
                "Миссия.",
                AutonomousAgentScheduleKind.Weekly);
            await CreateRepository(databasePath).UpsertDefinitionAsync(safe, CancellationToken.None);
            var loadedSafe = await CreateRepository(databasePath)
                .GetDefinitionAsync(safe.Id, CancellationToken.None);
            Assert.False(loadedSafe!.ToolScope.AllowProposeActions);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Allow_cross_agent_context_flag_round_trips()
    {
        // HPA-039: галочка «видеть находки других агентов» тоже сохраняется и читается из /data.
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var on = AutonomousAgentDefinition.Create(
                "Кросс-агент",
                "Миссия.",
                AutonomousAgentScheduleKind.Weekly,
                toolScope: AutonomousAgentToolScope.Create(
                    true, true, true, true, 3, allowProposeActions: false, allowCrossAgentContext: true));
            await CreateRepository(databasePath).UpsertDefinitionAsync(on, CancellationToken.None);
            var loadedOn = await CreateRepository(databasePath).GetDefinitionAsync(on.Id, CancellationToken.None);
            Assert.True(loadedOn!.ToolScope.AllowCrossAgentContext);

            var off = AutonomousAgentDefinition.Create("Обычный", "Миссия.", AutonomousAgentScheduleKind.Weekly);
            await CreateRepository(databasePath).UpsertDefinitionAsync(off, CancellationToken.None);
            var loadedOff = await CreateRepository(databasePath).GetDefinitionAsync(off.Id, CancellationToken.None);
            Assert.False(loadedOff!.ToolScope.AllowCrossAgentContext);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Missing_allow_propose_actions_column_is_migrated_on_existing_databases()
    {
        // HPA-035: у пользователей с базой ДО этой колонки старт не должен падать — репозиторий доигрывает схему.
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
            await using (var seed = new SqliteConnection(connectionString))
            {
                await seed.OpenAsync();
                await using var create = seed.CreateCommand();
                create.CommandText =
                    """
                    CREATE TABLE autonomous_agents (
                        id TEXT PRIMARY KEY NOT NULL,
                        name TEXT NOT NULL,
                        mission TEXT NOT NULL,
                        schedule_kind TEXT NOT NULL,
                        schedule_expression TEXT NULL,
                        status TEXT NOT NULL,
                        allow_home_assistant_read INTEGER NOT NULL,
                        allow_web_search INTEGER NOT NULL,
                        allow_memory_read INTEGER NOT NULL,
                        allow_memory_write INTEGER NOT NULL,
                        max_durable_facts_per_run INTEGER NOT NULL,
                        delivery_telegram_chat_id INTEGER NULL,
                        created_utc TEXT NOT NULL,
                        updated_utc TEXT NOT NULL,
                        next_run_utc TEXT NULL,
                        last_run_utc TEXT NULL
                    );
                    INSERT INTO autonomous_agents (
                        id, name, mission, schedule_kind, schedule_expression, status,
                        allow_home_assistant_read, allow_web_search, allow_memory_read, allow_memory_write,
                        max_durable_facts_per_run, delivery_telegram_chat_id,
                        created_utc, updated_utc, next_run_utc, last_run_utc)
                    VALUES (
                        'legacy-1', 'Старый агент', 'миссия', 'Weekly', NULL, 'Active',
                        1, 1, 1, 1, 3, NULL,
                        '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00', NULL, NULL);
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var loaded = await CreateRepository(databasePath).GetDefinitionAsync("legacy-1", CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("Старый агент", loaded!.Name);
            // Колонки не было — добавлена с безопасным дефолтом (не может предлагать).
            Assert.False(loaded.ToolScope.AllowProposeActions);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Schedule_state_is_updated_without_touching_user_fields()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var definition = AutonomousAgentDefinition.Create(
                "Weekly digest",
                "Собери сводку по дому.",
                AutonomousAgentScheduleKind.Weekly);
            await repository.UpsertDefinitionAsync(definition, CancellationToken.None);

            var nextRun = DateTimeOffset.UtcNow.AddDays(7);
            var lastRun = DateTimeOffset.UtcNow;
            await repository.UpdateScheduleStateAsync(definition.Id, nextRun, lastRun, CancellationToken.None);

            var loaded = await repository.GetDefinitionAsync(definition.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("Weekly digest", loaded!.Name);
            Assert.NotNull(loaded.NextRunUtc);
            Assert.NotNull(loaded.LastRunUtc);
            Assert.Equal(nextRun.ToUnixTimeSeconds(), loaded.NextRunUtc!.Value.ToUnixTimeSeconds());
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Run_lifecycle_persists_and_detects_running_run()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var agentId = await SeedAgentAsync(repository);

            var run = AutonomousAgentRun.Start(agentId);
            await repository.AppendRunAsync(run, CancellationToken.None);

            Assert.True(await repository.HasRunningRunAsync(agentId, CancellationToken.None));

            var completed = run.Complete(
                summary: "Нашёл три ниши.",
                questionsJson: """["Интересует B2B?"]""",
                diagnostics: "tools=2",
                toolCallCount: 2);
            await repository.UpdateRunAsync(completed, CancellationToken.None);

            Assert.False(await repository.HasRunningRunAsync(agentId, CancellationToken.None));

            var loaded = await repository.GetRunAsync(run.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(AutonomousAgentRunStatus.Completed, loaded!.Status);
            Assert.Equal("Нашёл три ниши.", loaded.Summary);
            Assert.Equal(2, loaded.ToolCallCount);
            Assert.NotNull(loaded.FinishedUtc);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Run_is_findable_by_delivered_message_id()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var agentId = await SeedAgentAsync(repository);

            var run = AutonomousAgentRun.Start(agentId) with { DeliveredMessageId = "telegram-777" };
            await repository.AppendRunAsync(run, CancellationToken.None);

            var found = await repository.FindRunByDeliveredMessageAsync("telegram-777", CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal(run.Id, found!.Id);
            Assert.Equal(agentId, found.AgentId);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Pending_replies_are_queued_until_consumed_by_a_run()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var agentId = await SeedAgentAsync(repository);

            await repository.EnqueueReplyAsync(
                AutonomousAgentInboxEntry.Create(agentId, "Да, B2B интересен.", AutonomousAgentReplySource.Telegram),
                CancellationToken.None);
            await repository.EnqueueReplyAsync(
                AutonomousAgentInboxEntry.Create(agentId, "Ответ, пришедший из панели.", AutonomousAgentReplySource.Web),
                CancellationToken.None);

            var pending = await repository.GetPendingRepliesAsync(agentId, CancellationToken.None);
            Assert.Equal(2, pending.Count);
            Assert.All(pending, entry => Assert.Null(entry.ConsumedUtc));

            await repository.MarkRepliesConsumedAsync(
                pending.Select(entry => entry.Id),
                "run-1",
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var afterConsumption = await repository.GetPendingRepliesAsync(agentId, CancellationToken.None);
            Assert.Empty(afterConsumption);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Continuity_upsert_overwrites_previous_state()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var agentId = await SeedAgentAsync(repository);

            await repository.UpsertContinuityAsync(
                AutonomousAgentContinuity.Empty(agentId),
                CancellationToken.None);
            await repository.UpsertContinuityAsync(
                new AutonomousAgentContinuity(
                    agentId,
                    "Проверить аренду помещений",
                    "Какой бюджет?",
                    $"hpa-agent-capsule-{agentId}",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);

            var loaded = await repository.GetContinuityAsync(agentId, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("Проверить аренду помещений", loaded!.Focus);
            Assert.Equal($"hpa-agent-capsule-{agentId}", loaded.CapsuleNoteKey);
            Assert.NotNull(loaded.CapsuleUpdatedUtc);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Deleting_an_agent_cascades_to_runs_inbox_and_continuity()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var agentId = await SeedAgentAsync(repository);
            await repository.AppendRunAsync(AutonomousAgentRun.Start(agentId), CancellationToken.None);
            await repository.EnqueueReplyAsync(
                AutonomousAgentInboxEntry.Create(agentId, "ответ", AutonomousAgentReplySource.Telegram),
                CancellationToken.None);
            await repository.UpsertContinuityAsync(
                AutonomousAgentContinuity.Empty(agentId),
                CancellationToken.None);

            var deleted = await repository.DeleteDefinitionAsync(agentId, CancellationToken.None);

            Assert.True(deleted);
            Assert.Null(await repository.GetDefinitionAsync(agentId, CancellationToken.None));
            Assert.Empty(await repository.ListRunsAsync(agentId, 10, CancellationToken.None));
            Assert.Empty(await repository.GetPendingRepliesAsync(agentId, CancellationToken.None));
            Assert.Null(await repository.GetContinuityAsync(agentId, CancellationToken.None));
            Assert.False(await repository.DeleteDefinitionAsync(agentId, CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public void Cron_schedule_requires_an_expression_and_presets_drop_it()
    {
        Assert.Throws<ArgumentException>(() => AutonomousAgentDefinition.Create(
            "cron agent",
            "миссия",
            AutonomousAgentScheduleKind.Cron,
            scheduleExpression: "   "));

        var weekly = AutonomousAgentDefinition.Create(
            "weekly agent",
            "миссия",
            AutonomousAgentScheduleKind.Weekly,
            scheduleExpression: "0 9 * * 1");

        Assert.Null(weekly.ScheduleExpression);
    }

    [Fact]
    public void Tool_scope_clamps_fact_budget_and_forbids_blind_writes()
    {
        var overBudget = AutonomousAgentToolScope.Create(true, true, true, true, 99);
        Assert.Equal(AutonomousAgentToolScope.MaxAllowedDurableFactsPerRun, overBudget.MaxDurableFactsPerRun);

        // Писать в память, не читая её, нельзя — иначе агент дублирует уже сохранённое.
        var writeWithoutRead = AutonomousAgentToolScope.Create(true, true, false, true, 2);
        Assert.False(writeWithoutRead.AllowMemoryWrite);
    }

    private static async Task<string> SeedAgentAsync(IAutonomousAgentRepository repository)
    {
        var definition = AutonomousAgentDefinition.Create(
            "seed agent",
            "миссия",
            AutonomousAgentScheduleKind.Daily);
        await repository.UpsertDefinitionAsync(definition, CancellationToken.None);
        return definition.Id;
    }

    private static SqliteAutonomousAgentRepository CreateRepository(string databasePath)
    {
        var options = Options.Create(new AgentOptions
        {
            StateDatabasePath = databasePath,
        });

        return new SqliteAutonomousAgentRepository(new SqliteConnectionFactory(options));
    }

    private static async Task<long> CountTablesAsync(string databasePath, string tableName)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string CreateTemporaryDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            "ha-personal-agent-tests",
            Guid.NewGuid().ToString("N"),
            "state.sqlite");

    private static void DeleteTemporaryDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
