using NetArchTest.Rules;
using NUnit.Framework;

namespace CodeReviewr.Core.Tests;

public sealed class ArchitectureTests
{
    [Test]
    public void Core_Git_Diff_Must_Not_Reference_Avalonia()
    {
        var result = Types.InAssemblies([
                typeof(FilePath).Assembly,
                typeof(CodeReviewr.Git.GitProcessRunner).Assembly,
                typeof(CodeReviewr.Diff.PatchParser).Assembly,
            ])
            .ShouldNot()
            .HaveDependencyOn("Avalonia")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            () => "Forbidden Avalonia references: " + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
