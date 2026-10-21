using System.IO;

namespace nVielLauncher.Services;

/// <summary>
/// The updater starts the new build with --update-health &lt;marker&gt;. The new build
/// writes that marker only after its main window is alive; otherwise the detached
/// helper restores the previous executable.
/// </summary>
public static class UpdateHealth
{
    public const string ArgumentName = "--update-health";

    public static string? GetMarkerPath(string[]? args)
    {
        if (args is null) return null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], ArgumentName, StringComparison.OrdinalIgnoreCase)) continue;

            var value = args[i + 1];
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    public static bool TryMarkHealthy(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(AppPaths.AppRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "ok " + AppInfo.Version + " " + DateTime.UtcNow.ToString("O"));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
