using CodeReviewr.Core;
using NUnit.Framework;

namespace CodeReviewr.Core.Tests;

public sealed class RepositoryPathResolverTests
{
    [Test]
    public void ResolveUnderRoot_Accepts_Nested_Relative_Path()
    {
        var root = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resolved = RepositoryPathResolver.ResolveUnderRoot(root, "src/App.cs");
            Assert.That(resolved, Does.StartWith(Path.GetFullPath(root)));
            Assert.That(resolved.Replace('\\', '/'), Does.EndWith("src/App.cs"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveUnderRoot_Rejects_Traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                RepositoryPathResolver.ResolveUnderRoot(root, "../outside.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
