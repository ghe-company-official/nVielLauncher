using System.IO;

namespace nVielLauncher.Services;

public sealed record InstanceStats(int Mods, int Worlds, long SizeBytes);
public sealed record InstanceRowData(GameInstance Instance, InstanceStats Stats, bool IsActive);

/// <summary>
/// Owns the list of game instances. Shared runtime files (versions, libraries,
/// assets) stay in the legacy game folder; each instance only has its own mods,
/// configs, worlds and resources.
/// </summary>
public sealed class InstanceService
{
    private readonly SettingsService? _settingsService;
    private readonly LauncherSettings _current;

    public InstanceService(SettingsService settings)
    {
        _settingsService = settings;
        _current = settings.Current;
        _current.EnsureInstances();
    }

    /// <summary>Test constructor: operates on a detached settings object without persistence.</summary>
    public InstanceService(LauncherSettings current)
    {
        _settingsService = null;
        _current = current;
        _current.EnsureInstances();
    }

    private LauncherSettings Current => _settingsService?.Current ?? _current;

    private void Save()
    {
        if (_settingsService is null)
            Current.EnsureInstances();
        else
            _settingsService.Save();
    }

    public IReadOnlyList<GameInstance> All => Current.Instances;

    public GameInstance? Active => Current.ActiveInstance;

    public string GetDirectory(GameInstance instance) =>
        instance.IsDefault
            ? Current.SharedRuntimeDirectory
            : instance.Directory;

