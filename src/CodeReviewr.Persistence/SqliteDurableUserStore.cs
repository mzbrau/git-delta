using Microsoft.Data.Sqlite;

namespace CodeReviewr.Persistence;

public sealed class SqliteDurableUserStore : IDurableUserStore
{
    public const int CurrentSchemaVersion = 4;

    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteDurableUserStore(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeReviewr",
            "durable.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
    }

    public int SchemaVersion { get; private set; }

    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();

        SchemaVersion = ReadSchemaVersion(connection);
        if (SchemaVersion < 1)
        {
            ApplyMigrationV1(connection);
            SchemaVersion = 1;
        }

        if (SchemaVersion < 2)
        {
            ApplyMigrationV2(connection);
            SchemaVersion = 2;
        }

        if (SchemaVersion < 3)
        {
            ApplyMigrationV3(connection);
            SchemaVersion = 3;
        }

        if (SchemaVersion < 4)
        {
            ApplyMigrationV4(connection);
            SchemaVersion = 4;
        }
    }

    public async Task EnqueueAsync(OutboxEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO outbox_entries (
                    id, account_host, account_login, pr_node_id, kind, payload_json,
                    created_utc, attempts, last_error, state)
                VALUES (
                    $id, $host, $login, $pr, $kind, $payload,
                    $created, $attempts, $error, $state);
                """;
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$host", entry.AccountHost);
            cmd.Parameters.AddWithValue("$login", entry.AccountLogin);
            cmd.Parameters.AddWithValue("$pr", entry.PrNodeId);
            cmd.Parameters.AddWithValue("$kind", entry.Kind.ToString());
            cmd.Parameters.AddWithValue("$payload", entry.PayloadJson);
            cmd.Parameters.AddWithValue("$created", entry.CreatedUtc.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$attempts", entry.Attempts);
            cmd.Parameters.AddWithValue("$error", (object?)entry.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", entry.State.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OutboxEntry>> ListAsync(
        OutboxState? state = null,
        string? prNodeId = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            var sql = """
                SELECT id, account_host, account_login, pr_node_id, kind, payload_json,
                       created_utc, attempts, last_error, state
                FROM outbox_entries
                WHERE 1=1
                """;
            if (state is not null)
            {
                sql += " AND state = $state";
                cmd.Parameters.AddWithValue("$state", state.Value.ToString());
            }

            if (prNodeId is not null)
            {
                sql += " AND pr_node_id = $pr";
                cmd.Parameters.AddWithValue("$pr", prNodeId);
            }

            sql += " ORDER BY created_utc ASC;";
            cmd.CommandText = sql;
            var results = new List<OutboxEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                results.Add(ReadOutboxEntry(reader));
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkInFlightAsync(string id, CancellationToken ct = default)
    {
        await UpdateStateAsync(id, OutboxState.InFlight, null, incrementAttempts: false, ct).ConfigureAwait(false);
    }

    public async Task MarkPendingAsync(string id, string? lastError = null, CancellationToken ct = default)
    {
        await UpdateStateAsync(id, OutboxState.Pending, lastError, incrementAttempts: true, ct).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(string id, string error, CancellationToken ct = default)
    {
        await UpdateStateAsync(id, OutboxState.Failed, error, incrementAttempts: true, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM outbox_entries WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecoverInFlightAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE outbox_entries
                SET state = 'Pending'
                WHERE state = 'InFlight';
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetNoteAsync(string prNodeId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT markdown FROM local_notes WHERE pr_node_id = $pr;";
            cmd.Parameters.AddWithValue("$pr", prNodeId);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is string s ? s : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetNoteAsync(string prNodeId, string markdown, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO local_notes (pr_node_id, markdown, updated_utc)
                VALUES ($pr, $markdown, $updated)
                ON CONFLICT(pr_node_id) DO UPDATE SET
                    markdown = excluded.markdown,
                    updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$pr", prNodeId);
            cmd.Parameters.AddWithValue("$markdown", markdown);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetViewedAsync(
        string prNodeId,
        string path,
        string contentId,
        DateTimeOffset viewedUtc,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO local_viewed (pr_node_id, path, content_id, viewed_utc)
                VALUES ($pr, $path, $contentId, $viewed)
                ON CONFLICT(pr_node_id, path) DO UPDATE SET
                    content_id = excluded.content_id,
                    viewed_utc = excluded.viewed_utc;
                """;
            cmd.Parameters.AddWithValue("$pr", prNodeId);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$contentId", contentId);
            cmd.Parameters.AddWithValue("$viewed", viewedUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveViewedAsync(string prNodeId, string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM local_viewed WHERE pr_node_id = $pr AND path = $path;";
            cmd.Parameters.AddWithValue("$pr", prNodeId);
            cmd.Parameters.AddWithValue("$path", path);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalViewedEntry>> ListAsync(string prNodeId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT pr_node_id, path, content_id, viewed_utc
                FROM local_viewed
                WHERE pr_node_id = $pr;
                """;
            cmd.Parameters.AddWithValue("$pr", prNodeId);
            var results = new List<LocalViewedEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new LocalViewedEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3))));
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsViewedAsync(string prNodeId, string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT 1 FROM local_viewed
                WHERE pr_node_id = $pr AND path = $path
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$pr", prNodeId);
            cmd.Parameters.AddWithValue("$path", path);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is not null and not DBNull;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        _gate.Dispose();
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        var existing = cmd.ExecuteScalar();
        return existing is null or DBNull ? 0 : Convert.ToInt32(existing);
    }

    private static void ApplyMigrationV1(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS outbox (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                payload TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT INTO schema_migrations (version) VALUES (1);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void ApplyMigrationV2(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS outbox;
            CREATE TABLE IF NOT EXISTS outbox_entries (
                id TEXT NOT NULL PRIMARY KEY,
                account_host TEXT NOT NULL,
                account_login TEXT NOT NULL,
                pr_node_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                state TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_outbox_entries_state ON outbox_entries(state);
            CREATE INDEX IF NOT EXISTS idx_outbox_entries_pr ON outbox_entries(pr_node_id);
            CREATE TABLE IF NOT EXISTS local_notes (
                pr_node_id TEXT NOT NULL PRIMARY KEY,
                markdown TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS local_viewed (
                pr_node_id TEXT NOT NULL,
                path TEXT NOT NULL,
                content_id TEXT NOT NULL,
                viewed_utc TEXT NOT NULL,
                PRIMARY KEY (pr_node_id, path)
            );
            INSERT INTO schema_migrations (version) VALUES (2);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void ApplyMigrationV3(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ai_runs (
                id TEXT NOT NULL PRIMARY KEY,
                pr_node_id TEXT NOT NULL,
                head_sha TEXT NOT NULL,
                merge_base_sha TEXT NOT NULL,
                copilot_session_id TEXT,
                state TEXT NOT NULL,
                turns_used INTEGER NOT NULL DEFAULT 0,
                ad_hoc_instructions TEXT,
                cache_key TEXT NOT NULL,
                error_message TEXT,
                started_utc TEXT NOT NULL,
                finished_utc TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_ai_runs_pr_node_id ON ai_runs(pr_node_id);
            CREATE INDEX IF NOT EXISTS idx_ai_runs_cache_key ON ai_runs(cache_key);

            CREATE TABLE IF NOT EXISTS ai_pr_results (
                run_id TEXT NOT NULL PRIMARY KEY,
                pr_node_id TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_ai_pr_results_cache_key ON ai_pr_results(cache_key);
            CREATE INDEX IF NOT EXISTS idx_ai_pr_results_pr_node_id ON ai_pr_results(pr_node_id);

            CREATE TABLE IF NOT EXISTS ai_file_results (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                pr_node_id TEXT NOT NULL,
                path TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                classification TEXT,
                priority_stars INTEGER NOT NULL DEFAULT 0,
                guidance TEXT,
                summary_json TEXT,
                updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_ai_file_results_cache_key ON ai_file_results(cache_key);
            CREATE INDEX IF NOT EXISTS idx_ai_file_results_run_id ON ai_file_results(run_id);
            CREATE INDEX IF NOT EXISTS idx_ai_file_results_pr_node_id ON ai_file_results(pr_node_id);

            CREATE TABLE IF NOT EXISTS ai_annotations (
                id TEXT NOT NULL PRIMARY KEY,
                run_id TEXT NOT NULL,
                pr_node_id TEXT NOT NULL,
                path TEXT NOT NULL,
                blob_oid TEXT NOT NULL,
                start_line INTEGER NOT NULL,
                end_line INTEGER NOT NULL,
                side TEXT NOT NULL,
                severity TEXT NOT NULL,
                body TEXT NOT NULL,
                read_state TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ai_annotations_pr_node_id ON ai_annotations(pr_node_id);
            CREATE INDEX IF NOT EXISTS idx_ai_annotations_run_id ON ai_annotations(run_id);

            CREATE TABLE IF NOT EXISTS ai_chat_messages (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                pr_node_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ai_chat_messages_pr_node_id ON ai_chat_messages(pr_node_id);

            INSERT INTO schema_migrations (version) VALUES (3);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void ApplyMigrationV4(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE ai_runs RENAME COLUMN pr_node_id TO session_key;
            DROP INDEX IF EXISTS idx_ai_runs_pr_node_id;
            CREATE INDEX IF NOT EXISTS idx_ai_runs_session_key ON ai_runs(session_key);

            ALTER TABLE ai_pr_results RENAME COLUMN pr_node_id TO session_key;
            DROP INDEX IF EXISTS idx_ai_pr_results_pr_node_id;
            CREATE INDEX IF NOT EXISTS idx_ai_pr_results_session_key ON ai_pr_results(session_key);

            ALTER TABLE ai_file_results RENAME COLUMN pr_node_id TO session_key;
            DROP INDEX IF EXISTS idx_ai_file_results_pr_node_id;
            CREATE INDEX IF NOT EXISTS idx_ai_file_results_session_key ON ai_file_results(session_key);

            ALTER TABLE ai_annotations RENAME COLUMN pr_node_id TO session_key;
            DROP INDEX IF EXISTS idx_ai_annotations_pr_node_id;
            CREATE INDEX IF NOT EXISTS idx_ai_annotations_session_key ON ai_annotations(session_key);

            ALTER TABLE ai_chat_messages RENAME COLUMN pr_node_id TO session_key;
            DROP INDEX IF EXISTS idx_ai_chat_messages_pr_node_id;
            CREATE INDEX IF NOT EXISTS idx_ai_chat_messages_session_key ON ai_chat_messages(session_key);

            CREATE TABLE IF NOT EXISTS local_review_comments (
              id TEXT NOT NULL PRIMARY KEY,
              repository_key TEXT NOT NULL,
              path TEXT NOT NULL,
              start_line INTEGER NOT NULL,
              end_line INTEGER NOT NULL,
              side TEXT NOT NULL,
              body TEXT NOT NULL,
              is_resolved INTEGER NOT NULL DEFAULT 0,
              content_id TEXT,
              created_utc TEXT NOT NULL,
              updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_local_review_comments_repo ON local_review_comments(repository_key);
            CREATE INDEX IF NOT EXISTS idx_local_review_comments_repo_unresolved ON local_review_comments(repository_key, is_resolved);

            INSERT INTO schema_migrations (version) VALUES (4);
            """;
        cmd.ExecuteNonQuery();
    }

    private async Task UpdateStateAsync(
        string id,
        OutboxState state,
        string? lastError,
        bool incrementAttempts,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = incrementAttempts
                ? """
                  UPDATE outbox_entries
                  SET state = $state,
                      last_error = $error,
                      attempts = attempts + 1
                  WHERE id = $id;
                  """
                : """
                  UPDATE outbox_entries
                  SET state = $state
                  WHERE id = $id;
                  """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$state", state.ToString());
            cmd.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static OutboxEntry ReadOutboxEntry(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Enum.Parse<OutboxKind>(reader.GetString(4)),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            Enum.Parse<OutboxState>(reader.GetString(9)));

    private SqliteConnection OpenConnection()
    {
        _connection ??= new SqliteConnection(_connectionString);
        if (_connection.State != System.Data.ConnectionState.Open)
            _connection.Open();
        return _connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
