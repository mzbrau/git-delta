namespace GitDelta.AI.Agent;

/// <summary>A single conversation session with the agent runtime.</summary>
internal interface IAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    /// <summary>
    /// Sends a prompt and awaits completion of that turn (including any tool calls it triggers).
    /// <paramref name="waitTimeout"/> is the SDK wait ceiling (required for Copilot — null means 60s).
    /// Prefer a large value when an idle watchdog owns cancellation.
    /// </summary>
    Task SendTurnAsync(string prompt, TimeSpan? waitTimeout = null, CancellationToken ct = default);

    Task AbortAsync(CancellationToken ct = default);

    /// <summary>Raised when a tool invocation begins (before the handler runs).</summary>
    event Action<string, string>? ToolActivityStarted;

    /// <summary>Raised once per tool invocation, after the tool's handler has produced a result.</summary>
    event Action<AgentToolCall>? ToolCallReceived;

    /// <summary>Raised for streamed assistant text as it is produced.</summary>
    event Action<string>? AssistantDelta;
}

/// <summary>A completed tool call: the arguments the model supplied and the JSON result returned to it.</summary>
internal sealed record AgentToolCall(string Name, string ArgumentsJson, string? ResultJson);
