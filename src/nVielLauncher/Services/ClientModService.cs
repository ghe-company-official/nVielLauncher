using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace nVielLauncher.Services;

public sealed record ClientModInstallResult(int Instances, string Version, bool BackedUp);

/// <summary>
/// Keeps the nViel client mod in sync with the signed release manifest. The mod is
/// installed into every instance, old versions are removed and the previous build is
/// backed up so nVielRE can restore it after a crash.
/// </summary>
public sealed class ClientModService
{
    private const string ModPrefix = "nviel";
    private const string EmbeddedPrefix = "embedded.";

    private readonly SettingsService _settings;
    private readonly InstanceService _instances;
    private readonly UpdateService _updates;
    private readonly string _backupRoot;

    public ClientModService(SettingsService settings, InstanceService instances, UpdateService updates)
        : this(settings, instances, updates, AppPaths.ModUpdatesBackupDirectory)
    {
    }

    /// <summary>Test constructor: redirects mod backups into a temporary folder.</summary>
    internal ClientModService(
        SettingsService settings,
        InstanceService instances,
        UpdateService updates,
        string backupRoot)
    {
        _settings = settings;
        _instances = instances;
        _updates = updates;
        _backupRoot = backupRoot;
    }

    /// <summary>True when the manifest build differs from the jar installed locally.</summary>
    public bool NeedsUpdate(ClientModInfo mod)
    {
        if (string.IsNullOrWhiteSpace(mod.Sha256)) return false;

        var local = GetInstalledSha256();
        return string.IsNullOrWhiteSpace(local) ||
               !local.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hash of the jar that would actually be loaded: the file in the default instance,
    /// then the embedded copy, then the last installed hash from settings.
    /// </summary>
    public string? GetInstalledSha256()
    {
        try
        {
            var defaultInstance = _instances.All.FirstOrDefault(i => i.IsDefault);
            if (defaultInstance is not null)
            {
                var mods = Path.Combine(_instances.GetDirectory(defaultInstance), "mods");
                var jar = FindClientJars(mods).FirstOrDefault();
                if (jar is not null)
                {
                    using var stream = File.OpenRead(jar);
                    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                }
            }
        }
        catch
        {
            // fall through to the embedded copy
        }

        var embedded = FindEmbeddedJar();
        if (embedded is not null)
        {
            try
            {
                using var stream = embedded.Value.Assembly.GetManifestResourceStream(embedded.Value.Resource);
                if (stream is not null)
                    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch
            {
                // fall through to settings
            }
        }

        return string.IsNullOrWhiteSpace(_settings.Current.ClientModSha256)
            ? null
            : _settings.Current.ClientModSha256;
    }

    public string? GetInstalledVersion()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Current.ClientModVersion))
            return _settings.Current.ClientModVersion;

        try
        {
            var defaultInstance = _instances.All.FirstOrDefault(i => i.IsDefault);
            if (defaultInstance is null) return null;
            var mods = Path.Combine(_instances.GetDirectory(defaultInstance), "mods");
            var jar = FindClientJars(mods).FirstOrDefault();
            return jar is null ? null : ParseVersionFromName(Path.GetFileName(jar));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads, verifies and installs the client mod into every instance.</summary>
    public async Task<ClientModInstallResult> InstallAsync(
        ClientModInfo mod,
        IProgress<double>? progress,
        IProgress<string>? stage,
        CancellationToken ct)
    {
        var jar = await _updates.DownloadClientModAsync(mod, progress, stage, ct);
        stage?.Report("Установка мода во все инстансы");
        return InstallFromFile(mod, jar, ct);
    }

    public ClientModInstallResult InstallFromFile(ClientModInfo mod, string jarPath, CancellationToken ct)
    {
        if (!File.Exists(jarPath))
            throw new FileNotFoundException("Скачанный клиентский мод не найден.", jarPath);

        var targetName = SafeJarName(mod.FileName);
        var backupDirectory = Path.Combine(
            _backupRoot,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);

        var previousVersion = GetInstalledVersion();
        var backedUp = false;
        var installed = 0;

        foreach (var instance in _instances.All)
        {
            ct.ThrowIfCancellationRequested();

            var mods = Path.Combine(_instances.GetDirectory(instance), "mods");
            Directory.CreateDirectory(mods);

            var existing = FindClientJars(mods).ToList();
            var wasDisabled = existing.Count > 0 &&
                              existing.All(f => f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));

            if (existing.Count > 0)
            {
                var instanceBackup = Path.Combine(backupDirectory, instance.Id);
                Directory.CreateDirectory(instanceBackup);

                foreach (var file in existing)
                {
                    try
                    {
                        File.Copy(file, Path.Combine(instanceBackup, Path.GetFileName(file)), overwrite: true);
                        backedUp = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"MOD UPDATE: не удалось сохранить {file}: {ex.Message}");
                    }
                }
            }

            foreach (var file in existing)
                TryDeleteFile(file);

            var destination = Path.Combine(mods, wasDisabled ? targetName + ".disabled" : targetName);
            File.Copy(jarPath, destination, overwrite: true);

            try { File.SetAttributes(destination, FileAttributes.ReadOnly); } catch { }
            installed++;
        }

        var settings = _settings.Current;
        settings.ClientModVersion = mod.Version;
        settings.ClientModSha256 = mod.Sha256;
        settings.ClientModFileName = targetName;
        settings.ModRollbackSuggested = false;

