using CodeReviewr.Core.AI;

namespace CodeReviewr.AI.Agent;

/// <summary>
/// In-memory <see cref="IAgentClient"/> that never spawns a real Copilot CLI process. The supplied
/// factory decides which <see cref="FakeAgentScript"/> a session should replay, based on the
/// session options it was created with (so tests can script different behaviour per PR/model/etc.).
/// </summary>
internal sealed class FakeAgentClient(Func<AgentSessionOptions, FakeAgentScript> scriptFactory) : IAgentClient
{
    public FakeAgentClient(FakeAgentScript script) : this(_ => script)
    {
    }

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IAgentSession> CreateSessionAsync(AgentSessionOptions options, CancellationToken ct = default)
    {
        IAgentSession session = new FakeAgentSession(Guid.NewGuid().ToString("N"), scriptFactory(options), options);
        return Task.FromResult(session);
    }

    public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionOptions options, CancellationToken ct = default)
    {
        IAgentSession session = new FakeAgentSession(sessionId, scriptFactory(options), options);
        return Task.FromResult(session);
    }

    public Task<AiConnectionProbeResult> ProbeAsync(CancellationToken ct = default) =>
        Task.FromResult(new AiConnectionProbeResult(true, "Fake agent connected."));

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(["default"]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
