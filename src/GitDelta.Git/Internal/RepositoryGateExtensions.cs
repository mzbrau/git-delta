using GitDelta.Core.Abstractions;

namespace GitDelta.Git.Internal;

/// <summary>Ergonomic non-generic overloads over <see cref="IRepositoryGate"/>'s <c>Task&lt;T&gt;</c>-only surface.</summary>
internal static class RepositoryGateExtensions
{
    public static async Task RunReadAsync(this IRepositoryGate gate, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await gate.RunReadAsync(async token =>
        {
            await action(token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public static async Task RunIndexWriteAsync(this IRepositoryGate gate, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await gate.RunIndexWriteAsync(async token =>
        {
            await action(token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public static async Task RunWorktreeWriteAsync(this IRepositoryGate gate, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await gate.RunWorktreeWriteAsync(async token =>
        {
            await action(token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }
}
