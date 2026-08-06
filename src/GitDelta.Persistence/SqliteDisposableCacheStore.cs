using Microsoft.Data.Sqlite;

namespace GitDelta.Persistence;

public sealed class SqliteDisposableCacheStore : IDisposableCacheStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public SqliteDisposableCacheStore(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitDelta",
            "cache.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
    }

    public int SchemaVersion { get; private set; }

    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        var existing = cmd.ExecuteScalar();
        if (existing is null)
        {
            cmd.CommandText = $"INSERT INTO schema_version (version) VALUES ({CurrentSchemaVersion});";
            cmd.ExecuteNonQuery();
            SchemaVersion = CurrentSchemaVersion;
        }
        else
        {
            SchemaVersion = Convert.ToInt32(existing);
        }
    }

    public void Wipe()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM schema_version;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"INSERT INTO schema_version (version) VALUES ({CurrentSchemaVersion});";
        cmd.ExecuteNonQuery();
        SchemaVersion = CurrentSchemaVersion;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private SqliteConnection OpenConnection()
    {
        _connection ??= new SqliteConnection(_connectionString);
        if (_connection.State != System.Data.ConnectionState.Open)
            _connection.Open();
        return _connection;
    }
}
