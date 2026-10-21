using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
    public void ProductVersionIs1021Global()
    {
        Assert.Equal("1.0.2.1", AppInfo.VersionLabel);
        Assert.Equal("1.0.2.1 Global", AppInfo.DisplayVersion);
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
