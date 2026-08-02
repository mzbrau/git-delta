using NetArchTest.Rules;
using NUnit.Framework;

namespace CodeReviewr.Core.Tests;

public sealed class ArchitectureTests
{
    [Test]
    public void Core_Git_Diff_GitHub_Review_Persistence_Must_Not_Reference_Avalonia()
    {
        var result = Types.InAssemblies([
                typeof(FilePath).Assembly,
                typeof(CodeReviewr.Git.GitProcessRunner).Assembly,
                typeof(CodeReviewr.Diff.PatchParser).Assembly,
                typeof(CodeReviewr.GitHub.GitHubClient).Assembly,
                typeof(CodeReviewr.Review.RepositoryLocator).Assembly,
                typeof(CodeReviewr.Persistence.PlatformTokenStore).Assembly,
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
        var result = Types.InAssembly(typeof(CodeReviewr.Git.GitProcessRunner).Assembly)
            .ShouldNot()
            .HaveDependencyOn("CodeReviewr.Diff")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            () => "Git must not reference Diff: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Test]
    public void Diff_Must_Not_Reference_Git()
    {
        var result = Types.InAssembly(typeof(CodeReviewr.Diff.PatchParser).Assembly)
            .ShouldNot()
            .HaveDependencyOn("CodeReviewr.Git")
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
                "CodeReviewr.Git",
                "CodeReviewr.Diff",
                "CodeReviewr.GitHub",
                "CodeReviewr.Review",
                "CodeReviewr.Persistence",
                "CodeReviewr.App",
                "CliWrap",
                "Avalonia")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            () => "Core leaked implementation deps: " + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
