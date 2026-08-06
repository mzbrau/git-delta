using System.Collections.ObjectModel;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Diff;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class FileListSearchLayoutTests
{
    [Test]
    public void RebuildSearchResults_HidesZeroHits_NestsExpandedHits()
    {
        var a = new FileItemViewModel(FilePath.From("a.txt"), ChangeKind.Modified, isStagedList: false);
        var b = new FileItemViewModel(FilePath.From("b.txt"), ChangeKind.Modified, isStagedList: false);
        var hits = new List<ChangedLineSearch.Hit>
        {
            new(DiffSide.New, 12, "hello dog", "hello dog", 6, 3),
            new(DiffSide.Old, 4, "dog house", "dog house", 0, 3),
        };
        var target = new ObservableCollection<FileListEntry>();
        var expand = new Dictionary<string, bool>(StringComparer.Ordinal);

        FileListLayoutHelper.RebuildSearchResults(
            target,
            [
                (a, hits),
                (b, Array.Empty<ChangedLineSearch.Hit>()),
            ],
            flatUsesFullPath: false,
            expand);

        Assert.That(target, Has.Count.EqualTo(3));
        Assert.That(target[0].IsSearchGroup, Is.True);
        Assert.That(target[0].Label, Is.EqualTo("a.txt"));
        Assert.That(target[0].IsExpanded, Is.True);
        Assert.That(target[1].IsSearchHit, Is.True);
        Assert.That(target[1].HitLine, Is.EqualTo(12));
        Assert.That(target[2].IsSearchHit, Is.True);
        Assert.That(target[2].HitLine, Is.EqualTo(4));
    }

    [Test]
    public void RebuildSearchResults_CollapsedGroup_OmitsHits()
    {
        var a = new FileItemViewModel(FilePath.From("a.txt"), ChangeKind.Modified, isStagedList: false);
        var hits = new List<ChangedLineSearch.Hit>
        {
            new(DiffSide.New, 1, "x", "x", 0, 1),
        };
        var target = new ObservableCollection<FileListEntry>();
        var expand = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["a.txt"] = false,
        };

        FileListLayoutHelper.RebuildSearchResults(
            target,
            [(a, hits)],
            flatUsesFullPath: false,
            expand);

        Assert.That(target, Has.Count.EqualTo(1));
        Assert.That(target[0].IsSearchGroup, Is.True);
        Assert.That(target[0].IsExpanded, Is.False);
    }
}
