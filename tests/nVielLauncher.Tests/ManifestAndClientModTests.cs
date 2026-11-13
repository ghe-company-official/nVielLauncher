using System.IO;
using System.Security.Cryptography;
using nVielLauncher.Services;
using Xunit;

namespace nVielLauncher.Tests;

public sealed class ReleaseManifestTests
{
    [Fact]
    public void ParsesLauncherClientModAndRequirements()
    {
        const string json = """
        {
          "schema": 1,
          "launcher": {
            "version": "1.2.1",
            "asset": "nVielLauncher.exe",
            "sha256": "1188e08d0da66c0948ea556a13fd81e1e66f2a8d6522fdb415f930468960c1bb",
            "size": 84398349
          },
          "client_mod": {
            "version": "1.0.0",
            "asset": "nviel-1.0.0.jar",
            "file_name": "nviel-1.0.0.jar",
            "sha256": "1188e08d0da66c0948ea556a13fd81e1e66f2a8d6522fdb415f930468960c1bb",
            "size": 16942564,
            "notes": "Новый мод"
          },
          "min_java": 21,
          "fabric": "1.21.4",
          "notes": "Что нового"
        }
        """;

        var manifest = UpdateService.ParseManifest(json);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.Schema);
        Assert.Equal("1.2.1", manifest.Launcher!.Version);
        Assert.Equal("nVielLauncher.exe", manifest.Launcher.Asset);
        Assert.Equal(84398349, manifest.Launcher.Size);
        Assert.Equal("1.0.0", manifest.ClientMod!.Version);
        Assert.Equal("nviel-1.0.0.jar", manifest.ClientMod.FileName);
        Assert.Equal(16942564, manifest.ClientMod.Size);
        Assert.Equal(21, manifest.MinJava);
        Assert.Equal("1.21.4", manifest.Fabric);
        Assert.Equal("Что нового", manifest.Notes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":2,\"launcher\":{\"version\":\"1.2.1\"}}")]
    [InlineData("{\"schema\":1,\"launcher\":{\"version\":\"1.2.1\",\"sha256\":\"not-a-hash\"}}")]
    [InlineData("not json")]
    public void RejectsInvalidManifests(string json)
    {
        Assert.Null(UpdateService.ParseManifest(json));
    }
}

