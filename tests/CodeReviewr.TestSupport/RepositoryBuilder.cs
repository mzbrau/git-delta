using System.Diagnostics;
using System.Text;

namespace CodeReviewr.TestSupport;

/// <summary>Builds temporary Git repositories for tests.</summary>
public sealed class RepositoryBuilder : IAsyncDisposable, IDisposable
{
    private readonly string _root;
    private readonly List<Action> _actions = [];
    private bool _built;
    private bool _disposed;

    public string Path => _root;

    private RepositoryBuilder(string? root = null)
    {
        _root = root ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public static RepositoryBuilder Create(string? root = null) => new(root);

    public RepositoryBuilder WithFile(string relativePath, string content)
    {
        _actions.Add(() =>
        {
            var full = System.IO.Path.Combine(_root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        });
        return this;
    }

    public RepositoryBuilder WithInitialCommit(string message = "initial")
    {
        _actions.Add(() =>
        {
            RunGit("add", "-A");
            RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test",
                "commit", "--allow-empty", "-m", message);
        });
        return this;
    }

    public RepositoryBuilder WithCommit(string message)
    {
        _actions.Add(() =>
        {
            RunGit("add", "-A");
            RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test",
                "commit", "-m", message);
        });
        return this;
    }

    public string Build()
    {
        if (_built) return _root;
        RunGit("init", "-b", "main");
        foreach (var action in _actions)
            action();
        _built = true;
        return _root;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public string RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }
}

public static class LargeRepositoryBuilder
{
    public static RepositoryBuilder PathologicalSingleFile(int lines = 10_000)
    {
        var sb = new StringBuilder(lines * 40);
        for (var i = 0; i < lines; i++)
            sb.AppendLine($"line {i} content for pathological corpus testing");
        return RepositoryBuilder.Create()
            .WithFile("huge.txt", sb.ToString())
            .WithInitialCommit("huge file");
    }

    public static RepositoryBuilder MediumSample()
    {
        var b = RepositoryBuilder.Create();
        for (var i = 0; i < 200; i++)
            b.WithFile($"src/file_{i:D5}.txt", $"content {i}\n");
        b.WithInitialCommit("seed");
        for (var c = 0; c < 5; c++)
        {
            b.WithFile($"src/file_{c:D5}.txt", $"modified {c}\n");
            b.WithCommit($"commit {c}");
        }
        return b;
    }
}
