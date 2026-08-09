using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class RecentViewedFilesStoreTests
{
    [Test]
    public void Remember_CapsAtFive_AndMovesExistingToFront()
    {
        var store = new RecentViewedFilesStore();
        var exclude = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 1; i <= 6; i++)
            store.Remember(FilePath.From($"f{i}.cs"), exclude);

        Assert.That(store.Items, Has.Count.EqualTo(5));
        Assert.That(store.Items[0].Path.Value, Is.EqualTo("f6.cs"));
        Assert.That(store.Items.Select(i => i.Path.Value), Does.Not.Contain("f1.cs"));

        store.Remember(FilePath.From("f3.cs"), exclude);
        Assert.That(store.Items[0].Path.Value, Is.EqualTo("f3.cs"));
        Assert.That(store.Items, Has.Count.EqualTo(5));
    }

    [Test]
    public void Remember_SkipsPathsInExcludeSet()
    {
        var store = new RecentViewedFilesStore();
        store.Remember(FilePath.From("keep.cs"), new HashSet<string>(StringComparer.Ordinal));
        store.Remember(FilePath.From("staged.cs"), new HashSet<string>(StringComparer.Ordinal) { "staged.cs" });

        Assert.That(store.Items, Has.Count.EqualTo(1));
        Assert.That(store.Items[0].Path.Value, Is.EqualTo("keep.cs"));
    }

    [Test]
    public void ExcludePaths_RemovesMatchingEntries()
    {
        var store = new RecentViewedFilesStore();
        var empty = new HashSet<string>(StringComparer.Ordinal);
        store.Remember(FilePath.From("a.cs"), empty);
        store.Remember(FilePath.From("b.cs"), empty);
        store.ExcludePaths(new HashSet<string>(StringComparer.Ordinal) { "a.cs" });

        Assert.That(store.Items, Has.Count.EqualTo(1));
        Assert.That(store.Items[0].Path.Value, Is.EqualTo("b.cs"));
    }
}

public sealed class FileHistoryCacheTests
{
    [Test]
    public void BuildTimeline_IncludesCreatedRecentAndCurrent()
    {
        var created = new CommitInfo("c1", "c1short", "add", "", "A", "a@b.c", DateTimeOffset.UtcNow.AddDays(-2), [], []);
        var recent = new[]
        {
            new CommitInfo("c2", "c2short", "update", "", "B", "b@b.c", DateTimeOffset.UtcNow.AddDays(-1), [], []),
        };

        var timeline = FileHistoryCacheEntry.BuildTimeline(created, recent);
        Assert.That(timeline, Has.Count.EqualTo(3));
        Assert.That(timeline[0].IsCurrent, Is.True);
        Assert.That(timeline[1].Oid, Is.EqualTo("c2"));
        Assert.That(timeline[^1].IsCreated, Is.True);
    }

    [Test]
    public void ApplyResult_CreatesSelectableItemViewModels()
    {
        var entry = new FileHistoryCacheEntry("session", "src/a.cs");
        var timeline = FileHistoryCacheEntry.BuildTimeline(
            created: null,
            recent: [new CommitInfo("oid", "oidshort", "subj", "", "A", "a@b.c", DateTimeOffset.UtcNow, [], [])]);
        entry.ApplyResult(timeline);

        Assert.That(entry.IsReady, Is.True);
        Assert.That(entry.Entries, Has.Count.EqualTo(2));
        Assert.That(entry.Entries[0], Is.TypeOf<FileHistoryItemViewModel>());
        Assert.That(entry.Entries[0].CanExpand, Is.False);
        Assert.That(entry.Entries[0].IsCurrent, Is.True);
        Assert.That(entry.Entries[^1].CanExpand, Is.True);
    }
}

