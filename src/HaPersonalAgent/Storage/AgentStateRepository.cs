using HaPersonalAgent.Agent;
using HaPersonalAgent.Confirmation;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: repository для небольшого persistent state агента.
/// Зачем: Telegram offset, краткосрочный контекст диалога и pending confirmations должны переживать рестарт add-on контейнера.
/// Как: при первом обращении создает таблицы, затем хранит offset как key/value, историю как append-only turns, а confirmation actions отдельно от memory.
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
