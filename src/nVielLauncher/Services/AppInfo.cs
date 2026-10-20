using System.IO;
using System.Reflection;

namespace nVielLauncher.Services;

/// <summary>Single source of truth for the product name, version and repositories.</summary>
public static class AppInfo
{
    public const string ProductName = "nViel Launcher";
    public const string GitHubOwner = "ghe-company-official";
    public const string GitHubRepository = "nVielLauncher";

    /// <summary>Private repository that stores release assets (the launcher exe).</summary>
    public const string ReleasesRepository = "nVielLauncher-releases";

    public static Version Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>Version in the form 1.2.3 (without the revision).</summary>
    public static string VersionLabel { get; } =
        $"{Version.Major}.{Version.Minor}.{Math.Max(Version.Build, 0)}";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "nVielLauncher.exe");
}
