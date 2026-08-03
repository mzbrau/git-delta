using CodeReviewr.AI.Agent;
using NUnit.Framework;

namespace CodeReviewr.AI.Tests;

public sealed class AgentPermissionPolicyTests
{
    [TestCase("read")]
    [TestCase("glob")]
    [TestCase("grep")]
    [TestCase("view")]
    public void Evaluate_ReadStyleKinds_AreApproved(string kind)
    {
        var policy = new AgentPermissionPolicy();

        var decision = policy.Evaluate(new AgentPermissionRequest(kind, null, "src/App.cs", null, "{}"));

        Assert.That(decision, Is.EqualTo(AgentPermissionDecision.Approve));
        Assert.That(policy.Denials, Is.Empty);
    }

    [TestCase("shell")]
    [TestCase("write")]
    [TestCase("edit")]
    [TestCase("custom_tool")]
    [TestCase("mcp")]
    [TestCase("url")]
    public void Evaluate_NonReadKinds_AreDenied(string kind)
    {
        var policy = new AgentPermissionPolicy();

        var decision = policy.Evaluate(new AgentPermissionRequest(kind, "some_tool", null, "rm -rf /", "{}"));

        Assert.That(decision, Is.EqualTo(AgentPermissionDecision.Deny));
    }

    [TestCase(".env")]
    [TestCase(".env.local")]
    [TestCase("secrets.pem")]
    [TestCase("id_rsa")]
    [TestCase("id_ed25519")]
    [TestCase("credentials.json")]
    [TestCase("secrets.json")]
    [TestCase("private.key")]
    [TestCase("cert.p12")]
    [TestCase("cert.pfx")]
    public void Evaluate_BuiltInDenylistedPaths_AreDenied(string fileName)
    {
        var policy = new AgentPermissionPolicy();

        var decision = policy.Evaluate(new AgentPermissionRequest("read", null, $"config/{fileName}", null, "{}"));

        Assert.That(decision, Is.EqualTo(AgentPermissionDecision.Deny));
    }

    [Test]
    public void Evaluate_ReadOfOrdinaryPath_IsApproved()
    {
        var policy = new AgentPermissionPolicy();

        var decision = policy.Evaluate(new AgentPermissionRequest("read", null, "src/Program.cs", null, "{}"));

        Assert.That(decision, Is.EqualTo(AgentPermissionDecision.Approve));
    }

    [Test]
    public void Evaluate_UserDenylistPattern_IsDenied()
    {
        var policy = new AgentPermissionPolicy(userPathDenylist: ["*internal-notes*"]);

        var decision = policy.Evaluate(new AgentPermissionRequest("read", null, "docs/internal-notes.md", null, "{}"));

        Assert.That(decision, Is.EqualTo(AgentPermissionDecision.Deny));
    }

    [Test]
    public void Evaluate_UserDenylist_DoesNotAffectUnrelatedPaths()
    {
        var policy = new AgentPermissionPolicy(userPathDenylist: ["*internal-notes*"]);

        var decision = policy.Evaluate(new AgentPermissionRequest("read", null, "docs/public.md", null, "{}"));

        Assert.That(decision, Is.EqualTo(AgentPermissionDecision.Approve));
    }

    [Test]
    public void Denials_RecordsDeniedRequests_ButNotApprovedOnes()
    {
        var policy = new AgentPermissionPolicy();

        policy.Evaluate(new AgentPermissionRequest("read", null, "src/App.cs", null, "{}"));
        policy.Evaluate(new AgentPermissionRequest("shell", null, null, "ls", "{}"));
        policy.Evaluate(new AgentPermissionRequest("read", null, ".env", null, "{}"));

        Assert.That(policy.Denials, Has.Count.EqualTo(2));
        Assert.That(policy.Denials[0].Kind, Is.EqualTo("shell"));
        Assert.That(policy.Denials[1].Path, Is.EqualTo(".env"));
    }

    [Test]
    public void Evaluate_ReadWithNoPath_IsApproved()
    {
        var policy = new AgentPermissionPolicy();

        var decision = policy.Evaluate(new AgentPermissionRequest("read", null, null, null, "{}"));

        Assert.That(decision, Is.EqualTo(AgentPermissionDecision.Approve));
    }
}
