using System.Xml.Linq;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class AppManifestTests
{
    [Test]
    public void AppManifest_Root_Is_Assembly_With_ManifestVersion()
    {
        var path = LocateAppManifest();
        Assert.That(File.Exists(path), Is.True, $"Expected app.manifest at {path}");

        var doc = XDocument.Load(path);
        var root = doc.Root;
        Assert.That(root, Is.Not.Null);
        Assert.That(root!.Name.LocalName, Is.EqualTo("assembly"),
            "Win32 application manifests must use an <assembly> root; <manifest> causes SxS error 14001.");
        Assert.That(root.Attribute("manifestVersion")?.Value, Is.EqualTo("1.0"));
    }

    private static string LocateAppManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "GitDelta.App", "app.manifest");
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/GitDelta.App/app.manifest by walking up from the test base directory.");
    }
}
