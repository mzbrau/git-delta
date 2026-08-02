using System.IO.Pipelines;
using System.Text;
using CliWrap;
using CodeReviewr.Core;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Git.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewr.Git;

/// <summary>
/// Hardened CliWrap-based `git` invocation.
///
/// Rules enforced on every call:
/// - Never routes through a shell (CliWrap invokes the executable directly).
/// - Streams stdout/stderr rather than performing a single blocking read; genuinely large
///   outputs should use <see cref="GitProcessOptions.StdoutTarget"/> or <see cref="StartLongLived"/>
///   instead of the buffered <see cref="GitCommandResult.Stdout"/> path.
/// - Sets <c>GIT_TERMINAL_PROMPT=0</c>, <c>LC_ALL=C</c>, <c>GIT_OPTIONAL_LOCKS=0</c>.
/// - Always passes <c>-c core.quotePath=false</c>.
/// - Propagates the caller's <see cref="CancellationToken"/> and supports a hard timeout.
/// - Classifies auth failures and index.lock contention from stderr.
/// </summary>
public sealed class GitProcessRunner : IGitProcessRunner
{
    private readonly ILogger<GitProcessRunner> _logger;
    private readonly IGitCommandLog? _commandLog;
    private readonly bool _assertNoUiSyncContext;
    private volatile string _executablePath = "git";

    public GitProcessRunner(
        ILogger<GitProcessRunner>? logger = null,
        IGitCommandLog? commandLog = null,
        bool assertNoUiSyncContext = false)
    {
        _logger = logger ?? NullLogger<GitProcessRunner>.Instance;
        _commandLog = commandLog;
        _assertNoUiSyncContext = assertNoUiSyncContext;
    }

    public string ExecutablePath => _executablePath;

    public void SetExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path must not be empty.", nameof(executablePath));

