using GitDelta.Core;
using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class GitTagServiceTests
{
    private static GitTagService CreateService()
    {
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance, commandLog: null, assertNoUiSyncContext: false);
        var gates = new RepositoryGateProvider(runner);
        return new GitTagService(runner, gates);
    }

    [Test]
    public async Task ListTags_Empty_Repository_Returns_Empty()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var service = CreateService();
        var tags = await service.ListTagsAsync(path);

        Assert.That(tags, Is.Empty);
    }

    [Test]
    public async Task CreateAnnotatedTag_Then_List_Shows_Name_Date_And_Message()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var service = CreateService();
        await service.CreateAnnotatedTagAsync(path, "v1.0.0", "Release 1.0.0");

        var tags = await service.ListTagsAsync(path);
        Assert.That(tags, Has.Count.EqualTo(1));
        Assert.That(tags[0].Name, Is.EqualTo("v1.0.0"));
        Assert.That(tags[0].Message, Is.EqualTo("Release 1.0.0"));
        Assert.That(tags[0].TargetOid, Is.Not.Null.And.Not.Empty);
        Assert.That(tags[0].Date, Is.Not.EqualTo(DateTimeOffset.MinValue));
    }

    [Test]
    public async Task ListTags_Sorts_Newest_First()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var service = CreateService();
        await service.CreateAnnotatedTagAsync(path, "v1.0.0", "First");
        await Task.Delay(1100);
        await service.CreateAnnotatedTagAsync(path, "v1.1.0", "Second");

        var tags = await service.ListTagsAsync(path);
        Assert.That(tags, Has.Count.EqualTo(2));
        Assert.That(tags[0].Name, Is.EqualTo("v1.1.0"));
        Assert.That(tags[1].Name, Is.EqualTo("v1.0.0"));
    }

    [Test]
    public async Task CreateAnnotatedTag_Duplicate_Throws()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var service = CreateService();
        await service.CreateAnnotatedTagAsync(path, "v1.0.0", "Release");

        Assert.ThrowsAsync<GitException>(async () =>
            await service.CreateAnnotatedTagAsync(path, "v1.0.0", "Again"));
    }

    [Test]
    public void ParseTags_Reads_Fields()
    {
        const char sep = '\u0001';
        var line =
            $"v2.0.0{sep}2024-06-15T12:00:00+00:00{sep}abc123{sep}Release notes\n";
        var parsed = GitTagService.ParseTags(line);
        Assert.That(parsed, Has.Count.EqualTo(1));
        Assert.That(parsed[0].Name, Is.EqualTo("v2.0.0"));
        Assert.That(parsed[0].TargetOid, Is.EqualTo("abc123"));
        Assert.That(parsed[0].Message, Is.EqualTo("Release notes"));
        Assert.That(parsed[0].Date, Is.EqualTo(DateTimeOffset.Parse("2024-06-15T12:00:00+00:00")));
    }

    [Test]
    public void ParseTags_Missing_Message_And_Date()
    {
        const char sep = '\u0001';
        var line = $"lightweight{sep}{sep}deadbeef\n";
        var parsed = GitTagService.ParseTags(line);
        Assert.That(parsed, Has.Count.EqualTo(1));
        Assert.That(parsed[0].Name, Is.EqualTo("lightweight"));
        Assert.That(parsed[0].Message, Is.Null);
        Assert.That(parsed[0].Date, Is.EqualTo(DateTimeOffset.MinValue));
    }
}
