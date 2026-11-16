using System;
using System.Windows;
using System.Windows.Media;

namespace nVielLauncher.Services;

/// <summary>Writes the built-in or user-supplied geometry of every launcher icon.</summary>
public static class IconTheme
{
    public static void Apply(ResourceDictionary resources, LauncherSettings settings)
    {
        var overrides = settings.IconOverrides;

        foreach (var icon in IconCatalog.All)
        {
            Geometry geometry;

            if (overrides is not null
                && overrides.TryGetValue(icon.Key, out var custom)
                && TryParse(custom, out var parsed))
            {
                geometry = parsed!;
            }
            else
            {
                geometry = IconCatalog.CreateGeometry(icon.Key);
            }

            if (geometry.CanFreeze) geometry.Freeze();
            resources[icon.Key] = geometry;
        }
    }

    /// <summary>Validates a user-supplied SVG path. Empty or broken paths are rejected.</summary>
    public static bool TryParse(string? pathData, out Geometry? geometry)
    {
        geometry = null;
        if (string.IsNullOrWhiteSpace(pathData)) return false;

        try
        {
            var parsed = Geometry.Parse(pathData.Trim());
            if (parsed.IsEmpty()) return false;
            geometry = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
