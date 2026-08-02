using CodeReviewr.Review;
using NUnit.Framework;

namespace CodeReviewr.Review.Tests;

public sealed class GitHeadReaderTests
{
    [Test]
    public void ParseHeadContent_ReadsBranchRef()
    {
        Assert.That(GitHeadReader.ParseHeadContent("ref: refs/heads/main\n"), Is.EqualTo("main"));
        Assert.That(GitHeadReader.ParseHeadContent("ref: refs/heads/feature/foo"), Is.EqualTo("feature/foo"));
    }

    [Test]
    public void ParseHeadContent_ReadsDetachedSha()
    {
        Assert.That(
            GitHeadReader.ParseHeadContent("abcdef0123456789abcdef0123456789abcdef01"),
            Is.EqualTo("abcdef0"));
    }

    [Test]
    public void ParseHeadContent_ReadsOtherRefs()
    {
        Assert.That(GitHeadReader.ParseHeadContent("ref: refs/remotes/origin/main"), Is.EqualTo("main"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ParseHeadContent_RejectsEmpty(string? content)
    {
        Assert.That(GitHeadReader.ParseHeadContent(content), Is.Null);
    }

    [Test]
    public void TryReadCurrentBranch_ReadsRepositoryHead()
    {
        var root = Path.Combine(Path.GetTempPath(), "codereviewr-head-" + Guid.NewGuid().ToString("N"));
        var gitDir = Path.Combine(root, ".git");
        Directory.CreateDirectory(gitDir);
        try
        {
            File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/develop\n");
            Assert.That(GitHeadReader.TryReadCurrentBranch(root), Is.EqualTo("develop"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void TryReadCurrentBranch_ResolvesGitDirFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "codereviewr-worktree-" + Guid.NewGuid().ToString("N"));
        var realGit = Path.Combine(Path.GetTempPath(), "codereviewr-gitdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(realGit);
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {realGit}\n");
            File.WriteAllText(Path.Combine(realGit, "HEAD"), "ref: refs/heads/wt-branch\n");
            Assert.That(GitHeadReader.TryReadCurrentBranch(root), Is.EqualTo("wt-branch"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(realGit, recursive: true);
        }
    }
}
