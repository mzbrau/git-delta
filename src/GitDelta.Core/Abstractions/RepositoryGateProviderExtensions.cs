namespace GitDelta.Core.Abstractions;

/// <summary>
/// Awaits <see cref="IRepositoryGateProvider.ForAsync"/> then runs an action on the resolved gate,
/// so callers never sync-block on common-dir resolution.
/// </summary>
public static class RepositoryGateProviderExtensions
{
    public static async Task<T> WithGateAsync<T>(
        this IRepositoryGateProvider gates,
        string repositoryPath,
        Func<IRepositoryGate, Task<T>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(action);
        var gate = await gates.ForAsync(repositoryPath, ct).ConfigureAwait(false);
        return await action(gate).ConfigureAwait(false);
    }

    public static async Task WithGateAsync(
        this IRepositoryGateProvider gates,
        string repositoryPath,
        Func<IRepositoryGate, Task> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(action);
        var gate = await gates.ForAsync(repositoryPath, ct).ConfigureAwait(false);
        await action(gate).ConfigureAwait(false);
    }
}