        _executablePath = executablePath;
    }

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitProcessOptions? options = null,
        CancellationToken ct = default)
    {
        // Prefer never starting git under a UI SynchronizationContext. When assert mode is on,
        // offload rather than throwing so existing ConfigureAwait(true) UI call sites keep working
        // while still freeing the UI thread for the duration of the process.
        if (_assertNoUiSyncContext && SynchronizationContext.Current is not null)
        {
            return await Task.Run(
                () => RunCoreAsync(workingDirectory, arguments, options, ct),
                ct).ConfigureAwait(false);
        }

        return await RunCoreAsync(workingDirectory, arguments, options, ct).ConfigureAwait(false);
    }

    private async Task<GitCommandResult> RunCoreAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitProcessOptions? options,
        CancellationToken ct)
    {
        options ??= GitProcessOptions.Default;

        var fullArguments = BuildFullArguments(arguments);
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        long bytesRead = 0;
        long stdoutBytes = 0;
        using var limitCts = options.MaxStdoutBytes is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        var stdoutTarget = options.StdoutTarget ?? BuildTextTarget(stdoutBuilder, options.OnStdoutLine);
        var stderrTarget = BuildTextTarget(stderrBuilder, options.OnStderrLine);

        stdoutTarget = new ByteCountingPipeTarget(stdoutTarget, n =>
        {
            Interlocked.Add(ref bytesRead, n);
            var total = Interlocked.Add(ref stdoutBytes, n);
            if (options.MaxStdoutBytes is { } max && total > max)
                limitCts?.Cancel();
        });
        stderrTarget = new ByteCountingPipeTarget(stderrTarget, n => Interlocked.Add(ref bytesRead, n));

        var command = Cli.Wrap(options.ExecutableOverride ?? _executablePath)
            .WithArguments(fullArguments)
            .WithWorkingDirectory(workingDirectory)
            .WithEnvironmentVariables(BuildEnvironment())
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(stdoutTarget)
            .WithStandardErrorPipe(stderrTarget);

        if (options.StdinText is not null)
            command = command.WithStandardInputPipe(PipeSource.FromString(options.StdinText, Encoding.UTF8));

        using var timeoutCts = options.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeoutCts?.Token ?? CancellationToken.None,
            limitCts?.Token ?? CancellationToken.None);
        var effectiveToken = linkedCts.Token;

        CodeReviewrMeters.GitInvocations.Add(1);
        _logger.LogDebug("git {Arguments}", string.Join(' ', fullArguments));

        CommandResult result;
        try
        {
            result = await command.ExecuteAsync(effectiveToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            options.MaxStdoutBytes is { } maxLimit
            && Interlocked.Read(ref stdoutBytes) > maxLimit
            && limitCts?.IsCancellationRequested == true
            && timeoutCts?.IsCancellationRequested != true
            && !ct.IsCancellationRequested)
        {
            throw new DiffTooLargeException(maxLimit, Interlocked.Read(ref stdoutBytes));
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            throw new GitException(
                $"git {string.Join(' ', arguments)} timed out after {options.Timeout}.",
                exitCode: -1);
        }
        finally
        {
            CodeReviewrMeters.GitBytesRead.Add(Interlocked.Read(ref bytesRead));
        }

        if (options.MaxStdoutBytes is { } postMax && Interlocked.Read(ref stdoutBytes) > postMax)
            throw new DiffTooLargeException(postMax, Interlocked.Read(ref stdoutBytes));

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();
        var commandResult = new GitCommandResult(
            result.ExitCode,
            stdout,
            stderr,
            GitErrorClassifier.IsAuthFailure(stderr),
            GitErrorClassifier.IsIndexLocked(stderr));

        _commandLog?.Append(new GitCommandLogEntry(
            DateTimeOffset.Now,
            workingDirectory,
            FormatCommandLine(fullArguments),
            result.ExitCode,
            stdout,
            stderr));

        if (!commandResult.Succeeded && !options.AllowNonZeroExitCode)
        {
            _logger.LogWarning("git {Arguments} exited {ExitCode}: {Stderr}", fullArguments, commandResult.ExitCode, stderr);
            throw commandResult.ToException(arguments);
        }

        return commandResult;
    }

    public ILongLivedGitProcess StartLongLived(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default)
    {
        // Long-lived starts must not capture the UI sync context either; callers should
        // already be off the UI thread, but Task.Run is unsafe here (returns a process handle).
        if (_assertNoUiSyncContext && SynchronizationContext.Current is not null)
        {
            _logger.LogWarning(
                "StartLongLived invoked with a UI SynchronizationContext; continuing on the current thread. Prefer calling from a background thread.");
        }

        var fullArguments = BuildFullArguments(arguments);
        var stdinPipe = new Pipe();
        var stdoutPipe = new Pipe();
        var stderrBuilder = new StringBuilder();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var command = Cli.Wrap(_executablePath)
            .WithArguments(fullArguments)
            .WithWorkingDirectory(workingDirectory)
            .WithEnvironmentVariables(BuildEnvironment())
            .WithValidation(CommandResultValidation.None)
            .WithStandardInputPipe(PipeSource.FromStream(stdinPipe.Reader.AsStream()))
            .WithStandardOutputPipe(PipeTarget.ToStream(stdoutPipe.Writer.AsStream()))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderrBuilder, Encoding.UTF8));

        CodeReviewrMeters.GitInvocations.Add(1);
        _logger.LogDebug("git {Arguments} (long-lived)", string.Join(' ', fullArguments));

        _commandLog?.Append(new GitCommandLogEntry(
            DateTimeOffset.Now,
            workingDirectory,
            FormatCommandLine(fullArguments) + " (long-lived)",
            ExitCode: null,
            Stdout: "",
            Stderr: "",
            IsLongLivedStart: true));

        var completion = RunLongLivedAsync(command, stdoutPipe.Writer, stderrBuilder, cts.Token);

        return new LongLivedGitProcess(stdinPipe.Writer.AsStream(), stdoutPipe.Reader.AsStream(), completion, cts);
    }

    private string FormatCommandLine(IReadOnlyList<string> fullArguments) =>
        $"{_executablePath} {string.Join(' ', fullArguments)}";

    private async Task<int> RunLongLivedAsync(Command command, PipeWriter stdoutWriter, StringBuilder stderrBuilder, CancellationToken ct)
    {
        try
        {
            var result = await command.ExecuteAsync(ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                _logger.LogWarning("long-lived git process exited {ExitCode}: {Stderr}", result.ExitCode, stderrBuilder.ToString());
            return result.ExitCode;
        }
        finally
        {
            await stdoutWriter.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static List<string> BuildFullArguments(IReadOnlyList<string> arguments)
    {
        var full = new List<string>(arguments.Count + 2) { "-c", "core.quotePath=false" };
        full.AddRange(arguments);
        return full;
    }

    private static Dictionary<string, string?> BuildEnvironment() => new()
    {
        // Fail fast instead of blocking on a terminal prompt a GUI process cannot answer.
        ["GIT_TERMINAL_PROMPT"] = "0",
        // stderr is parsed as a structured result; error text must be stable across locales.
        ["LC_ALL"] = "C",
        // Background reads must never take the index lock.
        ["GIT_OPTIONAL_LOCKS"] = "0",
        // A pager waiting on a TTY that doesn't exist looks exactly like a hang.
        ["GIT_PAGER"] = "cat",
        ["GIT_ASKPASS"] = "",
        // Commands like `merge --continue` fall back to an interactive commit message editor
        // unless one is already staged; a spawned editor with no TTY is another way to freeze.
        ["GIT_EDITOR"] = "true",
    };

    private static PipeTarget BuildTextTarget(StringBuilder builder, Action<string>? onLine) =>
        onLine is null
            ? PipeTarget.ToStringBuilder(builder, Encoding.UTF8)
            : PipeTarget.Merge(PipeTarget.ToStringBuilder(builder, Encoding.UTF8), PipeTarget.ToDelegate(onLine, Encoding.UTF8));
}
