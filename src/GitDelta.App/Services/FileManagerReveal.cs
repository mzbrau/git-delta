using System.Diagnostics;

namespace GitDelta.App.Services;

/// <summary>Opens the OS file manager and selects a path when possible.</summary>
public static class FileManagerReveal
{
    public static string Label =>
        OperatingSystem.IsMacOS() ? "Show in Finder"
        : OperatingSystem.IsWindows() ? "Show in Explorer"
        : "Show in File Manager";

    public static void Reveal(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return;

        absolutePath = Path.GetFullPath(absolutePath);

        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { "-R", absolutePath },
                UseShellExecute = false,
            });
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{absolutePath}\"",
                UseShellExecute = true,
            });
            return;
        }

        var directory = File.Exists(absolutePath)
            ? Path.GetDirectoryName(absolutePath)
            : absolutePath;
        if (string.IsNullOrEmpty(directory))
            directory = absolutePath;

        Process.Start(new ProcessStartInfo
        {
            FileName = "xdg-open",
            ArgumentList = { directory },
            UseShellExecute = false,
        });
    }
}
