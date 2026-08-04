using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace CodeReviewr.Persistence;

/// <summary>
/// Local review comment store backed by durable.db. Prefer running
/// <see cref="SqliteDurableUserStore.EnsureSchema"/> first (schema v4+ owns the
/// canonical migration); <see cref="EnsureTables"/> also creates
/// <c>local_review_comments</c> idempotently so this store is self-sufficient
/// if the migration has not been applied in-process yet.
/// </summary>
public sealed class SqliteLocalCommentStore : ILocalCommentStore, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteLocalCommentStore(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeReviewr",
            "durable.db");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
        EnsureTables();
    }

    public async Task<LocalCommentRecord> AddAsync(LocalCommentCreate create, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var record = new LocalCommentRecord(
                Guid.NewGuid().ToString("N"),
                create.RepositoryKey,
                create.Path,
                create.StartLine,
                create.EndLine,
                create.Side,
                create.Body,
                IsResolved: false,
                create.ContentId,
                now,
                now);

            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO local_review_comments (
                    id, repository_key, path, start_line, end_line, side, body,
                    is_resolved, content_id, created_utc, updated_utc)
                VALUES (
                    $id, $repo, $path, $start, $end, $side, $body,
                    $resolved, $contentId, $created, $updated);
                """;
            BindRecord(cmd, record);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalCommentRecord>> ListAsync(string repositoryKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, repository_key, path, start_line, end_line, side, body,
                       is_resolved, content_id, created_utc, updated_utc
                FROM local_review_comments
                WHERE repository_key = $repo
                ORDER BY path ASC, start_line ASC, created_utc ASC;
                """;
            cmd.Parameters.AddWithValue("$repo", repositoryKey);
            var results = new List<LocalCommentRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                results.Add(ReadRecord(reader));
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountUnresolvedAsync(string repositoryKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM local_review_comments
                WHERE repository_key = $repo
                  AND is_resolved = 0;
                """;
            cmd.Parameters.AddWithValue("$repo", repositoryKey);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt32(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetResolvedAsync(string id, bool isResolved, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE local_review_comments
                SET is_resolved = $resolved,
                    updated_utc = $updated
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$resolved", isResolved ? 1 : 0);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateBodyAsync(string id, string body, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE local_review_comments
                SET body = $body,
                    updated_utc = $updated
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$body", body);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM local_review_comments
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private void EnsureTables()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
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
            CREATE INDEX IF NOT EXISTS idx_local_review_comments_repo
                ON local_review_comments(repository_key);
            CREATE INDEX IF NOT EXISTS idx_local_review_comments_repo_unresolved
                ON local_review_comments(repository_key, is_resolved);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void BindRecord(SqliteCommand cmd, LocalCommentRecord record)
    {
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$repo", record.RepositoryKey);
        cmd.Parameters.AddWithValue("$path", record.Path);
        cmd.Parameters.AddWithValue("$start", record.StartLine);
        cmd.Parameters.AddWithValue("$end", record.EndLine);
        cmd.Parameters.AddWithValue("$side", record.Side.ToString());
        cmd.Parameters.AddWithValue("$body", record.Body);
        cmd.Parameters.AddWithValue("$resolved", record.IsResolved ? 1 : 0);
        cmd.Parameters.AddWithValue("$contentId", (object?)record.ContentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", record.CreatedUtc.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", record.UpdatedUtc.UtcDateTime.ToString("O"));
    }

    private static LocalCommentRecord ReadRecord(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            Enum.Parse<DiffSide>(reader.GetString(5)),
            reader.GetString(6),
            reader.GetInt32(7) != 0,
            reader.IsDBNull(8) ? null : reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9)),
            DateTimeOffset.Parse(reader.GetString(10)));

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