    public void SetActive(string id)
    {
        if (All.All(i => !string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Инстанс не найден.");

        Current.ActiveInstanceId = id;
        Save();
    }

    public GameInstance Create(
        string name,
        bool copyFromActive,
        IProgress<LaunchProgress>? progress,
        CancellationToken ct)
    {
        var settings = Current;
        var active = settings.ActiveInstance;
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(AppPaths.InstancesDirectory, id);

        var instance = new GameInstance
        {
            Id = id,
            Name = CleanName(name),
            Directory = directory,
            IsDefault = false,
            CreatedUtc = DateTime.UtcNow
        };

        Directory.CreateDirectory(directory);
        EnsureLayout(directory);

        if (copyFromActive && active is not null)
        {
            var source = GetDirectory(active);
            CopyDirectory(source, directory, "mods", progress, ct);
            CopyDirectory(source, directory, "config", progress, ct);
            CopyFile(source, directory, "options.txt");
            CopyFile(source, directory, "servers.dat");
        }

        settings.Instances.Add(instance);
        settings.ActiveInstanceId = instance.Id;
        Save();

        Log.Write($"INSTANCE: создан «{instance.Name}» → {directory}");
        return instance;
    }

    public void Rename(string id, string name)
    {
        var instance = All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException("Инстанс не найден.");

        instance.Name = CleanName(name);
        Save();
    }

    /// <summary>
    /// Creates an exact copy of an instance: mods, configs, worlds, resource packs,
    /// screenshots, root files plus every per-instance setting and the server list.
    /// </summary>
    public GameInstance Clone(string id, IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        var settings = Current;
        var source = All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException("Инстанс не найден.");

        var newId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(AppPaths.InstancesDirectory, newId);
        var clone = new GameInstance
        {
            Id = newId,
            Name = CloneName(source.Name, settings),
            Directory = directory,
            IsDefault = false,
            CreatedUtc = DateTime.UtcNow,
            Nickname = source.Nickname,
            RamMb = source.RamMb,
            JavaPath = source.JavaPath,
            ShowResolution = source.ShowResolution,
            Fullscreen = source.Fullscreen,
            ResolutionWidth = source.ResolutionWidth,
            ResolutionHeight = source.ResolutionHeight,
            Servers = source.Servers.Select(s => new ServerEntry
            {
                Name = s.Name,
                Host = s.Host,
                Port = s.Port,
                LastPlayedUtc = s.LastPlayedUtc
            }).ToList()
        };

        try
        {
            Directory.CreateDirectory(directory);
            EnsureLayout(directory);

            var sourceDirectory = GetDirectory(source);
            foreach (var folder in new[] { "mods", "config", "saves", "resourcepacks", "shaderpacks", "screenshots" })
                CopyDirectory(sourceDirectory, directory, folder, progress, ct);
            foreach (var file in new[] { "options.txt", "servers.dat", "servers.dat_old", "usercache.json" })
                CopyFile(sourceDirectory, directory, file);
        }
        catch (Exception ex)
        {
            Log.Write($"INSTANCE: клон «{clone.Name}» не создан: {ex.Message}");
            TryDelete(directory);
            throw new InvalidOperationException("Не удалось скопировать инстанс: " + ex.Message, ex);
        }

        settings.Instances.Add(clone);
        settings.ActiveInstanceId = clone.Id;
        Save();

        Log.Write($"INSTANCE: клон «{clone.Name}» из «{source.Name}» → {directory}");
        return clone;
    }

    /// <summary>Clone names look like «X (копия)» and then «X (копия 2)» without duplicates.</summary>
    private static string CloneName(string baseName, LauncherSettings settings)
    {
        var taken = settings.Instances
            .Select(i => i.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var first = CleanName(baseName + " (копия)");
        if (!taken.Contains(first)) return first;

        for (var n = 2;; n++)
        {
            var candidate = CleanName(baseName + " (копия " + n + ")");
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    public bool Delete(string id)
    {
        var settings = Current;
        var instance = All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (instance is null) return false;

        if (instance.IsDefault)
            throw new InvalidOperationException("Основной инстанс удалить нельзя — он хранит общие файлы игры.");

        if (All.Count <= 1)
            throw new InvalidOperationException("Должен остаться хотя бы один инстанс.");

        // Delete files first: if the game is holding a world open, the instance stays in
        // the list and the user can retry instead of silently losing the entry.
        try
        {
            DeleteDirectory(instance.Directory);
        }
        catch (Exception ex)
        {
            Log.Write($"INSTANCE: папка «{instance.Name}» не удалена: {ex.Message}");
            throw new InvalidOperationException(
                "Не удалось удалить папку инстанса. Закрой игру и попробуй снова.", ex);
        }

        var wasActive = string.Equals(settings.ActiveInstanceId, instance.Id, StringComparison.OrdinalIgnoreCase);
        settings.Instances.Remove(instance);

        if (wasActive)
        {
            var next = settings.Instances.FirstOrDefault(i => i.IsDefault) ?? settings.Instances[0];
            settings.ActiveInstanceId = next.Id;
        }

        Save();
        Log.Write($"INSTANCE: удалён «{instance.Name}»");
        return true;
    }

    public InstanceStats GetStats(GameInstance instance)
    {
        var directory = GetDirectory(instance);
        var mods = 0;
        var worlds = 0;

        try
        {
            var modsDirectory = Path.Combine(directory, "mods");
            if (Directory.Exists(modsDirectory))
            {
                mods = Directory.EnumerateFiles(modsDirectory)
                    .Count(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase));
            }

            var savesDirectory = Path.Combine(directory, "saves");
            if (Directory.Exists(savesDirectory))
                worlds = Directory.EnumerateDirectories(savesDirectory).Count();
        }
        catch
        {
            // unreadable instance: show zeroes rather than breaking the page
        }

        long size = 0;
        var excluded = instance.IsDefault
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "app", "instances", "libraries", "assets", "versions", "natives", "logs", "backups"
            }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var relative = Path.GetRelativePath(directory, file);
                        var first = relative.Split(Path.DirectorySeparatorChar, '/')[0];
                        if (excluded.Contains(first)) continue;
                        size += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // unreadable file: skip
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        return new InstanceStats(mods, worlds, size);
    }

    private static void EnsureLayout(string directory)
    {
        foreach (var name in new[] { "mods", "config", "saves", "resourcepacks", "shaderpacks" })
            Directory.CreateDirectory(Path.Combine(directory, name));
    }

    /// <summary>
    /// Mods imported from other launchers often keep the read-only attribute.
    /// Windows refuses to delete such files, so clear it before removing an instance.
    /// </summary>
    private static void DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            ClearReadOnly(file);
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            ClearReadOnly(child);

        Directory.Delete(directory, recursive: true);
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // Let the caller decide whether the file can still be deleted.
        }
    }

    /// <summary>Best-effort cleanup after a failed clone: no half-copied folders.</summary>
    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                DeleteDirectory(directory);
        }
        catch
        {
            // ignored
        }
    }

    private static string CleanName(string name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) clean = "Новый инстанс";
        if (clean.Length > 40) clean = clean[..40].TrimEnd();
        return clean;
    }

    private static void CopyDirectory(
        string sourceRoot, string targetRoot, string folder,
        IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        var source = Path.Combine(sourceRoot, folder);
        if (!Directory.Exists(source)) return;

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, files[i]);
            var destination = Path.Combine(targetRoot, folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(files[i], destination, overwrite: false);
            ClearReadOnly(destination);

            progress?.Report(new LaunchProgress(
                files.Count == 0 ? 100 : (i + 1) * 100d / files.Count,
                "Копирование инстанса",
                $"{folder}: {i + 1} / {files.Count}"));
        }
    }

    private static void CopyFile(string sourceRoot, string targetRoot, string name)
    {
        var source = Path.Combine(sourceRoot, name);
        if (!File.Exists(source)) return;

        try
        {
            var destination = Path.Combine(targetRoot, name);
            File.Copy(source, destination, overwrite: false);
            ClearReadOnly(destination);
        }
        catch
        {
            // a locked options.txt should not block creating the instance
        }
    }
}
