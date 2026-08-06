using GitDelta.Core;
using NUnit.Framework;

namespace GitDelta.Core.Tests;

public sealed class TicketFromBranchTests
{
    [Test]
    public void Default_Regex_Extracts_Ticket_From_Example_Branch()
    {
        Assert.That(
            TicketFromBranch.TryExtract("bugfix/SMITH-123/3", TicketFromBranch.DefaultRegex, out var ticket, out var error),
            Is.True);
        Assert.That(ticket, Is.EqualTo("SMITH-123"));
        Assert.That(error, Is.Null);
    }

    [TestCase("feature/ABC-9", "ABC-9")]
    [TestCase("SMITH-1", "SMITH-1")]
    [TestCase("release/PROJ-42/hotfix", "PROJ-42")]
    public void Default_Regex_Extracts_Common_Shapes(string branch, string expected)
    {
        Assert.That(TicketFromBranch.TryExtract(branch, null, out var ticket, out _), Is.True);
        Assert.That(ticket, Is.EqualTo(expected));
    }

    [Test]
    public void Custom_Regex_Is_Honoured()
    {
        Assert.That(
            TicketFromBranch.TryExtract("ticket/9999-work", @"ticket/(\d+)", out var ticket, out _),
            Is.True);
        Assert.That(ticket, Is.EqualTo("9999"));
    }

    [Test]
    public void Missing_Match_Returns_False()
    {
        Assert.That(
            TicketFromBranch.TryExtract("main", TicketFromBranch.DefaultRegex, out var ticket, out var error),
            Is.False);
        Assert.That(ticket, Is.Empty);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Invalid_Regex_Returns_Error()
    {
        Assert.That(
            TicketFromBranch.TryExtract("bugfix/SMITH-123/3", "(", out _, out var error),
            Is.False);
        Assert.That(error, Does.Contain("Invalid ticket regex"));
    }

    [Test]
    public void PrependTicket_Inserts_When_Absent()
    {
        Assert.That(TicketFromBranch.PrependTicket("fix login", "SMITH-123"), Is.EqualTo("SMITH-123 fix login"));
        Assert.That(TicketFromBranch.PrependTicket("", "SMITH-123"), Is.EqualTo("SMITH-123"));
        Assert.That(TicketFromBranch.PrependTicket("SMITH-123 already", "SMITH-123"), Is.EqualTo("SMITH-123 already"));
    }
}
