using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Caching;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using CodeReviewr.Review;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CodeReviewr.Review.Tests;

public sealed class GitReviewTreeTests
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDiffCache, MemoryDiffCache>();
        services.AddCodeReviewrGit();
        services.AddCodeReviewrDiff();
        services.AddCodeReviewrReview();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Read_List_And_Search_At_Pinned_Commit()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("src/App.cs", "class App {}\n")
            .WithInitialCommit("root")
            .WithFile("src/App.cs", "class App { void Run() {} }\n")
            .WithFile("src2/Other.cs", "class Other {}\n")
            .WithFile("README.md", "hello -n flag\n")
            .WithCommit("feature");
        var path = repo.Build();
        var head = repo.RunGit("rev-parse", "HEAD").Trim();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var factory = sp.GetRequiredService<IReviewTreeFactory>();
        var tree = factory.Create(path, CommitId.FromSha(head));

        Assert.That(tree.MaterialisedPath, Is.Null);

        var files = await tree.ListAsync(FilePath.From("src"), CancellationToken.None);
        Assert.That(files.Any(f => f.Value == "src/App.cs"), Is.True);
        Assert.That(files.Any(f => f.Value == "src2/Other.cs"), Is.False);
        Assert.That(files.Any(f => f.Value == "README.md"), Is.False);

        var content = await tree.ReadAsync(FilePath.From("src/App.cs"), CancellationToken.None);
        Assert.That(System.Text.Encoding.UTF8.GetString(content.Span), Does.Contain("Run"));

        var hits = await tree.SearchAsync("class App", CancellationToken.None);
        Assert.That(hits.Any(h => h.Path.Value == "src/App.cs"), Is.True);

        var readmeHits = await tree.SearchAsync("hello", CancellationToken.None);
        Assert.That(readmeHits.Any(h => h.Path.Value == "README.md"), Is.True);

        // Patterns starting with '-' must be passed via -e, not as git options.
        var dashHits = await tree.SearchAsync("-n", CancellationToken.None);
        Assert.That(dashHits.Any(h => h.Path.Value == "README.md"), Is.True);
    }
}
