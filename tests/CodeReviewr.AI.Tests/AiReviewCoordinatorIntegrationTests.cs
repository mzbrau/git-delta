using System.Text.Json;
using System.Text.Json.Serialization;
using CodeReviewr.AI.Agent;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.AI;
using CodeReviewr.Core.Settings;
using CodeReviewr.Git;
using CodeReviewr.Persistence;
using CodeReviewr.Review;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace CodeReviewr.AI.Tests;

/// <summary>
/// End-to-end tests for <see cref="AiReviewCoordinator"/> wired through the real DI graph
/// (<c>AddCodeReviewrAIWithFakeAgent</c>) with a <see cref="FakeAgentClient"/> standing in for the
/// GitHub Copilot CLI, so persistence, caching, cancellation, and the custom-tool contract are all
/// exercised without a live agent process.
/// </summary>
public sealed class AiReviewCoordinatorIntegrationTests
{
    private const string RepositoryKey = "owner/repo";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Test]
    public async Task StartReviewAsync_SubmitsChangeBriefing_AndPersistsIt()
    {
        var sessionCreations = 0;
        await using var harness = await Harness.CreateAsync(_ =>
        {
            sessionCreations++;
            return new FakeAgentScript().OnTurn(ChangeBriefingTurn);
        });

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1)]);

        var snapshot = await harness.Service.StartReviewAsync(request);

        Assert.That(snapshot.State, Is.EqualTo(AiRunState.Complete));
        Assert.That(snapshot.ChangeBriefing, Is.Not.Null);
        Assert.That(snapshot.ChangeBriefing!.ExecutiveSummary, Is.EqualTo(DefaultChangeBriefing.ExecutiveSummary));
        Assert.That(snapshot.CopilotSessionId, Is.Not.Null.And.Not.Empty);
        Assert.That(sessionCreations, Is.EqualTo(1));

        var run = await harness.Store.GetRunAsync(snapshot.RunId);
        Assert.That(run, Is.Not.Null);
        Assert.That(run!.State, Is.EqualTo(AiRunState.Complete));

        Assert.That(await harness.Store.GetPrResultForRunAsync(snapshot.RunId), Is.Not.Null);
        Assert.That(await harness.Store.ListFileResultsForRunAsync(snapshot.RunId), Is.Empty);
    }

    [Test]
    public async Task StartReviewAsync_AgentDoesNotSubmitChangeBriefing_ReturnsFailedRun()
    {
        await using var harness = await Harness.CreateAsync(_ => FakeAgentScript.Empty);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1)]);

        var snapshot = await harness.Service.StartReviewAsync(request);

        Assert.That(snapshot.State, Is.EqualTo(AiRunState.Failed));
        Assert.That(snapshot.ChangeBriefing, Is.Null);
        Assert.That(snapshot.ErrorMessage, Does.Contain("did not submit a change briefing"));
    }

    [Test]
    public async Task GetCachedRunAsync_ReturnsCompletedRun_WithoutTouchingAgentAgain()
    {
        var sessionCreations = 0;
        await using var harness = await Harness.CreateAsync(_ =>
        {
            sessionCreations++;
            return new FakeAgentScript().OnTurn(ChangeBriefingTurn);
        });

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 2, 0)]);
        await harness.Service.StartReviewAsync(request);
        Assert.That(sessionCreations, Is.EqualTo(1));

        var cached = await harness.Service.GetCachedRunAsync("PR_1");

        Assert.That(cached, Is.Not.Null);
        Assert.That(cached!.State, Is.EqualTo(AiRunState.Complete));
        Assert.That(cached.ChangeBriefing, Is.Not.Null);
        Assert.That(sessionCreations, Is.EqualTo(1), "GetCachedRunAsync must never touch the agent.");
    }

    [Test]
    public async Task TryGetMatchingCachedRunAsync_ReturnsRun_WhenHeadAndMergeBaseMatch()
    {
        await using var harness = await Harness.CreateAsync(_ => new FakeAgentScript().OnTurn(ChangeBriefingTurn));

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_MATCH", head, head, [ChangedFile("src/App.cs", 2, 0)]);
        var started = await harness.Service.StartReviewAsync(request);

        var matched = await harness.Service.TryGetMatchingCachedRunAsync(request);

        Assert.That(matched, Is.Not.Null);
        Assert.That(matched!.RunId, Is.EqualTo(started.RunId));
        Assert.That(matched.ChangeBriefing, Is.Not.Null);
        Assert.That(await harness.Store.GetRunAsync(started.RunId), Is.Not.Null,
            "Matching hydrate must not delete durable rows.");
    }

    [Test]
    public async Task TryGetMatchingCachedRunAsync_ReturnsNull_WhenHeadShaChanged_KeepsDurableRun()
    {
        await using var harness = await Harness.CreateAsync(
            _ => new FakeAgentScript().OnTurn(ChangeBriefingTurn),
            addSecondCommit: true);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var parent = harness.Repo.RunGit("rev-parse", "HEAD^").Trim();
        var request = harness.BuildRequest("PR_STALE", head, parent, [ChangedFile("src/App.cs", 2, 0)]);
        var started = await harness.Service.StartReviewAsync(request);
        Assert.That(started.State, Is.EqualTo(AiRunState.Complete));

        // Simulate a new push: same merge-base, different head.
        var staleRequest = request with { HeadSha = parent };
        var matched = await harness.Service.TryGetMatchingCachedRunAsync(staleRequest);

        Assert.That(matched, Is.Null);
        Assert.That(await harness.Service.GetCachedRunAsync("PR_STALE"), Is.Not.Null,
            "Raw latest-run lookup still finds the row for History.");
        Assert.That(await harness.Store.GetRunAsync(started.RunId), Is.Not.Null);
    }

    [Test]
    public async Task GetFileBriefingAsync_ReturnsNull_WhenAfterOidDoesNotMatchStoredKey()
    {
        var fileBriefing = new AiFileBriefingResult(
            "src/App.cs", "Entry point of the app.", AiChangeClassification.BugFix, ["Added a null guard."]);
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Call("submit_file_briefing", ToJson(fileBriefing)));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        await harness.Service.StartReviewAsync(
            harness.BuildRequest("PR_OID", head, head, [ChangedFile("src/App.cs", 5, 1, "after-oid-1")]));
        await harness.Service.RequestFileDepthAsync(
            new AiFileDepthRequest("PR_OID", "src/App.cs", "before-oid", "after-oid-1"));

        Assert.That(
            await harness.Service.GetFileBriefingAsync("PR_OID", "src/App.cs", "before-oid", "after-oid-1"),
            Is.Not.Null);

        Assert.That(
            await harness.Service.GetFileBriefingAsync("PR_OID", "src/App.cs", "before-oid", "after-oid-DIFFERENT"),
            Is.Null,
            "A different after OID must not reuse the briefing for an older change on the same path.");
    }

    [Test]
    public async Task TryGetMatchingCachedRunAsync_WorkingCopy_ReturnsNull_AfterContentChanges_KeepsDurableRun()
    {
        await using var harness = await Harness.CreateAsync(_ => new FakeAgentScript().OnTurn(ChangeBriefingTurn));

        var appPath = Path.Combine(harness.RepoPath, "src", "App.cs");
        await File.WriteAllTextAsync(appPath, "class App { void A() {} }\n");
        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var sessionKey = $"local:{harness.RepoPath}";
        var request = harness.BuildRequest(
                sessionKey,
                headSha: "",
                mergeBaseSha: head,
                [ChangedFile("src/App.cs", 2, 0)]) with
            {
                Scope = AiReviewScope.WorkingCopyAll,
                Title = "Pending changes",
                Body = null,
                Author = null,
                BaseBranch = null,
                HeadBranch = null,
            };

        var started = await harness.Service.StartReviewAsync(request);
        Assert.That(started.State, Is.EqualTo(AiRunState.Complete));
        Assert.That(started.HeadSha, Is.Not.Null.And.Not.Empty);

        Assert.That(await harness.Service.TryGetMatchingCachedRunAsync(request), Is.Not.Null,
            "Unchanged working copy should still match the cached run.");

        await File.WriteAllTextAsync(appPath, "class App { void B() {} }\n");
        Assert.That(await harness.Service.TryGetMatchingCachedRunAsync(request), Is.Null,
            "A different working-copy tree must not revive the prior AI briefing.");
        Assert.That(await harness.Store.GetRunAsync(started.RunId), Is.Not.Null,
            "Durable AI rows must remain for later History use.");
        Assert.That(await harness.Service.GetCachedRunAsync(sessionKey), Is.Not.Null);
    }

    [Test]
    public async Task StartReviewAsync_SecondCallWithUnchangedRequest_ReturnsCachedResult_WithoutNewAgentSession()
    {
        var sessionCreations = 0;
        await using var harness = await Harness.CreateAsync(_ =>
        {
            sessionCreations++;
            return new FakeAgentScript().OnTurn(ChangeBriefingTurn);
        });

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 1, 0)]);

        var first = await harness.Service.StartReviewAsync(request);
        var second = await harness.Service.StartReviewAsync(request);

        Assert.That(sessionCreations, Is.EqualTo(1));
        Assert.That(second.RunId, Is.EqualTo(first.RunId));
        Assert.That(second.State, Is.EqualTo(AiRunState.Complete));
        Assert.That(second.ChangeBriefing, Is.Not.Null);
    }

    [Test]
    public async Task StartReviewAsync_DiscardCached_CreatesNewRunShell()
    {
        await using var harness = await Harness.CreateAsync(_ => new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(ChangeBriefingTurn));

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 1, 0)]);

        var first = await harness.Service.StartReviewAsync(request);
        var second = await harness.Service.StartReviewAsync(request with { DiscardCached = true });

        Assert.That(second.RunId, Is.Not.EqualTo(first.RunId));
        Assert.That(second.State, Is.EqualTo(AiRunState.Complete));
        Assert.That(second.ChangeBriefing, Is.Not.Null);
    }

    [Test]
    public async Task StartReviewAsync_ChangedHeadSha_MissesCacheAndCreatesNewRunShell()
    {
        await using var harness = await Harness.CreateAsync(
            _ => new FakeAgentScript().OnTurn(ChangeBriefingTurn).OnTurn(ChangeBriefingTurn),
            addSecondCommit: true);

        var sha1 = harness.Repo.RunGit("rev-parse", "HEAD~1").Trim();
        var sha2 = harness.Repo.RunGit("rev-parse", "HEAD").Trim();

        var request1 = harness.BuildRequest("PR_1", sha1, sha1, [ChangedFile("src/App.cs", 1, 0)]);
        var snapshot1 = await harness.Service.StartReviewAsync(request1);

        var request2 = request1 with { HeadSha = sha2, MergeBaseSha = sha1 };
        var snapshot2 = await harness.Service.StartReviewAsync(request2);

        Assert.That(snapshot1.ChangeBriefing, Is.Not.Null);
        Assert.That(snapshot2.ChangeBriefing, Is.Not.Null);
        Assert.That(snapshot2.RunId, Is.Not.EqualTo(snapshot1.RunId));
        Assert.That(snapshot1.State, Is.EqualTo(AiRunState.Complete));
        Assert.That(snapshot2.State, Is.EqualTo(AiRunState.Complete));
    }

    [Test]
    public async Task StartReviewAsync_ActivityLog_IncludesChangeBriefingPromptAndTool()
    {
        var log = new List<string>();
        await using var harness = await Harness.CreateAsync(_ => new FakeAgentScript().OnTurn(ChangeBriefingTurn));

        using var _ = harness.Service.ObserveActivityLog(RepositoryKey, line => log.Add(line));

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_LOG", head, head, [ChangedFile("src/App.cs", 1, 0)]);
        var snapshot = await harness.Service.StartReviewAsync(request);

        Assert.That(snapshot.State, Is.EqualTo(AiRunState.Complete));
        Assert.That(log, Has.Some.Contain("Waiting on: change briefing"));
        Assert.That(log, Has.Some.Contain(">>> Tool start: submit_change_briefing"));
        Assert.That(log, Has.Some.Contain("<<< Tool result: submit_change_briefing"));
        Assert.That(log, Has.Some.Contain("Review completed."));
        Assert.That(log, Has.Some.Contain(">>> Prompt"));
    }

    [Test]
    public async Task CancelAsync_WhileFileDepthInFlight_CancelsTheTurn()
    {
        var reachedAgent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.BlockUntilCancelled(() => reachedAgent.TrySetResult()));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_CANCEL", head, head, [ChangedFile("src/App.cs", 1, 0)]);
        await harness.Service.StartReviewAsync(request);

        var depthTask = harness.Service.RequestFileDepthAsync(
            new AiFileDepthRequest("PR_CANCEL", "src/App.cs", "before-oid", "after-oid"));
        await reachedAgent.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await harness.Service.CancelAsync(RepositoryKey);

        var ex = Assert.CatchAsync(async () =>
            await depthTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.That(ex, Is.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task RequestFileDepthAsync_SdkTimeoutException_Propagates()
    {
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.BeforeCalls(_ =>
                throw new TimeoutException("SendAndWaitAsync timed out after 00:01:00")));
        await using var harness = await Harness.CreateAsync(
            _ => script,
            configureSettings: s => s.AiTurnTimeoutSeconds = 120);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        await harness.Service.StartReviewAsync(
            harness.BuildRequest("PR_TIMEOUT", head, head, [ChangedFile("src/App.cs", 1, 0)]));

        var ex = Assert.ThrowsAsync<TimeoutException>(async () =>
            await harness.Service.RequestFileDepthAsync(
                new AiFileDepthRequest("PR_TIMEOUT", "src/App.cs", "before-oid", "after-oid")));

        Assert.That(ex!.Message, Does.Contain("timed out"));
    }

    [Test]
    public async Task RequestFileDepthAsync_IdleSilence_ProducesTurnIdleTimeout()
    {
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.BlockUntilCancelled());
        await using var harness = await Harness.CreateAsync(
            _ => script,
            configureSettings: s =>
            {
                s.AiTurnTimeoutSeconds = 10;
                s.AiRunTimeoutSeconds = 0;
            });

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        await harness.Service.StartReviewAsync(
            harness.BuildRequest("PR_IDLE_TIMEOUT", head, head, [ChangedFile("src/App.cs", 1, 0)]));

        var ex = Assert.ThrowsAsync<TimeoutException>(async () =>
            await harness.Service.RequestFileDepthAsync(
                new AiFileDepthRequest("PR_IDLE_TIMEOUT", "src/App.cs", "before-oid", "after-oid")));

        Assert.That(ex!.Message, Does.Contain("turn idle timeout"));
    }

    [Test]
    public async Task RequestFileDepthAsync_ToolInFlight_PausesIdleTimeout_Completes()
    {
        var fileBriefing = new AiFileBriefingResult(
            "src/App.cs", "Entry point.", AiChangeClassification.BugFix, ["Check the guard."]);
        await using var harness = await Harness.CreateAsync(
            _ => new FakeAgentScript()
                .OnTurn(ChangeBriefingTurn)
                .OnTurn(t => t
                    .Text("thinking...\n")
                    .Call("submit_file_briefing", ToJson(fileBriefing))
                    .DelayEachTool(TimeSpan.FromSeconds(12))),
            configureSettings: s =>
            {
                s.AiTurnTimeoutSeconds = 10;
                s.AiRunTimeoutSeconds = 0;
            });

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        await harness.Service.StartReviewAsync(
            harness.BuildRequest("PR_IDLE_RESET", head, head, [ChangedFile("src/App.cs", 1, 0)]));

        await harness.Service.RequestFileDepthAsync(
            new AiFileDepthRequest("PR_IDLE_RESET", "src/App.cs", "before-oid", "after-oid"));

        var briefing = await harness.Service.GetFileBriefingAsync(
            "PR_IDLE_RESET", "src/App.cs", "before-oid", "after-oid");
        Assert.That(briefing, Is.Not.Null);
        Assert.That(briefing!.Overview, Is.EqualTo("Entry point."));
    }

    [Test]
    public async Task RequestFileDepthAsync_SubmitsFileBriefingAndAnnotation_PersistsBoth()
    {
        const string annotationJson = """
            {
              "path": "src/App.cs",
              "blobOid": "after-oid-1",
              "startLine": 3,
              "endLine": 3,
              "side": "New",
              "severity": "Warning",
              "body": "Consider handling the null case here."
            }
            """;
        var fileBriefing = new AiFileBriefingResult(
            "src/App.cs", "Entry point of the app.", AiChangeClassification.BugFix, ["Added a null guard."]);

        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Call("submit_file_briefing", ToJson(fileBriefing)).Call("add_annotation", annotationJson));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1, "after-oid-1")]);
        await harness.Service.StartReviewAsync(request);

        await harness.Service.RequestFileDepthAsync(new AiFileDepthRequest("PR_1", "src/App.cs", "before-oid", "after-oid-1"));

        var briefing = await harness.Service.GetFileBriefingAsync(
            "PR_1", "src/App.cs", "before-oid", "after-oid-1");
        Assert.That(briefing, Is.Not.Null);
        Assert.That(briefing!.Overview, Is.EqualTo("Entry point of the app."));
        Assert.That(briefing.Classification, Is.EqualTo(AiChangeClassification.BugFix));

        var annotations = await harness.Service.GetFileAnnotationsAsync("PR_1", "src/App.cs");
        Assert.That(annotations, Has.Count.EqualTo(1));
        Assert.That(annotations[0].Body, Is.EqualTo("Consider handling the null case here."));
        Assert.That(annotations[0].Severity, Is.EqualTo(AiAnnotationSeverity.Warning));
        Assert.That(annotations[0].BlobOid, Is.EqualTo("after-oid-1"));
    }

    [Test]
    public async Task RequestFileDepthAsync_AnnotationMissingBlobOid_ResolvesFromSide()
    {
        const string annotationJson = """
            {
              "path": "src/App.cs",
              "startLine": 3,
              "endLine": 3,
              "side": "New",
              "severity": "Suggestion",
              "body": "Missing blobOid should be filled from AfterOid."
            }
            """;
        var fileBriefing = new AiFileBriefingResult(
            "src/App.cs", "Entry point.", AiChangeClassification.BugFix, ["Note."]);

        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Call("submit_file_briefing", ToJson(fileBriefing)).Call("add_annotation", annotationJson));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        await harness.Service.StartReviewAsync(
            harness.BuildRequest("PR_BLOB", head, head, [ChangedFile("src/App.cs", 5, 1, "after-oid-resolved")]));

        await harness.Service.RequestFileDepthAsync(
            new AiFileDepthRequest("PR_BLOB", "src/App.cs", "before-oid", "after-oid-resolved"));

        var annotations = await harness.Service.GetFileAnnotationsAsync("PR_BLOB", "src/App.cs");
        Assert.That(annotations, Has.Count.EqualTo(1));
        Assert.That(annotations[0].BlobOid, Is.EqualTo("after-oid-resolved"));
        Assert.That(annotations[0].Body, Does.Contain("Missing blobOid"));
    }

    [Test]
    public async Task RequestFileDepthAsync_ActivityLog_IncludesPromptAndToolIo()
    {
        var log = new List<string>();
        var fileBriefing = new AiFileBriefingResult(
            "src/App.cs", "Purpose", AiChangeClassification.RefactorOnly, ["Look at this."]);
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Call("submit_file_briefing", ToJson(fileBriefing)));
        await using var harness = await Harness.CreateAsync(_ => script);

        using var _ = harness.Service.ObserveActivityLog(RepositoryKey, line => log.Add(line));

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        await harness.Service.StartReviewAsync(
            harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 1, 0)]));
        await harness.Service.RequestFileDepthAsync(
            new AiFileDepthRequest("PR_1", "src/App.cs", "before-oid", "after-oid"));

        Assert.That(log, Has.Some.Contain(">>> Prompt"));
        Assert.That(log, Has.Some.Contain(">>> Tool start: submit_file_briefing"));
        Assert.That(log, Has.Some.Contain("<<< Tool result: submit_file_briefing"));
    }

    [Test]
    public async Task AskAsync_ReturnsScriptedAssistantAnswer()
    {
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Text("This adds a null check before dereferencing the argument."));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1)]);
        await harness.Service.StartReviewAsync(request);

        var answer = await harness.Service.AskAsync(new AiQuestionRequest("PR_1", "src/App.cs", "Why was this changed?"));

        Assert.That(answer, Is.EqualTo("This adds a null check before dereferencing the argument."));
    }

    [Test]
    public async Task ChatAsync_ReturnsScriptedAnswer_AndPersistsBothMessages()
    {
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Text("Because it fixes a crash reported in issue #42."));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1)]);
        await harness.Service.StartReviewAsync(request);

        var answer = await harness.Service.ChatAsync(new AiQuestionRequest("PR_1", null, "Why was this changed?"));

        Assert.That(answer, Is.EqualTo("Because it fixes a crash reported in issue #42."));

        var history = await harness.Service.GetChatHistoryAsync("PR_1");
        Assert.That(history, Has.Count.EqualTo(2));
        Assert.That(history[0].Role, Is.EqualTo("user"));
        Assert.That(history[1].Role, Is.EqualTo("assistant"));
        Assert.That(history[1].Content, Is.EqualTo("Because it fixes a crash reported in issue #42."));
    }

    [Test]
    public async Task ChatAsync_AfterRestart_ResumesStoredCopilotSession()
    {
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Text("First answer, on the already-open session."));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1)]);
        var snapshot = await harness.Service.StartReviewAsync(request);
        Assert.That(snapshot.State, Is.EqualTo(AiRunState.Complete));
        Assert.That(snapshot.CopilotSessionId, Is.Not.Null.And.Not.Empty);
        var storedSessionId = snapshot.CopilotSessionId!;

        await harness.Service.ChatAsync(new AiQuestionRequest("PR_1", null, "Open the session"));

        var runAfterChat = await harness.Store.GetRunAsync(snapshot.RunId);
        Assert.That(runAfterChat!.CopilotSessionId, Is.EqualTo(storedSessionId));

        // Simulate app restart: new coordinator / FakeAgentClient, same durable store + repo.
        var chatScript = new FakeAgentScript()
            .OnTurn(t => t.Text("The ::: markers are Markdown callout fences."));
        await using var harness2 = await harness.RestartWithNewCoordinatorAsync(_ => chatScript);

        await harness2.Service.AttachCachedRunAsync(request);

        var answer = await harness2.Service.ChatAsync(
            new AiQuestionRequest("PR_1", "src/App.cs", "What do the ::: characters mean?"));

        Assert.That(answer, Is.EqualTo("The ::: markers are Markdown callout fences."));
        Assert.That(harness2.Agent.ResumedSessionIds, Does.Contain(storedSessionId));
        Assert.That(harness2.Agent.LastSession, Is.Not.Null);
        Assert.That(harness2.Agent.LastSession!.SentPrompts, Has.Count.EqualTo(1));
        Assert.That(harness2.Agent.LastSession.SentPrompts[0], Does.Contain("Currently selected file: src/App.cs"));
    }

    [Test]
    public async Task ChatAsync_WithoutAttach_ThrowsClearError()
    {
        await using var harness = await Harness.CreateAsync(_ => FakeAgentScript.Empty);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.Service.ChatAsync(new AiQuestionRequest("PR_missing", null, "Hello?")));
        Assert.That(ex!.Message, Does.Contain("No AI review context"));
    }

    [Test]
    public async Task ChatAsync_WithSelectedFile_IncludesFileFramingInPrompt()
    {
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Text("Answer about App.cs"));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1)]);
        await harness.Service.StartReviewAsync(request);

        await harness.Service.ChatAsync(new AiQuestionRequest(
            "PR_1",
            "src/App.cs",
            "Explain this",
            SelectedLinesContext: "Selected lines:\nCheck nulls"));

        Assert.That(harness.Agent.LastSession, Is.Not.Null);
        var prompt = harness.Agent.LastSession!.SentPrompts.Last();
        Assert.That(prompt, Does.Contain("Currently selected file: src/App.cs"));
        Assert.That(prompt, Does.Contain("Selected lines:"));
        Assert.That(prompt, Does.Contain("Check nulls"));
    }

    [Test]
    public async Task ChatAsync_SecondTurn_IncludesPriorConversationInPrompt()
    {
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Text("First answer about the crash."))
            .OnTurn(t => t.Text("Second answer referring to earlier context."));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1)]);
        await harness.Service.StartReviewAsync(request);

        await harness.Service.ChatAsync(new AiQuestionRequest("PR_1", null, "Why was this changed?"));
        await harness.Service.ChatAsync(new AiQuestionRequest("PR_1", null, "Can you elaborate?"));

        Assert.That(harness.Agent.LastSession, Is.Not.Null);
        var secondPrompt = harness.Agent.LastSession!.SentPrompts.Last();
        Assert.That(secondPrompt, Does.Contain("Conversation so far:"));
        Assert.That(secondPrompt, Does.Contain("user: Why was this changed?"));
        Assert.That(secondPrompt, Does.Contain("assistant: First answer about the crash."));
        Assert.That(secondPrompt, Does.Contain("Can you elaborate?"));
    }

    [Test]
    public async Task ClearChatHistoryAsync_RemovesPersistedMessages_KeepsRun()
    {
        var script = new FakeAgentScript()
            .OnTurn(ChangeBriefingTurn)
            .OnTurn(t => t.Text("Because it fixes a crash."));
        await using var harness = await Harness.CreateAsync(_ => script);

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1)]);
        var snapshot = await harness.Service.StartReviewAsync(request);
        await harness.Service.ChatAsync(new AiQuestionRequest("PR_1", null, "Why was this changed?"));

        Assert.That(await harness.Service.GetChatHistoryAsync("PR_1"), Has.Count.EqualTo(2));

        await harness.Service.ClearChatHistoryAsync("PR_1");

        Assert.That(await harness.Service.GetChatHistoryAsync("PR_1"), Is.Empty);
        Assert.That(await harness.Store.GetRunAsync(snapshot.RunId), Is.Not.Null);
    }

    [Test]
    public async Task ClearAiDataAsync_EmptiesTheDurableStore()
    {
        await using var harness = await Harness.CreateAsync(_ => new FakeAgentScript().OnTurn(ChangeBriefingTurn));

        var head = harness.Repo.RunGit("rev-parse", "HEAD").Trim();
        var request = harness.BuildRequest("PR_1", head, head, [ChangedFile("src/App.cs", 5, 1)]);
        var snapshot = await harness.Service.StartReviewAsync(request);
        Assert.That(await harness.Store.GetRunAsync(snapshot.RunId), Is.Not.Null);

        await harness.Service.ClearAiDataAsync();

        Assert.That(await harness.Store.GetRunAsync(snapshot.RunId), Is.Null);
        Assert.That(await harness.Service.GetCachedRunAsync("PR_1"), Is.Null);
    }

    private static readonly AiChangeBriefingResult DefaultChangeBriefing = new(
        ExecutiveSummary: "This adds a null check before dereferencing the argument.",
        Risk: AiRiskLevel.Low,
        RiskDrivers: ["Small, well-scoped change."],
        WhatChanged: ["Bug fix"],
        ReviewFocus: ["Verify the new guard clause."],
        TestingStatus: new AiTestingStatus("No new tests were added.", []),
        Dependencies: []);

    /// <summary>Scripts the mandatory <c>submit_change_briefing</c> tool call every <c>StartReviewAsync</c> requires.</summary>
    private static void ChangeBriefingTurn(FakeAgentTurnBuilder t) =>
        t.Call("submit_change_briefing", ToJson(DefaultChangeBriefing));

    private static AiChangedFileFact ChangedFile(string path, int added, int removed, string? afterOid = "after-oid") =>
        new(path, "Modified", "before-oid", afterOid, added, removed);

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>Wires the real AI DI graph with a temp repo, a scratch durable.db, and a scripted <see cref="FakeAgentClient"/>.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private ServiceProvider _provider;
        private readonly string _tempRoot;
        private bool _ownsRepo;
        private bool _ownsTempRoot;
        private bool _disposed;

        public required RepositoryBuilder Repo { get; init; }
        public required string RepoPath { get; init; }
        public required string DurableDbPath { get; init; }
        public required ISettingsStore Settings { get; init; }
        public bool ClearAiDataOnDispose { get; set; } = true;

        private Harness(ServiceProvider provider, string tempRoot, bool ownsRepo, bool ownsTempRoot)
        {
            _provider = provider;
            _tempRoot = tempRoot;
            _ownsRepo = ownsRepo;
            _ownsTempRoot = ownsTempRoot;
        }

        public IAIReviewService Service => _provider.GetRequiredService<IAIReviewService>();

        public IAiResultStore Store => _provider.GetRequiredService<IAiResultStore>();

        public FakeAgentClient Agent => (FakeAgentClient)_provider.GetRequiredService<IAgentClient>();

        public static async Task<Harness> CreateAsync(
            Func<AgentSessionOptions, FakeAgentScript> scriptFactory,
            bool addSecondCommit = false,
            Action<AppSettings>? configureSettings = null)
        {
            var builder = RepositoryBuilder.Create()
                .WithFile("src/App.cs", "class App {}\n")
                .WithInitialCommit("root");
            if (addSecondCommit)
            {
                builder
                    .WithFile("src/App.cs", "class App { void Run() {} }\n")
                    .WithCommit("feature work");
            }

            var repoPath = builder.Build();

            var tempRoot = Path.Combine(Path.GetTempPath(), "CodeReviewr.AI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var dbPath = Path.Combine(tempRoot, "durable.db");
            using (var durable = new SqliteDurableUserStore(dbPath))
                durable.EnsureSchema();

            return await CreateCoreAsync(tempRoot, dbPath, repoPath, builder, scriptFactory, configureSettings,
                ownsRepo: true, ownsTempRoot: true).ConfigureAwait(false);
        }

        /// <summary>
        /// Disposes the current DI graph (in-memory sessions) and returns a new harness sharing the
        /// same durable.db and git repo — simulating an app restart.
        /// </summary>
        public async Task<Harness> RestartWithNewCoordinatorAsync(
            Func<AgentSessionOptions, FakeAgentScript> scriptFactory,
            Action<AppSettings>? configureSettings = null)
        {
            // Drop live sessions without wiping durable AI results or the temp repo.
            ClearAiDataOnDispose = false;
            await _provider.DisposeAsync().ConfigureAwait(false);
            _ownsRepo = false;
            _ownsTempRoot = false;
            _disposed = true;

            return await CreateCoreAsync(
                    _tempRoot,
                    DurableDbPath,
                    RepoPath,
                    Repo,
                    scriptFactory,
                    configureSettings,
                    ownsRepo: true,
                    ownsTempRoot: true)
                .ConfigureAwait(false);
        }

        private static async Task<Harness> CreateCoreAsync(
            string tempRoot,
            string dbPath,
            string repoPath,
            RepositoryBuilder repo,
            Func<AgentSessionOptions, FakeAgentScript> scriptFactory,
            Action<AppSettings>? configureSettings,
            bool ownsRepo,
            bool ownsTempRoot)
        {
            var settingsFile = ownsTempRoot && !File.Exists(Path.Combine(tempRoot, "settings.json"))
                ? "settings.json"
                : $"settings-{Guid.NewGuid():N}.json";
            var settingsStore = new JsonSettingsStore(Path.Combine(tempRoot, settingsFile));
            settingsStore.Update(s =>
            {
                s.AiAssistanceEnabled = true;
                s.AiDisclosureAcknowledged = true;
                configureSettings?.Invoke(s);
            });

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddCodeReviewrGit();
            services.AddCodeReviewrReview();
            services.AddSingleton<ISettingsStore>(settingsStore);
            services.AddSingleton<ITokenStore, MemoryTokenStore>();
            services.AddSingleton<IAiResultStore>(_ => new SqliteAiResultStore(dbPath));
            services.AddCodeReviewrAIWithFakeAgent(scriptFactory);

            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IGitEnvironment>().DetectAsync();

            return new Harness(provider, tempRoot, ownsRepo, ownsTempRoot)
            {
                Repo = repo,
                RepoPath = repoPath,
                DurableDbPath = dbPath,
                Settings = settingsStore,
            };
        }

        public AiReviewRequest BuildRequest(
            string prNodeId, string headSha, string mergeBaseSha, IReadOnlyList<AiChangedFileFact> files) => new(
            SessionKey: prNodeId,
            RepositoryPath: RepoPath,
            RepositoryKey: RepositoryKey,
            HeadSha: headSha,
            MergeBaseSha: mergeBaseSha,
            Title: "Add null check",
            Body: "Fixes a crash on null input.",
            Author: "octocat",
            BaseBranch: "main",
            HeadBranch: "feature",
            ChangedFiles: files);

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            if (ClearAiDataOnDispose)
            {
                try
                {
                    await Service.ClearAiDataAsync();
                }
                catch
                {
                    // Best-effort cleanup; some tests intentionally leave the coordinator mid-run.
                }
            }

            await _provider.DisposeAsync();
            _disposed = true;

            if (_ownsRepo)
                Repo.Dispose();

            if (_ownsTempRoot)
            {
                try
                {
                    if (Directory.Exists(_tempRoot))
                        Directory.Delete(_tempRoot, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }
}
