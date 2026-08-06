using NetArchTest.Rules;
using NUnit.Framework;

namespace GitDelta.Core.Tests;

public sealed class ArchitectureTests
{
    [Test]
    public void Core_Git_Diff_GitHub_Review_Persistence_Must_Not_Reference_Avalonia()
    {
        var result = Types.InAssemblies([
                typeof(FilePath).Assembly,
                typeof(GitDelta.Git.GitProcessRunner).Assembly,
                typeof(GitDelta.Diff.PatchParser).Assembly,
                typeof(GitDelta.GitHub.GitHubClient).Assembly,
                typeof(GitDelta.Review.RepositoryLocator).Assembly,
                typeof(GitDelta.Persistence.PlatformTokenStore).Assembly,
            ])
            .ShouldNot()
            .HaveDependencyOn("Avalonia")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            () => "Forbidden Avalonia references: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Test]
    public void Git_Must_Not_Reference_Diff()
    {
        var result = Types.InAssembly(typeof(GitDelta.Git.GitProcessRunner).Assembly)
            .ShouldNot()
            .HaveDependencyOn("GitDelta.Diff")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            () => "Git must not reference Diff: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Test]
    public void Diff_Must_Not_Reference_Git()
    {
        var result = Types.InAssembly(typeof(GitDelta.Diff.PatchParser).Assembly)
            .ShouldNot()
            .HaveDependencyOn("GitDelta.Git")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            () => "Diff must not reference Git: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Test]
    public void Core_Must_Not_Reference_Implementation_Assemblies()
    {
        var result = Types.InAssembly(typeof(FilePath).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "GitDelta.Git",
                "GitDelta.Diff",
                "GitDelta.GitHub",
                "GitDelta.Review",
                "GitDelta.Persistence",
                "GitDelta.AI",
                "GitDelta.App",
                "CliWrap",
                "Avalonia",
                "GitHub.Copilot.SDK")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            () => "Core leaked implementation deps: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Test]
    public void AI_Must_Not_Reference_Avalonia()
    {
        var result = Types.InAssembly(typeof(GitDelta.AI.AiPromptCatalog).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Avalonia")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            () => "Forbidden Avalonia references in AI: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Test]
    public void Copilot_SDK_May_Only_Be_Referenced_From_AI()
    {
        var forbidden = new[]
        {
            typeof(FilePath).Assembly,
            typeof(GitDelta.Git.GitProcessRunner).Assembly,
            typeof(GitDelta.Diff.PatchParser).Assembly,
            typeof(GitDelta.GitHub.GitHubClient).Assembly,
            typeof(GitDelta.Review.RepositoryLocator).Assembly,
            typeof(GitDelta.Persistence.PlatformTokenStore).Assembly,
        };

        foreach (var assembly in forbidden)
        {
            var refs = assembly.GetReferencedAssemblies().Select(a => a.Name);
            Assert.That(refs, Does.Not.Contain("GitHub.Copilot.SDK"),
                $"{assembly.GetName().Name} must not reference GitHub.Copilot.SDK");
        }

        var aiRefs = typeof(GitDelta.AI.AiPromptCatalog).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name);
        Assert.That(aiRefs, Does.Contain("GitHub.Copilot.SDK"));
    }
}
