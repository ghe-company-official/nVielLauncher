using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace nVielLauncher.Services;

public sealed record BackupInfo(string FilePath, string FileName, long Size, DateTime CreatedUtc, string Reason);

/// <summary>
/// ZIP backups of everything that is expensive to lose: worlds, configs,
/// options and the server list. Runtime files and mods are not included.
/// </summary>
public sealed class BackupService
{
    private static readonly string[] Folders = { "saves", "config" };
    private static readonly string[] Files = { "options.txt", "servers.dat" };
    private static readonly Regex NamePattern = new(@"^(\d{8}-\d{6}-\d{3})-(.+)$", RegexOptions.Compiled);

    private readonly string _root;

    public BackupService(string? root = null)
    {
        _root = root ?? AppPaths.BackupsDirectory;
    }

    public string Root => _root;

    public string DirectoryFor(string instanceId) => Path.Combine(_root, instanceId);

    public IReadOnlyList<BackupInfo> List(string instanceId)
    {
        var directory = DirectoryFor(instanceId);
        if (!Directory.Exists(directory)) return Array.Empty<BackupInfo>();

        var result = new List<BackupInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.zip"))
        {
            try
            {
                var info = new FileInfo(path);
                var name = Path.GetFileNameWithoutExtension(info.Name);
                var match = NamePattern.Match(name);
                var reason = match.Success ? match.Groups[2].Value : "backup";
                result.Add(new BackupInfo(info.FullName, info.Name, info.Length, info.LastWriteTimeUtc, reason));
            }
            catch
            {
                // unreadable file: skip it
            }
        }

        return result.OrderByDescending(b => b.CreatedUtc).ToList();
    }

    public async Task<BackupInfo?> CreateAsync(
        string gameDirectory,
        string instanceId,
        string reason,
        int retention,
        IProgress<LaunchProgress>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() => Create(gameDirectory, instanceId, reason, retention, progress, ct), ct);
    }

    private BackupInfo? Create(
        string gameDirectory,
        string instanceId,
        string reason,
        int retention,
        IProgress<LaunchProgress>? progress,
        CancellationToken ct)
    {
        if (!Directory.Exists(gameDirectory)) return null;

        var files = Collect(gameDirectory);
        if (files.Count == 0)
        {
            progress?.Report(new LaunchProgress(100, "Резервная копия", "Нечего сохранять"));
            return null;
        }

        var directory = DirectoryFor(instanceId);
        Directory.CreateDirectory(directory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var safeReason = Sanitize(reason);
        var path = Path.Combine(directory, $"{stamp}-{safeReason}.zip");

        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                for (var i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (fullPath, entryName) = files[i];
                    archive.CreateEntryFromFile(fullPath, entryName, CompressionLevel.Fastest);

                    progress?.Report(new LaunchProgress(
                        (i + 1) * 100d / files.Count,
                        "Резервная копия",
                        $"{i + 1} / {files.Count} файлов"));
                }
            }
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }

        if (retention > 0)
            Trim(instanceId, retention);

        var info = new FileInfo(path);
        Log.Write($"BACKUP: {info.Name} · {info.Length / 1024 / 1024} МБ · {instanceId}");
        return new BackupInfo(info.FullName, info.Name, info.Length, info.LastWriteTimeUtc, safeReason);
    }

    public async Task RestoreAsync(
        BackupInfo backup,
        string gameDirectory,
        IProgress<LaunchProgress>? progress,
        CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(backup.FilePath))
                throw new FileNotFoundException("Резервная копия не найдена.", backup.FilePath);

            Directory.CreateDirectory(gameDirectory);
            var root = Path.GetFullPath(gameDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(backup.FilePath);
            for (var i = 0; i < archive.Entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.Entries[i];
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var destination = Path.GetFullPath(Path.Combine(gameDirectory, entry.FullName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);

                progress?.Report(new LaunchProgress(
                    (i + 1) * 100d / archive.Entries.Count,
                    "Восстановление",
                    $"{i + 1} / {archive.Entries.Count} файлов"));
            }
        }, ct);
    }

    public void Delete(BackupInfo backup)
    {
        try
        {
            if (File.Exists(backup.FilePath))
                File.Delete(backup.FilePath);
        }
        catch (Exception ex)
        {
            Log.Write("BACKUP: не удалось удалить копию: " + ex.Message);
        }
    }

    public bool ShouldAutoBackup(GameInstance instance)
    {
        var settings = AppServices.Settings.Current;
        if (!settings.AutoBackupBeforeLaunch) return false;
        if (instance.LastBackupUtc is { } last &&
            DateTime.UtcNow - last < TimeSpan.FromHours(settings.AutoBackupIntervalHours))
        {
            return false;
        }

        return Collect(AppServices.Instances.GetDirectory(instance)).Count > 0;
    }

    private void Trim(string instanceId, int retention)
    {
        var backups = List(instanceId);
        foreach (var old in backups.Skip(Math.Max(1, retention)))
            Delete(old);
    }

    private static List<(string FullPath, string EntryName)> Collect(string gameDirectory)
    {
        var result = new List<(string, string)>();

        foreach (var folder in Folders)
        {
            var path = Path.Combine(gameDirectory, folder);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(gameDirectory, file).Replace('\\', '/');
                result.Add((file, relative));
            }
        }

        foreach (var name in Files)
        {
            var path = Path.Combine(gameDirectory, name);
            if (File.Exists(path))
                result.Add((path, name));
        }

        return result;
    }

    private static string Sanitize(string value)
    {
        var clean = new string((value ?? "backup")
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());
        return clean.Length == 0 ? "backup" : clean[..Math.Min(clean.Length, 30)];
    }
}
