using System.Text.Json;
using System.Text.Json.Serialization;

namespace nVielLauncher.Services;

public sealed record LaunchProgress(double Percent, string Status, string? Detail = null, string? Log = null);

public sealed class VersionManifest
{
    public List<VersionSummary>? Versions { get; set; }
}

public sealed class VersionSummary
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class VersionDetails
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string MainClass { get; set; } = "";
    public string? InheritsFrom { get; set; }
    public string? Assets { get; set; }
    public AssetIndexInfo? AssetIndex { get; set; }
    public VersionDownloads? Downloads { get; set; }
    public List<LibraryInfo>? Libraries { get; set; }
    public VersionArguments? Arguments { get; set; }
    public JavaVersionInfo? JavaVersion { get; set; }
    public LoggingInfo? Logging { get; set; }
}

public sealed class VersionDownloads
{
    public DownloadArtifact? Client { get; set; }
}

public sealed class AssetIndexInfo
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
}

public sealed class DownloadArtifact
{
    public string? Id { get; set; }
    public string Url { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Path { get; set; }
}

public sealed class LibraryInfo
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public LibraryDownloads? Downloads { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public List<RuleInfo>? Rules { get; set; }
    public ExtractInfo? Extract { get; set; }

    /// <summary>
    /// group:artifact[:classifier] — de-duplicates base vs Fabric libraries while keeping
    /// native classifiers (e.g. com.mojang:jtracy vs com.mojang:jtracy:natives-windows) apart.
    /// </summary>
    [JsonIgnore]
    public string ModuleKey
    {
        get
        {
            var parts = Name.Split(':');
            if (parts.Length < 2) return Name;

            var key = parts[0] + ":" + parts[1];
            if (parts.Length >= 4)
            {
                var classifier = parts[3];
                var at = classifier.IndexOf('@');
                if (at >= 0) classifier = classifier[..at];
                if (classifier.Length > 0) key += ":" + classifier;
            }

            return key;
        }
    }
}

public sealed class LibraryDownloads
{
    public DownloadArtifact? Artifact { get; set; }
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public sealed class RuleInfo
{
    public string Action { get; set; } = "allow";
    public OsInfo? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsInfo
{
    public string? Name { get; set; }
    public string? Arch { get; set; }
    public string? Version { get; set; }
}

public sealed class ExtractInfo
{
    public List<string>? Exclude { get; set; }
}

public sealed class VersionArguments
{
    public List<JsonElement>? Game { get; set; }
    public List<JsonElement>? Jvm { get; set; }
}

public sealed class JavaVersionInfo
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public sealed class LoggingInfo
{
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    public string Argument { get; set; } = "";
    public DownloadArtifact? File { get; set; }
}

public sealed class AssetIndex
{
    public Dictionary<string, AssetObject>? Objects { get; set; }
}

public sealed class AssetObject
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
}

public sealed class FabricLoaderEntry
{
    public FabricLoaderInfo? Loader { get; set; }
}

public sealed class FabricLoaderInfo
{
    public string Version { get; set; } = "";
    public bool Stable { get; set; }
}

public sealed class LaunchPlan
{
    public required VersionDetails BaseVersion { get; init; }
    public required VersionDetails FabricProfile { get; init; }
    public required string VersionName { get; init; }
    public required string GameDirectory { get; init; }
    public required string RuntimeDirectory { get; init; }
    public required string ClientJar { get; init; }
    public required List<string> Classpath { get; init; }
    public required string NativesDirectory { get; init; }
    public required string AssetsDirectory { get; init; }
    public required string AssetIndexName { get; init; }
    public string? LogConfigPath { get; init; }
    public int RequiredJavaMajor { get; init; } = 21;
}
