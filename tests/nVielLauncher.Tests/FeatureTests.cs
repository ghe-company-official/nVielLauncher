using System.IO;
using System.IO.Compression;
using nVielLauncher.Services;
using Xunit;

namespace nVielLauncher.Tests;

public sealed class InstanceSettingsTests
{
    [Fact]
    public void CreatesDefaultInstanceForLegacySettings()
    {
        var settings = new LauncherSettings
        {
            GameDirectory = Path.Combine(Path.GetTempPath(), "nviel-instance-test")
        };

        settings.EnsureInstances();

        var instance = Assert.Single(settings.Instances);
        Assert.True(instance.IsDefault);
        Assert.Equal(SettingsService.DefaultInstanceId, instance.Id);
        Assert.Equal("Основная", instance.Name);
        Assert.Equal(Path.GetFullPath(settings.GameDirectory), settings.EffectiveGameDirectory);
    }

    [Fact]
    public void ActiveInstanceChangesEffectiveDirectory()
    {
        var settings = new LauncherSettings
        {
            GameDirectory = Path.Combine(Path.GetTempPath(), "nviel-default")
        };
        settings.EnsureInstances();

        var otherDirectory = Path.Combine(Path.GetTempPath(), "nviel-second");
        settings.Instances.Add(new GameInstance
        {
            Id = "second",
            Name = "Вторая",
            Directory = otherDirectory
        });
        settings.ActiveInstanceId = "second";

        Assert.Equal(Path.GetFullPath(otherDirectory), settings.EffectiveGameDirectory);
        Assert.Equal(Path.GetFullPath(settings.GameDirectory), settings.SharedRuntimeDirectory);
    }
}

public sealed class InstanceLegacyServersTests
{
    [Fact]
    public void LegacyGlobalServersMoveIntoDefaultInstanceOnce()
    {
        var settings = new LauncherSettings
        {
            GameDirectory = Path.Combine(Path.GetTempPath(), "nviel-servers-test")
        };
        settings.Servers.Add(new ServerEntry { Name = "Hypixel", Host = "hypixel.net" });
        settings.Servers.Add(new ServerEntry { Name = "Local", Host = "localhost", Port = 25566 });

        settings.EnsureInstances();
        var defaultInstance = Assert.Single(settings.Instances);

        Assert.Equal(2, defaultInstance.Servers.Count);
        Assert.Equal("hypixel.net", defaultInstance.Servers[0].Host);
        Assert.Empty(settings.Servers);

        // one-way migration: re-running must not duplicate or resurrect entries
        settings.EnsureInstances();
        Assert.Equal(2, defaultInstance.Servers.Count);
        Assert.Empty(settings.Servers);
    }

    [Fact]
    public void OverrideHelpersFallBackToGlobalValues()
    {
        var settings = new LauncherSettings
        {
            GameDirectory = Path.Combine(Path.GetTempPath(), "nviel-override-test"),
            Nickname = "GlobalNick",
            RamMb = 4096,
            JavaPath = @"C:\java\global.exe",
            Fullscreen = true,
            ShowResolution = false,
            ResolutionWidth = 1280,
            ResolutionHeight = 720
        };
        settings.EnsureInstances();
        var instance = settings.ActiveInstance!;

        // nothing set: everything inherits
        Assert.Equal("GlobalNick", settings.NicknameFor(instance));
        Assert.Equal(4096, settings.RamMbFor(instance));
        Assert.Equal(@"C:\java\global.exe", settings.JavaPathFor(instance));
        Assert.True(settings.FullscreenFor(instance));
        Assert.False(settings.ShowResolutionFor(instance));
        Assert.Equal(1280, settings.ResolutionWidthFor(instance));
        Assert.Equal(720, settings.ResolutionHeightFor(instance));

        // set overrides
        instance.Nickname = "InstanceNick";
        instance.RamMb = 6144;
        instance.JavaPath = @"C:\java\instance.exe";
        instance.Fullscreen = false;
        instance.ShowResolution = true;
        instance.ResolutionWidth = 1920;
        instance.ResolutionHeight = 1080;

        Assert.Equal("InstanceNick", settings.NicknameFor(instance));
        Assert.Equal(6144, settings.RamMbFor(instance));
        Assert.Equal(@"C:\java\instance.exe", settings.JavaPathFor(instance));
        Assert.False(settings.FullscreenFor(instance));
        Assert.True(settings.ShowResolutionFor(instance));
        Assert.Equal(1920, settings.ResolutionWidthFor(instance));
        Assert.Equal(1080, settings.ResolutionHeightFor(instance));

        // null/zero again: back to global
        instance.Nickname = null;
        instance.RamMb = 0;
        instance.JavaPath = null;
        instance.Fullscreen = null;
        instance.ShowResolution = null;

        Assert.Equal("GlobalNick", settings.NicknameFor(instance));
        Assert.Equal(4096, settings.RamMbFor(instance));
        Assert.Equal(@"C:\java\global.exe", settings.JavaPathFor(instance));
        Assert.True(settings.FullscreenFor(instance));
        Assert.False(settings.ShowResolutionFor(instance));
    }
}

