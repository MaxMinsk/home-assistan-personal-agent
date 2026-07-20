using System.Globalization;
using HaPersonalAgent.Storage;
using Microsoft.Data.Sqlite;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: SQLite-хранилище автономных агентов (определения, запуски, inbox, непрерывность).
/// Зачем: эти данные операционные — как telegram offset и pending_confirmations, они живут локально и переживают рестарт контейнера, но НЕ засоряют общую Memory MCP.
/// Как: повторяет паттерн AgentStateRepository — ленивое создание схемы под семафором, именованные параметры, время как ISO-8601 TEXT.
/// </summary>
public sealed class SqliteAutonomousAgentRepository : IAutonomousAgentRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteAutonomousAgentRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS autonomous_agents (
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

                CREATE INDEX IF NOT EXISTS idx_autonomous_agents_status_next_run
                ON autonomous_agents (status, next_run_utc);

                CREATE TABLE IF NOT EXISTS autonomous_agent_runs (
                    id TEXT PRIMARY KEY NOT NULL,
                    agent_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    started_utc TEXT NOT NULL,
                    finished_utc TEXT NULL,
                    summary TEXT NULL,
                    questions_json TEXT NULL,
                    diagnostics TEXT NULL,
                    error TEXT NULL,
                    tool_call_count INTEGER NOT NULL,
                    correlation_id TEXT NOT NULL,
                    delivered_message_id TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_autonomous_agent_runs_agent_started
                ON autonomous_agent_runs (agent_id, started_utc DESC);

                CREATE INDEX IF NOT EXISTS idx_autonomous_agent_runs_delivered_message
                ON autonomous_agent_runs (delivered_message_id);

                CREATE TABLE IF NOT EXISTS autonomous_agent_inbox (
                    id TEXT PRIMARY KEY NOT NULL,
                    agent_id TEXT NOT NULL,
                    run_id TEXT NULL,
                    source TEXT NOT NULL,
                    text TEXT NOT NULL,
                    received_utc TEXT NOT NULL,
                    consumed_utc TEXT NULL,
                    consumed_by_run_id TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_autonomous_agent_inbox_pending
                ON autonomous_agent_inbox (agent_id, consumed_utc, received_utc);

                CREATE TABLE IF NOT EXISTS autonomous_agent_continuity (
                    agent_id TEXT PRIMARY KEY NOT NULL,
                    focus TEXT NULL,
                    open_questions TEXT NULL,
                    capsule_note_key TEXT NULL,
                    capsule_updated_utc TEXT NULL,
                    updated_utc TEXT NOT NULL
                );
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task UpsertDefinitionAsync(
        AutonomousAgentDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO autonomous_agents (
                id, name, mission, schedule_kind, schedule_expression, status,
                allow_home_assistant_read, allow_web_search, allow_memory_read, allow_memory_write,
                max_durable_facts_per_run, delivery_telegram_chat_id,
                created_utc, updated_utc, next_run_utc, last_run_utc)
            VALUES (
                $id, $name, $mission, $scheduleKind, $scheduleExpression, $status,
                $allowHaRead, $allowWebSearch, $allowMemoryRead, $allowMemoryWrite,
                $maxDurableFacts, $telegramChatId,
                $createdUtc, $updatedUtc, $nextRunUtc, $lastRunUtc)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                mission = excluded.mission,
                schedule_kind = excluded.schedule_kind,
                schedule_expression = excluded.schedule_expression,
                status = excluded.status,
                allow_home_assistant_read = excluded.allow_home_assistant_read,
                allow_web_search = excluded.allow_web_search,
                allow_memory_read = excluded.allow_memory_read,
                allow_memory_write = excluded.allow_memory_write,
                max_durable_facts_per_run = excluded.max_durable_facts_per_run,
                delivery_telegram_chat_id = excluded.delivery_telegram_chat_id,
                updated_utc = excluded.updated_utc,
                next_run_utc = excluded.next_run_utc,
                last_run_utc = excluded.last_run_utc;
            """;
        BindDefinition(command, definition);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AutonomousAgentDefinition?> GetDefinitionAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = DefinitionSelect + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", agentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDefinition(reader) : null;
    }

    public async Task<IReadOnlyList<AutonomousAgentDefinition>> ListDefinitionsAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = DefinitionSelect + " ORDER BY created_utc ASC;";

        var definitions = new List<AutonomousAgentDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            definitions.Add(ReadDefinition(reader));
        }

        return definitions;
    }

    public async Task<bool> DeleteDefinitionAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        // Каскад вручную: SQLite-схема без FK-констрейнтов, но осиротевшие запуски/inbox не нужны никому.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM autonomous_agent_runs WHERE agent_id = $id;
            DELETE FROM autonomous_agent_inbox WHERE agent_id = $id;
            DELETE FROM autonomous_agent_continuity WHERE agent_id = $id;
            DELETE FROM autonomous_agents WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", agentId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var changesCommand = connection.CreateCommand();
        changesCommand.Transaction = transaction;
        changesCommand.CommandText = "SELECT changes();";
        var deleted = Convert.ToInt64(
            await changesCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        await transaction.CommitAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task UpdateScheduleStateAsync(
        string agentId,
        DateTimeOffset? nextRunUtc,
        DateTimeOffset? lastRunUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE autonomous_agents
            SET next_run_utc = $nextRunUtc,
                last_run_utc = $lastRunUtc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", agentId);
        command.Parameters.AddWithValue("$nextRunUtc", ToDbValue(nextRunUtc));
        command.Parameters.AddWithValue("$lastRunUtc", ToDbValue(lastRunUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendRunAsync(AutonomousAgentRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO autonomous_agent_runs (
                id, agent_id, status, started_utc, finished_utc, summary, questions_json,
                diagnostics, error, tool_call_count, correlation_id, delivered_message_id)
            VALUES (
                $id, $agentId, $status, $startedUtc, $finishedUtc, $summary, $questionsJson,
                $diagnostics, $error, $toolCallCount, $correlationId, $deliveredMessageId);
            """;
        BindRun(command, run);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateRunAsync(AutonomousAgentRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE autonomous_agent_runs
            SET status = $status,
                started_utc = $startedUtc,
                finished_utc = $finishedUtc,
                summary = $summary,
                questions_json = $questionsJson,
                diagnostics = $diagnostics,
                error = $error,
                tool_call_count = $toolCallCount,
                correlation_id = $correlationId,
                delivered_message_id = $deliveredMessageId
            WHERE id = $id;
            """;
        BindRun(command, run);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AutonomousAgentRun?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RunSelect + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    public async Task<IReadOnlyList<AutonomousAgentRun>> ListRunsAsync(
        string agentId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RunSelect + " WHERE agent_id = $agentId ORDER BY started_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var runs = new List<AutonomousAgentRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    public async Task<bool> HasRunningRunAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(1) FROM autonomous_agent_runs
            WHERE agent_id = $agentId AND status = $status;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$status", AutonomousAgentRunStatus.Running.ToString());

        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return count > 0;
    }

    public async Task<AutonomousAgentRun?> FindRunByDeliveredMessageAsync(
        string deliveredMessageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveredMessageId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RunSelect
            + " WHERE delivered_message_id = $deliveredMessageId ORDER BY started_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$deliveredMessageId", deliveredMessageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    public async Task EnqueueReplyAsync(
        AutonomousAgentInboxEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO autonomous_agent_inbox (
                id, agent_id, run_id, source, text, received_utc, consumed_utc, consumed_by_run_id)
            VALUES (
                $id, $agentId, $runId, $source, $text, $receivedUtc, $consumedUtc, $consumedByRunId);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$agentId", entry.AgentId);
        command.Parameters.AddWithValue("$runId", ToDbValue(entry.RunId));
        command.Parameters.AddWithValue("$source", entry.Source.ToString());
        command.Parameters.AddWithValue("$text", entry.Text);
        command.Parameters.AddWithValue("$receivedUtc", ToIsoString(entry.ReceivedUtc));
        command.Parameters.AddWithValue("$consumedUtc", ToDbValue(entry.ConsumedUtc));
        command.Parameters.AddWithValue("$consumedByRunId", ToDbValue(entry.ConsumedByRunId));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AutonomousAgentInboxEntry>> GetPendingRepliesAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = InboxSelect
            + " WHERE agent_id = $agentId AND consumed_utc IS NULL ORDER BY received_utc ASC;";
        command.Parameters.AddWithValue("$agentId", agentId);

        var entries = new List<AutonomousAgentInboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ReadInboxEntry(reader));
        }

        return entries;
    }

    public async Task MarkRepliesConsumedAsync(
        IEnumerable<string> entryIds,
        string consumedByRunId,
        DateTimeOffset consumedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumedByRunId);

        var ids = entryIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE autonomous_agent_inbox
            SET consumed_utc = $consumedUtc,
                consumed_by_run_id = $consumedByRunId
            WHERE id = $id AND consumed_utc IS NULL;
            """;

        foreach (var id in ids)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$consumedUtc", ToIsoString(consumedUtc));
            command.Parameters.AddWithValue("$consumedByRunId", consumedByRunId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AutonomousAgentContinuity?> GetContinuityAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT agent_id, focus, open_questions, capsule_note_key, capsule_updated_utc, updated_utc
            FROM autonomous_agent_continuity
            WHERE agent_id = $agentId;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AutonomousAgentContinuity(
            reader.GetString(0),
            ReadNullableString(reader, 1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            ReadNullableDate(reader, 4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture));
    }

    public async Task UpsertContinuityAsync(
        AutonomousAgentContinuity continuity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(continuity);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO autonomous_agent_continuity (
                agent_id, focus, open_questions, capsule_note_key, capsule_updated_utc, updated_utc)
            VALUES (
                $agentId, $focus, $openQuestions, $capsuleNoteKey, $capsuleUpdatedUtc, $updatedUtc)
            ON CONFLICT(agent_id) DO UPDATE SET
                focus = excluded.focus,
                open_questions = excluded.open_questions,
                capsule_note_key = excluded.capsule_note_key,
                capsule_updated_utc = excluded.capsule_updated_utc,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$agentId", continuity.AgentId);
        command.Parameters.AddWithValue("$focus", ToDbValue(continuity.Focus));
        command.Parameters.AddWithValue("$openQuestions", ToDbValue(continuity.OpenQuestions));
        command.Parameters.AddWithValue("$capsuleNoteKey", ToDbValue(continuity.CapsuleNoteKey));
        command.Parameters.AddWithValue("$capsuleUpdatedUtc", ToDbValue(continuity.CapsuleUpdatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", ToIsoString(continuity.UpdatedUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string DefinitionSelect =
        """
        SELECT id, name, mission, schedule_kind, schedule_expression, status,
               allow_home_assistant_read, allow_web_search, allow_memory_read, allow_memory_write,
               max_durable_facts_per_run, delivery_telegram_chat_id,
               created_utc, updated_utc, next_run_utc, last_run_utc
        FROM autonomous_agents
        """;

    private const string RunSelect =
        """
        SELECT id, agent_id, status, started_utc, finished_utc, summary, questions_json,
               diagnostics, error, tool_call_count, correlation_id, delivered_message_id
        FROM autonomous_agent_runs
        """;

    private const string InboxSelect =
        """
        SELECT id, agent_id, run_id, source, text, received_utc, consumed_utc, consumed_by_run_id
        FROM autonomous_agent_inbox
        """;

    private static void BindDefinition(SqliteCommand command, AutonomousAgentDefinition definition)
    {
        command.Parameters.AddWithValue("$id", definition.Id);
        command.Parameters.AddWithValue("$name", definition.Name);
        command.Parameters.AddWithValue("$mission", definition.Mission);
        command.Parameters.AddWithValue("$scheduleKind", definition.ScheduleKind.ToString());
        command.Parameters.AddWithValue("$scheduleExpression", ToDbValue(definition.ScheduleExpression));
        command.Parameters.AddWithValue("$status", definition.Status.ToString());
        command.Parameters.AddWithValue("$allowHaRead", definition.ToolScope.AllowHomeAssistantRead ? 1 : 0);
        command.Parameters.AddWithValue("$allowWebSearch", definition.ToolScope.AllowWebSearch ? 1 : 0);
        command.Parameters.AddWithValue("$allowMemoryRead", definition.ToolScope.AllowMemoryRead ? 1 : 0);
        command.Parameters.AddWithValue("$allowMemoryWrite", definition.ToolScope.AllowMemoryWrite ? 1 : 0);
        command.Parameters.AddWithValue("$maxDurableFacts", definition.ToolScope.MaxDurableFactsPerRun);
        command.Parameters.AddWithValue("$telegramChatId", ToDbValue(definition.DeliveryTelegramChatId));
        command.Parameters.AddWithValue("$createdUtc", ToIsoString(definition.CreatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", ToIsoString(definition.UpdatedUtc));
        command.Parameters.AddWithValue("$nextRunUtc", ToDbValue(definition.NextRunUtc));
        command.Parameters.AddWithValue("$lastRunUtc", ToDbValue(definition.LastRunUtc));
    }

    private static void BindRun(SqliteCommand command, AutonomousAgentRun run)
    {
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$agentId", run.AgentId);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$startedUtc", ToIsoString(run.StartedUtc));
        command.Parameters.AddWithValue("$finishedUtc", ToDbValue(run.FinishedUtc));
        command.Parameters.AddWithValue("$summary", ToDbValue(run.Summary));
        command.Parameters.AddWithValue("$questionsJson", ToDbValue(run.QuestionsJson));
        command.Parameters.AddWithValue("$diagnostics", ToDbValue(run.Diagnostics));
        command.Parameters.AddWithValue("$error", ToDbValue(run.Error));
        command.Parameters.AddWithValue("$toolCallCount", run.ToolCallCount);
        command.Parameters.AddWithValue("$correlationId", run.CorrelationId);
        command.Parameters.AddWithValue("$deliveredMessageId", ToDbValue(run.DeliveredMessageId));
    }

    private static AutonomousAgentDefinition ReadDefinition(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseEnum(reader.GetString(3), AutonomousAgentScheduleKind.Manual),
            ReadNullableString(reader, 4),
            ParseEnum(reader.GetString(5), AutonomousAgentStatus.Active),
            AutonomousAgentToolScope.Create(
                reader.GetInt32(6) != 0,
                reader.GetInt32(7) != 0,
                reader.GetInt32(8) != 0,
                reader.GetInt32(9) != 0,
                reader.GetInt32(10)),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
            ReadNullableDate(reader, 14),
            ReadNullableDate(reader, 15));

    private static AutonomousAgentRun ReadRun(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            ParseEnum(reader.GetString(2), AutonomousAgentRunStatus.Running),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            ReadNullableDate(reader, 4),
            ReadNullableString(reader, 5),
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            reader.GetInt32(9),
            reader.GetString(10),
            ReadNullableString(reader, 11));

    private static AutonomousAgentInboxEntry ReadInboxEntry(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            ReadNullableString(reader, 2),
            ParseEnum(reader.GetString(3), AutonomousAgentReplySource.Telegram),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            ReadNullableDate(reader, 6),
            ReadNullableString(reader, 7));

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static string ToIsoString(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static object ToDbValue(DateTimeOffset? value) =>
        value.HasValue ? value.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value;

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object ToDbValue(long? value) =>
        value.HasValue ? value.Value : DBNull.Value;
}
