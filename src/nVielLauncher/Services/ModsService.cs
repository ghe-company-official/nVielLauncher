using System.IO;

namespace nVielLauncher.Services;

public sealed record ModEntry(string FileName, long Size, bool Installed, bool UpToDate);

public sealed class ModsService
{
    private const string EmbeddedPrefix = "embedded.";

    public string BundledDirectory => AppPaths.BundledModsDirectory;

    /// <summary>
    /// Installs the launcher's built-in mods into the game folder on every start so they
    /// can never be removed or tampered with.
    /// </summary>
    public async Task EnsureCoreModsAsync(string gameDirectory, CancellationToken ct)
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
