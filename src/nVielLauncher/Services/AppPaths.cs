using System.IO;

namespace nVielLauncher.Services;

/// <summary>All launcher paths live here. Nothing outside this class hardcodes folders.</summary>
public static class AppPaths
{
    public const string PortableFlagName = "portable.flag";

    private static readonly bool Portable = DetectPortable();

    public static bool IsPortable => Portable;

    public static string ExecutableDirectory => AppContext.BaseDirectory;

    public static string PortableFlagFile => Path.Combine(ExecutableDirectory, PortableFlagName);

    public static string AppRoot { get; } = Portable
        ? Path.Combine(ExecutableDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nViel Launcher");

    public static string SettingsFile => Path.Combine(AppRoot, "settings.json");
    public static string LogsDirectory => Path.Combine(AppRoot, "logs");
    public static string ReportsDirectory => Path.Combine(AppRoot, "reports");
    public static string MigrationDirectory => Path.Combine(AppRoot, "migration");
    public static string MigrationBackupsDirectory => Path.Combine(MigrationDirectory, "backups");
    public static string JavaDirectory => Path.Combine(AppRoot, "java");
    public static string UpdateDirectory => Path.Combine(AppRoot, "update");
    public static string ModrinthStateFile => Path.Combine(AppRoot, "modrinth-state.json");

    public static string DefaultGameDirectory { get; } = Portable
        ? Path.Combine(AppRoot, "game")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".nviel");

    private static string? _bundledMods;

    /// <summary>
    /// Finds the folder the user drops mods into: next to the exe first, then up the tree
    /// (so running from bin/Debug still picks up the repo's mods folder).
    /// </summary>
    public static string BundledModsDirectory => _bundledMods ??= ResolveModsDirectory();

    private static bool DetectPortable()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("NVIEL_PORTABLE"), "1", StringComparison.Ordinal))
            return true;

        try
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, PortableFlagName)))
                return true;
        }
        catch
        {
            // unreadable folder: use the normal per-user location
        }

        return Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--portable", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveModsDirectory()
    {
        var exeMods = Path.Combine(ExecutableDirectory, "mods");

        // Installed builds live in %APPDATA%\.nviel\app — right above the game's own mods
        // folder. Walking up from there would mistake game mods for bundled ones, so only
        // dev builds (which have nVielLauncher.dll next to the exe) search the tree.
        if (!File.Exists(Path.Combine(ExecutableDirectory, "nVielLauncher.dll")))
            return exeMods;

        var candidates = new List<string> { exeMods };

        var dir = new DirectoryInfo(ExecutableDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, "mods"));
            dir = dir.Parent;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.jar").Any())
                    return candidate;
            }
            catch
            {
                // ignored: unreadable folder, keep looking
            }
        }

        return candidates.FirstOrDefault(Directory.Exists) ?? exeMods;
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ReportsDirectory);
        Directory.CreateDirectory(MigrationBackupsDirectory);
        Directory.CreateDirectory(JavaDirectory);
        Directory.CreateDirectory(UpdateDirectory);
    }
}
