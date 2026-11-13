using System.IO;
using System.Security.Cryptography;

namespace nVielLauncher.Services;

public sealed record ModEntry(string FileName, long Size, bool Installed, bool UpToDate);

public sealed class ModsService
{
    private const string EmbeddedPrefix = "embedded.";

    public string BundledDirectory => AppPaths.BundledModsDirectory;

    /// <summary>
    /// Installs the launcher's built-in mods into the game folder on every start so they
    /// can never be removed or tampered with. A client mod downloaded from the release
    /// manifest takes precedence over the embedded copy, and stale nViel jars are removed.
    /// </summary>
    public async Task EnsureCoreModsAsync(
        string gameDirectory,
        CancellationToken ct,
        string? clientModSha256 = null,
        string? clientModFileName = null)
    {
        try
        {
            var assembly = typeof(ModsService).Assembly;
            var resources = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(EmbeddedPrefix, StringComparison.Ordinal))
                .ToList();

            if (resources.Count == 0) return;

            var targetFolder = Path.Combine(gameDirectory, "mods");
            Directory.CreateDirectory(targetFolder);

            foreach (var resource in resources)
            {
                ct.ThrowIfCancellationRequested();

                var fileName = resource[EmbeddedPrefix.Length..];
                var destination = Path.Combine(targetFolder, fileName);

                try
                {
                    if (IsClientMod(fileName))
                    {
                        // A client mod installed from manifest.json owns the nViel jar:
                        // never add the embedded (possibly older) copy next to it.
                        if (!string.IsNullOrWhiteSpace(clientModFileName))
                        {
                            var managed = Path.Combine(targetFolder, clientModFileName);
                            if (File.Exists(managed) || File.Exists(managed + ".disabled"))
                                continue;
                        }

                        // The user may have disabled the built-in mod on purpose.
                        if (File.Exists(destination + ".disabled"))
                            continue;

                        // Same build already installed from the manifest.
                        if (!string.IsNullOrWhiteSpace(clientModSha256) &&
                            File.Exists(destination) &&
                            await Sha256MatchesAsync(destination, clientModSha256, ct))
                            continue;
                    }

                    await using var source = assembly.GetManifestResourceStream(resource);
                    if (source is null) continue;

                    if (!await MatchesAsync(source, destination, ct))
                    {
                        if (File.Exists(destination))
                        {
                            try { File.SetAttributes(destination, FileAttributes.Normal); } catch { }
                        }

                        source.Position = 0;
                        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write,
                            FileShare.None, 81920, useAsync: true);
                        await source.CopyToAsync(target, ct);
                    }

                    try { File.SetAttributes(destination, FileAttributes.ReadOnly); } catch { }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // built-in mods must never break the launch
                }
            }

