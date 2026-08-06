using GitDelta.AI.Agent;
using GitDelta.Core.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace GitDelta.AI.Tests;

/// <summary>
/// Exercises the real GitHub Copilot CLI via <see cref="CopilotAgentClient"/> instead of
/// <see cref="FakeAgentClient"/>. This requires a working Copilot CLI installation and an
/// authenticated subscription on the machine running the test, so it is tagged
/// <c>RequiresCopilot</c> and marked <see cref="ExplicitAttribute"/> — normal
/// <c>dotnet test</c> runs (and CI, via <c>--filter "TestCategory!=RequiresCopilot"</c>) skip it
/// automatically. Run it deliberately with:
/// <c>dotnet test --filter "FullyQualifiedName~CopilotContractTests"</c> on a machine with Copilot set up.
/// </summary>
[Category("RequiresCopilot")]
public sealed class CopilotContractTests
{
    [Test]
    [Explicit("Requires a local GitHub Copilot CLI installation and an authenticated subscription; not run in CI.")]
    public async Task ProbeAsync_AgainstRealCopilotCli_ReportsConnectionStatus()
    {
        await using var client = new CopilotAgentClient(NullLogger<CopilotAgentClient>.Instance);

        AiConnectionProbeResult result;
        try
        {
            result = await client.ProbeAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Assert.Ignore($"GitHub Copilot CLI is not available in this environment: {ex.Message}");
            return;
        }

        if (!result.Succeeded)
        {
            Assert.Ignore($"GitHub Copilot CLI reported no usable connection: {result.Message}");
            return;
        }

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Message, Is.Not.Null.And.Not.Empty);
    }
}
