using System.ComponentModel;
using System.Text.Json;
using GitDelta.Core;
using GitDelta.Core.AI;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace GitDelta.AI.Agent;

/// <summary>
/// The only file in this project allowed to reference the GitHub Copilot SDK. Wraps
/// <see cref="CopilotClient"/>/<see cref="CopilotSession"/> behind the SDK-agnostic
/// <see cref="IAgentClient"/>/<see cref="IAgentSession"/> seam so the rest of the AI project — and
/// all of its tests — never need to touch <c>GitHub.Copilot</c> types directly.
/// </summary>
internal sealed class CopilotAgentClient(ILogger<CopilotAgentClient> logger) : IAgentClient
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CopilotClient? _client;
    private volatile bool _started;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started)
            return;

        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started)
                return;

            _client = new CopilotClient();
            await _client.StartAsync(ct).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<IAgentSession> CreateSessionAsync(AgentSessionOptions options, CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);

        var wrapper = new CopilotAgentSession(options.Streaming);
        var config = new SessionConfig
        {
            WorkingDirectory = options.Cwd,
            GitHubToken = options.GitHubToken,
            Model = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            Streaming = options.Streaming,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = true },
            Tools = BuildTools(options.Tools, wrapper),
            OnPermissionRequest = (request, _) => Task.FromResult(MapDecision(options.OnPermissionRequest, request)),
        };

        var session = await _client!.CreateSessionAsync(config, ct).ConfigureAwait(false);
        wrapper.Attach(session);
        return wrapper;
    }

    public async Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionOptions options, CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);

        var wrapper = new CopilotAgentSession(options.Streaming);
        var config = new ResumeSessionConfig
        {
            WorkingDirectory = options.Cwd,
            GitHubToken = options.GitHubToken,
            Model = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            Streaming = options.Streaming,
            Tools = BuildTools(options.Tools, wrapper),
            OnPermissionRequest = (request, _) => Task.FromResult(MapDecision(options.OnPermissionRequest, request)),
        };

        var session = await _client!.ResumeSessionAsync(sessionId, config, ct).ConfigureAwait(false);
        wrapper.Attach(session);
        return wrapper;
    }

    public async Task<AiConnectionProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await StartAsync(ct).ConfigureAwait(false);
            var auth = await _client!.GetAuthStatusAsync(ct).ConfigureAwait(false);

            if (!auth.IsAuthenticated)
            {
                return new AiConnectionProbeResult(
                    Succeeded: false,
                    Message: auth.StatusMessage is { Length: > 0 } msg
                        ? msg
                        : "GitHub Copilot is not authenticated. Sign in via the Copilot CLI or provide a dedicated token.",
                    NeedsDedicatedToken: true);
            }

            var who = auth.Login is { Length: > 0 } login ? $" as {login}" : "";
            return new AiConnectionProbeResult(true, $"Connected to GitHub Copilot{who}.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GitHub Copilot connection probe failed.");
            return new AiConnectionProbeResult(false, $"Could not start the GitHub Copilot CLI: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        var models = await _client!.ListModelsAsync(ct).ConfigureAwait(false);
        return [.. models.Select(m => m.Id).Where(id => !string.IsNullOrWhiteSpace(id))];
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error stopping the GitHub Copilot CLI during shutdown.");
            }

            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _startLock.Dispose();
    }

    private static ICollection<AIFunctionDeclaration> BuildTools(
        IReadOnlyList<AgentCustomTool> tools,
        CopilotAgentSession wrapper)
    {
        var declarations = new List<AIFunctionDeclaration>(tools.Count);
        foreach (var tool in tools)
        {
            var name = tool.Name;
            var handler = tool.Handler;

            Func<string, CancellationToken, Task<string>> invoke =
                async ([Description("JSON-encoded arguments for this tool call.")] string argsJson, CancellationToken ct) =>
                {
                    wrapper.RaiseToolActivityStarted(name, argsJson);
                    var result = await handler(argsJson, ct).ConfigureAwait(false);
                    wrapper.RaiseToolCallReceived(new AgentToolCall(name, argsJson, result));
                    return result;
                };

            var function = CopilotTool.DefineTool(
                invoke,
                toolOptions: new CopilotToolOptions { SkipPermission = true },
                factoryOptions: new AIFunctionFactoryOptions { Name = tool.Name, Description = tool.Description });

            declarations.Add(function);
        }

        return declarations;
    }

    private static PermissionDecision MapDecision(
        Func<AgentPermissionRequest, AgentPermissionDecision> policy,
        PermissionRequest request)
    {
        var mapped = MapPermissionRequest(request);
        var decision = policy(mapped);
        return decision == AgentPermissionDecision.Approve
            ? PermissionDecision.ApproveOnce()
            : PermissionDecision.Reject($"Denied by {ProductInfo.DisplayName}'s AI permission policy.");
    }

    private static AgentPermissionRequest MapPermissionRequest(PermissionRequest request)
    {
        var rawJson = TrySerialize(request);
        return request switch
        {
            PermissionRequestRead r => new AgentPermissionRequest("read", null, r.Path, null, rawJson),
            PermissionRequestWrite w => new AgentPermissionRequest("write", null, w.FileName, null, rawJson),
            PermissionRequestShell s => new AgentPermissionRequest("shell", null, null, s.FullCommandText, rawJson),
            PermissionRequestCustomTool c => new AgentPermissionRequest("custom_tool", c.ToolName, null, null, rawJson),
            PermissionRequestUrl u => new AgentPermissionRequest("url", null, u.Url, null, rawJson),
            PermissionRequestMcp m => new AgentPermissionRequest("mcp", m.ToolName, null, null, rawJson),
            _ => new AgentPermissionRequest(request.Kind ?? "unknown", null, null, null, rawJson),
        };
    }

    private static string TrySerialize(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value.GetType());
        }
        catch (Exception)
        {
            return "{}";
        }
    }
}

