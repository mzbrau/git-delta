using System.Text;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Git.Internal;

namespace GitDelta.Git;

/// <summary>
/// Interactive rebase driven by a prepared todo list. Uses <c>GIT_SEQUENCE_EDITOR</c> /
/// <c>GIT_EDITOR</c> helpers so the GUI owns the plan without spawning a real editor.
/// </summary>
public sealed class GitRebaseService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitRebaseService
{
    private const string SessionMarkerFileName = "gitdelta-rebase-session";

    public Task<RebaseRunResult> StartInteractiveAsync(
        string repositoryPath,
        string ontoRef,
        IReadOnlyList<RebaseTodoEntry> todo,
        CancellationToken ct = default) =>
        gates.For(repositoryPath).RunWorktreeWriteAsync(async token =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ontoRef);
            ArgumentNullException.ThrowIfNull(todo);

            var kept = todo.Where(t => t.Action != RebaseTodoAction.Drop).ToList();
            if (kept.Count == 0)
                throw new GitException("Interactive rebase requires at least one non-dropped commit.");

            ValidateTodo(kept);

            // Drop any leftover session from a previous interrupted rebase in this repo.
            CleanupSession(repositoryPath);

            var sessionDir = Path.Combine(
                Path.GetTempPath(),
                "gitdelta-rebase",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);

