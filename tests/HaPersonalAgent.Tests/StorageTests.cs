using HaPersonalAgent.Configuration;
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
