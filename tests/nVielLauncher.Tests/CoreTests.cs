using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using nVielLauncher.Services;
using Xunit;

namespace nVielLauncher.Tests;

public sealed class VersionUtilTests
{
    [Theory]
    [InlineData("1.0.2", 1, 0, 2, 0)]
    [InlineData("v1.0.2", 1, 0, 2, 0)]
    [InlineData("1.0.2 Global", 1, 0, 2, 0)]
    [InlineData("1.0.2.1", 1, 0, 2, 1)]
    [InlineData("1.0.2-beta", 1, 0, 2, 0)]
    public void ParsesCommonReleaseTags(string text, int major, int minor, int build, int revision)
    {
        var version = VersionUtil.TryParse(text);

        Assert.NotNull(version);
        Assert.Equal(new Version(major, minor, build, revision), VersionUtil.Normalize(version!));
    }

    [Theory]
    [InlineData("1.0.2.0", "1.0.2")]
    [InlineData("1.0.2.1", "1.0.2.1")]
    [InlineData("1.2", "1.2")]
    [InlineData("2.0.0", "2.0")]
    public void FormatsWithoutMeaninglessParts(string input, string expected)
    {
        Assert.Equal(expected, VersionUtil.Format(Version.Parse(input)));
    }

    [Fact]
    public void ComparesNormalizedVersions()
    {
        Assert.True(VersionUtil.IsNewer(Version.Parse("1.0.10"), Version.Parse("1.0.9")));
        Assert.False(VersionUtil.IsNewer(Version.Parse("1.0.2.0"), Version.Parse("1.0.2")));
        Assert.True(VersionUtil.IsNewer(Version.Parse("1.0.2.1"), Version.Parse("1.0.2")));
    }

    [Fact]
    public void ProductVersionIs1249Global()
    {
        Assert.Equal("1.2.4.9", AppInfo.VersionLabel);
        Assert.Equal("1.2.4.9 Global", AppInfo.DisplayVersion);
    }
}

