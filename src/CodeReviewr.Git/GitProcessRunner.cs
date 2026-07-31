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
    private readonly bool _assertNoUiSyncContext;
    private volatile string _executablePath = "git";

    public GitProcessRunner(ILogger<GitProcessRunner>? logger = null, bool assertNoUiSyncContext = false)
    {
        _logger = logger ?? NullLogger<GitProcessRunner>.Instance;
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
        AssertNotOnUiSyncContext();
        options ??= GitProcessOptions.Default;

        var fullArguments = BuildFullArguments(arguments);
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        long bytesRead = 0;

        var stdoutTarget = options.StdoutTarget ?? BuildTextTarget(stdoutBuilder, options.OnStdoutLine);
        var stderrTarget = BuildTextTarget(stderrBuilder, options.OnStderrLine);

        stdoutTarget = new ByteCountingPipeTarget(stdoutTarget, n => Interlocked.Add(ref bytesRead, n));
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
        using var linkedCts = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var effectiveToken = linkedCts?.Token ?? ct;

        CodeReviewrMeters.GitInvocations.Add(1);
        _logger.LogDebug("git {Arguments}", string.Join(' ', fullArguments));

        CommandResult result;
        try
        {
            result = await command.ExecuteAsync(effectiveToken).ConfigureAwait(false);
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

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();
        var commandResult = new GitCommandResult(
            result.ExitCode,
            stdout,
            stderr,
            GitErrorClassifier.IsAuthFailure(stderr),
            GitErrorClassifier.IsIndexLocked(stderr));

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
        AssertNotOnUiSyncContext();

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

        var completion = RunLongLivedAsync(command, stdoutPipe.Writer, stderrBuilder, cts.Token);

        return new LongLivedGitProcess(stdinPipe.Writer.AsStream(), stdoutPipe.Reader.AsStream(), completion, cts);
    }

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

    private void AssertNotOnUiSyncContext()
    {
        if (_assertNoUiSyncContext && SynchronizationContext.Current is not null)
        {
            throw new InvalidOperationException(
                "A git process was invoked while a UI SynchronizationContext was current. " +
                "Dispatch to a background thread (e.g. Task.Run) before invoking git.");
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
