using GitDelta.Core;
using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class GitFileHistoryTests
{
    [Test]
    public async Task ListFileHistoryAsync_FollowsPath_AndReturnsNewestFirst()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("src/a.txt", "one\n")
            .WithInitialCommit("create a")
            .WithFile("src/a.txt", "one\ntwo\n")
            .WithCommit("update a");
        var path = repo.Build();

        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance, commandLog: null, assertNoUiSyncContext: false);
        var gates = new RepositoryGateProvider(runner);
        var history = new GitHistoryService(runner, gates);

        var commits = await history.ListFileHistoryAsync(path, "src/a.txt", take: 10);
        Assert.That(commits, Has.Count.EqualTo(2));
        Assert.That(commits[0].Subject, Is.EqualTo("update a"));
        Assert.That(commits[1].Subject, Is.EqualTo("create a"));

        var created = await history.GetFileCreatedCommitAsync(path, "src/a.txt");
        Assert.That(created, Is.Not.Null);
        Assert.That(created!.Subject, Is.EqualTo("create a"));
    }

    [Test]
    public async Task ListTrackedFilesAsync_ReturnsSortedIndexPaths()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("z.txt", "z\n")
            .WithFile("a/b.txt", "ab\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance, commandLog: null, assertNoUiSyncContext: false);
        var gates = new RepositoryGateProvider(runner);
        var history = new GitHistoryService(runner, gates);

        var files = await history.ListTrackedFilesAsync(path);
        Assert.That(files.Select(f => f.Value), Is.EqualTo(new[] { "a/b.txt", "z.txt" }));
    }

    [Test]
    public void ParsePathAtCommit_Reads_Add_And_Rename_Entries()
    {
        const string createOid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string renameOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var stdout =
            $"{renameOid}\n" +
            "R100\told/Foo.cs\tnew/Foo.cs\n" +
            "\n" +
            $"{createOid}\n" +
            "A\told/Foo.cs\n";

        var rename = GitHistoryService.ParsePathAtCommit(stdout, renameOid);
        Assert.That(rename, Is.Not.Null);
        Assert.That(rename!.Value.Path, Is.EqualTo("new/Foo.cs"));
        Assert.That(rename.Value.OldPath, Is.EqualTo("old/Foo.cs"));

        var create = GitHistoryService.ParsePathAtCommit(stdout, createOid);
        Assert.That(create, Is.Not.Null);
        Assert.That(create!.Value.Path, Is.EqualTo("old/Foo.cs"));
        Assert.That(create.Value.OldPath, Is.Null);
    }

    [Test]
    public async Task GetCommitPatchAsync_Follows_Rename_For_Create_And_Rename_Commits()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile(
                "old/Foo.cs",
                "namespace Old;\nclass Foo\n{\n    public void A() { }\n    public void B() { }\n}\n")
            .WithInitialCommit("created");
        var path = repo.Build();

        Directory.CreateDirectory(Path.Combine(path, "new"));
        repo.RunGit("mv", "old/Foo.cs", "new/Foo.cs");
        // Keep most lines identical so git detects a rename (Rxxx), not delete+add.
        File.WriteAllText(
            Path.Combine(path, "new", "Foo.cs"),
            "namespace New;\nclass Foo\n{\n    public void A() { }\n    public void B() { }\n}\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "rename");

        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance, commandLog: null, assertNoUiSyncContext: false);
        var gates = new RepositoryGateProvider(runner);
        var history = new GitHistoryService(runner, gates);

        var commits = await history.ListFileHistoryAsync(path, "new/Foo.cs", take: 10);
        Assert.That(commits, Has.Count.EqualTo(2));
        Assert.That(commits[0].Subject, Is.EqualTo("rename"));
        Assert.That(commits[1].Subject, Is.EqualTo("created"));

        var createPatch = await history.GetCommitPatchAsync(
            path, commits[1].Oid, FilePath.From("new/Foo.cs"), DiffOptions.Default);
        Assert.That(createPatch, Is.Not.Empty);
        Assert.That(createPatch, Does.Contain("namespace Old"));
        // Full-file add at create — should not be empty / missing hunk.
        Assert.That(createPatch, Does.Contain("@@"));

        var renamePatch = await history.GetCommitPatchAsync(
            path, commits[0].Oid, FilePath.From("new/Foo.cs"), DiffOptions.Default);
        Assert.That(renamePatch, Is.Not.Empty);
        Assert.That(renamePatch, Does.Contain("namespace Old").Or.Contain("-namespace Old"));
        Assert.That(renamePatch, Does.Contain("namespace New").Or.Contain("+namespace New"));
        // Must not be a pure "created from nothing" add of the post-rename file.
        Assert.That(renamePatch, Does.Not.Contain("@@ -0,0 +1,"));
    }
}