public sealed class InstanceCloneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nviel-clone-" + Guid.NewGuid().ToString("N"));

    public InstanceCloneTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.InstancesOverride = Path.Combine(_root, "instances");
    }

    public void Dispose()
    {
        AppPaths.InstancesOverride = null;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp cleanup is best effort */ }
    }

    [Fact]
    public void CloneCopiesContentSettingsServersAndActivates()
    {
        var gameDirectory = Path.Combine(_root, "game");
        var settings = new LauncherSettings
        {
            GameDirectory = gameDirectory,
            Nickname = "GlobalNick",
            RamMb = 2048
        };
        settings.EnsureInstances();

        var sourceDirectory = Path.Combine(_root, "source-instance");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "mods"));
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "saves", "world1"));
        File.WriteAllText(Path.Combine(sourceDirectory, "mods", "a.jar"), "mod a");
        File.WriteAllText(Path.Combine(sourceDirectory, "saves", "world1", "level.dat"), "w");
        File.WriteAllText(Path.Combine(sourceDirectory, "options.txt"), "lang:ru");

        var master = new GameInstance
        {
            Id = "master",
            Name = "Мастер",
            Directory = sourceDirectory,
            RamMb = 4096,
            Fullscreen = false,
            ShowResolution = true,
            ResolutionWidth = 1920,
            ResolutionHeight = 1080
        };
        master.Servers.Add(new ServerEntry { Name = "Hypixel", Host = "hypixel.net" });
        settings.Instances.Add(master);
        settings.ActiveInstanceId = master.Id;

        var service = new InstanceService(settings);
        var clone = service.Clone("master", null, CancellationToken.None);

        Assert.False(clone.IsDefault);
        Assert.Equal("Мастер (копия)", clone.Name);
        Assert.NotEqual(sourceDirectory, clone.Directory);
        Assert.Equal(clone.Id, settings.ActiveInstanceId);

        // content
        Assert.True(File.Exists(Path.Combine(clone.Directory, "mods", "a.jar")));
        Assert.True(File.Exists(Path.Combine(clone.Directory, "saves", "world1", "level.dat")));
        Assert.True(File.Exists(Path.Combine(clone.Directory, "options.txt")));

        // per-instance settings and servers are copied, not shared
        Assert.Equal(4096, clone.RamMb);
        Assert.False(clone.Fullscreen);
        Assert.True(clone.ShowResolution);
        Assert.Equal(1920, clone.ResolutionWidth);
        Assert.Equal(1080, clone.ResolutionHeight);
        var server = Assert.Single(clone.Servers);
        Assert.Equal("hypixel.net", server.Host);
        Assert.Single(master.Servers); // source untouched

        // source stays intact after cleanup-on-failure helpers ran
        Assert.True(File.Exists(Path.Combine(sourceDirectory, "mods", "a.jar")));
    }

    [Fact]
    public void CloneNamesStayUnique()
    {
        var gameDirectory = Path.Combine(_root, "game-unique");
        var settings = new LauncherSettings { GameDirectory = gameDirectory };
        settings.EnsureInstances();

        var sourceDirectory = Path.Combine(_root, "unique-source");
        Directory.CreateDirectory(sourceDirectory);
        var master = new GameInstance { Id = "master", Name = "Опасные эксперименты", Directory = sourceDirectory };
        settings.Instances.Add(master);
        settings.ActiveInstanceId = master.Id;

        var service = new InstanceService(settings);
        var first = service.Clone("master", null, CancellationToken.None);
        var second = service.Clone("master", null, CancellationToken.None);

        Assert.NotEqual(first.Name, second.Name);
        Assert.Equal("Опасные эксперименты (копия)", first.Name);
        Assert.Equal("Опасные эксперименты (копия 2)", second.Name);
    }
}

public sealed class VersionPredicateTests
{
    [Theory]
    [InlineData(">=1.20 <1.22", "1.21.4", true)]
    [InlineData(">=1.22", "1.21.4", false)]
    [InlineData("1.21.x", "1.21.4", true)]
    [InlineData("~1.21.2", "1.21.9", true)]
    [InlineData("~1.21.2", "1.22.0", false)]
    [InlineData("^1.21.4", "1.30.0", true)]
    [InlineData("^0.1.0", "0.1.5", true)]
    [InlineData("^0.1.0", "0.2.0", false)]
    [InlineData("1.21.4", "1.21.4", true)]
    [InlineData("1.21.4", "1.21.5", false)]
    public void MatchesFabricStyleRanges(string predicate, string version, bool expected)
    {
        Assert.Equal(expected, VersionPredicate.MatchesAny(new[] { predicate }, version));
    }
}