            CleanupStaleClientMods(targetFolder, resources, clientModFileName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ignored
        }
    }

    private static bool IsClientMod(string fileName) =>
        fileName.StartsWith("nviel", StringComparison.OrdinalIgnoreCase);

    private static string StripDisabledSuffix(string fileName) =>
        fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;

    /// <summary>Removes older nViel jars so Fabric does not load two client mod versions.</summary>
    private static void CleanupStaleClientMods(
        string modsFolder,
        IEnumerable<string> embeddedResources,
        string? clientModFileName)
    {
        try
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var managed = string.IsNullOrWhiteSpace(clientModFileName)
                ? null
                : StripDisabledSuffix(clientModFileName);

            if (managed is not null &&
                (File.Exists(Path.Combine(modsFolder, managed)) ||
                 File.Exists(Path.Combine(modsFolder, managed + ".disabled"))))
            {
                // The manifest-managed build wins; any embedded or renamed nViel jar is stale.
                keep.Add(managed);
            }
            else
            {
                foreach (var resource in embeddedResources)
                {
                    var name = resource[EmbeddedPrefix.Length..];
                    if (IsClientMod(name)) keep.Add(name);
                }

                if (managed is not null) keep.Add(managed);
            }

            foreach (var file in Directory.EnumerateFiles(modsFolder, "*.jar*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (!IsClientMod(name)) continue;

                var baseName = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? name[..^".disabled".Length]
                    : name;

                if (keep.Contains(baseName)) continue;

                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    Log.Write("Удалён устаревший клиентский мод: " + name);
                }
                catch
                {
                    // a locked stale jar is retried on the next launch
                }
            }
        }
        catch
        {
            // optional cleanup
        }
    }

    private static async Task<bool> Sha256MatchesAsync(string path, string expected, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> MatchesAsync(Stream source, string destination, CancellationToken ct)
    {
        if (!File.Exists(destination)) return false;

        var info = new FileInfo(destination);
        if (info.Length != source.Length) return false;

        var incoming = new byte[source.Length];
        source.Position = 0;
        await source.ReadExactlyAsync(incoming, ct);
        source.Position = 0;

        var existing = await File.ReadAllBytesAsync(destination, ct);
        return existing.AsSpan().SequenceEqual(incoming);
    }

    public IReadOnlyList<string> GetBundledJars()
    {
        try
        {
            if (!Directory.Exists(BundledDirectory)) return Array.Empty<string>();
            return Directory.EnumerateFiles(BundledDirectory, "*.jar", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<ModEntry> GetEntries(string gameDirectory)
    {
        var installedFolder = Path.Combine(gameDirectory, "mods");
        var entries = new List<ModEntry>();

        foreach (var source in GetBundledJars())
        {
            var name = Path.GetFileName(source);
            var dest = Path.Combine(installedFolder, name);
            var size = new FileInfo(source).Length;
            var installed = File.Exists(dest);
            var upToDate = installed && new FileInfo(dest).Length == size &&
                           new FileInfo(dest).LastWriteTimeUtc >= new FileInfo(source).LastWriteTimeUtc;

            entries.Add(new ModEntry(name, size, installed, upToDate));
        }

        return entries;
    }

    public int CountBundled() => GetBundledJars().Count;

    /// <summary>
    /// Reports how many built-in mods are present without exposing their names.
    /// Used by the recovery mode / status cards.
    /// </summary>
    public (int Installed, int Total) GetCoreModStatus(string gameDirectory)
    {
        try
        {
            var assembly = typeof(ModsService).Assembly;
            var resources = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(EmbeddedPrefix, StringComparison.Ordinal))
                .ToList();

            if (resources.Count == 0) return (0, 0);

            var folder = Path.Combine(gameDirectory, "mods");
            var installed = 0;

            foreach (var resource in resources)
            {
                var destination = Path.Combine(folder, resource[EmbeddedPrefix.Length..]);
                if (!File.Exists(destination)) continue;

                try
                {
                    using var source = assembly.GetManifestResourceStream(resource);
                    if (source is not null && new FileInfo(destination).Length == source.Length)
                        installed++;
                }
                catch
                {
                    // a locked file counts as missing
                }
            }

            return (installed, resources.Count);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>Copies bundled mods into the game's mods folder. Returns number copied.</summary>
    public Task<int> SyncAsync(string gameDirectory, IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        var sources = GetBundledJars();
        if (sources.Count == 0)
        {
            Log.Write("Папка mods пуста — пропускаем установку модов.");
            return Task.FromResult(0);
        }

        var targetFolder = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(targetFolder);

        var copied = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var source = sources[i];
            var dest = Path.Combine(targetFolder, Path.GetFileName(source));

            try
            {
                var sourceInfo = new FileInfo(source);
                var needsCopy = !File.Exists(dest);
                if (!needsCopy)
                {
                    var destInfo = new FileInfo(dest);
                    needsCopy = destInfo.Length != sourceInfo.Length ||
                                destInfo.LastWriteTimeUtc < sourceInfo.LastWriteTimeUtc;
                }

                if (needsCopy)
                {
                    File.Copy(source, dest, overwrite: true);
                    File.SetLastWriteTimeUtc(dest, sourceInfo.LastWriteTimeUtc);
                    copied++;
                    Log.Write("Мод установлен: " + Path.GetFileName(source));
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Не удалось установить мод {Path.GetFileName(source)}: {ex.Message}");
            }

            progress?.Report(new LaunchProgress((i + 1) * 100d / sources.Count, "Установка модов",
                $"{i + 1} / {sources.Count}"));
        }

        Log.Write($"Моды синхронизированы: {copied} новых из {sources.Count}.");
        return Task.FromResult(copied);
    }
}
