using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace nVielLauncher.Services;

/// <summary>A launcher found on this PC and the Minecraft data that can be imported from it.</summary>
public sealed class DetectedLauncher : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string InstallPath { get; init; } = "";
    public string? UninstallString { get; init; }
    public IReadOnlyList<string> DataRoots { get; init; } = Array.Empty<string>();
    public int Worlds { get; init; }
    public int Mods { get; init; }
    public int ResourcePacks { get; init; }
    public int ShaderPacks { get; init; }
    public string Note { get; set; } = "";

    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallString);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string DataSummary
    {
        get
        {
            if (DataRoots.Count == 0)
                return CanUninstall ? "Данные не найдены — можно удалить лаунчер" : "Данные не найдены";

            var parts = new List<string>();
            if (Worlds > 0) parts.Add($"{Worlds} {Plural(Worlds, "мир", "мира", "миров")}");
            if (Mods > 0) parts.Add($"{Mods} {Plural(Mods, "мод", "мода", "модов")}");
            if (ResourcePacks > 0) parts.Add($"{ResourcePacks} {Plural(ResourcePacks, "ресурспак", "ресурспака", "ресурспаков")}");
            if (ShaderPacks > 0) parts.Add($"{ShaderPacks} {Plural(ShaderPacks, "шейдер", "шейдера", "шейдеров")}");
            return parts.Count == 0 ? "Данные для переноса не найдены" : string.Join(" · ", parts);
        }
    }

    public string PathText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(InstallPath)) return InstallPath;
            return DataRoots.FirstOrDefault() ?? "путь не найден";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string Plural(int value, string one, string few, string many)
    {
        var mod100 = value % 100;
        if (mod100 is >= 11 and <= 14) return many;
        return (value % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }
}

public sealed class MigrationOptions
{
    public bool Worlds { get; set; } = true;
    public bool Mods { get; set; } = true;
    public bool ResourcePacks { get; set; } = true;
    public bool ShaderPacks { get; set; } = true;
    public bool Configs { get; set; } = true;
}

public sealed record MigrationSummary(
    int Worlds,
    int Mods,
    int ResourcePacks,
    int ShaderPacks,
    int ConfigFiles,
    int Skipped,
    int Errors,
    string TargetDirectory);

/// <summary>Finds known launchers and moves only user data, never accounts or binaries.</summary>
public static class LauncherDiscovery
{
    private static readonly string AppData =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string ProgramFiles =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static readonly string ProgramFilesX86 =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static readonly string MinecraftData = Path.Combine(AppData, ".minecraft");

