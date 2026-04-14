using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: repository для небольшого persistent state агента.
/// Зачем: Telegram long polling должен переживать рестарт и не обрабатывать старые updates повторно.
/// Как: при первом обращении создает таблицу agent_state и хранит значения как key/value строки в SQLite.
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
}
