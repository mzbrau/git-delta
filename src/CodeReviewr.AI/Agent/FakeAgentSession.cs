namespace CodeReviewr.AI.Agent;

/// <summary>One scripted turn: the tool calls (if any) to replay, assistant text to stream, and an
/// optional hook that runs before any tool calls — e.g. to block until the turn is cancelled.</summary>
internal readonly record struct FakeAgentTurn(
    IReadOnlyList<AgentToolCall> ToolCalls,
    string? AssistantText,
    Func<CancellationToken, Task>? BeforeCalls,
    TimeSpan? DelayEachTool);

/// <summary>Fluent builder for a single <see cref="FakeAgentTurn"/>, passed to <see cref="FakeAgentScript.OnTurn"/>.</summary>
internal sealed class FakeAgentTurnBuilder
{
    private readonly List<AgentToolCall> _toolCalls = [];
    private string? _assistantText;
    private Func<CancellationToken, Task>? _beforeCalls;
    private TimeSpan? _delayEachTool;

    /// <summary>Queues a tool call the fake agent "makes" during this turn.</summary>
    public FakeAgentTurnBuilder Call(string toolName, string argumentsJson)
    {
        _toolCalls.Add(new AgentToolCall(toolName, argumentsJson, ResultJson: null));
        return this;
    }

    /// <summary>Streams assistant text for this turn (raised via <see cref="IAgentSession.AssistantDelta"/>).</summary>
    public FakeAgentTurnBuilder Text(string assistantText)
    {
        _assistantText = assistantText;
        return this;
    }

    /// <summary>Runs before any tool calls in this turn.</summary>
    public FakeAgentTurnBuilder BeforeCalls(Func<CancellationToken, Task> action)
    {
        _beforeCalls = action;
        return this;
    }

    /// <summary>
    /// Delays after raising tool-start activity and before invoking the tool handler — used to
    /// verify idle timeout pauses while a tool is in flight.
    /// </summary>
    public FakeAgentTurnBuilder DelayEachTool(TimeSpan delay)
    {
        _delayEachTool = delay;
        return this;
    }

    /// <summary>
    /// Blocks the turn until its cancellation token fires — useful for deterministically testing
    /// mid-run cancellation without relying on real delays.
    /// </summary>
    public FakeAgentTurnBuilder BlockUntilCancelled(Action? onBlocking = null) =>
        BeforeCalls(ct =>
        {
            onBlocking?.Invoke();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

    internal FakeAgentTurn Build() => new(_toolCalls, _assistantText, _beforeCalls, _delayEachTool);
}

/// <summary>
/// A scripted sequence of turns a fake agent "replays" as <see cref="IAgentSession.SendTurnAsync"/>
/// is called repeatedly on the same session (e.g. triage, then file-depth, then chat). Each turn
/// names registered <see cref="AgentCustomTool"/>s to invoke and/or assistant text to stream; the
/// fake session looks up matching tools by name and runs their real handlers, so the rest of the
/// pipeline (persistence, progress, etc.) is exercised exactly as it would be against the real CLI.
/// </summary>
internal sealed class FakeAgentScript
{
    private readonly Queue<FakeAgentTurn> _turns = new();

    public static FakeAgentScript Empty { get; } = new();

    /// <summary>Queues one turn's worth of scripted behaviour, consumed the next time <c>SendTurnAsync</c> runs.</summary>
    public FakeAgentScript OnTurn(Action<FakeAgentTurnBuilder> configureTurn)
    {
        var builder = new FakeAgentTurnBuilder();
        configureTurn(builder);
        _turns.Enqueue(builder.Build());
        return this;
    }

    /// <summary>Convenience builder for a single scripted tool call, optionally with assistant text.</summary>
    public static FakeAgentScript ForToolCall(string toolName, string argumentsJson, string? assistantText = null) =>
        new FakeAgentScript().OnTurn(t =>
        {
            t.Call(toolName, argumentsJson);
            if (assistantText is not null)
                t.Text(assistantText);
        });

    /// <summary>Dequeues the next scripted turn, or an empty no-op turn once the script is exhausted.</summary>
    internal FakeAgentTurn NextTurn() =>
        _turns.Count > 0 ? _turns.Dequeue() : new FakeAgentTurn([], null, null, null);
}

/// <summary>
/// In-memory <see cref="IAgentSession"/> that replays a <see cref="FakeAgentScript"/> instead of
/// talking to a real Copilot CLI process. Used by tests.
/// </summary>
internal sealed class FakeAgentSession(
    string sessionId,
    FakeAgentScript script,
    AgentSessionOptions options) : IAgentSession
{
    public string SessionId { get; } = sessionId;

    /// <summary>Prompts passed to <see cref="SendTurnAsync"/> (test spy).</summary>
    public List<string> SentPrompts { get; } = [];

    public event Action<string, string>? ToolActivityStarted;

    public event Action<AgentToolCall>? ToolCallReceived;

    public event Action<string>? AssistantDelta;

    public async Task SendTurnAsync(string prompt, TimeSpan? waitTimeout = null, CancellationToken ct = default)
    {
        _ = waitTimeout;
        SentPrompts.Add(prompt);
        var turn = script.NextTurn();

        if (turn.BeforeCalls is not null)
            await turn.BeforeCalls(ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(turn.AssistantText))
            AssistantDelta?.Invoke(turn.AssistantText);

        foreach (var call in turn.ToolCalls)
        {
            ct.ThrowIfCancellationRequested();

            ToolActivityStarted?.Invoke(call.Name, call.ArgumentsJson);

            if (turn.DelayEachTool is { } delay)
                await Task.Delay(delay, ct).ConfigureAwait(false);

            var tool = options.Tools.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.Ordinal));
            var resultJson = call.ResultJson;
            if (tool is not null)
                resultJson = await tool.Handler(call.ArgumentsJson, ct).ConfigureAwait(false);

            ToolCallReceived?.Invoke(call with { ResultJson = resultJson });
        }
    }

    public Task AbortAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
