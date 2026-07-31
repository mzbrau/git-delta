using System.Text.Json;
using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.Core.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private AppSettings _current = new();

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeReviewr",
            "settings.json");
    }

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return;

        await using var stream = File.OpenRead(_path);
        var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken: ct);
        if (loaded is not null)
        {
            lock (_lock)
                _current = loaded;
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        AppSettings snapshot;
        lock (_lock)
            snapshot = Clone(_current);

        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, new JsonSerializerOptions { WriteIndented = true }, ct);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_lock)
            mutate(_current);
    }

    public void AddRecentRepository(string path)
    {
        Update(s =>
        {
            s.RecentRepositories.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            s.RecentRepositories.Insert(0, path);
            if (s.RecentRepositories.Count > 20)
                s.RecentRepositories.RemoveRange(20, s.RecentRepositories.Count - 20);
        });
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        GitExecutablePath = s.GitExecutablePath,
        Theme = s.Theme,
        FontSize = s.FontSize,
        DefaultDiffMode = s.DefaultDiffMode,
        IgnoreWhitespace = s.IgnoreWhitespace,
        ShowWhitespace = s.ShowWhitespace,
        DiffAlgorithm = s.DiffAlgorithm,
        ContextLines = s.ContextLines,
        SyntaxHighlightingSizeCapBytes = s.SyntaxHighlightingSizeCapBytes,
        SyntaxHighlightingLineLengthCap = s.SyntaxHighlightingLineLengthCap,
        RecentRepositories = [.. s.RecentRepositories],
        WindowWidth = s.WindowWidth,
        WindowHeight = s.WindowHeight,
        NavigatorWidth = s.NavigatorWidth,
        FileListWidth = s.FileListWidth,
    };
}
