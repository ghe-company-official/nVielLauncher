namespace nVielLauncher.Services;

/// <summary>Version parsing/formatting shared by the updater, installer and tests.</summary>
public static class VersionUtil
{
    public static Version? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) text = text[..cut];

        return Version.TryParse(text, out var version) ? version : null;
    }

    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>1.0.2.0 → 1.0.2, 1.0.2.1 → 1.0.2.1.</summary>
    public static string Format(Version version)
    {
        var normalized = Normalize(version);
        if (normalized.Revision > 0)
            return $"{normalized.Major}.{normalized.Minor}.{normalized.Build}.{normalized.Revision}";

        if (normalized.Build > 0)
            return $"{normalized.Major}.{normalized.Minor}.{normalized.Build}";

        return $"{normalized.Major}.{normalized.Minor}";
    }

    public static bool IsNewer(Version remote, Version current) =>
        Normalize(remote) > Normalize(current);
}
