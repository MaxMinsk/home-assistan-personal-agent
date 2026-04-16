using HaPersonalAgent.Agent;
using HaPersonalAgent.Confirmation;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: repository для небольшого persistent state агента.
/// Зачем: Telegram offset, краткосрочный контекст диалога и pending confirmations должны переживать рестарт add-on контейнера.
/// Как: при первом обращении создает таблицы, затем хранит offset как key/value, историю как append-only turns, overflow vector memory, project capsules и confirmation actions отдельно от memory.
/// </summary>
public sealed class AgentStateRepository
{
    private const string TelegramUpdateOffsetKey = "telegram.update_offset";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public AgentStateRepository(SqliteConnectionFactory connectionFactory)
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
                CREATE TABLE IF NOT EXISTS agent_state (
                    key TEXT PRIMARY KEY NOT NULL,
                    value TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS conversation_messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_key TEXT NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    created_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_conversation_messages_key_id
                ON conversation_messages (conversation_key, id);

                CREATE TABLE IF NOT EXISTS conversation_summary (
                    conversation_key TEXT PRIMARY KEY NOT NULL,
                    summary TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    source_last_message_id INTEGER NOT NULL,
                    summary_version INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS conversation_vector_memory (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_key TEXT NOT NULL,
                    source_message_id INTEGER NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    embedding TEXT NOT NULL,
                    created_utc TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_conversation_vector_memory_source
                ON conversation_vector_memory (conversation_key, source_message_id);

                CREATE INDEX IF NOT EXISTS idx_conversation_vector_memory_key_id
                ON conversation_vector_memory (conversation_key, id);

                CREATE TABLE IF NOT EXISTS project_capsules (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_key TEXT NOT NULL,
                    capsule_key TEXT NOT NULL,
                    title TEXT NOT NULL,
                    content_markdown TEXT NOT NULL,
                    scope TEXT NOT NULL,
                    confidence REAL NOT NULL,
                    source_event_id INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL,
                    version INTEGER NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_project_capsules_key
                ON project_capsules (conversation_key, capsule_key);

                CREATE INDEX IF NOT EXISTS idx_project_capsules_scope_updated
                ON project_capsules (conversation_key, updated_utc DESC);

                CREATE TABLE IF NOT EXISTS project_capsule_extraction_state (
                    conversation_key TEXT PRIMARY KEY NOT NULL,
                    last_raw_event_id INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL,
                    runs_count INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS raw_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_key TEXT NOT NULL,
                    transport TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    participant_id TEXT NOT NULL,
                    event_kind TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    source_id TEXT NULL,
                    correlation_id TEXT NULL,
                    created_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_raw_events_conversation_id
                ON raw_events (conversation_key, id);

                CREATE INDEX IF NOT EXISTS idx_raw_events_kind_created_utc
                ON raw_events (event_kind, created_utc);

                CREATE TABLE IF NOT EXISTS pending_confirmations (
                    id TEXT PRIMARY KEY NOT NULL,
                    action_kind TEXT NOT NULL,
                    conversation_key TEXT NOT NULL,
                    participant_id TEXT NOT NULL,
                    operation_name TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    risk TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    expires_utc TEXT NOT NULL,
                    completed_utc TEXT NULL,
                    correlation_id TEXT NOT NULL,
                    result_json TEXT NULL,
                    error TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_pending_confirmations_scope
                ON pending_confirmations (conversation_key, participant_id, status, expires_utc);

                CREATE TABLE IF NOT EXISTS confirmation_audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    confirmation_id TEXT NOT NULL,
                    action_kind TEXT NOT NULL,
                    conversation_key TEXT NOT NULL,
                    participant_id TEXT NOT NULL,
                    operation_name TEXT NOT NULL,
                    event TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    details TEXT NULL,
                    created_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_confirmation_audit_confirmation
                ON confirmation_audit (confirmation_id, id);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<long?> GetTelegramUpdateOffsetAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT value
            FROM agent_state
            WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$key", TelegramUpdateOffsetKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return long.Parse((string)value, CultureInfo.InvariantCulture);
    }

    public async Task SaveTelegramUpdateOffsetAsync(long offset, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO agent_state (key, value, updated_utc)
            VALUES ($key, $value, $updatedUtc)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", TelegramUpdateOffsetKey);
        command.Parameters.AddWithValue("$value", offset.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentConversationMessage>> GetConversationMessagesAsync(
        string conversationKey,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        if (limit <= 0)
        {
            return Array.Empty<AgentConversationMessage>();
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT role, content, created_utc
            FROM (
                SELECT id, role, content, created_utc
                FROM conversation_messages
                WHERE conversation_key = $conversationKey
                ORDER BY id DESC
                LIMIT $limit
            )
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$limit", limit);

        var messages = new List<AgentConversationMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new AgentConversationMessage(
                Enum.Parse<AgentConversationRole>(reader.GetString(0), ignoreCase: true),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        }

        return messages;
    }

    public async Task<int> GetConversationMessageCountAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM conversation_messages
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return 0;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<StoredConversationMessage>> GetOverflowConversationMessagesAsync(
        string conversationKey,
        int retainedMessageCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        if (retainedMessageCount <= 0)
        {
            command.CommandText =
                """
                SELECT id, role, content, created_utc
                FROM conversation_messages
                WHERE conversation_key = $conversationKey
                ORDER BY id ASC;
                """;
            command.Parameters.AddWithValue("$conversationKey", conversationKey);
        }
        else
        {
            command.CommandText =
                """
                SELECT id, role, content, created_utc
                FROM conversation_messages
                WHERE conversation_key = $conversationKey
                  AND id NOT IN (
                      SELECT id
                      FROM conversation_messages
                      WHERE conversation_key = $conversationKey
                      ORDER BY id DESC
                      LIMIT $retainedMessageCount
                  )
                ORDER BY id ASC;
                """;
            command.Parameters.AddWithValue("$conversationKey", conversationKey);
            command.Parameters.AddWithValue("$retainedMessageCount", retainedMessageCount);
        }

        var messages = new List<StoredConversationMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new StoredConversationMessage(
                reader.GetInt64(0),
                Enum.Parse<AgentConversationRole>(reader.GetString(1), ignoreCase: true),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
        }

        return messages;
    }

    public async Task UpsertConversationVectorMemoryAsync(
        IEnumerable<ConversationVectorMemoryEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var entryList = entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.ConversationKey)
                && entry.SourceMessageId > 0
                && !string.IsNullOrWhiteSpace(entry.Content)
                && !string.IsNullOrWhiteSpace(entry.Embedding))
            .ToArray();
        if (entryList.Length == 0)
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
            INSERT INTO conversation_vector_memory (
                conversation_key,
                source_message_id,
                role,
                content,
                embedding,
                created_utc)
            VALUES (
                $conversationKey,
                $sourceMessageId,
                $role,
                $content,
                $embedding,
                $createdUtc)
            ON CONFLICT(conversation_key, source_message_id) DO UPDATE SET
                role = excluded.role,
                content = excluded.content,
                embedding = excluded.embedding,
                created_utc = excluded.created_utc;
            """;

        foreach (var entry in entryList)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$conversationKey", entry.ConversationKey);
            command.Parameters.AddWithValue("$sourceMessageId", entry.SourceMessageId);
            command.Parameters.AddWithValue("$role", entry.Role.ToString());
            command.Parameters.AddWithValue("$content", entry.Content);
            command.Parameters.AddWithValue("$embedding", entry.Embedding);
            command.Parameters.AddWithValue("$createdUtc", entry.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationVectorMemoryRecord>> GetConversationVectorMemoryAsync(
        string conversationKey,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        if (limit <= 0)
        {
            return Array.Empty<ConversationVectorMemoryRecord>();
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                conversation_key,
                source_message_id,
                role,
                content,
                embedding,
                created_utc
            FROM conversation_vector_memory
            WHERE conversation_key = $conversationKey
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<ConversationVectorMemoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ConversationVectorMemoryRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                Enum.Parse<AgentConversationRole>(reader.GetString(3), ignoreCase: true),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }

        return records;
    }

    public async Task<int> GetConversationVectorMemoryCountAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM conversation_vector_memory
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return 0;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ProjectCapsuleMemory>> GetProjectCapsulesAsync(
        string conversationKey,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        if (limit <= 0)
        {
            return Array.Empty<ProjectCapsuleMemory>();
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                conversation_key,
                capsule_key,
                title,
                content_markdown,
                scope,
                confidence,
                source_event_id,
                updated_utc,
                version
            FROM project_capsules
            WHERE conversation_key = $conversationKey
            ORDER BY updated_utc DESC, capsule_key ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$limit", limit);

        var capsules = new List<ProjectCapsuleMemory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            capsules.Add(new ProjectCapsuleMemory(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.GetInt64(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.GetInt32(8)));
        }

        return capsules;
    }

    public async Task<ProjectCapsuleMemory?> GetProjectCapsuleByKeyAsync(
        string conversationKey,
        string capsuleKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(capsuleKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                conversation_key,
                capsule_key,
                title,
                content_markdown,
                scope,
                confidence,
                source_event_id,
                updated_utc,
                version
            FROM project_capsules
            WHERE conversation_key = $conversationKey
              AND capsule_key = $capsuleKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$capsuleKey", capsuleKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ProjectCapsuleMemory(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDouble(5),
            reader.GetInt64(6),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            reader.GetInt32(8));
    }

    public async Task UpsertProjectCapsulesAsync(
        IEnumerable<ProjectCapsuleMemory> capsules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capsules);

        var capsuleList = capsules
            .Where(capsule =>
                !string.IsNullOrWhiteSpace(capsule.ConversationKey)
                && !string.IsNullOrWhiteSpace(capsule.CapsuleKey)
                && !string.IsNullOrWhiteSpace(capsule.Title)
                && !string.IsNullOrWhiteSpace(capsule.ContentMarkdown)
                && !string.IsNullOrWhiteSpace(capsule.Scope)
                && capsule.SourceEventId > 0
                && capsule.Version > 0)
            .ToArray();
        if (capsuleList.Length == 0)
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
            INSERT INTO project_capsules (
                conversation_key,
                capsule_key,
                title,
                content_markdown,
                scope,
                confidence,
                source_event_id,
                updated_utc,
                version)
            VALUES (
                $conversationKey,
                $capsuleKey,
                $title,
                $contentMarkdown,
                $scope,
                $confidence,
                $sourceEventId,
                $updatedUtc,
                $version)
            ON CONFLICT(conversation_key, capsule_key) DO UPDATE SET
                title = excluded.title,
                content_markdown = excluded.content_markdown,
                scope = excluded.scope,
                confidence = excluded.confidence,
                source_event_id = excluded.source_event_id,
                updated_utc = excluded.updated_utc,
                version = excluded.version;
            """;

        foreach (var capsule in capsuleList)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$conversationKey", capsule.ConversationKey);
            command.Parameters.AddWithValue("$capsuleKey", capsule.CapsuleKey);
            command.Parameters.AddWithValue("$title", capsule.Title);
            command.Parameters.AddWithValue("$contentMarkdown", capsule.ContentMarkdown);
            command.Parameters.AddWithValue("$scope", capsule.Scope);
            command.Parameters.AddWithValue("$confidence", Math.Clamp(capsule.Confidence, 0d, 1d));
            command.Parameters.AddWithValue("$sourceEventId", capsule.SourceEventId);
            command.Parameters.AddWithValue("$updatedUtc", capsule.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$version", capsule.Version);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> GetProjectCapsuleCountAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM project_capsules
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return 0;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<long?> GetProjectCapsuleLatestSourceEventIdAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MAX(source_event_id)
            FROM project_capsules
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<DateTimeOffset?> GetProjectCapsuleLastUpdatedAtUtcAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MAX(updated_utc)
            FROM project_capsules
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    }

    public async Task<ProjectCapsuleExtractionState?> GetProjectCapsuleExtractionStateAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT conversation_key, last_raw_event_id, updated_utc, runs_count
            FROM project_capsule_extraction_state
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ProjectCapsuleExtractionState(
            reader.GetString(0),
            reader.GetInt64(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            reader.GetInt32(3));
    }

    public async Task UpsertProjectCapsuleExtractionStateAsync(
        ProjectCapsuleExtractionState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ConversationKey);
        if (state.LastRawEventId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "LastRawEventId must be greater than or equal to zero.");
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO project_capsule_extraction_state (
                conversation_key,
                last_raw_event_id,
                updated_utc,
                runs_count)
            VALUES (
                $conversationKey,
                $lastRawEventId,
                $updatedUtc,
                $runsCount)
            ON CONFLICT(conversation_key) DO UPDATE SET
                last_raw_event_id = excluded.last_raw_event_id,
                updated_utc = excluded.updated_utc,
                runs_count = excluded.runs_count;
            """;
        command.Parameters.AddWithValue("$conversationKey", state.ConversationKey);
        command.Parameters.AddWithValue("$lastRawEventId", state.LastRawEventId);
        command.Parameters.AddWithValue("$updatedUtc", state.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$runsCount", Math.Max(state.RunsCount, 0));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearProjectCapsulesAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM project_capsules
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearProjectCapsuleExtractionStateAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM project_capsule_extraction_state
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendRawEventsAsync(
        IEnumerable<RawEventEntry> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        var eventList = events
            .Where(rawEvent =>
                !string.IsNullOrWhiteSpace(rawEvent.ConversationKey)
                && !string.IsNullOrWhiteSpace(rawEvent.Transport)
                && !string.IsNullOrWhiteSpace(rawEvent.ConversationId)
                && !string.IsNullOrWhiteSpace(rawEvent.ParticipantId)
                && !string.IsNullOrWhiteSpace(rawEvent.EventKind)
                && !string.IsNullOrWhiteSpace(rawEvent.Payload))
            .ToArray();
        if (eventList.Length == 0)
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
            INSERT INTO raw_events (
                conversation_key,
                transport,
                conversation_id,
                participant_id,
                event_kind,
                payload,
                source_id,
                correlation_id,
                created_utc)
            VALUES (
                $conversationKey,
                $transport,
                $conversationId,
                $participantId,
                $eventKind,
                $payload,
                $sourceId,
                $correlationId,
                $createdUtc);
            """;

        foreach (var rawEvent in eventList)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$conversationKey", rawEvent.ConversationKey);
            command.Parameters.AddWithValue("$transport", rawEvent.Transport);
            command.Parameters.AddWithValue("$conversationId", rawEvent.ConversationId);
            command.Parameters.AddWithValue("$participantId", rawEvent.ParticipantId);
            command.Parameters.AddWithValue("$eventKind", rawEvent.EventKind);
            command.Parameters.AddWithValue("$payload", rawEvent.Payload);
            command.Parameters.AddWithValue("$sourceId", ToDbValue(rawEvent.SourceId));
            command.Parameters.AddWithValue("$correlationId", ToDbValue(rawEvent.CorrelationId));
            command.Parameters.AddWithValue("$createdUtc", rawEvent.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long?> GetLatestRawEventIdAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MAX(id)
            FROM raw_events
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<int> GetRawEventCountSinceIdAsync(
        string conversationKey,
        long afterIdExclusive,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM raw_events
            WHERE conversation_key = $conversationKey
              AND id > $afterIdExclusive;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$afterIdExclusive", Math.Max(afterIdExclusive, 0));

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return 0;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<RawEventRecord>> GetRawEventsSinceIdAsync(
        string conversationKey,
        long afterIdExclusive,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        if (limit <= 0)
        {
            return Array.Empty<RawEventRecord>();
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                conversation_key,
                transport,
                conversation_id,
                participant_id,
                event_kind,
                payload,
                source_id,
                correlation_id,
                created_utc
            FROM raw_events
            WHERE conversation_key = $conversationKey
              AND id > $afterIdExclusive
            ORDER BY id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$afterIdExclusive", Math.Max(afterIdExclusive, 0));
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<RawEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new RawEventRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        }

        return events;
    }

    public async Task<int> GetRawEventCountAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM raw_events
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return 0;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<RawEventRecord>> GetRawEventsAsync(
        string conversationKey,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        if (limit <= 0)
        {
            return Array.Empty<RawEventRecord>();
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                conversation_key,
                transport,
                conversation_id,
                participant_id,
                event_kind,
                payload,
                source_id,
                correlation_id,
                created_utc
            FROM (
                SELECT
                    id,
                    conversation_key,
                    transport,
                    conversation_id,
                    participant_id,
                    event_kind,
                    payload,
                    source_id,
                    correlation_id,
                    created_utc
                FROM raw_events
                WHERE conversation_key = $conversationKey
                ORDER BY id DESC
                LIMIT $limit
            )
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<RawEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new RawEventRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        }

        return events;
    }

    public async Task<ConversationSummaryMemory?> GetConversationSummaryAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT summary, updated_utc, source_last_message_id, summary_version
            FROM conversation_summary
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ConversationSummaryMemory(
            conversationKey,
            reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetInt64(2),
            reader.GetInt32(3));
    }

    public async Task UpsertConversationSummaryAsync(
        ConversationSummaryMemory summaryMemory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summaryMemory);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryMemory.ConversationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryMemory.Summary);
        if (summaryMemory.SourceLastMessageId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(summaryMemory),
                "SourceLastMessageId must be greater than zero.");
        }

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO conversation_summary (
                conversation_key,
                summary,
                updated_utc,
                source_last_message_id,
                summary_version)
            VALUES (
                $conversationKey,
                $summary,
                $updatedUtc,
                $sourceLastMessageId,
                $summaryVersion)
            ON CONFLICT(conversation_key) DO UPDATE SET
                summary = excluded.summary,
                updated_utc = excluded.updated_utc,
                source_last_message_id = excluded.source_last_message_id,
                summary_version = excluded.summary_version;
            """;
        command.Parameters.AddWithValue("$conversationKey", summaryMemory.ConversationKey);
        command.Parameters.AddWithValue("$summary", summaryMemory.Summary);
        command.Parameters.AddWithValue("$updatedUtc", summaryMemory.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sourceLastMessageId", summaryMemory.SourceLastMessageId);
        command.Parameters.AddWithValue("$summaryVersion", summaryMemory.SummaryVersion);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long?> GetLatestConversationMessageIdAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MAX(id)
            FROM conversation_messages
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task AppendConversationMessagesAsync(
        string conversationKey,
        IEnumerable<AgentConversationMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ArgumentNullException.ThrowIfNull(messages);

        var messageList = messages
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .ToArray();
        if (messageList.Length == 0)
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
            INSERT INTO conversation_messages (conversation_key, role, content, created_utc)
            VALUES ($conversationKey, $role, $content, $createdUtc);
            """;

        foreach (var message in messageList)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$conversationKey", conversationKey);
            command.Parameters.AddWithValue("$role", message.Role.ToString());
            command.Parameters.AddWithValue("$content", message.Text);
            command.Parameters.AddWithValue("$createdUtc", message.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task TrimConversationMessagesAsync(
        string conversationKey,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        if (maxMessages <= 0)
        {
            command.CommandText =
                """
                DELETE FROM conversation_messages
                WHERE conversation_key = $conversationKey;
                """;
            command.Parameters.AddWithValue("$conversationKey", conversationKey);

            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        command.CommandText =
            """
            DELETE FROM conversation_messages
            WHERE conversation_key = $conversationKey
              AND id NOT IN (
                  SELECT id
                  FROM conversation_messages
                  WHERE conversation_key = $conversationKey
                  ORDER BY id DESC
                  LIMIT $maxMessages
              );
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$maxMessages", maxMessages);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SavePendingConfirmationAsync(
        PendingConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO pending_confirmations (
                id,
                action_kind,
                conversation_key,
                participant_id,
                operation_name,
                payload_json,
                summary,
                risk,
                status,
                created_utc,
                expires_utc,
                completed_utc,
                correlation_id,
                result_json,
                error)
            VALUES (
                $id,
                $actionKind,
                $conversationKey,
                $participantId,
                $operationName,
                $payloadJson,
                $summary,
                $risk,
                $status,
                $createdUtc,
                $expiresUtc,
                $completedUtc,
                $correlationId,
                $resultJson,
                $error);
            """;

        AddPendingConfirmationParameters(command, confirmation);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PendingConfirmation?> GetPendingConfirmationAsync(
        string conversationKey,
        string participantId,
        string confirmationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                action_kind,
                conversation_key,
                participant_id,
                operation_name,
                payload_json,
                summary,
                risk,
                status,
                created_utc,
                expires_utc,
                completed_utc,
                correlation_id,
                result_json,
                error
            FROM pending_confirmations
            WHERE id = $id
              AND conversation_key = $conversationKey
              AND participant_id = $participantId;
            """;
        command.Parameters.AddWithValue("$id", confirmationId);
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$participantId", participantId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPendingConfirmation(reader)
            : null;
    }

    public async Task<PendingConfirmation?> GetPendingConfirmationByIdAsync(
        string confirmationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                action_kind,
                conversation_key,
                participant_id,
                operation_name,
                payload_json,
                summary,
                risk,
                status,
                created_utc,
                expires_utc,
                completed_utc,
                correlation_id,
                result_json,
                error
            FROM pending_confirmations
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", confirmationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPendingConfirmation(reader)
            : null;
    }

    public async Task<PendingConfirmation?> GetLatestPendingConfirmationAsync(
        string conversationKey,
        string participantId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                action_kind,
                conversation_key,
                participant_id,
                operation_name,
                payload_json,
                summary,
                risk,
                status,
                created_utc,
                expires_utc,
                completed_utc,
                correlation_id,
                result_json,
                error
            FROM pending_confirmations
            WHERE conversation_key = $conversationKey
              AND participant_id = $participantId
              AND correlation_id = $correlationId
              AND status = $status
            ORDER BY created_utc DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);
        command.Parameters.AddWithValue("$participantId", participantId);
        command.Parameters.AddWithValue("$correlationId", correlationId);
        command.Parameters.AddWithValue("$status", ConfirmationActionStatus.Pending.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPendingConfirmation(reader)
            : null;
    }

    public async Task<bool> TryUpdateConfirmationStatusAsync(
        string confirmationId,
        ConfirmationActionStatus expectedStatus,
        ConfirmationActionStatus newStatus,
        DateTimeOffset? completedAtUtc,
        string? resultJson,
        string? error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationId);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE pending_confirmations
            SET status = $newStatus,
                completed_utc = $completedUtc,
                result_json = $resultJson,
                error = $error
            WHERE id = $id
              AND status = $expectedStatus;
            """;
        command.Parameters.AddWithValue("$id", confirmationId);
        command.Parameters.AddWithValue("$expectedStatus", expectedStatus.ToString());
        command.Parameters.AddWithValue("$newStatus", newStatus.ToString());
        command.Parameters.AddWithValue("$completedUtc", ToDbValue(completedAtUtc));
        command.Parameters.AddWithValue("$resultJson", ToDbValue(resultJson));
        command.Parameters.AddWithValue("$error", ToDbValue(error));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task AppendConfirmationAuditAsync(
        ConfirmationAuditRecord auditRecord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditRecord);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO confirmation_audit (
                confirmation_id,
                action_kind,
                conversation_key,
                participant_id,
                operation_name,
                event,
                summary,
                details,
                created_utc)
            VALUES (
                $confirmationId,
                $actionKind,
                $conversationKey,
                $participantId,
                $operationName,
                $event,
                $summary,
                $details,
                $createdUtc);
            """;
        command.Parameters.AddWithValue("$confirmationId", auditRecord.ConfirmationId);
        command.Parameters.AddWithValue("$actionKind", auditRecord.ActionKind);
        command.Parameters.AddWithValue("$conversationKey", auditRecord.ConversationKey);
        command.Parameters.AddWithValue("$participantId", auditRecord.ParticipantId);
        command.Parameters.AddWithValue("$operationName", auditRecord.OperationName);
        command.Parameters.AddWithValue("$event", auditRecord.Event);
        command.Parameters.AddWithValue("$summary", auditRecord.Summary);
        command.Parameters.AddWithValue("$details", ToDbValue(auditRecord.Details));
        command.Parameters.AddWithValue("$createdUtc", auditRecord.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearConversationMessagesAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM conversation_messages
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearConversationSummaryAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM conversation_summary
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearConversationVectorMemoryAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM conversation_vector_memory
            WHERE conversation_key = $conversationKey;
            """;
        command.Parameters.AddWithValue("$conversationKey", conversationKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddPendingConfirmationParameters(
        SqliteCommand command,
        PendingConfirmation confirmation)
    {
        command.Parameters.AddWithValue("$id", confirmation.Id);
        command.Parameters.AddWithValue("$actionKind", confirmation.ActionKind);
        command.Parameters.AddWithValue("$conversationKey", confirmation.ConversationKey);
        command.Parameters.AddWithValue("$participantId", confirmation.ParticipantId);
        command.Parameters.AddWithValue("$operationName", confirmation.OperationName);
        command.Parameters.AddWithValue("$payloadJson", confirmation.PayloadJson);
        command.Parameters.AddWithValue("$summary", confirmation.Summary);
        command.Parameters.AddWithValue("$risk", confirmation.Risk);
        command.Parameters.AddWithValue("$status", confirmation.Status.ToString());
        command.Parameters.AddWithValue("$createdUtc", confirmation.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expiresUtc", confirmation.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$completedUtc", ToDbValue(confirmation.CompletedAtUtc));
        command.Parameters.AddWithValue("$correlationId", confirmation.CorrelationId);
        command.Parameters.AddWithValue("$resultJson", ToDbValue(confirmation.ResultJson));
        command.Parameters.AddWithValue("$error", ToDbValue(confirmation.Error));
    }

    private static PendingConfirmation ReadPendingConfirmation(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            Enum.Parse<ConfirmationActionStatus>(reader.GetString(8), ignoreCase: true),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));

    private static object ToDbValue(DateTimeOffset? value) =>
        value.HasValue ? value.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value;

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