public sealed class ModConflictTests
{
    [Fact]
    public void FindsDuplicateModsAndMissingDependencies()
    {
        var root = TempDirectory();
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);

        CreateJar(mods, "dupe-old.jar", """
            {"id":"dupe","name":"Dupe Old","version":"1.0.0","depends":{"missinglib":">=2.0"}}
            """);
        CreateJar(mods, "dupe-new.jar", """
            {"id":"dupe","name":"Dupe New","version":"2.0.0"}
            """);

        var report = ModConflictService.Scan(root, CancellationToken.None);

        Assert.Equal(2, report.EnabledCount);
        Assert.Contains(report.Issues, i => i.Code == "duplicate");
        Assert.Contains(report.Issues, i => i.Code == "dependency-missing" && i.Title.Contains("missinglib"));
        Assert.True(report.HasErrors);

        Assert.True(ModConflictService.SetEnabled(mods, "dupe-old.jar", enabled: false));
        Assert.True(File.Exists(Path.Combine(mods, "dupe-old.jar.disabled")));

        var second = ModConflictService.Scan(root, CancellationToken.None);
        Assert.DoesNotContain(second.Issues, i => i.Code == "duplicate");
        Assert.Equal(1, second.DisabledCount);
    }

    [Fact]
    public void CanDisableReadOnlyMod()
    {
        var root = TempDirectory();
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);
        var jar = Path.Combine(mods, "readonly.jar");
        CreateJar(mods, "readonly.jar", """
            {"id":"readonly","name":"Read Only","version":"1.0.0"}
            """);
        File.SetAttributes(jar, File.GetAttributes(jar) | FileAttributes.ReadOnly);

        try
        {
            Assert.True(ModConflictService.SetEnabled(mods, "readonly.jar", enabled: false));
            Assert.False(File.Exists(jar));
            Assert.True(File.Exists(jar + ".disabled"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void WarnsWhenModTargetsAnotherMinecraftVersion()
    {
        var root = TempDirectory();
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);
        CreateJar(mods, "old.jar", """
            {"id":"old","name":"Old","version":"1.0","depends":{"minecraft":"1.20.x"}}
            """);

        var report = ModConflictService.Scan(root, CancellationToken.None);

        Assert.Contains(report.Issues, i => i.Code == "minecraft-version");
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "nviel-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateJar(string directory, string fileName, string fabricModJson)
    {
        using var archive = ZipFile.Open(Path.Combine(directory, fileName), ZipArchiveMode.Create);
        var entry = archive.CreateEntry("fabric.mod.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(fabricModJson);
    }
}

public sealed class BackupServiceTests
{
    [Fact]
    public async Task RestoreOverwritesReadOnlyFiles()
    {
        var game = Path.Combine(Path.GetTempPath(), "nviel-backup-ro-" + Guid.NewGuid().ToString("N"));
        var backups = Path.Combine(Path.GetTempPath(), "nviel-backup-root-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(game, "config"));
        var target = Path.Combine(game, "config", "readonly.json");
        await File.WriteAllTextAsync(target, """{"value":"original"}""");

        try
        {
            var service = new BackupService(backups);
            var info = await service.CreateAsync(game, "instance-ro", "manual", 5, null, CancellationToken.None);
            Assert.NotNull(info);

            await File.WriteAllTextAsync(target, """{"value":"changed"}""");
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
            await service.RestoreAsync(info!, game, null, CancellationToken.None);

            Assert.Equal("""{"value":"original"}""", await File.ReadAllTextAsync(target));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            try { Directory.Delete(game, true); } catch { }
            try { Directory.Delete(backups, true); } catch { }
        }
    }

    [Fact]
    public async Task CreatesRestoresAndDeletesWorldBackup()
    {
        var game = Path.Combine(Path.GetTempPath(), "nviel-backup-game-" + Guid.NewGuid().ToString("N"));
        var backups = Path.Combine(Path.GetTempPath(), "nviel-backup-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(game, "saves", "world"));
        Directory.CreateDirectory(Path.Combine(game, "config"));
        await File.WriteAllTextAsync(Path.Combine(game, "saves", "world", "level.dat"), "original");
        await File.WriteAllTextAsync(Path.Combine(game, "config", "test.json"), """{"value":1}""");

        try
        {
            var service = new BackupService(backups);
            var info = await service.CreateAsync(game, "instance", "manual", 5, null, CancellationToken.None);

            Assert.NotNull(info);
            Assert.True(File.Exists(info!.FilePath));
            Assert.Single(service.List("instance"));

            await File.WriteAllTextAsync(Path.Combine(game, "saves", "world", "level.dat"), "changed");
            await service.RestoreAsync(info, game, null, CancellationToken.None);

            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(game, "saves", "world", "level.dat")));

            service.Delete(info);
            Assert.Empty(service.List("instance"));
        }
        finally
        {
            try { Directory.Delete(game, true); } catch { }
            try { Directory.Delete(backups, true); } catch { }
        }
    }
}
