namespace GitDelta.Persistence;

public interface IDurableUserStore : IOutboxStore, ILocalNotesStore, ILocalViewedStore, IDisposable
{
    int SchemaVersion { get; }
    void EnsureSchema();
}