            var retainSession = false;
            try
            {
                var todoPath = Path.Combine(sessionDir, "todo");
                var messagesDir = Path.Combine(sessionDir, "messages");
                Directory.CreateDirectory(messagesDir);

                await File.WriteAllTextAsync(todoPath, BuildTodoFile(kept), token).ConfigureAwait(false);

                var messageIndex = 0;
                for (var i = 0; i < kept.Count; i++)
                {
                    var entry = kept[i];
                    if (entry.Action == RebaseTodoAction.Reword)
                    {
                        WriteQueuedMessage(messagesDir, messageIndex++, entry.Message!, token);
                        continue;
                    }

                    if (entry.Action != RebaseTodoAction.Squash)
                        continue;

                    // Git opens the editor once at the end of a contiguous squash/fixup run.
                    var message = entry.Message;
                    var runEnd = i;
                    while (runEnd + 1 < kept.Count
                           && kept[runEnd + 1].Action is RebaseTodoAction.Squash or RebaseTodoAction.Fixup)
                    {
                        runEnd++;
                        if (kept[runEnd].Action == RebaseTodoAction.Squash
                            && !string.IsNullOrWhiteSpace(kept[runEnd].Message))
                        {
                            message = kept[runEnd].Message;
                        }
                    }

                    WriteQueuedMessage(messagesDir, messageIndex++, message!, token);
                    i = runEnd;
                }

                static void WriteQueuedMessage(string dir, int index, string message, CancellationToken token)
                {
                    var text = message.TrimEnd() + "\n";
                    File.WriteAllText(Path.Combine(dir, $"{index}.txt"), text);
                }

                await File.WriteAllTextAsync(
                    Path.Combine(sessionDir, "msg_index"),
                    "0",
                    token).ConfigureAwait(false);

                var (sequenceEditor, commitEditor) = WriteEditorScripts(sessionDir, todoPath);
                WriteSessionMarker(repositoryPath, sessionDir);

                var env = new Dictionary<string, string?>
                {
                    ["GIT_SEQUENCE_EDITOR"] = QuoteEditorCommand(sequenceEditor),
                    ["GIT_EDITOR"] = QuoteEditorCommand(commitEditor),
                    // Prevent nested editors / prompts if a hook tries to open one.
                    ["GIT_TERMINAL_PROMPT"] = "0",
                };

                var result = await runner.RunAsync(
                    repositoryPath,
                    ["rebase", "-i", ontoRef],
                    new GitProcessOptions
                    {
                        AllowNonZeroExitCode = true,
                        ExtraEnvironment = env,
                    },
                    token).ConfigureAwait(false);

                var interpreted = InterpretResult(repositoryPath, result);
                retainSession = interpreted.Outcome == RebaseRunOutcome.Conflicts;
                return interpreted;
            }
            finally
            {
                if (!retainSession)
                    CleanupSession(repositoryPath, sessionDir);
            }
        }, ct);

    public Task<RebaseRunResult> ContinueAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.For(repositoryPath).RunWorktreeWriteAsync(async token =>
        {
            var env = TryBuildContinueEnvironment(repositoryPath);
            var result = await runner.RunAsync(
                repositoryPath,
                ["rebase", "--continue"],
                new GitProcessOptions
                {
                    AllowNonZeroExitCode = true,
                    ExtraEnvironment = env,
                },
                token).ConfigureAwait(false);

            var interpreted = InterpretResult(repositoryPath, result);
            if (interpreted.Outcome != RebaseRunOutcome.Conflicts)
                CleanupSession(repositoryPath);

            return interpreted;
        }, ct);

    public Task AbortAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.For(repositoryPath).RunWorktreeWriteAsync(async token =>
        {
            var inProgress = GitRepositoryPaths.DetectInProgress(repositoryPath);
            if (inProgress == InProgressOperation.Rebase)
            {
                await runner.RunAsync(repositoryPath, ["rebase", "--abort"], options: null, token)
                    .ConfigureAwait(false);
            }

            CleanupSession(repositoryPath);
        }, ct);

    private static RebaseRunResult InterpretResult(string repositoryPath, GitCommandResult result)
    {
        if (result.Succeeded)
            return new RebaseRunResult(RebaseRunOutcome.Completed);

        var inProgress = GitRepositoryPaths.DetectInProgress(repositoryPath);
        if (inProgress == InProgressOperation.Rebase)
        {
            var detail = string.IsNullOrWhiteSpace(result.Stderr)
                ? "Resolve conflicts, stage the changes, then resume."
                : result.Stderr.Trim();
            return new RebaseRunResult(RebaseRunOutcome.Conflicts, detail);
        }

        throw result.ToException(["rebase"]);
    }

    internal static void ValidateTodo(IReadOnlyList<RebaseTodoEntry> kept)
    {
        var first = kept[0].Action;
        if (first is RebaseTodoAction.Squash or RebaseTodoAction.Fixup)
            throw new GitException("The first commit in the rebase plan cannot be squash or fixup.");

        for (var i = 0; i < kept.Count; i++)
        {
            var entry = kept[i];
            if (string.IsNullOrWhiteSpace(entry.Oid))
                throw new GitException("Every rebase todo entry requires a commit oid.");

            if (entry.Action is RebaseTodoAction.Reword or RebaseTodoAction.Squash
                && string.IsNullOrWhiteSpace(entry.Message))
            {
                throw new GitException($"Commit {entry.Oid} requires a message for {entry.Action}.");
            }
        }
    }

    /// <summary>
    /// Filters dropped entries the same way <see cref="StartInteractiveAsync"/> does before building the todo file.
    /// </summary>
    internal static IReadOnlyList<RebaseTodoEntry> FilterDropped(IReadOnlyList<RebaseTodoEntry> todo) =>
        todo.Where(t => t.Action != RebaseTodoAction.Drop).ToList();

    internal static string BuildTodoFile(IReadOnlyList<RebaseTodoEntry> kept)
    {
        var sb = new StringBuilder();
        foreach (var entry in kept)
        {
            var verb = entry.Action switch
            {
                RebaseTodoAction.Pick => "pick",
                RebaseTodoAction.Reword => "reword",
                RebaseTodoAction.Squash => "squash",
                RebaseTodoAction.Fixup => "fixup",
                _ => throw new GitException($"Unsupported rebase action: {entry.Action}"),
            };
            sb.Append(verb).Append(' ').Append(entry.Oid).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Git treats editor env vars as a command line; quote so paths with spaces still launch.
    /// </summary>
    internal static string QuoteEditorCommand(string path) => $"\"{path}\"";

    private static (string SequenceEditor, string CommitEditor) WriteEditorScripts(
        string sessionDir,
        string todoPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var seqCmd = Path.Combine(sessionDir, "sequence-editor.cmd");
            var commitCmd = Path.Combine(sessionDir, "commit-editor.cmd");

            // %~1 is the path Git passes to the editor.
            File.WriteAllText(seqCmd, $"""
                @echo off
                copy /Y "{todoPath}" "%~1" >nul
                exit /b 0
                """);

            // Only overwrite for reword/squash. A conflict --continue on pick may still
            // invoke GIT_EDITOR; consuming a queued message there would steal it.
            File.WriteAllText(commitCmd, $$"""
                @echo off
                setlocal EnableDelayedExpansion
                set "DIR={{sessionDir}}"
                for /f "delims=" %%G in ('git rev-parse --git-dir 2^>nul') do set "GITDIR=%%G"
                if not defined GITDIR exit /b 0
                set "VERB="
                if exist "!GITDIR!\rebase-merge\done" (
                  for /f "usebackq delims=" %%L in ("!GITDIR!\rebase-merge\done") do set "LASTDONE=%%L"
                  for /f "tokens=1" %%V in ("!LASTDONE!") do set "VERB=%%V"
                )
                if /I "!VERB!"=="pick" exit /b 0
                if /I "!VERB!"=="p" exit /b 0
                if /I "!VERB!"=="edit" exit /b 0
                if /I "!VERB!"=="e" exit /b 0
                if /I "!VERB!"=="exec" exit /b 0
                if /I "!VERB!"=="x" exit /b 0
                if /I "!VERB!"=="break" exit /b 0
                if /I "!VERB!"=="b" exit /b 0
                if /I "!VERB!"=="drop" exit /b 0
                if /I "!VERB!"=="d" exit /b 0
                if "!VERB!"=="" exit /b 0
                set /p IDX=<"%DIR%\msg_index"
                set "MSG=%DIR%\messages\!IDX!.txt"
                if exist "!MSG!" (
                  copy /Y "!MSG!" "%~1" >nul
                  set /a NEXT=IDX+1
                  >"%DIR%\msg_index" echo !NEXT!
                )
                exit /b 0
                """);

            return (seqCmd, commitCmd);
        }

        var seqSh = Path.Combine(sessionDir, "sequence-editor.sh");
        var commitSh = Path.Combine(sessionDir, "commit-editor.sh");

        File.WriteAllText(seqSh, $$"""
            #!/bin/sh
            cp "{{todoPath}}" "$1"
            """);

        // Only overwrite for reword/squash. A conflict --continue on pick may still
        // invoke GIT_EDITOR; consuming a queued message there would steal it.
        File.WriteAllText(commitSh, $$"""
            #!/bin/sh
            DIR="{{sessionDir}}"
            GITDIR=$(git rev-parse --git-dir 2>/dev/null) || exit 0
            DONE="$GITDIR/rebase-merge/done"
            [ -f "$DONE" ] || exit 0
            VERB=$(awk 'NF { line=$0 } END { print line }' "$DONE" | awk '{ print $1 }')
            case "$VERB" in
              pick|p|edit|e|exec|x|break|b|drop|d|"") exit 0 ;;
            esac
            IDX=$(cat "$DIR/msg_index")
            MSG="$DIR/messages/$IDX.txt"
            if [ -f "$MSG" ]; then
              cp "$MSG" "$1"
              echo $((IDX + 1)) > "$DIR/msg_index"
            fi
            """);

        TryChmodExecutable(seqSh);
        TryChmodExecutable(commitSh);
        return (seqSh, commitSh);
    }

    private static void TryChmodExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            // Ignore — script may still run if the filesystem grants execute another way.
        }
    }

    private static string SessionMarkerPath(string repositoryPath) =>
        Path.Combine(GitRepositoryPaths.ResolveGitDir(repositoryPath), SessionMarkerFileName);

    private static void WriteSessionMarker(string repositoryPath, string sessionDir) =>
        File.WriteAllText(SessionMarkerPath(repositoryPath), sessionDir);

    private static string? TryReadSessionDir(string repositoryPath)
    {
        var marker = SessionMarkerPath(repositoryPath);
        if (!File.Exists(marker))
            return null;

        try
        {
            var sessionDir = File.ReadAllText(marker).Trim();
            return string.IsNullOrWhiteSpace(sessionDir) ? null : sessionDir;
        }
        catch
        {
            return null;
        }
    }

    private static string CommitEditorPath(string sessionDir) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(sessionDir, "commit-editor.cmd")
            : Path.Combine(sessionDir, "commit-editor.sh");

    private static Dictionary<string, string?>? TryBuildContinueEnvironment(string repositoryPath)
    {
        var sessionDir = TryReadSessionDir(repositoryPath);
        if (sessionDir is null || !Directory.Exists(sessionDir))
            return null;

        var commitEditor = CommitEditorPath(sessionDir);
        if (!File.Exists(commitEditor))
            return null;

        return new Dictionary<string, string?>
        {
            ["GIT_EDITOR"] = QuoteEditorCommand(commitEditor),
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
    }

    private static void CleanupSession(string repositoryPath, string? knownSessionDir = null)
    {
        var sessionDir = knownSessionDir ?? TryReadSessionDir(repositoryPath);
        if (sessionDir is not null)
        {
            try
            {
                if (Directory.Exists(sessionDir))
                    Directory.Delete(sessionDir, recursive: true);
            }
            catch
            {
                // Best effort; leftover temp dirs are harmless.
            }
        }

        try
        {
            var marker = SessionMarkerPath(repositoryPath);
            if (File.Exists(marker))
                File.Delete(marker);
        }
        catch
        {
            // Best effort.
        }
    }
}
