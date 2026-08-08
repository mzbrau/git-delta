using System.Reflection;

namespace GitDelta.Core;

/// <summary>Product branding constants.</summary>
public static class ProductInfo
{
    /// <summary>PascalCase product id used in code, assemblies, and OS store folders.</summary>
    public const string Name = "GitDelta";

    /// <summary>In-app product name (window titles, dialogs, chrome).</summary>
    public const string DisplayName = "GIT DELTA";

    /// <summary>OS shell product name (installer, Start menu, .app bundle).</summary>
    public const string ShellDisplayName = "Git Delta";

    /// <summary>MinVer / assembly informational version without <c>+</c> build metadata.</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>User-visible version label matching git tag style (e.g. <c>v0.3.1</c>).</summary>
    public static string VersionLabel =>
        Version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? Version : $"v{Version}";

    private static string ResolveVersion()
    {
        var info = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(info))
            info = typeof(ProductInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        var plus = info.IndexOf('+');
        if (plus >= 0)
            info = info[..plus];

        return info;
    }
}
