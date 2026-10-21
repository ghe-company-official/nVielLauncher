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