/// <summary>Adapts a live <see cref="CopilotSession"/> to <see cref="IAgentSession"/>.</summary>
internal sealed class CopilotAgentSession(bool streaming) : IAgentSession
{
    private CopilotSession? _session;
    private IDisposable? _subscription;

    public string SessionId => _session?.SessionId ?? string.Empty;

    public event Action<string, string>? ToolActivityStarted;

    public event Action<AgentToolCall>? ToolCallReceived;

    public event Action<string>? AssistantDelta;

    internal void Attach(CopilotSession session)
    {
        _session = session;
        _subscription = session.On<SessionEvent>(HandleEvent);
    }

    internal void RaiseToolActivityStarted(string name, string argumentsJson) =>
        ToolActivityStarted?.Invoke(name, argumentsJson);

    internal void RaiseToolCallReceived(AgentToolCall call) => ToolCallReceived?.Invoke(call);

    public async Task SendTurnAsync(string prompt, TimeSpan? waitTimeout = null, CancellationToken ct = default)
    {
        if (_session is null)
            throw new InvalidOperationException("The agent session has not been started.");

        // Copilot SDK treats timeout: null as 60s — always pass an explicit wait matching settings.
        await _session.SendAndWaitAsync(prompt, timeout: waitTimeout, ct).ConfigureAwait(false);
    }

    public async Task AbortAsync(CancellationToken ct = default)
    {
        if (_session is not null)
            await _session.AbortAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        if (_session is not null)
            await _session.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleEvent(SessionEvent evt)
    {
        switch (evt)
        {
            case AssistantMessageDeltaEvent delta when streaming:
                if (!string.IsNullOrEmpty(delta.Data?.DeltaContent))
                    AssistantDelta?.Invoke(delta.Data.DeltaContent);
                break;

            case AssistantMessageEvent msg when !streaming:
                if (!string.IsNullOrEmpty(msg.Data?.Content))
                    AssistantDelta?.Invoke(msg.Data.Content);
                break;
        }
    }
}
