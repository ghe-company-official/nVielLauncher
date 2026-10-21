using System.IO;
using System.Reflection;

namespace nVielLauncher.Services;

/// <summary>Single source of truth for the product name, version and repositories.</summary>
public static class AppInfo
{
    public const string ProductName = "nViel Launcher";
    public const string Edition = "Global";
    public const string GitHubOwner = "ghe-company-official";
    public const string GitHubRepository = "nVielLauncher";

    /// <summary>Private repository that stores release assets (the launcher exe).</summary>
    public const string ReleasesRepository = "nVielLauncher-releases";

    /// <summary>
    /// Public half of the detached release-signing key. The private half is kept
    /// outside the repository and is only used by build-release.ps1.
    /// </summary>
    public const string UpdatePublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9OwcU4x/XP1hWa6VW9f8tOCnqbxGiMteyxlC3DgtvwpNVMKhAtTH6z/cZsDUfilAz5YFvHyDXA4gHViF14TfOg==";

    public static Version Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>Version in the form 1.2.3 (without the revision).</summary>
    public static string VersionLabel { get; } = VersionUtil.Format(Version);

    public static string DisplayVersion { get; } = VersionLabel + " " + Edition;

    public static string DisplayName { get; } = ProductName + " " + DisplayVersion;

    public static string FormatDisplay(Version version) => VersionUtil.Format(version) + " " + Edition;

    public static string FormatDisplay(string version) => version + " " + Edition;

    /// <summary>Optional zero-touch relay. When set, no GitHub credential is needed.</summary>
    public static string UpdateProxyUrl =>
        (Environment.GetEnvironmentVariable("NVIEL_UPDATE_PROXY") ?? "").Trim().TrimEnd('/');

    public static string UpdateChannel { get; } =
        string.Equals(Environment.GetEnvironmentVariable("NVIEL_UPDATE_CHANNEL"), "beta",
            StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "nVielLauncher.exe");
}
