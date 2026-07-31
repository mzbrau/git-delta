using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewr.Benchmarks;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
public class DiffBenchmarks
{
    private ServiceProvider _sp = null!;
    private string _repo = null!;
    private FilePath _file;
    private RepositoryBuilder? _builder;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewrGit();
        services.AddCodeReviewrDiff();
        _sp = services.BuildServiceProvider();
        _sp.GetRequiredService<IGitEnvironment>().DetectAsync().GetAwaiter().GetResult();

        _builder = RepositoryBuilder.Create()
            .WithFile("a.txt", "hello\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "hello world\n");
        _repo = _builder.Build();
        _file = FilePath.From("a.txt");
    }

    [GlobalCleanup]
    public void Cleanup() => _builder?.Dispose();

    [Benchmark]
    public async Task StatusRefresh() =>
        await _sp.GetRequiredService<IGitStatusService>().GetStatusAsync(_repo);

    [Benchmark]
    public async Task DiffGeneration() =>
        await _sp.GetRequiredService<IGitDiffService>()
            .GetDiffAsync(_repo, _file, DiffTarget.IndexToWorktree, DiffOptions.Default);
}
