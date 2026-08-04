using CodeReviewr.Core.AI;
using NUnit.Framework;

namespace CodeReviewr.AI.Tests;

public sealed class PrFactsAssemblerTests
{
    [Test]
    public void ComputeMeasuredFacts_SumsLinesAcrossFiles()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest(
        [
            new AiChangedFileFact("src/a.cs", "Modified", "b1", "a1", LinesAdded: 5, LinesRemoved: 2),
            new AiChangedFileFact("src/b.cs", "Added", null, "a2", LinesAdded: 10, LinesRemoved: 0),
            new AiChangedFileFact("src/c.cs", "Deleted", "b3", null, LinesAdded: 0, LinesRemoved: 7),
        ]);

        var measured = assembler.ComputeMeasuredFacts(request);

        Assert.That(measured.FilesChanged, Is.EqualTo(3));
        Assert.That(measured.LinesAdded, Is.EqualTo(15));
        Assert.That(measured.LinesRemoved, Is.EqualTo(9));
    }

    [Test]
    public void ComputeMeasuredFacts_NullLineCounts_TreatedAsZero()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest(
        [
            new AiChangedFileFact("src/a.cs", "Modified", "b1", "a1"),
        ]);

        var measured = assembler.ComputeMeasuredFacts(request);

        Assert.That(measured.FilesChanged, Is.EqualTo(1));
        Assert.That(measured.LinesAdded, Is.EqualTo(0));
        Assert.That(measured.LinesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void ComputeMeasuredFacts_NoFiles_ReturnsZeroes()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest([]);

        var measured = assembler.ComputeMeasuredFacts(request);

        Assert.That(measured, Is.EqualTo(new AiMeasuredFacts(0, 0, 0)));
    }

    [Test]
    public void BuildFactsBlock_IncludesTitleAndShas()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest(
        [
            new AiChangedFileFact("src/a.cs", "Modified", "before-oid", "after-oid", LinesAdded: 3, LinesRemoved: 1),
        ]) with
        {
            Title = "Fix login bug",
            HeadSha = "deadbeef",
            MergeBaseSha = "cafef00d",
        };

        var block = assembler.BuildFactsBlock(request);

        Assert.That(block, Does.Contain("Fix login bug"));
        Assert.That(block, Does.Contain("deadbeef"));
        Assert.That(block, Does.Contain("cafef00d"));
    }

    [Test]
    public void BuildFactsBlock_IncludesChangedFilePathsAndKinds()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest(
        [
            new AiChangedFileFact("src/a.cs", "Modified", "b1", "a1", LinesAdded: 4, LinesRemoved: 1),
            new AiChangedFileFact("src/new.cs", "Added", null, "a2", LinesAdded: 20, LinesRemoved: 0),
        ]);

        var block = assembler.BuildFactsBlock(request);

        Assert.That(block, Does.Contain("src/a.cs"));
        Assert.That(block, Does.Contain("[Modified]"));
        Assert.That(block, Does.Contain("src/new.cs"));
        Assert.That(block, Does.Contain("[Added]"));
        Assert.That(block, Does.Contain("+4 / -1"));
        Assert.That(block, Does.Contain("+20 / -0"));
    }

    [Test]
    public void BuildFactsBlock_IncludesMeasuredFileCountAndTotals()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest(
        [
            new AiChangedFileFact("src/a.cs", "Modified", "b1", "a1", LinesAdded: 4, LinesRemoved: 1),
            new AiChangedFileFact("src/b.cs", "Modified", "b2", "a2", LinesAdded: 6, LinesRemoved: 3),
        ]);

        var block = assembler.BuildFactsBlock(request);

        Assert.That(block, Does.Contain("Files changed: 2 (+10 / -4)"));
    }

    [Test]
    public void BuildFactsBlock_WithoutBody_OmitsDescriptionSection()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest([]) with { Body = null };

        var block = assembler.BuildFactsBlock(request);

        Assert.That(block, Does.Not.Contain("Description:"));
    }

    [Test]
    public void BuildFactsBlock_WithBody_IncludesDescriptionSection()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest([]) with { Body = "This fixes the login retry loop." };

        var block = assembler.BuildFactsBlock(request);

        Assert.That(block, Does.Contain("Description:"));
        Assert.That(block, Does.Contain("This fixes the login retry loop."));
    }

    [Test]
    public void BuildFactsBlock_WithThreadSummary_IncludesDiscussionSection()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest([]);

        var block = assembler.BuildFactsBlock(request, threadSummary: "Reviewer asked about test coverage.");

        Assert.That(block, Does.Contain("Discussion so far:"));
        Assert.That(block, Does.Contain("Reviewer asked about test coverage."));
    }

    [Test]
    public void BuildFactsBlock_MissingOptionalFields_UsesPlaceholders()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest([]) with { Title = null, Author = null, HeadBranch = null, BaseBranch = null };

        var block = assembler.BuildFactsBlock(request);

        Assert.That(block, Does.Contain("(no title)"));
        Assert.That(block, Does.Contain("(unknown)"));
    }

    [Test]
    public void BuildFactsBlock_WorkingCopyScope_UsesPendingChangesLabels()
    {
        var assembler = new PrFactsAssembler();
        var request = CreateRequest([]) with
        {
            Scope = AiReviewScope.WorkingCopyAll,
            Title = null,
            Author = null,
            HeadBranch = null,
            BaseBranch = null,
            HeadSha = "snap-tree",
            MergeBaseSha = "head-ref",
        };

        var block = assembler.BuildFactsBlock(request);

        Assert.That(block, Does.Contain("Pending changes"));
        Assert.That(block, Does.Contain("Snapshot tree: snap-tree"));
        Assert.That(block, Does.Contain("Base (HEAD): head-ref"));
        Assert.That(block, Does.Not.Contain("Author:"));
        Assert.That(block, Does.Not.Contain("Branch:"));
        Assert.That(block, Does.Not.Contain("Head SHA:"));
        Assert.That(block, Does.Not.Contain("Merge-base SHA:"));
    }

    private static AiReviewRequest CreateRequest(IReadOnlyList<AiChangedFileFact> files) => new(
        SessionKey: "PR_1",
        RepositoryPath: "/tmp/repo",
        RepositoryKey: "owner/repo",
        HeadSha: "head-sha",
        MergeBaseSha: "base-sha",
        Title: "Some PR",
        Body: null,
        Author: "octocat",
        BaseBranch: "main",
        HeadBranch: "feature",
        ChangedFiles: files);
}
