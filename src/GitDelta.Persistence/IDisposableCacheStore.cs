namespace GitDelta.Persistence;

public interface IDisposableCacheStore : IDisposable
{
    int SchemaVersion { get; }
    void EnsureSchema();
    void Wipe();
}
