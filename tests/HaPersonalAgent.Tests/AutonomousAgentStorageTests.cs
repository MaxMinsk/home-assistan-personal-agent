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
                "Бизнес в Минске",
                "Исследуй, каким бизнесом заняться после увольнения.",
                AutonomousAgentScheduleKind.Weekly,
                toolScope: AutonomousAgentToolScope.Create(true, true, true, true, 3),
                deliveryTelegramChatId: 4242);

            await CreateRepository(databasePath).UpsertDefinitionAsync(definition, CancellationToken.None);

            var loaded = await CreateRepository(databasePath)
                .GetDefinitionAsync(definition.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("Бизнес в Минске", loaded!.Name);
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
                AutonomousAgentInboxEntry.Create(agentId, "Регион — только Минск.", AutonomousAgentReplySource.Web),
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
