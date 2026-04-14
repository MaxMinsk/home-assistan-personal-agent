using HaPersonalAgent.Agent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: repository для небольшого persistent state агента.
/// Зачем: Telegram long polling и краткосрочный контекст диалога должны переживать рестарт add-on контейнера.
/// Как: при первом обращении создает таблицы agent_state и conversation_messages, затем хранит offset как key/value, а историю как append-only сообщения по conversation key.
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
}
