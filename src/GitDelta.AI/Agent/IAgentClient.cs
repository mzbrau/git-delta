using GitDelta.Core.AI;

namespace GitDelta.AI.Agent;

/// <summary>
/// Internal test seam over the underlying agent runtime (GitHub Copilot CLI). The only production
/// implementation is <see cref="CopilotAgentClient"/>; tests use <see cref="FakeAgentClient"/>.
/// </summary>
internal interface IAgentClient : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct = default);

    Task<IAgentSession> CreateSessionAsync(AgentSessionOptions options, CancellationToken ct = default);

    Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionOptions options, CancellationToken ct = default);

    Task<AiConnectionProbeResult> ProbeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>Options used to create or resume an agent session, independent of the underlying SDK.</summary>
internal sealed record AgentSessionOptions(
    string Cwd,
    string? GitHubToken,
    string? Model,
    string? ReasoningEffort,
    IReadOnlyList<AgentCustomTool> Tools,
    Func<AgentPermissionRequest, AgentPermissionDecision> OnPermissionRequest,
    bool Streaming = true);

/// <summary>
/// A host-defined tool exposed to the agent. <paramref name="Handler"/> receives the raw JSON
/// arguments the model supplied and returns a raw JSON result string.
/// </summary>
internal sealed record AgentCustomTool(
    string Name,
    string Description,
    Func<string, CancellationToken, Task<string>> Handler);

/// <summary>A permission prompt raised by the agent before executing a tool, normalised across SDKs.</summary>
internal sealed record AgentPermissionRequest(
    string Kind,
    string? ToolName,
    string? Path,
    string? Command,
    string RawJson);

internal enum AgentPermissionDecision
{
    Approve,
    Deny,
}