public sealed class FileHistoryBrowseControllerTests
{
    [Test]
    public async Task SelectCommitFile_PushesBreadcrumb_AndNavigateBackRestores()
    {
        var host = new FakeBrowseHost { BrowseSubjectPath = FilePath.From("src/a.cs") };
        var controller = CreateController(host);
        host.Controller = controller;

        var item = HistoryItem("oid1", "o1", "change a");
        await controller.SelectHistoryItemAsync(item, cache: null);

        Assert.That(controller.CanNavigateBack, Is.False);
        Assert.That(controller.BreadcrumbTrail, Is.EqualTo("a.cs"));

        await controller.SelectCommitFileAsync(
            item,
            new FileHistoryCommitFileItem(FilePath.From("src/b.cs"), ChangeKind.Modified),
            cache: null);

        Assert.That(controller.CanNavigateBack, Is.True);
        Assert.That(controller.BreadcrumbTrail, Is.EqualTo("a.cs › b.cs"));
        Assert.That(host.BrowseSubjectPath?.Value, Is.EqualTo("src/b.cs"));

        await controller.SelectCommitFileAsync(
            item,
            new FileHistoryCommitFileItem(FilePath.From("src/c.cs"), ChangeKind.Modified),
            cache: null);

        Assert.That(controller.BreadcrumbTrail, Is.EqualTo("a.cs › b.cs › c.cs"));

        await controller.NavigateBackCommand.ExecuteAsync(null);
        Assert.That(host.BrowseSubjectPath?.Value, Is.EqualTo("src/b.cs"));
        Assert.That(controller.BreadcrumbTrail, Is.EqualTo("a.cs › b.cs"));

        await controller.NavigateBackCommand.ExecuteAsync(null);
        Assert.That(host.BrowseSubjectPath?.Value, Is.EqualTo("src/a.cs"));
        Assert.That(controller.CanNavigateBack, Is.False);
        Assert.That(controller.BreadcrumbTrail, Is.EqualTo("a.cs"));
    }

    [Test]
    public async Task SelectCommitFile_SamePath_DoesNotPush()
    {
        var host = new FakeBrowseHost { BrowseSubjectPath = FilePath.From("src/a.cs") };
        var controller = CreateController(host);
        host.Controller = controller;

        var item = HistoryItem("oid1", "o1", "change a");
        await controller.SelectHistoryItemAsync(item, cache: null);
        await controller.SelectCommitFileAsync(
            item,
            new FileHistoryCommitFileItem(FilePath.From("src/a.cs"), ChangeKind.Modified),
            cache: null);

        Assert.That(controller.CanNavigateBack, Is.False);
        Assert.That(controller.BreadcrumbTrail, Is.EqualTo("a.cs"));
    }

