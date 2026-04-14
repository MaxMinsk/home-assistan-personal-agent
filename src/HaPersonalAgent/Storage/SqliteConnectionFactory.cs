using HaPersonalAgent.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: фабрика открытых SQLite connections.
/// Зачем: путь к базе приходит из конфигурации, а создание директорий и connection string лучше держать в одном месте.
/// Как: на каждый вызов создает директорию базы при необходимости, открывает новый SqliteConnection и отдает его вызывающему коду.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly IOptions<AgentOptions> _options;

    public SqliteConnectionFactory(IOptions<AgentOptions> options)
    {
        _options = options;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var databasePath = _options.Value.StateDatabasePath;
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new InvalidOperationException("Agent state database path is not configured.");
        }

        var fullDatabasePath = Path.GetFullPath(databasePath);
        var databaseDirectory = Path.GetDirectoryName(fullDatabasePath);
        if (!string.IsNullOrEmpty(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullDatabasePath,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        return connection;
    }
}
