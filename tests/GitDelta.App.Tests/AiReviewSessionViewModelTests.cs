using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.AI;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class AiReviewSessionViewModelTests
{
    [Test]
    public void FormatProgressText_Includes_Stage_And_Turns()
    {
        var text = AiReviewSessionViewModel.FormatProgressText(
            new AiRunProgress(
                Stage: AiRunStage.FileDepth,
                TurnsUsed: 3,
                TurnBudget: 10,
                FilesCompleted: 2,
                FilesTotal: 5,
                Elapsed: TimeSpan.FromSeconds(65),
                Message: "working"));

        Assert.That(text, Does.Contain("FileDepth"));
        Assert.That(text, Does.Contain("01:05"));
        Assert.That(text, Does.Contain("3/10 turns"));
        Assert.That(text, Does.Contain("2/5 files"));
        Assert.That(text, Does.Contain("working"));
    }

    [Test]
    public void ButtonLabel_And_StatusTitle_Match_Run_State()
    {
        Assert.That(AiReviewSessionViewModel.ButtonLabel(AiRunState.Running), Is.EqualTo("Reviewing…"));
        Assert.That(AiReviewSessionViewModel.ButtonLabel(AiRunState.Idle), Is.EqualTo("AI review"));
        Assert.That(AiReviewSessionViewModel.StatusDialogTitle(AiRunState.Complete), Is.EqualTo("AI review complete"));
    }

    [Test]
    public void FormatDiagnosticsText_Includes_Settings_And_Error()
    {
        var settings = new AppSettings { AiTurnTimeoutSeconds = 30, AiRunTimeoutSeconds = 0, AiTurnBudget = 20 };
        var text = AiReviewSessionViewModel.FormatDiagnosticsText(
            settings,
            AiRunState.Failed,
            progress: null,
            copilotSessionId: "sess-1",
            lastError: "boom",
            activityLog: "line1");

        Assert.That(text, Does.Contain("Turn budget: 20"));
        Assert.That(text, Does.Contain("Run timeout: unlimited"));
        Assert.That(text, Does.Contain("Copilot session: sess-1"));
        Assert.That(text, Does.Contain("Error: boom"));
        Assert.That(text, Does.Contain("line1"));
        Assert.That(AiReviewSessionViewModel.HasDiagnostics(AiRunState.Failed, "boom", null), Is.True);
    }
}
