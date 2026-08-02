using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.Git.Internal;
using NUnit.Framework;

namespace CodeReviewr.Git.Tests;

public sealed class GitDiffArgumentBuilderTests
{
    [Test]
    public void BuildPatchArgs_Revisions_UsesThreeDotSyntax()
    {
        var baseId = CommitId.FromSha("aaa".PadRight(40, 'a'));
        var headId = CommitId.FromSha("bbb".PadRight(40, 'b'));
        var scope = new DiffScope.Revisions(baseId, headId);

        var args = GitDiffArgumentBuilder.BuildPatchArgs(scope, DiffOptions.Default, FilePath.From("src/a.cs"));

        Assert.That(args[0], Is.EqualTo("diff"));
        Assert.That(args[1], Is.EqualTo($"{baseId.Value}...{headId.Value}"));
        Assert.That(args, Does.Contain("--no-color"));
        Assert.That(args, Does.Contain("--no-ext-diff"));
        Assert.That(args, Does.Contain("--"));
        Assert.That(args[^1], Is.EqualTo("src/a.cs"));
    }

    [Test]
    public void BuildRawArgs_WorkingCopy_HeadToIndex_IncludesCachedFlag()
    {
        var args = GitDiffArgumentBuilder.BuildRawArgs(
            DiffTarget.HeadToIndex.AsWorkingCopy(),
            DiffOptions.Default,
            path: null);

        Assert.That(args[0], Is.EqualTo("diff"));
        Assert.That(args[1], Is.EqualTo("--cached"));
        Assert.That(args, Does.Contain("--raw"));
        Assert.That(args, Does.Contain("-z"));
    }
}