        settings.PendingModRollback = backedUp
            ? new ModRollbackInfo
            {
                BackupDirectory = backupDirectory,
                PreviousVersion = previousVersion,
                NewVersion = mod.Version,
                NewSha256 = mod.Sha256,
                InstalledUtc = DateTime.UtcNow
            }
            : null;

        _settings.Save();

        if (!backedUp)
        {
            try
            {
                if (Directory.Exists(backupDirectory))
                    Directory.Delete(backupDirectory, recursive: true);
            }
            catch
            {
                // empty backup folder is harmless
            }
        }

        Log.Write($"MOD UPDATE: установлен nViel {mod.Version} в {installed} инстанс(ов); " +
                  $"бэкап: {(backedUp ? backupDirectory : "нет (первая установка)")}");
        return new ClientModInstallResult(installed, mod.Version, backedUp);
    }

    /// <summary>Puts the backed-up jar back into every instance.</summary>
    public async Task<int> RollbackAsync(CancellationToken ct)
    {
        var rollback = _settings.Current.PendingModRollback;
        if (rollback is null || !rollback.IsUsable)
            throw new InvalidOperationException("Резервная копия предыдущего мода не найдена.");

        var restored = 0;

        foreach (var instance in _instances.All)
        {
            ct.ThrowIfCancellationRequested();

            var mods = Path.Combine(_instances.GetDirectory(instance), "mods");
            Directory.CreateDirectory(mods);

            foreach (var file in FindClientJars(mods).ToList())
                TryDeleteFile(file);

            var backup = Path.Combine(rollback.BackupDirectory, instance.Id);
            if (!Directory.Exists(backup)) continue;

            foreach (var source in Directory.EnumerateFiles(backup))
            {
                var destination = Path.Combine(mods, Path.GetFileName(source));
                File.Copy(source, destination, overwrite: true);
                try { File.SetAttributes(destination, FileAttributes.ReadOnly); } catch { }
            }

            restored++;
        }

        var settings = _settings.Current;
        settings.ClientModVersion = rollback.PreviousVersion;
        settings.ClientModSha256 = await ComputeInstalledHashAsync(ct);
        settings.PendingModRollback = null;
        settings.ModRollbackSuggested = false;
        _settings.Save();

        TryDeleteDirectory(rollback.BackupDirectory);
        Log.Write($"MOD UPDATE: выполнен откат мода на {rollback.PreviousVersion ?? "предыдущую версию"} " +
                  $"в {restored} инстанс(ов).");
        return restored;
    }

    /// <summary>Keeps the new mod and forgets the rollback point.</summary>
    public void AcceptCurrent()
    {
        var rollback = _settings.Current.PendingModRollback;
        _settings.Current.PendingModRollback = null;
        _settings.Current.ModRollbackSuggested = false;
        _settings.Save();

        if (rollback is not null)
            TryDeleteDirectory(rollback.BackupDirectory);

        Log.Write("MOD UPDATE: новая версия мода оставлена, точка отката удалена.");
    }

    /// <summary>Called when Minecraft exits: a crash right after an update asks nVielRE to offer a rollback.</summary>
    public void HandleGameExit(int exitCode)
    {
        var settings = _settings.Current;
        var pending = settings.PendingModRollback;
        if (pending is null) return;

        if (exitCode == 0)
        {
            settings.PendingModRollback = null;
            settings.ModRollbackSuggested = false;
            _settings.Save();
            TryDeleteDirectory(pending.BackupDirectory);
            Log.Write("MOD UPDATE: игра запустилась и закрылась штатно — точка отката удалена.");
            return;
        }

        if (DateTime.UtcNow - pending.InstalledUtc > TimeSpan.FromDays(14))
        {
            AcceptCurrent();
            return;
        }

        settings.ModRollbackSuggested = true;
        _settings.Save();
        Log.Write($"MOD UPDATE: игра завершилась с кодом {exitCode}; nVielRE предложит откат мода.");
    }

    private async Task<string?> ComputeInstalledHashAsync(CancellationToken ct)
    {
        try
        {
            var defaultInstance = _instances.All.FirstOrDefault(i => i.IsDefault);
            if (defaultInstance is null) return null;

            var mods = Path.Combine(_instances.GetDirectory(defaultInstance), "mods");
            var jar = FindClientJars(mods).FirstOrDefault();
            return jar is null ? null : await UpdateService.ComputeSha256Async(jar, ct);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> FindClientJars(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory)) yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(modsDirectory, "*.jar*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(ModPrefix, StringComparison.OrdinalIgnoreCase) &&
                (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))
                yield return file;
        }
    }

    private static (Assembly Assembly, string Resource)? FindEmbeddedJar()
    {
        var assembly = typeof(ClientModService).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(name =>
            name.StartsWith(EmbeddedPrefix + ModPrefix, StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

        return resource is null ? null : (assembly, resource);
    }

    private static string SafeJarName(string value)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(value) ? "nviel-client.jar" : value);
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            name += ".jar";
        return name;
    }

    private static string? ParseVersionFromName(string fileName)
    {
        var name = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;
        name = Path.GetFileNameWithoutExtension(name);

        if (!name.StartsWith(ModPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = name[ModPrefix.Length..].TrimStart('-', '_', ' ');
        return rest.Length == 0 ? null : rest;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Write($"MOD UPDATE: не удалось удалить {path}: {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // stale backups are cleaned up on the next successful update
        }
    }
}
