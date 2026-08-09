using GitDelta.App.Services;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class TagReleaseDialogViewModelTests
{
    private IGitTagService _tags = null!;
    private NotificationService _notifications = null!;
    private int _completedCount;

    [SetUp]
    public void SetUp()
    {
        _tags = Substitute.For<IGitTagService>();
        _notifications = new NotificationService();
        _completedCount = 0;
        _tags.ListTagsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private TagReleaseDialogViewModel CreateVm() =>
        new(_tags, _notifications, () =>
        {
            _completedCount++;
            return Task.CompletedTask;
        });

    [Test]
    public async Task Open_Shows_Warning_Off_Main()
    {
        var vm = CreateVm();
        await vm.OpenAsync("/tmp/repo", "feature");
        Assert.That(vm.ShowBranchWarning, Is.True);
        Assert.That(vm.CurrentBranch, Is.EqualTo("feature"));
    }

    [Test]
    public async Task Open_Hides_Warning_On_Main_And_Master()
    {
        var vm = CreateVm();
        await vm.OpenAsync("/tmp/repo", "main");
        Assert.That(vm.ShowBranchWarning, Is.False);

        await vm.OpenAsync("/tmp/repo", "Master");
        Assert.That(vm.ShowBranchWarning, Is.False);
    }

    [Test]
    public async Task Filter_Narrows_List()
    {
        _tags.ListTagsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new TagInfo("v1.0.0", DateTimeOffset.UnixEpoch, "aaa", "First"),
                new TagInfo("v2.0.0", DateTimeOffset.UnixEpoch.AddDays(1), "bbb", "Second"),
                new TagInfo("hotfix", DateTimeOffset.UnixEpoch.AddDays(2), "ccc", "urgent fix"),
            ]);

        var vm = CreateVm();
        await vm.OpenAsync("/tmp/repo", "main");
        Assert.That(vm.FilteredTags, Has.Count.EqualTo(3));

        vm.FilterText = "v2";
        Assert.That(vm.FilteredTags.Select(t => t.Name).ToList(), Is.EqualTo(new[] { "v2.0.0" }));

        vm.FilterText = "urgent";
        Assert.That(vm.FilteredTags.Select(t => t.Name).ToList(), Is.EqualTo(new[] { "hotfix" }));
    }

    [Test]
    public async Task AddAndPush_Creates_Then_Pushes_Single_Tag_By_Default()
    {
        var vm = CreateVm();
        await vm.OpenAsync("/tmp/repo", "main");
        vm.NewTagName = "v1.2.0";
        vm.TagMessage = "Release 1.2.0";

        await vm.AddAndPushCommand.ExecuteAsync(null);

        await _tags.Received(1).CreateAnnotatedTagAsync("/tmp/repo", "v1.2.0", "Release 1.2.0", Arg.Any<CancellationToken>());
        await _tags.Received(1).PushTagAsync("/tmp/repo", "v1.2.0", Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
        await _tags.DidNotReceive().PushAllTagsAsync(Arg.Any<string>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
        Assert.That(_completedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task AddAndPush_With_Checkbox_Pushes_All_Tags()
    {
        var vm = CreateVm();
        await vm.OpenAsync("/tmp/repo", "main");
        vm.NewTagName = "v1.2.0";
        vm.TagMessage = "Release";
        vm.PushAllTags = true;

        await vm.AddAndPushCommand.ExecuteAsync(null);

        await _tags.Received(1).CreateAnnotatedTagAsync("/tmp/repo", "v1.2.0", "Release", Arg.Any<CancellationToken>());
        await _tags.Received(1).PushAllTagsAsync("/tmp/repo", Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
        await _tags.DidNotReceive().PushTagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
        Assert.That(_completedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task AddAndPush_Failure_Keeps_Dialog_Open()
    {
        _tags.CreateAnnotatedTagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new GitException("tag exists")));

        var vm = CreateVm();
        await vm.OpenAsync("/tmp/repo", "main");
        vm.NewTagName = "v1.0.0";
        vm.TagMessage = "dup";

        await vm.AddAndPushCommand.ExecuteAsync(null);

        Assert.That(_completedCount, Is.EqualTo(0));
        Assert.That(vm.NewTagName, Is.EqualTo("v1.0.0"));
        Assert.That(vm.IsBusy, Is.False);
    }

    [Test]
    public async Task CanAddAndPush_Requires_Name_Only()
    {
        var vm = CreateVm();
        await vm.OpenAsync("/tmp/repo", "main");
        Assert.That(vm.CanAddAndPush, Is.False);

        vm.NewTagName = "v1";
        Assert.That(vm.CanAddAndPush, Is.True);
        Assert.That(vm.AddAndPushCommand.CanExecute(null), Is.True);
    }

    [Test]
    public async Task AddAndPush_Empty_Message_Creates_Lightweight_Tag()
    {
        var vm = CreateVm();
        await vm.OpenAsync("/tmp/repo", "main");

        vm.NewTagName = "v1.2.0";
        vm.TagMessage = "   ";
        await vm.AddAndPushCommand.ExecuteAsync(null);

        await _tags.Received(1).CreateAnnotatedTagAsync("/tmp/repo", "v1.2.0", "", Arg.Any<CancellationToken>());
        await _tags.Received(1).PushTagAsync("/tmp/repo", "v1.2.0", Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
        Assert.That(_completedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Typing_Name_And_Message_Raises_CanAddAndPush()
    {
        var vm = CreateVm();
        await vm.OpenAsync("/tmp/repo", "main");

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.NewTagName = "v1.0.0";
        Assert.That(raised, Does.Contain(nameof(TagReleaseDialogViewModel.CanAddAndPush)));

        raised.Clear();
        vm.TagMessage = "Release";
        Assert.That(raised, Does.Contain(nameof(TagReleaseDialogViewModel.CanAddAndPush)));
        Assert.That(vm.CanAddAndPush, Is.True);
        Assert.That(vm.AddAndPushCommand.CanExecute(null), Is.True);
    }

    [Test]
    public async Task Reload_Ownership_Ignores_Stale_Results()
    {
        var tcs1 = new TaskCompletionSource<IReadOnlyList<TagInfo>>();
        var tcs2 = new TaskCompletionSource<IReadOnlyList<TagInfo>>();
        var call = 0;
        _tags.ListTagsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                call++;
                return call == 1 ? tcs1.Task : tcs2.Task;
            });

        var vm = CreateVm();
        var open1 = vm.OpenAsync("/tmp/repo", "main");
        var open2 = vm.OpenAsync("/tmp/repo", "feature");

        tcs2.SetResult([new TagInfo("new", DateTimeOffset.UnixEpoch, "oid", "m")]);
        await open2;

        tcs1.SetResult([new TagInfo("stale", DateTimeOffset.UnixEpoch, "oid", "m")]);
        await open1;

        Assert.That(vm.FilteredTags.Select(t => t.Name).ToList(), Is.EqualTo(new[] { "new" }));
        Assert.That(vm.CurrentBranch, Is.EqualTo("feature"));
        Assert.That(vm.IsLoading, Is.False);
    }
}