    public static IReadOnlyList<DetectedLauncher> Discover()
    {
        var launchers = new List<DetectedLauncher>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uninstallers = ReadUninstallers();

        void Add(
            string id,
            string name,
            string description,
            string? executable,
            IEnumerable<string> dataRoots,
            params string[] uninstallKeywords)
        {
            var roots = dataRoots
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var installPath = executable;
            if (string.IsNullOrWhiteSpace(installPath))
                installPath = roots.FirstOrDefault() ?? "";

            var hasInstall = !string.IsNullOrWhiteSpace(installPath) &&
                             (File.Exists(installPath) || Directory.Exists(installPath));
            if (!hasInstall && roots.Count == 0) return;

            var key = id + "|" + string.Join("|", roots);
            if (!seen.Add(key)) return;

            var stats = CountData(roots);
            launchers.Add(new DetectedLauncher
            {
                Id = id,
                Name = name,
                Description = description,
                InstallPath = installPath ?? "",
                UninstallString = FindUninstaller(uninstallers, uninstallKeywords),
                DataRoots = roots,
                Worlds = stats.Worlds,
                Mods = stats.Mods,
                ResourcePacks = stats.ResourcePacks,
                ShaderPacks = stats.ShaderPacks
            });
        }

        // Official launcher and TLauncher share .minecraft on most PCs. The import
        // helper de-duplicates shared roots, so it is safe to show both.
        var officialExe = FirstFile(
            Path.Combine(ProgramFilesX86, "Minecraft Launcher", "MinecraftLauncher.exe"),
            Path.Combine(ProgramFiles, "Minecraft Launcher", "MinecraftLauncher.exe"),
            Path.Combine(LocalAppData, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe"));
        var officialData = Directory.Exists(MinecraftData) ? new[] { MinecraftData } : Array.Empty<string>();
        if (officialExe is not null || officialData.Length > 0)
        {
            Add("official", "Официальный Minecraft Launcher",
                "Mojang / Microsoft · общая папка .minecraft",
                officialExe, officialData, "minecraft launcher");
        }

        var tlauncherExe = FirstFile(
            Path.Combine(MinecraftData, "TLauncher.exe"),
            Path.Combine(LocalAppData, "Programs", "TLauncher", "TLauncher.exe"),
            Path.Combine(LocalAppData, "TLauncher", "TLauncher.exe"),
            Path.Combine(ProgramFiles, "TLauncher", "TLauncher.exe"),
            Path.Combine(UserProfile, "TLauncher", "TLauncher.exe"));
        var tlauncherMarker = File.Exists(Path.Combine(MinecraftData, "TlauncherProfiles.json")) ||
                              Directory.Exists(Path.Combine(MinecraftData, "tlauncher"));
        if (tlauncherExe is not null || tlauncherMarker)
        {
            Add("tlauncher", "TLauncher", "Неофициальный лаунчер · папка .minecraft",
                tlauncherExe, new[] { MinecraftData }, "tlauncher");
        }

        var legacyExe = FirstFile(
            Path.Combine(MinecraftData, "LegacyLauncher.exe"),
            Path.Combine(LocalAppData, "Programs", "LegacyLauncher", "LegacyLauncher.exe"),
            Path.Combine(LocalAppData, "LegacyLauncher", "LegacyLauncher.exe"),
            Path.Combine(ProgramFiles, "LegacyLauncher", "LegacyLauncher.exe"));
        var legacyMarkers = Directory.Exists(Path.Combine(MinecraftData, "legacylauncher")) ||
                            Directory.Exists(Path.Combine(AppData, "legacylauncher")) ||
                            Directory.Exists(Path.Combine(LocalAppData, "LegacyLauncher"));
        if (legacyExe is not null || legacyMarkers)
        {
            var roots = new List<string> { MinecraftData };
            roots.AddRange(ChildDirectories(Path.Combine(MinecraftData, "home")));
            Add("legacy", "Legacy Launcher", "Старый лаунчер · .minecraft и home",
                legacyExe, roots, "legacy launcher", "legacylauncher");
        }

        var lexplosionData = Path.Combine(AppData, "lexplosion-data");
        var lexplosionExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "Lexplosion", "Lexplosion.exe"),
            Path.Combine(LocalAppData, "Programs", "LExplosion", "LExplosion.exe"),
            Path.Combine(ProgramFiles, "Lexplosion", "Lexplosion.exe"),
            Path.Combine(ProgramFiles, "LExplosion", "LExplosion.exe"),
            lexplosionData);
        if (lexplosionExe is not null || Directory.Exists(lexplosionData))
        {
            Add("lexplosion", "LExplosion (NightWorld)",
                "Сборки NightWorld · папка lexplosion-data",
                lexplosionExe, InstanceRoots(Path.Combine(lexplosionData, "instances")),
                "lexplosion", "nightworld");
        }

        var modrinthData = Path.Combine(AppData, "ModrinthApp");
        var modrinthOldData = Path.Combine(AppData, "com.modrinth.theseus");
        var modrinthExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "Modrinth App", "Modrinth App.exe"),
            Path.Combine(LocalAppData, "Programs", "ModrinthApp", "Modrinth App.exe"),
            modrinthData, modrinthOldData);
        if (modrinthExe is not null || Directory.Exists(modrinthData) || Directory.Exists(modrinthOldData))
        {
            var roots = InstanceRoots(Path.Combine(modrinthData, "profiles"));
            roots.AddRange(InstanceRoots(Path.Combine(modrinthOldData, "profiles")));
            Add("modrinth", "Modrinth App", "Профили Modrinth · миры и моды",
                modrinthExe, roots, "modrinth");
        }

        var prismData = Path.Combine(AppData, "PrismLauncher");
        var prismExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "PrismLauncher", "prismlauncher.exe"),
            Path.Combine(ProgramFiles, "PrismLauncher", "prismlauncher.exe"),
            prismData);
        if (prismExe is not null || Directory.Exists(prismData))
        {
            Add("prism", "Prism Launcher", "Инстансы Prism · отдельные папки",
                prismExe, InstanceRoots(Path.Combine(prismData, "instances")), "prism launcher", "prismlauncher");
        }

        var multimcData = Path.Combine(AppData, "MultiMC");
        var multimcExe = FirstFile(
            Path.Combine(ProgramFiles, "MultiMC", "MultiMC.exe"),
            Path.Combine(LocalAppData, "Programs", "MultiMC", "MultiMC.exe"),
            multimcData);
        if (multimcExe is not null || Directory.Exists(multimcData))
        {
            Add("multimc", "MultiMC", "Инстансы MultiMC · отдельные папки",
                multimcExe, InstanceRoots(Path.Combine(multimcData, "instances")), "multimc");
        }

        var polymcData = Path.Combine(AppData, "PolyMC");
        var polymcExe = FirstFile(Path.Combine(ProgramFiles, "PolyMC", "PolyMC.exe"), polymcData);
        if (polymcExe is not null || Directory.Exists(polymcData))
        {
            Add("polymc", "PolyMC", "Инстансы PolyMC · отдельные папки",
                polymcExe, InstanceRoots(Path.Combine(polymcData, "instances")), "polymc");
        }

        var atData = Path.Combine(AppData, "ATLauncher");
        var atExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "ATLauncher", "ATLauncher.exe"),
            Path.Combine(ProgramFiles, "ATLauncher", "ATLauncher.exe"),
            atData);
        if (atExe is not null || Directory.Exists(atData))
        {
            Add("atlauncher", "ATLauncher", "Инстансы ATLauncher · отдельные папки",
                atExe, InstanceRoots(Path.Combine(atData, "instances")), "atlauncher");
        }

        var gdData = Path.Combine(AppData, "gdlauncher_next");
        var gdOldData = Path.Combine(UserProfile, ".gdlauncher");
        var gdExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "GDLauncher", "GDLauncher.exe"),
            Path.Combine(LocalAppData, "Programs", "gdlauncher", "gdlauncher.exe"),
            gdData, gdOldData);
        if (gdExe is not null || Directory.Exists(gdData) || Directory.Exists(gdOldData))
        {
            var roots = InstanceRoots(Path.Combine(gdData, "instances"));
            roots.AddRange(InstanceRoots(Path.Combine(gdOldData, "instances")));
            Add("gdlauncher", "GDLauncher", "Инстансы GDLauncher · отдельные папки",
                gdExe, roots, "gdlauncher");
        }

        var curseData = Path.Combine(UserProfile, "curseforge", "minecraft");
        var curseExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "CurseForge", "CurseForge.exe"),
            Path.Combine(ProgramFiles, "CurseForge", "CurseForge.exe"),
            curseData);
        if (curseExe is not null || Directory.Exists(curseData))
        {
            Add("curseforge", "CurseForge", "Инстансы CurseForge · отдельные папки",
                curseExe, InstanceRoots(Path.Combine(curseData, "Instances")), "curseforge");
        }

        var xmclData = Path.Combine(AppData, "xmcl");
        var xmclExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "xmcl", "xmcl.exe"),
            Path.Combine(LocalAppData, "Programs", "XMCL", "XMCL.exe"),
            xmclData);
        if (xmclExe is not null || Directory.Exists(xmclData))
        {
            Add("xmcl", "XMCL", "Инстансы XMCL · отдельные папки",
                xmclExe, InstanceRoots(Path.Combine(xmclData, "instances")), "xmcl");
        }

        var hmclData = Path.Combine(AppData, ".hmcl");
        var hmclExe = FirstFile(Path.Combine(UserProfile, ".hmcl", "hmcl.exe"), hmclData);
        if (hmclExe is not null || Directory.Exists(hmclData))
        {
            Add("hmcl", "HMCL", "HMCL · использует папку .minecraft",
                hmclExe, new[] { MinecraftData }, "hmcl");
        }

        var lunarData = Path.Combine(UserProfile, ".lunarclient");
        var lunarExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "Lunar Client", "Lunar Client.exe"),
            Path.Combine(ProgramFiles, "Lunar Client", "Lunar Client.exe"),
            lunarData);
        if (lunarExe is not null || Directory.Exists(lunarData))
        {
            Add("lunar", "Lunar Client", "PvP-клиент · данные в .lunarclient",
                lunarExe, new[] { lunarData }, "lunar client");
        }

        var badlionData = Path.Combine(UserProfile, ".badlion");
        var badlionExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "Badlion Client", "Badlion Client.exe"),
            Path.Combine(ProgramFiles, "Badlion Client", "Badlion Client.exe"),
            badlionData);
        if (badlionExe is not null || Directory.Exists(badlionData))
        {
            Add("badlion", "Badlion Client", "PvP-клиент · данные в .badlion",
                badlionExe, new[] { badlionData }, "badlion");
        }

        var featherData = Path.Combine(AppData, ".feather");
        var featherExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "Feather", "Feather.exe"),
            Path.Combine(ProgramFiles, "Feather", "Feather.exe"),
            featherData);
        if (featherExe is not null || Directory.Exists(featherData))
        {
            Add("feather", "Feather", "PvP-клиент · данные в .feather",
                featherExe, new[] { featherData }, "feather");
        }

        var skData = Path.Combine(AppData, ".sklauncher");
        var skExe = FirstFile(
            Path.Combine(LocalAppData, "Programs", "SKLauncher", "SKLauncher.exe"),
            Path.Combine(ProgramFiles, "SKLauncher", "SKLauncher.exe"),
            skData);
        if (skExe is not null || Directory.Exists(skData))
        {
            Add("sklauncher", "SKLauncher", "SKLauncher · использует папку .minecraft",
                skExe, new[] { MinecraftData }, "sklauncher");
        }

        // A shared .minecraft should be explained in the UI, otherwise users may
        // think TLauncher and the official launcher have separate worlds.
        foreach (var launcher in launchers.Where(l =>
                     l.DataRoots.Any(r => SamePath(r, MinecraftData))))
        {
            launcher.Note = "общая папка .minecraft";
        }

        return launchers
            .OrderByDescending(l => l.DataRoots.Count > 0)
            .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? FirstFile(params string[] candidates)
        => candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) &&
                                           (File.Exists(p) || Directory.Exists(p)));

    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ChildDirectories(string parent)
    {
        try
        {
            return Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static List<string> InstanceRoots(string parent)
    {
        var roots = new List<string>();
        foreach (var instance in ChildDirectories(parent))
        {
            var nested = new[]
            {
                Path.Combine(instance, "minecraft"),
                Path.Combine(instance, ".minecraft")
            }.FirstOrDefault(Directory.Exists);

            roots.Add(nested ?? instance);
        }

        return roots;
    }

    private static (int Worlds, int Mods, int ResourcePacks, int ShaderPacks) CountData(
        IReadOnlyList<string> roots)
    {
        var worlds = 0;
        var mods = 0;
        var resourcePacks = 0;
        var shaderPacks = 0;

        foreach (var root in roots)
        {
            worlds += CountDirectories(Path.Combine(root, "saves"));
            mods += CountFiles(Path.Combine(root, "mods"), "*.jar");
            resourcePacks += CountEntries(Path.Combine(root, "resourcepacks"));
            shaderPacks += CountEntries(Path.Combine(root, "shaderpacks"));
        }

        return (worlds, mods, resourcePacks, shaderPacks);
    }

    private static int CountDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Count() : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountFiles(string path, string pattern)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern).Count() : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountEntries(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFileSystemEntries(path).Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private sealed record UninstallEntry(string DisplayName, string? UninstallString);

    private static IReadOnlyList<UninstallEntry> ReadUninstallers()
    {
        var result = new List<UninstallEntry>();

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (key is null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(subKeyName);
                            var name = subKey?.GetValue("DisplayName") as string;
                            var command = subKey?.GetValue("UninstallString") as string;
                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
                                continue;
                            if (!IsSafeUninstaller(command)) continue;

                            result.Add(new UninstallEntry(name, command));
                        }
                        catch
                        {
                            // unreadable registry entry: ignore
                        }
                    }
                }
                catch
                {
                    // registry view unavailable: ignore
                }
            }
        }

        return result;
    }

    private static string? FindUninstaller(
        IReadOnlyList<UninstallEntry> entries,
        IEnumerable<string> keywords)
    {
        foreach (var entry in entries)
        {
            if (keywords.Any(k => entry.DisplayName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return entry.UninstallString;
        }

        return null;
    }

    private static bool IsSafeUninstaller(string command)
    {
        if (command.Contains("ms-appx", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            return false;

        return command.Contains(".exe", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Copies user data between launchers and creates portable export archives.</summary>
public static class LauncherMigrationService
{
    public static Task<MigrationSummary> ImportAsync(
        IReadOnlyList<DetectedLauncher> launchers,
        string targetDirectory,
        MigrationOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ImportCore(launchers, targetDirectory, options, progress, cancellationToken),
            cancellationToken);

    public static Task<string> ExportAsync(
        IReadOnlyList<DetectedLauncher> launchers,
        string archivePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ExportCore(launchers, archivePath, progress, cancellationToken),
            cancellationToken);

    public static async Task<string> BackupBeforeRemovalAsync(
        IReadOnlyList<DetectedLauncher> launchers,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.MigrationBackupsDirectory);
        var path = Path.Combine(AppPaths.MigrationBackupsDirectory,
            $"old-launchers-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        var temp = path + ".tmp";
        try
        {
            await ExportAsync(launchers, temp, progress, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    public static IReadOnlyList<string> LaunchUninstallers(IEnumerable<DetectedLauncher> launchers)
    {
        var started = new List<string>();

        foreach (var launcher in launchers.Where(l => l.CanUninstall))
        {
            try
            {
                var startInfo = BuildUninstallStartInfo(launcher.UninstallString!);
                using var process = Process.Start(startInfo);
                if (process is not null) started.Add(launcher.Name);
            }
            catch (Exception ex)
            {
                Log.Write($"MIGRATION: не удалось запустить деинсталлятор {launcher.Name}: {ex.Message}");
            }
        }

        return started;
    }

    private static ProcessStartInfo BuildUninstallStartInfo(string command)
    {
        command = command.Trim();

        string executable;
        string arguments;

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0)
            {
                executable = command.Trim('"');
                arguments = "";
            }
            else
            {
                executable = command[1..end];
                arguments = command[(end + 1)..].Trim();
            }
        }
        else
        {
            var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                var cut = exeIndex + 4;
                executable = command[..cut].Trim();
                arguments = command[cut..].Trim();
            }
            else
            {
                executable = command;
                arguments = "";
            }
        }

        return new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true
        };
    }

    private static MigrationSummary ImportCore(
        IReadOnlyList<DetectedLauncher> launchers,
        string targetDirectory,
        MigrationOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(target);

        var worlds = 0;
        var mods = 0;
        var resourcePacks = 0;
        var shaderPacks = 0;
        var configs = 0;
        var skipped = 0;
        var errors = 0;

        var processedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var launcher in launchers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var root in launcher.DataRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = Path.GetFullPath(root);
                if (!Directory.Exists(source) || SamePath(source, target)) continue;
                if (!processedRoots.Add(source)) continue;

                progress?.Report($"Импорт: {launcher.Name}");

                if (options.Worlds)
                {
                    worlds += CopyWorlds(Path.Combine(source, "saves"),
                        Path.Combine(target, "saves"), ref skipped, ref errors, cancellationToken);
                }

                if (options.Mods)
                {
                    mods += CopyFiles(Path.Combine(source, "mods"),
                        Path.Combine(target, "mods"), "*.jar", ref skipped, ref errors, cancellationToken);
                }

                if (options.ResourcePacks)
                {
                    resourcePacks += CopyEntries(Path.Combine(source, "resourcepacks"),
                        Path.Combine(target, "resourcepacks"), ref skipped, ref errors, cancellationToken);
                }

                if (options.ShaderPacks)
                {
                    shaderPacks += CopyEntries(Path.Combine(source, "shaderpacks"),
                        Path.Combine(target, "shaderpacks"), ref skipped, ref errors, cancellationToken);
                }

                if (options.Configs)
                {
                    configs += CopyEntries(Path.Combine(source, "config"),
                        Path.Combine(target, "config"), ref skipped, ref errors, cancellationToken);
                    configs += CopyRootFile(Path.Combine(source, "options.txt"),
                        Path.Combine(target, "options.txt"), ref skipped, ref errors);
                    configs += CopyRootFile(Path.Combine(source, "servers.dat"),
                        Path.Combine(target, "servers.dat"), ref skipped, ref errors);
                }
            }
        }

        progress?.Report("Импорт завершён.");
        return new MigrationSummary(worlds, mods, resourcePacks, shaderPacks, configs, skipped, errors,
            target);
    }

    private static int CopyWorlds(
        string source,
        string target,
        ref int skipped,
        ref int errors,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return 0;

        Directory.CreateDirectory(target);
        var copied = 0;

        foreach (var world in SafeDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var name = Path.GetFileName(world);
                var existing = Path.Combine(target, name);
                if (Directory.Exists(existing) && SameWorld(existing, world))
                {
                    skipped++;
                    continue;
                }

                var destination = UniqueDirectory(target, name);
                CopyDirectory(world, destination, cancellationToken, skipSessionLock: true);
                copied++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors++;
                Log.Write("MIGRATION: мир не скопирован: " + ex.Message);
            }
        }

        return copied;
    }

    private static int CopyFiles(
        string source,
        string target,
        string pattern,
        ref int skipped,
        ref int errors,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return 0;

        Directory.CreateDirectory(target);
        var copied = 0;

        foreach (var file in SafeFiles(source, pattern))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var destination = Path.Combine(target, Path.GetFileName(file));
                if (File.Exists(destination))
                {
                    skipped++;
                    continue;
                }

                File.Copy(file, destination);
                copied++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors++;
                Log.Write("MIGRATION: файл не скопирован: " + ex.Message);
            }
        }

        return copied;
    }

    private static int CopyEntries(
        string source,
        string target,
        ref int skipped,
        ref int errors,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return 0;

        Directory.CreateDirectory(target);
        var copied = 0;

        foreach (var entry in SafeEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var name = Path.GetFileName(entry);
                var destination = Path.Combine(target, name);
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    skipped++;
                    continue;
                }

                if (Directory.Exists(entry))
                    CopyDirectory(entry, destination, cancellationToken, skipSessionLock: false);
                else
                    File.Copy(entry, destination);

                copied++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors++;
                Log.Write("MIGRATION: элемент не скопирован: " + ex.Message);
            }
        }

        return copied;
    }

    private static int CopyRootFile(
        string source,
        string destination,
        ref int skipped,
        ref int errors)
    {
        if (!File.Exists(source)) return 0;
        if (File.Exists(destination))
        {
            skipped++;
            return 0;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            return 1;
        }
        catch (Exception ex)
        {
            errors++;
            Log.Write("MIGRATION: файл не скопирован: " + ex.Message);
            return 0;
        }
    }

    private static void CopyDirectory(
        string source,
        string target,
        CancellationToken cancellationToken,
        bool skipSessionLock)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(target);

        foreach (var file in SafeFiles(source, "*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skipSessionLock &&
                string.Equals(Path.GetFileName(file), "session.lock", StringComparison.OrdinalIgnoreCase))
                continue;

            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in SafeDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)),
                cancellationToken, skipSessionLock);
        }
    }

    private static string ExportCore(
        IReadOnlyList<DetectedLauncher> launchers,
        string archivePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        using var archive = ZipFile.Open(fullPath, ZipArchiveMode.Create);
        var exportedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = 0;

        foreach (var launcher in launchers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var validName = SafeArchiveName(launcher.Name);
            var rootIndex = 0;

            foreach (var root in launcher.DataRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = Path.GetFullPath(root);
                if (!Directory.Exists(source) || !exportedRoots.Add(source)) continue;

                progress?.Report($"Экспорт: {launcher.Name}");
                var prefix = $"launchers/{validName}/{++rootIndex}";

                entries += AddDirectoryToArchive(archive, Path.Combine(source, "saves"),
                    prefix + "/saves", cancellationToken);
                entries += AddDirectoryToArchive(archive, Path.Combine(source, "mods"),
                    prefix + "/mods", cancellationToken);
                entries += AddDirectoryToArchive(archive, Path.Combine(source, "resourcepacks"),
                    prefix + "/resourcepacks", cancellationToken);
                entries += AddDirectoryToArchive(archive, Path.Combine(source, "shaderpacks"),
                    prefix + "/shaderpacks", cancellationToken);
                entries += AddDirectoryToArchive(archive, Path.Combine(source, "config"),
                    prefix + "/config", cancellationToken);
                entries += AddDirectoryToArchive(archive, Path.Combine(source, "screenshots"),
                    prefix + "/screenshots", cancellationToken);
                entries += AddFileToArchive(archive, Path.Combine(source, "options.txt"),
                    prefix + "/options.txt");
                entries += AddFileToArchive(archive, Path.Combine(source, "servers.dat"),
                    prefix + "/servers.dat");
            }
        }

        if (entries == 0)
        {
            try { File.Delete(fullPath); } catch { }
            throw new InvalidOperationException("В выбранных лаунчерах нет данных для экспорта.");
        }

        progress?.Report("Экспорт завершён.");
        return fullPath;
    }

    private static int AddDirectoryToArchive(
        ZipArchive archive,
        string source,
        string entryPrefix,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return 0;

        var count = 0;
        foreach (var file in SafeFilesRecursive(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            var entryName = entryPrefix + "/" + relative;
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            count++;
        }

        return count;
    }

    private static int AddFileToArchive(ZipArchive archive, string source, string entryName)
    {
        if (!File.Exists(source)) return 0;
        archive.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
        return 1;
    }

    private static string SafeArchiveName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "launcher" : safe.Trim();
    }

    private static bool SameWorld(string left, string right)
    {
        try
        {
            var leftLevel = Path.Combine(left, "level.dat");
            var rightLevel = Path.Combine(right, "level.dat");
            if (!File.Exists(leftLevel) || !File.Exists(rightLevel)) return false;

            using var leftStream = File.OpenRead(leftLevel);
            using var rightStream = File.OpenRead(rightLevel);
            return SHA256.HashData(leftStream).SequenceEqual(SHA256.HashData(rightStream));
        }
        catch
        {
            return false;
        }
    }

    private static string UniqueDirectory(string parent, string name)
    {
        var candidate = Path.Combine(parent, name);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;

        var baseName = name;
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(parent, $"{baseName} (импорт {i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeFiles(string path, string pattern)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, pattern)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeFilesRecursive(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEntries(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFileSystemEntries(path)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
