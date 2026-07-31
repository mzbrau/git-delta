namespace CodeReviewr.Git.Internal;

internal sealed class LongLivedGitProcess(Stream standardInput, Stream standardOutput, Task<int> completion, CancellationTokenSource cts)
    : ILongLivedGitProcess
{
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(5);
    private int _disposed;

    public Stream StandardInput { get; } = standardInput;
    public Stream StandardOutput { get; } = standardOutput;
    public Task<int> Completion { get; } = completion;
    public bool HasExited => Completion.IsCompleted;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await StandardInput.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the process may already have exited.
        }

        try
        {
            await Completion.WaitAsync(GracefulShutdownTimeout).ConfigureAwait(false);
        }
        catch
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            cts.Dispose();
        }
    }
}
