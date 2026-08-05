using CodeReviewr.Core.AI;
using CodeReviewr.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace CodeReviewr.Persistence;

/// <summary>
/// Durable AI result store backed by durable.db (schema v5+). Assumes the
/// ai_* tables already exist — they are created by <see cref="SqliteDurableUserStore"/>'s
/// schema migration V3, renamed to <c>session_key</c> in V4, and slimmed in V5.
/// </summary>
public sealed class SqliteAiResultStore : IAiResultStore, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteAiResultStore(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeReviewr",
            "durable.db");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
    }

    public async Task UpsertRunAsync(AiRunRecord run, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ai_runs (
                    id, session_key, head_sha, merge_base_sha, copilot_session_id, state,
                    turns_used, ad_hoc_instructions, cache_key, error_message, started_utc, finished_utc)
                VALUES (
                    $id, $pr, $head, $base, $session, $state,
                    $turns, $adhoc, $cacheKey, $error, $started, $finished)
                ON CONFLICT(id) DO UPDATE SET
                    session_key = excluded.session_key,
                    head_sha = excluded.head_sha,
                    merge_base_sha = excluded.merge_base_sha,
                    copilot_session_id = excluded.copilot_session_id,
                    state = excluded.state,
                    turns_used = excluded.turns_used,
                    ad_hoc_instructions = excluded.ad_hoc_instructions,
                    cache_key = excluded.cache_key,
                    error_message = excluded.error_message,
                    started_utc = excluded.started_utc,
                    finished_utc = excluded.finished_utc;
                """;
            cmd.Parameters.AddWithValue("$id", run.Id);
            cmd.Parameters.AddWithValue("$pr", run.SessionKey);
            cmd.Parameters.AddWithValue("$head", run.HeadSha);
            cmd.Parameters.AddWithValue("$base", run.MergeBaseSha);
            cmd.Parameters.AddWithValue("$session", (object?)run.CopilotSessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", run.State.ToString());
            cmd.Parameters.AddWithValue("$turns", run.TurnsUsed);
            cmd.Parameters.AddWithValue("$adhoc", (object?)run.AdHocInstructions ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cacheKey", run.CacheKey);
            cmd.Parameters.AddWithValue("$error", (object?)run.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$started", run.StartedUtc.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$finished", (object?)run.FinishedUtc?.UtcDateTime.ToString("O") ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AiRunRecord?> GetLatestRunAsync(string sessionKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_key, head_sha, merge_base_sha, copilot_session_id, state,
                       turns_used, ad_hoc_instructions, cache_key, error_message, started_utc, finished_utc
                FROM ai_runs
                WHERE session_key = $pr
                ORDER BY started_utc DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$pr", sessionKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRun(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AiRunRecord?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_key, head_sha, merge_base_sha, copilot_session_id, state,
                       turns_used, ad_hoc_instructions, cache_key, error_message, started_utc, finished_utc
                FROM ai_runs
                WHERE id = $id
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", runId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRun(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertPrResultAsync(AiPrResultRecord result, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ai_pr_results (run_id, session_key, cache_key, payload_json, updated_utc)
                VALUES ($run, $pr, $cacheKey, $payload, $updated)
                ON CONFLICT(run_id) DO UPDATE SET
                    session_key = excluded.session_key,
                    cache_key = excluded.cache_key,
                    payload_json = excluded.payload_json,
                    updated_utc = excluded.updated_utc
                ON CONFLICT(cache_key) DO UPDATE SET
                    run_id = excluded.run_id,
                    session_key = excluded.session_key,
                    payload_json = excluded.payload_json,
                    updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$run", result.RunId);
            cmd.Parameters.AddWithValue("$pr", result.SessionKey);
            cmd.Parameters.AddWithValue("$cacheKey", result.CacheKey);
            cmd.Parameters.AddWithValue("$payload", result.PayloadJson);
            cmd.Parameters.AddWithValue("$updated", result.UpdatedUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AiPrResultRecord?> GetPrResultByCacheKeyAsync(string cacheKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, session_key, cache_key, payload_json, updated_utc
                FROM ai_pr_results
                WHERE cache_key = $cacheKey
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$cacheKey", cacheKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadPrResult(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AiPrResultRecord?> GetPrResultForRunAsync(string runId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, session_key, cache_key, payload_json, updated_utc
                FROM ai_pr_results
                WHERE run_id = $run
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$run", runId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadPrResult(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertFileResultAsync(AiFileResultRecord result, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ai_file_results (
                    run_id, session_key, path, cache_key, classification, summary_json, updated_utc)
                VALUES (
                    $run, $pr, $path, $cacheKey, $classification, $summary, $updated)
                ON CONFLICT(cache_key) DO UPDATE SET
                    run_id = excluded.run_id,
                    session_key = excluded.session_key,
                    path = excluded.path,
                    classification = excluded.classification,
                    summary_json = excluded.summary_json,
                    updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$run", result.RunId);
            cmd.Parameters.AddWithValue("$pr", result.SessionKey);
            cmd.Parameters.AddWithValue("$path", result.Path);
            cmd.Parameters.AddWithValue("$cacheKey", result.CacheKey);
            cmd.Parameters.AddWithValue("$classification", (object?)result.Classification ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$summary", (object?)result.SummaryJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", result.UpdatedUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AiFileResultRecord?> GetFileResultByCacheKeyAsync(string cacheKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, session_key, path, cache_key, classification, summary_json, updated_utc
                FROM ai_file_results
                WHERE cache_key = $cacheKey
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$cacheKey", cacheKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadFileResult(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AiFileResultRecord>> ListFileResultsForRunAsync(string runId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, session_key, path, cache_key, classification, summary_json, updated_utc
                FROM ai_file_results
                WHERE run_id = $run
                ORDER BY path ASC;
                """;
            cmd.Parameters.AddWithValue("$run", runId);
            var results = new List<AiFileResultRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                results.Add(ReadFileResult(reader));
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAnnotationAsync(AiAnnotationRecord annotation, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ai_annotations (
                    id, run_id, session_key, path, blob_oid, start_line, end_line, side, severity, body, read_state, updated_utc)
                VALUES (
                    $id, $run, $pr, $path, $blob, $start, $end, $side, $severity, $body, $readState, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    run_id = excluded.run_id,
                    session_key = excluded.session_key,
                    path = excluded.path,
                    blob_oid = excluded.blob_oid,
                    start_line = excluded.start_line,
                    end_line = excluded.end_line,
                    side = excluded.side,
                    severity = excluded.severity,
                    body = excluded.body,
                    read_state = excluded.read_state,
                    updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$id", annotation.Id);
            cmd.Parameters.AddWithValue("$run", annotation.RunId);
            cmd.Parameters.AddWithValue("$pr", annotation.SessionKey);
            cmd.Parameters.AddWithValue("$path", annotation.Path);
            cmd.Parameters.AddWithValue("$blob", annotation.BlobOid);
            cmd.Parameters.AddWithValue("$start", annotation.StartLine);
            cmd.Parameters.AddWithValue("$end", annotation.EndLine);
            cmd.Parameters.AddWithValue("$side", annotation.Side);
            cmd.Parameters.AddWithValue("$severity", annotation.Severity);
            cmd.Parameters.AddWithValue("$body", annotation.Body);
            cmd.Parameters.AddWithValue("$readState", annotation.ReadState.ToString());
            cmd.Parameters.AddWithValue("$updated", annotation.UpdatedUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AiAnnotationRecord>> ListAnnotationsAsync(
        string sessionKey,
        string? path = null,
        bool includeDismissed = false,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            var sql = """
                SELECT id, run_id, session_key, path, blob_oid, start_line, end_line, side, severity, body, read_state, updated_utc
                FROM ai_annotations
                WHERE session_key = $pr
                """;
            cmd.Parameters.AddWithValue("$pr", sessionKey);

            if (path is not null)
            {
                sql += " AND path = $path";
                cmd.Parameters.AddWithValue("$path", path);
            }

            if (!includeDismissed)
            {
                sql += " AND read_state <> $dismissed";
                cmd.Parameters.AddWithValue("$dismissed", AiAnnotationReadState.Dismissed.ToString());
            }

            sql += " ORDER BY path ASC, start_line ASC;";
            cmd.CommandText = sql;

            var results = new List<AiAnnotationRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                results.Add(ReadAnnotation(reader));
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAnnotationReadStateAsync(string id, AiAnnotationReadState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE ai_annotations
                SET read_state = $state,
                    updated_utc = $updated
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$state", state.ToString());
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendChatMessageAsync(string sessionKey, AiChatMessage message, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ai_chat_messages (session_key, role, content, timestamp_utc)
                VALUES ($pr, $role, $content, $timestamp);
                """;
            cmd.Parameters.AddWithValue("$pr", sessionKey);
            cmd.Parameters.AddWithValue("$role", message.Role);
            cmd.Parameters.AddWithValue("$content", message.Content);
            cmd.Parameters.AddWithValue("$timestamp", message.TimestampUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AiChatMessage>> ListChatMessagesAsync(string sessionKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT role, content, timestamp_utc
                FROM ai_chat_messages
                WHERE session_key = $pr
                ORDER BY id ASC;
                """;
            cmd.Parameters.AddWithValue("$pr", sessionKey);
            var results = new List<AiChatMessage>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new AiChatMessage(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2))));
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearChatMessagesAsync(string sessionKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM ai_chat_messages
                WHERE session_key = $pr;
                """;
            cmd.Parameters.AddWithValue("$pr", sessionKey);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM ai_annotations;
                DELETE FROM ai_file_results;
                DELETE FROM ai_pr_results;
                DELETE FROM ai_chat_messages;
                DELETE FROM ai_runs;
                """;
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

    private static AiRunRecord ReadRun(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            Enum.Parse<AiRunState>(reader.GetString(5)),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            DateTimeOffset.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)));

    private static AiPrResultRecord ReadPrResult(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)));

    private static AiFileResultRecord ReadFileResult(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)));

    private static AiAnnotationRecord ReadAnnotation(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            Enum.Parse<AiAnnotationReadState>(reader.GetString(10)),
            DateTimeOffset.Parse(reader.GetString(11)));

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