    [Test]
    public async Task Reset_AndExit_ClearBreadcrumbStack()
    {
        var host = new FakeBrowseHost { BrowseSubjectPath = FilePath.From("src/a.cs") };
        var controller = CreateController(host);
        host.Controller = controller;

        var item = HistoryItem("oid1", "o1", "change a");
        await controller.SelectHistoryItemAsync(item, cache: null);
        await controller.SelectCommitFileAsync(
            item,
            new FileHistoryCommitFileItem(FilePath.From("src/b.cs"), ChangeKind.Modified),
            cache: null);

        controller.Reset();
        Assert.That(controller.CanNavigateBack, Is.False);
        Assert.That(controller.BreadcrumbTrail, Is.EqualTo(""));
        Assert.That(controller.IsFileHistoryBrowseMode, Is.False);

        await controller.SelectHistoryItemAsync(item, cache: null);
        await controller.SelectCommitFileAsync(
            item,
            new FileHistoryCommitFileItem(FilePath.From("src/b.cs"), ChangeKind.Modified),
            cache: null);
        await controller.ExitFileHistoryBrowseCommand.ExecuteAsync(null);

        Assert.That(controller.CanNavigateBack, Is.False);
        Assert.That(controller.BreadcrumbTrail, Is.EqualTo(""));
        Assert.That(host.ExitCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CompareModeToggle_PreservesBreadcrumbStack()
    {
        var host = new FakeBrowseHost { BrowseSubjectPath = FilePath.From("src/a.cs") };
        var controller = CreateController(host);
        host.Controller = controller;

        var item = HistoryItem("oid1", "o1", "change a");
        await controller.SelectHistoryItemAsync(item, cache: null);
        await controller.SelectCommitFileAsync(
            item,
            new FileHistoryCommitFileItem(FilePath.From("src/b.cs"), ChangeKind.Modified),
            cache: null);

        controller.CompareMode = FileHistoryCompareMode.VsCurrent;

        Assert.That(controller.CanNavigateBack, Is.True);
        Assert.That(controller.BreadcrumbTrail, Is.EqualTo("a.cs › b.cs"));
        Assert.That(controller.IsFileHistoryBrowseMode, Is.True);
    }

    [Test]
    public async Task SideLabels_VsCurrent_UsesShortOidAndCurrent()
    {
        var host = new FakeBrowseHost { BrowseSubjectPath = FilePath.From("src/a.cs") };
        var controller = CreateController(host);
        host.Controller = controller;

        Assert.That(controller.PreviousSideLabel, Is.EqualTo("PREVIOUS VERSION"));
        Assert.That(controller.NewSideLabel, Is.EqualTo("NEW VERSION"));

        var item = HistoryItem("oid1", "abc1234", "change a");
        await controller.SelectHistoryItemAsync(item, cache: null);

        Assert.That(controller.PreviousSideLabel, Is.EqualTo("PREVIOUS VERSION"));
        Assert.That(controller.NewSideLabel, Is.EqualTo("NEW VERSION"));

        controller.CompareMode = FileHistoryCompareMode.VsCurrent;
        Assert.That(controller.PreviousSideLabel, Is.EqualTo("abc1234"));
        Assert.That(controller.NewSideLabel, Is.EqualTo("CURRENT"));

        controller.CompareMode = FileHistoryCompareMode.InCommit;
        Assert.That(controller.PreviousSideLabel, Is.EqualTo("PREVIOUS VERSION"));
        Assert.That(controller.NewSideLabel, Is.EqualTo("NEW VERSION"));

        controller.CompareMode = FileHistoryCompareMode.VsCurrent;
        controller.Reset();
        Assert.That(controller.PreviousSideLabel, Is.EqualTo("PREVIOUS VERSION"));
        Assert.That(controller.NewSideLabel, Is.EqualTo("NEW VERSION"));
    }

    private static FileHistoryBrowseController CreateController(FakeBrowseHost host) =>
        new(new StubHistory(), new StubDiff(), () => host);

    private static FileHistoryItemViewModel HistoryItem(string oid, string shortOid, string subject) =>
        new(new FileHistoryEntry(oid, shortOid, subject, DateTimeOffset.UtcNow, "A", IsCreated: false, IsCurrent: false));

    private sealed class FakeBrowseHost : IFileHistoryBrowseHost
    {
        public FileHistoryBrowseController? Controller { get; set; }
        public string? RepositoryPath => "/tmp/repo";
        public FilePath? BrowseSubjectPath { get; set; }
        public CommitId? CurrentRevision => null;
        public int ExitCount { get; private set; }
        public List<FilePath> PresentedPaths { get; } = [];

        public DiffOptions BuildDiffOptions() => new();

        public Task BeginFileHistoryDiffLoadAsync() => Task.CompletedTask;

        public Task EndFileHistoryDiffLoadAsync() => Task.CompletedTask;

        public Task PresentFileHistoryDiffAsync(FilePath path, FileDiff diff, CancellationToken ct)
        {
            PresentedPaths.Add(path);
            BrowseSubjectPath = path;
            return Task.CompletedTask;
        }

        public Task ExitFileHistoryBrowseAsync()
        {
            ExitCount++;
            return Task.CompletedTask;
        }

        public async Task OpenPathInFileHistoryBrowseAsync(FilePath path, string oid, CancellationToken ct)
        {
            BrowseSubjectPath = path;
            if (Controller is not null)
                await Controller.ReloadForPathAsync(path, oid, ct).ConfigureAwait(false);
        }
    }

    private sealed class StubHistory : IGitHistoryService
    {
        public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(
            string repositoryPath, int skip, int take, string revision = "HEAD", CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CommitInfo>>([]);

        public Task<IReadOnlyList<CommitInfo>> ListCommitsRangeAsync(
            string repositoryPath, string baseRef, string headRef = "HEAD", bool oldestFirst = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CommitInfo>>([]);

        public Task<IReadOnlyList<CommitInfo>> ListFileHistoryAsync(
            string repositoryPath, string path, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CommitInfo>>([]);

        public Task<CommitInfo?> GetFileCreatedCommitAsync(
            string repositoryPath, string path, CancellationToken ct = default) =>
            Task.FromResult<CommitInfo?>(null);

        public Task<IReadOnlyList<FilePath>> ListTrackedFilesAsync(
            string repositoryPath, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FilePath>>([]);

        public Task<IReadOnlyList<(FilePath Path, ChangeKind Kind)>> GetCommitFilesAsync(
            string repositoryPath, string oid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(FilePath, ChangeKind)>>([]);

        public Task<CommitStat> GetCommitStatAsync(
            string repositoryPath, string oid, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> GetCommitPatchAsync(
            string repositoryPath, string oid, FilePath path, DiffOptions options, CancellationToken ct = default) =>
            Task.FromResult("");
    }

    private sealed class StubDiff : IGitDiffService
    {
        public Task<FileDiff> GetDiffAsync(
            string repositoryPath, FilePath path, DiffScope scope, DiffOptions options, CancellationToken ct = default) =>
            Task.FromResult(CleanFileDiff.Create(path, Array.Empty<byte>(), scope));

        public Task<IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)>> GetRawDiffAsync(
            string repositoryPath, DiffScope scope, DiffOptions options, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(FilePath, ContentId, ContentId, ChangeKind)>>([]);
    }
}
