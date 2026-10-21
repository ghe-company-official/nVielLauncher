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
    private readonly SettingsService _settings;

    public InstanceService(SettingsService settings)
    {
        _settings = settings;
        _settings.Current.EnsureInstances();
    }

    public IReadOnlyList<GameInstance> All => _settings.Current.Instances;

    public GameInstance? Active => _settings.Current.ActiveInstance;

    public string GetDirectory(GameInstance instance) =>
        instance.IsDefault
            ? _settings.Current.SharedRuntimeDirectory
            : instance.Directory;

    public void SetActive(string id)
    {
        if (All.All(i => !string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Инстанс не найден.");

        _settings.Current.ActiveInstanceId = id;
        _settings.Save();
    }

    public GameInstance Create(
        string name,
        bool copyFromActive,
        IProgress<LaunchProgress>? progress,
        CancellationToken ct)
    {
        var settings = _settings.Current;
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
        _settings.Save();

        Log.Write($"INSTANCE: создан «{instance.Name}» → {directory}");
        return instance;
    }

    public void Rename(string id, string name)
    {
        var instance = All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException("Инстанс не найден.");

        instance.Name = CleanName(name);
        _settings.Save();
    }

    public bool Delete(string id)
    {
        var settings = _settings.Current;
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
            if (Directory.Exists(instance.Directory))
                Directory.Delete(instance.Directory, recursive: true);
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

        _settings.Save();
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
            File.Copy(source, Path.Combine(targetRoot, name), overwrite: false);
        }
        catch
        {
            // a locked options.txt should not block creating the instance
        }
    }
}
