using NUnit.Framework;

namespace CodeReviewr.App.Tests;

public sealed class AppArchitectureTests
{
    [Test]
    public void App_Must_Not_Reference_CliWrap_Directly()
    {
        var refs = typeof(CodeReviewr.App.App).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        Assert.That(refs, Does.Not.Contain("CliWrap"));
    }

    [Test]
    public void App_Must_Not_Use_IGitProcessRunner()
    {
        var appAsm = typeof(CodeReviewr.App.App).Assembly;
        var runnerType = typeof(CodeReviewr.Git.IGitProcessRunner);
        var offenders = appAsm.GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters().Select(p => (Type: t, Param: p.ParameterType))))
            .Where(x => runnerType.IsAssignableFrom(x.Param))
            .Select(x => x.Type.FullName)
            .Distinct()
            .ToList();

        Assert.That(offenders, Is.Empty,
            "App types must not take IGitProcessRunner: " + string.Join(", ", offenders));
    }
}
