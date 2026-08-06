using System.Text;
using System.Text.Json;
using GitDelta.AI.Agent;
using GitDelta.Core;
using GitDelta.Core.AI;
using GitDelta.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GitDelta.AI;

/// <summary>
/// Short-lived Copilot sessions for commit-message generation and Magic Commit planning.
/// Does not mutate the working copy.
/// </summary>
internal sealed class AiCommitAssistService(
    ISettingsStore settingsStore,
    IAgentClient agentClient,
    AiPromptCatalog prompts,
    ITokenStore tokenStore,
    ILogger<AiCommitAssistService> logger) : IAiCommitAssistService
{
    private const string DedicatedTokenHost = "copilot.github.com";
    private const string DedicatedTokenLogin = "dedicated-copilot-token";

    private static readonly TimeSpan SdkTurnWaitCeiling = TimeSpan.FromDays(1);

    public async Task<string> GenerateCommitMessageAsync(
        string repositoryKey,
        string repositoryPath,
        string diffSummary,
        CancellationToken ct = default)
    {
        var gate = CheckGate(repositoryKey);
        if (gate is not null)
            throw new InvalidOperationException(gate);

        if (string.IsNullOrWhiteSpace(diffSummary))
            throw new InvalidOperationException("No staged changes to summarise.");

        var prompt = prompts.GetCommitMessagePrompt(new Dictionary<string, string>
        {
            ["diff_summary"] = diffSummary,
        });

        var text = await RunTextTurnAsync(repositoryPath, tools: [], prompt, activity: null, ct).ConfigureAwait(false);
        var cleaned = StripFences(text);
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new InvalidOperationException("Copilot returned an empty commit message.");
        return cleaned.Trim();
    }

    public async Task<MagicCommitPlan> ProposeMagicCommitPlanAsync(
        string repositoryKey,
        string repositoryPath,
        string hunkInventory,
        string? adHocInstructions,
        IProgress<string>? activity = null,
        CancellationToken ct = default)
    {
        var gate = CheckGate(repositoryKey);
        if (gate is not null)
            throw new InvalidOperationException(gate);

        if (string.IsNullOrWhiteSpace(hunkInventory))
            throw new InvalidOperationException("No changes to plan commits for.");

        MagicCommitPlan? plan = null;
        var tools = new AgentCustomTool[]
        {
            new(
                "submit_magic_commit_plan",
                """Submit the final Magic Commit plan. Preferred JSON: {"commits":[{"message":"Subject line\n\nOptional body","hunkIds":["h1","h2"]}]}. Every inventory hunk ID must appear in exactly one commit.""",
                (argsJson, _) =>
                {
                    try
                    {
                        plan = MagicCommitPlanParser.Parse(argsJson);
                        return Task.FromResult("""{"status":"ok"}""");
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult(
                            JsonSerializer.Serialize(new Dictionary<string, string>
                            {
                                ["status"] = "error",
                                ["message"] = ex.Message,
                            }));
                    }
                }),
        };

        var prompt = prompts.GetMagicCommitPrompt(new Dictionary<string, string>
        {
            ["adhoc_instructions"] = string.IsNullOrWhiteSpace(adHocInstructions) ? "(none)" : adHocInstructions.Trim(),
            ["hunk_inventory"] = hunkInventory,
        });

        await RunTextTurnAsync(repositoryPath, tools, prompt, activity, ct).ConfigureAwait(false);

        if (plan is null)
            throw new InvalidOperationException("Copilot did not submit a Magic Commit plan.");

        return plan;
    }

    private async Task<string> RunTextTurnAsync(
        string repositoryPath,
        IReadOnlyList<AgentCustomTool> tools,
        string prompt,
        IProgress<string>? activity = null,
        CancellationToken ct = default)
    {
        await agentClient.StartAsync(ct).ConfigureAwait(false);

        var token = await ResolveTokenAsync(repositoryPath, ct).ConfigureAwait(false);
        var settings = settingsStore.Current;
        var policy = new AgentPermissionPolicy(settings.AiPathDenylist);

        var options = new AgentSessionOptions(
            Cwd: repositoryPath,
            GitHubToken: token,
            Model: string.IsNullOrWhiteSpace(settings.AiModelOverride) ? null : settings.AiModelOverride,
            ReasoningEffort: settings.AiReasoningEffort,
            Tools: tools,
            OnPermissionRequest: policy.Evaluate,
            Streaming: true);

        await using var session = await agentClient.CreateSessionAsync(options, ct).ConfigureAwait(false);

        void Log(string line) =>
            activity?.Report($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {line}");

        Log(">>> Prompt");
        Log(prompt);
        Log("<<< End prompt");

        var buffer = new StringBuilder();
        var assistantBuffer = new StringBuilder();

        void FlushAssistant()
        {
            if (assistantBuffer.Length == 0)
                return;
            Log(">>> Assistant");
            Log(assistantBuffer.ToString());
            Log("<<< End assistant");
            assistantBuffer.Clear();
        }

        void OnDelta(string delta)
        {
            buffer.Append(delta);
            assistantBuffer.Append(delta);
            if (assistantBuffer.Length >= 256 || delta.Contains('\n'))
                FlushAssistant();
        }

        void OnToolStarted(string name, string argsJson)
        {
            FlushAssistant();
            Log($">>> Tool start: {name}");
            Log(argsJson);
        }

        void OnToolCompleted(AgentToolCall call)
        {
            Log($"<<< Tool result: {call.Name}");
            if (!string.IsNullOrEmpty(call.ResultJson))
                Log(call.ResultJson);
        }

        session.AssistantDelta += OnDelta;
        session.ToolActivityStarted += OnToolStarted;
        session.ToolCallReceived += OnToolCompleted;
        try
        {
            var turnTimeoutSeconds = Math.Max(10, settings.AiTurnTimeoutSeconds);
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            turnCts.CancelAfter(TimeSpan.FromSeconds(turnTimeoutSeconds));

            try
            {
                await session.SendTurnAsync(prompt, SdkTurnWaitCeiling, turnCts.Token).ConfigureAwait(false);
                FlushAssistant();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                FlushAssistant();
                throw new TimeoutException($"Commit assist timed out after {turnTimeoutSeconds}s of inactivity.");
            }
        }
        finally
        {
            session.AssistantDelta -= OnDelta;
            session.ToolActivityStarted -= OnToolStarted;
            session.ToolCallReceived -= OnToolCompleted;
        }

        if (policy.Denials.Count > 0)
            logger.LogInformation("Commit assist had {Count} denied agent permission requests.", policy.Denials.Count);

        return buffer.ToString();
    }

    private string? CheckGate(string repositoryKey)
    {
        var settings = settingsStore.Current;
        if (!settings.AiAssistanceEnabled)
            return "AI assistance is disabled in settings.";

        if (!settings.AiDisclosureAcknowledged)
            return "AI assistance requires acknowledging the data-sharing disclosure in settings.";

        if (settings.AiExcludedRepositories.Contains(repositoryKey, StringComparer.OrdinalIgnoreCase))
            return "AI assistance is disabled for this repository.";

        return null;
    }

    private async Task<string?> ResolveTokenAsync(string repositoryPath, CancellationToken ct)
    {
        var settings = settingsStore.Current;
        if (settings.AiUseDedicatedCopilotToken)
            return await tokenStore.GetTokenAsync(DedicatedTokenHost, DedicatedTokenLogin, ct).ConfigureAwait(false);

        var binding = settings.RepositoryBindings.FirstOrDefault(b =>
            string.Equals(b.LocalPath, repositoryPath, StringComparison.OrdinalIgnoreCase));
        if (binding is null)
            return null;

        var account = settings.Accounts.FirstOrDefault(a =>
            string.Equals(a.Host, binding.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Login, binding.AccountLogin, StringComparison.OrdinalIgnoreCase));
        if (account is null)
            return null;

        return await tokenStore.GetTokenAsync(account.Host, account.Login, ct).ConfigureAwait(false);
    }

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
            return t;

        var firstNl = t.IndexOf('\n');
        if (firstNl < 0)
            return t;

        t = t[(firstNl + 1)..];
        if (t.EndsWith("```", StringComparison.Ordinal))
            t = t[..^3];
        return t.Trim();
    }
}
