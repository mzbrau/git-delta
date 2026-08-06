using System;
using GitDelta.App.Services;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class NotificationServiceTests
{
    [Test]
    public void Error_Stores_Exception_Detail_And_CopyText()
    {
        var svc = new NotificationService();
        Exception ex;
        try
        {
            throw new InvalidOperationException("thread affinity");
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        svc.Error("Failed to open pull request: thread affinity", exception: ex);

        Assert.That(svc.Notifications, Has.Count.EqualTo(1));
        var n = svc.Notifications[0];
        Assert.That(n.IsError, Is.True);
        Assert.That(n.Message, Is.EqualTo("Failed to open pull request: thread affinity"));
        Assert.That(n.HasDetail, Is.True);
        Assert.That(n.Detail, Does.Contain("InvalidOperationException"));
        Assert.That(n.Detail, Does.Contain("thread affinity"));
        Assert.That(n.CopyText, Does.StartWith(n.Message));
        Assert.That(n.CopyText, Does.Contain(n.Detail!));
    }

    [Test]
    public void Error_Without_Exception_Has_No_Detail_CopyText_Is_Message()
    {
        var svc = new NotificationService();
        svc.Error("No pull request URL available.");

        Assert.That(svc.Notifications, Has.Count.EqualTo(1));
        var n = svc.Notifications[0];
        Assert.That(n.IsError, Is.True);
        Assert.That(n.HasDetail, Is.False);
        Assert.That(n.CopyText, Is.EqualTo(n.Message));
    }

    [Test]
    public void Error_With_Detail_String_Stores_Detail_Without_Exception()
    {
        var svc = new NotificationService();
        svc.Error("AI review: timed out", detail: "Turn timeout: 180s\nRun timeout: 1800s");

        var n = svc.Notifications[0];
        Assert.That(n.HasDetail, Is.True);
        Assert.That(n.Detail, Does.Contain("Turn timeout: 180s"));
        Assert.That(n.CopyText, Does.Contain("AI review: timed out"));
        Assert.That(n.CopyText, Does.Contain("Turn timeout: 180s"));
    }
}