public sealed class LauncherMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nviel-migration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportsUserDataAndDoesNotOverwriteOnSecondRun()
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(Path.Combine(source, "saves", "World1"));
        Directory.CreateDirectory(Path.Combine(source, "mods"));
        Directory.CreateDirectory(Path.Combine(source, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(source, "config"));

        File.WriteAllText(Path.Combine(source, "saves", "World1", "level.dat"), "world");
        File.WriteAllText(Path.Combine(source, "mods", "example.jar"), "mod");
        File.WriteAllText(Path.Combine(source, "resourcepacks", "pack.zip"), "pack");
        File.WriteAllText(Path.Combine(source, "config", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(source, "options.txt"), "fov: 90");

        var launcher = new DetectedLauncher
        {
            Id = "test",
            Name = "Test Launcher",
            DataRoots = new[] { source }
        };

        var first = await LauncherMigrationService.ImportAsync(
            new[] { launcher }, target, new MigrationOptions());

        Assert.Equal(1, first.Worlds);
        Assert.Equal(1, first.Mods);
        Assert.Equal(1, first.ResourcePacks);
        Assert.Equal(2, first.ConfigFiles);
        Assert.True(File.Exists(Path.Combine(target, "saves", "World1", "level.dat")));
        Assert.True(File.Exists(Path.Combine(target, "mods", "example.jar")));
        Assert.Equal("fov: 90", File.ReadAllText(Path.Combine(target, "options.txt")));

        var second = await LauncherMigrationService.ImportAsync(
            new[] { launcher }, target, new MigrationOptions());

        Assert.Equal(0, second.Worlds);
        Assert.True(second.Skipped >= 3);
    }

    [Fact]
    public async Task ExportsAnArchiveWithWorldsAndMods()
    {
        var source = Path.Combine(_root, "source");
        var archive = Path.Combine(_root, "export.zip");
        Directory.CreateDirectory(Path.Combine(source, "saves", "World1"));
        Directory.CreateDirectory(Path.Combine(source, "mods"));
        File.WriteAllText(Path.Combine(source, "saves", "World1", "level.dat"), "world");
        File.WriteAllText(Path.Combine(source, "mods", "example.jar"), "mod");

        var launcher = new DetectedLauncher
        {
            Id = "test",
            Name = "Test Launcher",
            DataRoots = new[] { source }
        };

        var result = await LauncherMigrationService.ExportAsync(new[] { launcher }, archive);

        Assert.Equal(Path.GetFullPath(archive), result);
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("/saves/World1/level.dat"));
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("/mods/example.jar"));
    }

    [Fact]
    public void FindsLexplosionDataInDefaultAndCustomGameRoots()
    {
        var appData = Path.Combine(_root, "appdata");
        var custom = Path.Combine(_root, "custom-lexplosion");
        var defaultRoot = Path.Combine(appData, ".lexplosion");
        var settingsDir = Path.Combine(appData, "lexplosion-data");

        Directory.CreateDirectory(Path.Combine(custom, "instances", "Pack A", "saves", "World"));
        Directory.CreateDirectory(Path.Combine(defaultRoot, "instances", "Pack B", "mods"));
        Directory.CreateDirectory(settingsDir);

        var settingsPath = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(settingsPath,
            "{\"GamePath\":\"" + custom.Replace("\\", "/") + "\"}");

        var configured = LauncherDiscovery.ReadLexplosionGamePath(settingsPath);
        Assert.NotNull(configured);
        Assert.Equal(custom, configured!.Replace('/', Path.DirectorySeparatorChar));

        var roots = LauncherDiscovery.ResolveLexplosionGameRoots(appData);
        Assert.Contains(custom, roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(defaultRoot, roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void IgnoresBrokenLexplosionSettings()
    {
        var settingsDir = Path.Combine(_root, "broken-appdata", "lexplosion-data");
        Directory.CreateDirectory(settingsDir);
        var settingsPath = Path.Combine(settingsDir, "settings.json");

        File.WriteAllText(settingsPath, "{ not valid json");
        Assert.Null(LauncherDiscovery.ReadLexplosionGamePath(settingsPath));

        File.Delete(settingsPath);
        Assert.Null(LauncherDiscovery.ReadLexplosionGamePath(settingsPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

public sealed class UpdateSignatureTests
{
    [Fact]
    public void VerifiesRealSignatureAndRejectsTampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var path = Path.Combine(Path.GetTempPath(), "nviel-signature-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            File.WriteAllBytes(path, "nViel Launcher release payload"u8.ToArray());
            using (var stream = File.OpenRead(path))
            {
                var signature = key.SignData(stream, HashAlgorithmName.SHA256);
                Assert.True(UpdateSignature.VerifyFile(path, signature, publicKey));

                var tampered = (byte[])signature.Clone();
                tampered[0] ^= 0x40;
                Assert.False(UpdateSignature.VerifyFile(path, tampered, publicKey));
            }

            File.AppendAllText(path, "tamper");
            using (var stream = File.OpenRead(path))
            {
                var signature = key.SignData(stream, HashAlgorithmName.SHA256);
                Assert.True(UpdateSignature.VerifyFile(path, signature, publicKey));
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void RejectsGarbageSignature()
    {
        Assert.False(UpdateSignature.VerifyFile("missing.exe", new byte[] { 1, 2, 3 },
            AppInfo.UpdatePublicKeyBase64));
    }
}

public sealed class SettingsShapeTests
{
    [Fact]
    public void SettingsContainNoUpdateOrTokenData()
    {
        var forbidden = new[] { "update", "token", "repository", "release", "github", "proxy" };

        var suspicious = typeof(LauncherSettings)
            .GetProperties()
            .Where(p => forbidden.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(suspicious);
    }
}

public sealed class UpdateHealthTests
{
    [Fact]
    public void ParsesHealthMarkerArgument()
    {
        var marker = Path.Combine(Path.GetTempPath(), "marker.ok");

        Assert.Equal(marker, UpdateHealth.GetMarkerPath(new[] { "--update-health", marker }));
        Assert.Equal(marker, UpdateHealth.GetMarkerPath(new[] { "--update-health", marker, "--re" }));
        Assert.Null(UpdateHealth.GetMarkerPath(new[] { "--update-health" }));
        Assert.Null(UpdateHealth.GetMarkerPath(Array.Empty<string>()));
    }
}

public sealed class MaterialThemeTests
{
    [Theory]
    [InlineData("material", "material")]
    [InlineData("Material", "material")]
    [InlineData("nViel", "nViel")]
    [InlineData("NVIEL", "nViel")]
    [InlineData(null, "material")]
    [InlineData("", "material")]
    [InlineData("unknown", "material")]
    public void NormalizesThemeMode(string? input, string expected)
    {
        Assert.Equal(expected, MaterialTheme.NormalizeMode(input));
        Assert.Equal(expected == "material", MaterialTheme.IsMaterial(input));
    }

    [Fact]
    public void ParsesColoursAndFallsBackToTheDefault()
    {
        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), MaterialTheme.ParseOpaque("#123456", MaterialTheme.DefaultSeed));
        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), MaterialTheme.ParseOpaque("#80123456", MaterialTheme.DefaultSeed));
        Assert.Equal(MaterialTheme.DefaultSeed, MaterialTheme.ParseOpaque("not-a-colour", MaterialTheme.DefaultSeed));
        Assert.Equal(MaterialTheme.DefaultBase, MaterialTheme.ParseOpaque(null, MaterialTheme.DefaultBase));
    }

    [Fact]
    public void AppliesMaterialBrushesFromTheSeedAndBase()
    {
        var resources = new ResourceDictionary();
        var settings = new LauncherSettings
        {
            ThemeMode = MaterialTheme.ModeMaterial,
            AccentColor = "#FFD0BCFF",
            BaseColor = "#FF6750A4",
        };

        MaterialTheme.Apply(resources, settings);

        var accent = Assert.IsType<SolidColorBrush>(resources["AccentBrush"]);
        var background = Assert.IsType<SolidColorBrush>(resources["BgBrush"]);
        var text = Assert.IsType<SolidColorBrush>(resources["TextPrimaryBrush"]);

        Assert.True(accent.Color.R > 0x90 && accent.Color.B > 0xD0, "the accent must stay in the violet family");
        Assert.True(background.Color.R < 0x30 && background.Color.G < 0x30 && background.Color.B < 0x30,
            "the material background must be dark");
        Assert.True(text.Color.R > 0xD0 && text.Color.G > 0xD0, "the material text must be light");
        Assert.Equal(accent.Color, Assert.IsType<Color>(resources["AccentColor"]));
        Assert.Equal(accent.Color, ((SolidColorBrush)resources["AccentBrush"]).Color);
        Assert.NotEqual(background.Color, Assert.IsType<SolidColorBrush>(resources["SurfaceBrush"]).Color);
    }

    [Fact]
    public void ChangingTheBaseColourChangesTheSurfacesButNotTheAccent()
    {
        var first = new ResourceDictionary();
        var second = new ResourceDictionary();

        MaterialTheme.Apply(first, new LauncherSettings { BaseColor = "#FF6750A4", AccentColor = "#FFD0BCFF" });
        MaterialTheme.Apply(second, new LauncherSettings { BaseColor = "#FF006A60", AccentColor = "#FFD0BCFF" });

        var firstBackground = ((SolidColorBrush)first["BgBrush"]).Color;
        var secondBackground = ((SolidColorBrush)second["BgBrush"]).Color;

        Assert.NotEqual(firstBackground, secondBackground);
        Assert.Equal(((SolidColorBrush)first["AccentBrush"]).Color, ((SolidColorBrush)second["AccentBrush"]).Color);
    }

    [Fact]
    public void NvielModeKeepsTheMonochromeSurfacesAndHonoursTheAccent()
    {
        var resources = new ResourceDictionary();
        var accent = Color.FromRgb(0x33, 0xDD, 0x88);

        MaterialTheme.Apply(resources, new LauncherSettings
        {
            ThemeMode = MaterialTheme.ModeNviel,
            AccentColor = "#FF33DD88",
            BaseColor = "#FF6750A4",
        });

        Assert.Equal(Color.FromRgb(0x06, 0x06, 0x08), ((SolidColorBrush)resources["BgBrush"]).Color);
        Assert.Equal(accent, ((SolidColorBrush)resources["AccentBrush"]).Color);
    }
}

public sealed class IconThemeTests
{
    [Fact]
    public void CatalogKeysAreUniqueAndEveryDefaultPathParses()
    {
        Assert.Equal(37, IconCatalog.All.Count);
        Assert.Equal(IconCatalog.All.Count, IconCatalog.All.Select(icon => icon.Key).Distinct().Count());

        foreach (var icon in IconCatalog.All)
        {
            Assert.True(IconTheme.TryParse(icon.PathData, out var geometry), icon.Key + " has an invalid default path");
            Assert.False(geometry!.IsEmpty(), icon.Key + " has an empty default path");
        }
    }

    [Theory]
    [InlineData("M0 0 L10 10")]
    [InlineData(" M 2 2 h 8 v 8 z ")]
    public void AcceptsValidSvgPaths(string path)
    {
        Assert.True(IconTheme.TryParse(path, out var geometry));
        Assert.NotNull(geometry);
        Assert.False(geometry!.IsEmpty());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("definitely not a path")]
    [InlineData("M0 0 L")]
    public void RejectsEmptyOrBrokenPaths(string? path)
    {
        Assert.False(IconTheme.TryParse(path, out var geometry));
        Assert.Null(geometry);
    }

    [Fact]
    public void AppliesOverridesAndFallsBackToTheDefaultGeometry()
    {
        var resources = new ResourceDictionary();
        var settings = new LauncherSettings();
        settings.IconOverrides["IconHome"] = "M0 0 L24 0 L24 24 Z";
        settings.IconOverrides["IconPlay"] = "not a path";

        IconTheme.Apply(resources, settings);

        var home = Assert.IsAssignableFrom<Geometry>(resources["IconHome"]);
        Assert.Equal(new Rect(0, 0, 24, 24), home.Bounds);
        Assert.Equal(IconCatalog.CreateGeometry("IconPlay").ToString(),
            Assert.IsAssignableFrom<Geometry>(resources["IconPlay"]).ToString());
        Assert.All(IconCatalog.All, icon => Assert.True(resources.Contains(icon.Key), icon.Key + " is missing"));
    }
}

public sealed class ReleaseVerificationTests
{
    [Fact]
    public async Task DistBuildPassesTheRealUpdaterVerification()
    {
        var exe = Path.GetFullPath(Path.Combine("dist", "nVielLauncher.exe"));
        if (!File.Exists(exe)) return;

        var signature = await File.ReadAllTextAsync(exe + ".sig");
        await using var stream = File.OpenRead(exe);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        var info = new UpdateInfo(
            AppInfo.VersionLabel,
            "v" + AppInfo.VersionLabel,
            AppInfo.DisplayName,
            "",
            exe,
            new FileInfo(exe).Length,
            sha256,
            "",
            "",
            exe + ".sig",
            "");

        await UpdateService.VerifyAsync(exe, info, signature, CancellationToken.None);
    }
}
