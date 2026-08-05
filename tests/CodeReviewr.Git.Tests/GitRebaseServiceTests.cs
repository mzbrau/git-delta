using CodeReviewr.Core;
using CodeReviewr.Git;
using NUnit.Framework;

namespace CodeReviewr.Git.Tests;

public sealed class GitRebaseServiceTests
{
    [Test]
    public void BuildTodoFile_Omits_Drop_And_Uses_Verbs()
    {
        var todo = new[]
        {
            new RebaseTodoEntry("aaa", RebaseTodoAction.Pick),
            new RebaseTodoEntry("bbb", RebaseTodoAction.Reword, "new subject"),
            new RebaseTodoEntry("ccc", RebaseTodoAction.Squash, "squashed"),
            new RebaseTodoEntry("ddd", RebaseTodoAction.Fixup),
        };

        var text = GitRebaseService.BuildTodoFile(todo);
        Assert.That(text, Is.EqualTo(
            "pick aaa\nreword bbb\nsquash ccc\nfixup ddd\n"));
    }

    [Test]
    public void ValidateTodo_Rejects_Squash_First()
    {
        var todo = new[]
        {
            new RebaseTodoEntry("aaa", RebaseTodoAction.Squash, "msg"),
        };

        Assert.Throws<GitException>(() => GitRebaseService.ValidateTodo(todo));
    }

    [Test]
    public void ValidateTodo_Requires_Reword_Message()
    {
        var todo = new[]
        {
            new RebaseTodoEntry("aaa", RebaseTodoAction.Reword),
        };

        Assert.Throws<GitException>(() => GitRebaseService.ValidateTodo(todo));
    }

    [Test]
    public void ParseNumstat_Sums_Insertions_And_Deletions()
    {
        var stdout = "3\t1\ta.txt\n0\t5\tb.txt\n-\t-\tbin.dat\n";
        var stat = GitHistoryService.ParseNumstat("abc", stdout);
        Assert.That(stat.Oid, Is.EqualTo("abc"));
        Assert.That(stat.FileCount, Is.EqualTo(3));
        Assert.That(stat.Insertions, Is.EqualTo(3));
        Assert.That(stat.Deletions, Is.EqualTo(6));
    }
}