public sealed class ClientModServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "nviel-clientmod-" + Guid.NewGuid().ToString("N"));

    public ClientModServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void NeedsUpdateComparesInstalledHash()
    {
        var game = Path.Combine(_root, "game");
        var mods = Path.Combine(game, "mods");
        Directory.CreateDirectory(mods);
        var installed = Path.Combine(mods, "nviel-1.0.0.jar");
        File.WriteAllText(installed, "installed-mod");

        var installedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installed)));

        var settings = new SettingsService(new LauncherSettings
        {
            GameDirectory = game,
            ClientModSha256 = installedHash
        });

        var service = CreateService(settings);
        var same = Mod("1.0.0", "nviel-1.0.0.jar", installedHash.ToUpperInvariant());
        var newer = Mod("1.0.1", "nviel-1.0.1.jar", new string('b', 64));

        Assert.False(service.NeedsUpdate(same));
        Assert.True(service.NeedsUpdate(newer));
    }

    [Fact]
    public void InstallCopiesModIntoEveryInstanceAndPreservesDisabledState()
    {
        var settings = CreateTwoInstanceSettings();
        var service = CreateService(settings);

        var defaultMods = Path.Combine(settings.Current.GameDirectory, "mods");
        var secondMods = Path.Combine(settings.Current.Instances[1].Directory, "mods");
        Directory.CreateDirectory(defaultMods);
        Directory.CreateDirectory(secondMods);
        File.WriteAllText(Path.Combine(defaultMods, "nviel-0.9.0.jar.disabled"), "old-default");
        File.WriteAllText(Path.Combine(secondMods, "nviel-0.9.0.jar"), "old-second");

        var jar = Path.Combine(_root, "nviel-1.0.0.jar");
        File.WriteAllText(jar, "new-mod-bytes");

        var result = service.InstallFromFile(Mod("1.0.0", "nviel-1.0.0.jar", "new"), jar, CancellationToken.None);

        Assert.Equal(2, result.Instances);
        Assert.True(result.BackedUp);
        Assert.True(File.Exists(Path.Combine(defaultMods, "nviel-1.0.0.jar.disabled")));
        Assert.True(File.Exists(Path.Combine(secondMods, "nviel-1.0.0.jar")));
        Assert.False(File.Exists(Path.Combine(defaultMods, "nviel-0.9.0.jar.disabled")));
        Assert.False(File.Exists(Path.Combine(secondMods, "nviel-0.9.0.jar")));

        var rollback = settings.Current.PendingModRollback;
        Assert.NotNull(rollback);
        Assert.True(rollback!.IsUsable);
        Assert.Equal("0.9.0", rollback.PreviousVersion);
        Assert.Equal("1.0.0", rollback.NewVersion);
        Assert.Equal("1.0.0", settings.Current.ClientModVersion);
    }

    [Fact]
    public async Task RollbackRestoresPreviousJarIntoEveryInstance()
    {
        var settings = CreateTwoInstanceSettings();
        var service = CreateService(settings);

        var defaultMods = Path.Combine(settings.Current.GameDirectory, "mods");
        var secondMods = Path.Combine(settings.Current.Instances[1].Directory, "mods");
        Directory.CreateDirectory(defaultMods);
        Directory.CreateDirectory(secondMods);
        File.WriteAllText(Path.Combine(defaultMods, "nviel-0.9.0.jar"), "old-default");
        File.WriteAllText(Path.Combine(secondMods, "nviel-0.9.0.jar"), "old-second");

        var jar = Path.Combine(_root, "nviel-1.0.0.jar");
        File.WriteAllText(jar, "new-mod-bytes");
        service.InstallFromFile(Mod("1.0.0", "nviel-1.0.0.jar", "new"), jar, CancellationToken.None);

        var restored = await service.RollbackAsync(CancellationToken.None);

        Assert.Equal(2, restored);
        Assert.Equal("old-default", File.ReadAllText(Path.Combine(defaultMods, "nviel-0.9.0.jar")));
        Assert.Equal("old-second", File.ReadAllText(Path.Combine(secondMods, "nviel-0.9.0.jar")));
        Assert.False(File.Exists(Path.Combine(defaultMods, "nviel-1.0.0.jar")));
        Assert.Equal("nviel-0.9.0.jar", settings.Current.ClientModFileName);
        Assert.Null(settings.Current.PendingModRollback);
        Assert.False(settings.Current.ModRollbackSuggested);
    }

    [Fact]
    public void CrashAfterUpdateSuggestsRollbackAndCleanExitClearsIt()
    {
        var settings = CreateTwoInstanceSettings();
        var service = CreateService(settings);

        var mods = Path.Combine(settings.Current.GameDirectory, "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "nviel-0.9.0.jar"), "old");

        var jar = Path.Combine(_root, "nviel-1.0.0.jar");
        File.WriteAllText(jar, "new-mod-bytes");
        service.InstallFromFile(Mod("1.0.0", "nviel-1.0.0.jar", "new"), jar, CancellationToken.None);

        service.HandleGameExit(1);
        Assert.True(settings.Current.ModRollbackSuggested);
        Assert.NotNull(settings.Current.PendingModRollback);

        service.HandleGameExit(0);
        Assert.False(settings.Current.ModRollbackSuggested);
        Assert.Null(settings.Current.PendingModRollback);
    }

    [Fact]
    public async Task CoreModSyncKeepsDisabledManagedJarAndRemovesStaleVersions()
    {
        var game = Path.Combine(_root, "game");
        var mods = Path.Combine(game, "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "nviel-1.0.0.jar.disabled"), "managed-disabled");
        File.WriteAllText(Path.Combine(mods, "nviel-0.9.0.jar"), "stale");

        await new ModsService().EnsureCoreModsAsync(
            game, CancellationToken.None, new string('a', 64), "nviel-1.0.0.jar");

        Assert.True(File.Exists(Path.Combine(mods, "nviel-1.0.0.jar.disabled")));
        Assert.False(File.Exists(Path.Combine(mods, "nviel-0.9.0.jar")));
        Assert.False(File.Exists(Path.Combine(mods, "nviel-1.0.0.jar")));
    }

    private SettingsService CreateTwoInstanceSettings()
    {
        var settings = new SettingsService(new LauncherSettings
        {
            GameDirectory = Path.Combine(_root, "game")
        });

        settings.Current.Instances.Add(new GameInstance
        {
            Id = "second",
            Name = "Вторая",
            Directory = Path.Combine(_root, "second")
        });

        return settings;
    }

    private ClientModService CreateService(SettingsService settings) =>
        new(settings, new InstanceService(settings), new UpdateService(), Path.Combine(_root, "backups"));

    private static ClientModInfo Mod(string version, string fileName, string sha256) =>
        new(version, fileName, "https://example.invalid/" + fileName, 13, sha256);

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root)) return;

            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // temporary test folder
        }
    }
}
