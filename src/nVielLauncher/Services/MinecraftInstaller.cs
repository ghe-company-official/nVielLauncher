using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace nVielLauncher.Services;

public sealed class MinecraftInstaller
{
    public const string MinecraftVersion = "1.21.4";

    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string FabricLoaderListUrl = "https://meta.fabricmc.net/v2/versions/loader/{0}";
    private const string FabricProfileUrl = "https://meta.fabricmc.net/v2/versions/loader/{0}/{1}/profile/json";
    private const string AssetBaseUrl = "https://resources.download.minecraft.net";

    private static readonly Dictionary<string, bool> Features = new(StringComparer.OrdinalIgnoreCase)
    {
        ["is_demo_user"] = false,
        ["has_custom_resolution"] = false,
        ["has_quick_plays_support"] = false,
        ["is_quick_play_singleplayer"] = false,
        ["is_quick_play_multiplayer"] = false,
        ["is_quick_play_realms"] = false
    };

    private readonly HttpService _http;
    private readonly object _reportGate = new();
    private long _lastReport;

    public MinecraftInstaller(HttpService http)
    {
        _http = http;
    }

    public async Task<LaunchPlan> EnsureAsync(
        string gameDirectory,
        IProgress<LaunchProgress> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(gameDirectory);
        progress.Report(new LaunchProgress(0, "Подготовка", "Minecraft 1.21.4 · Fabric"));

        var baseVersion = await LoadBaseVersionAsync(gameDirectory, progress, ct);
        progress.Report(new LaunchProgress(3, "Версия найдена", "Minecraft " + baseVersion.Id));

        var (fabricProfile, loaderVersion) = await LoadFabricAsync(gameDirectory, progress, ct);
        var versionName = string.IsNullOrWhiteSpace(fabricProfile.Id)
            ? $"fabric-loader-{loaderVersion}-{MinecraftVersion}"
            : fabricProfile.Id;

        progress.Report(new LaunchProgress(5, "Fabric готов", $"Загрузчик {loaderVersion}"));

        var versionDir = Path.Combine(gameDirectory, "versions", MinecraftVersion);
        var clientJar = Path.Combine(versionDir, $"{MinecraftVersion}.jar");
        var clientDownload = baseVersion.Downloads?.Client
                             ?? throw new InvalidOperationException("В манифесте нет клиента Minecraft.");

        await _http.DownloadAsync(clientDownload.Url, clientJar, clientDownload.Size, clientDownload.Sha1,
            new Progress<double>(p => Report(progress, 6 + p * 6, "Клиент Minecraft", $"{p * 100:0}%")), ct);

        progress.Report(new LaunchProgress(12, "Библиотеки", "Собираем список файлов"));

        var classpath = new List<string>();
        var downloads = new List<DownloadItem>();
        var natives = new List<NativeItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var libraries = MergeLibraries(baseVersion, fabricProfile);
        foreach (var library in libraries)
        {
            if (!IsAllowed(library.Rules)) continue;

            // New-style native libraries are separate entries whose Maven name carries a
            // "natives-<os>" classifier; they must be extracted, never put on the classpath.
            if (IsNativeLibrary(library.Name))
            {
                var nativeJar = library.Downloads?.Artifact;
                if (nativeJar is not null && !string.IsNullOrWhiteSpace(nativeJar.Url))
                {
                    var relative = nativeJar.Path ?? MavenPath(library.Name);
                    var destination = Path.Combine(gameDirectory, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));

                    AddDownload(downloads, seenPaths, nativeJar, destination);
                    natives.Add(new NativeItem(destination, library.Extract?.Exclude ?? new List<string>()));
                }

                continue;
            }

            var artifact = library.Downloads?.Artifact;
            if (artifact is not null && !string.IsNullOrWhiteSpace(artifact.Url))
            {
                var relative = artifact.Path ?? MavenPath(library.Name);
                var destination = Path.Combine(gameDirectory, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));

                if (!classpath.Contains(destination, StringComparer.OrdinalIgnoreCase))
                    classpath.Add(destination);

                AddDownload(downloads, seenPaths, artifact, destination);
            }
            else if (!string.IsNullOrWhiteSpace(library.Url) && !string.IsNullOrWhiteSpace(library.Name))
            {
                // Fabric (and some other) libraries only provide a Maven repo + name, without a downloads block.
                var relative = MavenPath(library.Name);
                var destination = Path.Combine(gameDirectory, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));

                if (!classpath.Contains(destination, StringComparer.OrdinalIgnoreCase))
                    classpath.Add(destination);

                var mavenArtifact = new DownloadArtifact
                {
                    Url = library.Url.TrimEnd('/') + "/" + relative,
                    Path = relative,
                    Size = 0,
                    Sha1 = null
                };

                AddDownload(downloads, seenPaths, mavenArtifact, destination);
            }

            // Legacy natives declaration (classifier map on the parent library).
            if (library.Natives is not null &&
                library.Natives.TryGetValue("windows", out var classifier) &&
                library.Downloads?.Classifiers is not null &&
                library.Downloads.Classifiers.TryGetValue(classifier, out var nativeArtifact) &&
                !string.IsNullOrWhiteSpace(nativeArtifact.Url))
            {
                var relative = nativeArtifact.Path ?? MavenPath(library.Name, classifier);
                var destination = Path.Combine(gameDirectory, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));

                AddDownload(downloads, seenPaths, nativeArtifact, destination);
                natives.Add(new NativeItem(destination, library.Extract?.Exclude ?? new List<string>()));
            }
        }

        await DownloadParallelAsync(downloads, 8, 12, 36, "Библиотеки", progress, ct);

        var assetsDirectory = Path.Combine(gameDirectory, "assets");
        var assetIndex = baseVersion.AssetIndex
                         ?? throw new InvalidOperationException("В версии нет индекса ресурсов.");

        var indexFile = Path.Combine(assetsDirectory, "indexes", assetIndex.Id + ".json");
        await _http.DownloadAsync(assetIndex.Url, indexFile, assetIndex.Size, assetIndex.Sha1, null, ct, false);
        progress.Report(new LaunchProgress(38, "Ресурсы", "Читаем индекс"));

        var indexJson = await File.ReadAllTextAsync(indexFile, ct);
        var index = JsonSerializer.Deserialize<AssetIndex>(indexJson, HttpService.JsonOptions)
                    ?? throw new InvalidOperationException("Не удалось прочитать индекс ресурсов.");

        var objects = index.Objects?.Values
            .GroupBy(o => o.Hash, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList() ?? new List<AssetObject>();

        await DownloadAssetsAsync(assetsDirectory, objects, progress, ct);

        progress.Report(new LaunchProgress(90, "Нативные библиотеки", "Распаковка"));

        var nativesDirectory = Path.Combine(gameDirectory, "natives", versionName);
        ExtractNatives(natives, nativesDirectory);
        progress.Report(new LaunchProgress(95, "Нативные библиотеки", "Готово"));

        string? logConfig = null;
        var loggingFile = baseVersion.Logging?.Client?.File;
        if (loggingFile is not null && !string.IsNullOrWhiteSpace(loggingFile.Url))
        {
            var name = string.IsNullOrWhiteSpace(loggingFile.Id)
                ? Path.GetFileName(new Uri(loggingFile.Url).LocalPath)
                : loggingFile.Id;
            var destination = Path.Combine(assetsDirectory, "log_configs", name);
            await _http.DownloadAsync(loggingFile.Url, destination, loggingFile.Size, loggingFile.Sha1, null, ct, false);
            logConfig = destination;
        }

        progress.Report(new LaunchProgress(97, "Проверка завершена", $"Fabric {loaderVersion} · Minecraft {MinecraftVersion}"));

        return new LaunchPlan
        {
            BaseVersion = baseVersion,
            FabricProfile = fabricProfile,
            VersionName = versionName,
            GameDirectory = gameDirectory,
            ClientJar = clientJar,
            Classpath = classpath.Append(clientJar).ToList(),
            NativesDirectory = nativesDirectory,
            AssetsDirectory = assetsDirectory,
            AssetIndexName = assetIndex.Id,
            LogConfigPath = logConfig,
            RequiredJavaMajor = baseVersion.JavaVersion?.MajorVersion > 0
                ? baseVersion.JavaVersion.MajorVersion
                : 21
        };
    }

    // ------------------------------------------------------------------ metadata

    private async Task<VersionDetails> LoadBaseVersionAsync(
        string gameDirectory, IProgress<LaunchProgress> progress, CancellationToken ct)
    {
        var versionDir = Path.Combine(gameDirectory, "versions", MinecraftVersion);
        Directory.CreateDirectory(versionDir);
        var jsonPath = Path.Combine(versionDir, MinecraftVersion + ".json");

        if (File.Exists(jsonPath))
        {
            var cached = TryReadVersion(jsonPath);
            if (cached is not null) return cached;
        }

        Report(progress, 1, "Загрузка манифеста", "piston-meta.mojang.com", force: true);

        var manifestPath = Path.Combine(gameDirectory, "versions", "version_manifest_v2.json");
        VersionManifest? manifest = null;

        try
        {
            var json = await _http.GetStringAsync(ManifestUrl, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(manifestPath, json, ct);
            manifest = JsonSerializer.Deserialize<VersionManifest>(json, HttpService.JsonOptions);
        }
        catch (Exception ex) when (File.Exists(manifestPath))
        {
            Log.Write("Манифест недоступен, используем кэш: " + ex.Message);
            manifest = JsonSerializer.Deserialize<VersionManifest>(
                await File.ReadAllTextAsync(manifestPath, ct), HttpService.JsonOptions);
        }

        var entry = manifest?.Versions?.FirstOrDefault(v => v.Id == MinecraftVersion)
                    ?? throw new InvalidOperationException("Minecraft " + MinecraftVersion + " не найден в манифесте Mojang.");

        Report(progress, 2, "Скачивание описания версии", MinecraftVersion, force: true);

        var versionJson = await _http.GetStringAsync(entry.Url, ct);
        var details = JsonSerializer.Deserialize<VersionDetails>(versionJson, HttpService.JsonOptions)
                      ?? throw new InvalidOperationException("Не удалось прочитать описание версии.");

        await File.WriteAllTextAsync(jsonPath, versionJson, ct);
        return details;
    }

    private async Task<(VersionDetails Profile, string LoaderVersion)> LoadFabricAsync(
        string gameDirectory, IProgress<LaunchProgress> progress, CancellationToken ct)
    {
        var cacheDirectory = Path.Combine(gameDirectory, "versions", "fabric-loader");
        Directory.CreateDirectory(cacheDirectory);
        var profileCache = Path.Combine(cacheDirectory, "profile.json");
        var loaderCache = Path.Combine(cacheDirectory, "loader.txt");

        try
        {
            Report(progress, 3.5, "Fabric", "Запрашиваем версию загрузчика", force: true);

            var list = await _http.GetJsonAsync<List<FabricLoaderEntry>>(
                string.Format(FabricLoaderListUrl, MinecraftVersion), ct);

            var entry = list?.FirstOrDefault(e => e.Loader?.Stable == true) ?? list?.FirstOrDefault();
            var loaderVersion = entry?.Loader?.Version;
            if (string.IsNullOrWhiteSpace(loaderVersion))
                throw new InvalidOperationException("Fabric meta вернул пустой список.");

            var profileJson = await _http.GetStringAsync(
                string.Format(FabricProfileUrl, MinecraftVersion, loaderVersion), ct);

            var profile = JsonSerializer.Deserialize<VersionDetails>(profileJson, HttpService.JsonOptions)
                          ?? throw new InvalidOperationException("Не удалось прочитать профиль Fabric.");

            await File.WriteAllTextAsync(profileCache, profileJson, ct);
            await File.WriteAllTextAsync(loaderCache, loaderVersion, ct);

            return (profile, loaderVersion);
        }
        catch (Exception ex) when (File.Exists(profileCache))
        {
            Log.Write("Fabric meta недоступен, используем кэш: " + ex.Message);
            var profile = TryReadVersion(profileCache)
                          ?? throw new InvalidOperationException("Кэш Fabric повреждён.");
            var loader = File.Exists(loaderCache)
                ? (await File.ReadAllTextAsync(loaderCache, ct)).Trim()
                : "unknown";
            return (profile, loader);
        }
    }

    private static VersionDetails? TryReadVersion(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<VersionDetails>(File.ReadAllText(path), HttpService.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ libraries

    private static List<LibraryInfo> MergeLibraries(VersionDetails baseVersion, VersionDetails fabric)
    {
        var result = new List<LibraryInfo>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(LibraryInfo library)
        {
            var key = library.ModuleKey;
            if (index.TryGetValue(key, out var position))
                result[position] = library;
            else
            {
                index[key] = result.Count;
                result.Add(library);
            }
        }

        foreach (var library in baseVersion.Libraries ?? new List<LibraryInfo>())
            if (IsAllowed(library.Rules))
                Add(library);

        foreach (var library in fabric.Libraries ?? new List<LibraryInfo>())
            Add(library);

        return result;
    }

    private static bool IsAllowed(List<RuleInfo>? rules)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (RuleMatches(rule))
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    /// <summary>True when a library's Maven name carries a "natives-..." classifier.</summary>
    private static bool IsNativeLibrary(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 4) return false;

        var classifier = parts[3];
        var at = classifier.IndexOf('@');
        if (at >= 0) classifier = classifier[..at];

        return classifier.StartsWith("natives", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RuleMatches(RuleInfo rule)
    {
        if (rule.Os is not null)
        {
            var name = rule.Os.Name;
            if (!string.IsNullOrWhiteSpace(name) && !name.Equals("windows", StringComparison.OrdinalIgnoreCase))
                return false;

            var arch = rule.Os.Arch;
            if (!string.IsNullOrWhiteSpace(arch))
            {
                var actual = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
                if (arch == "x86" && actual == "x86_64") { /* x86 libs are allowed on x64 */ }
                else if (!arch.Equals(actual, StringComparison.OrdinalIgnoreCase)) return false;
            }
        }

        if (rule.Features is not null)
        {
            foreach (var feature in rule.Features)
            {
                var actual = Features.TryGetValue(feature.Key, out var value) && value;
                if (actual != feature.Value) return false;
            }
        }

        return true;
    }

    private static void AddDownload(
        List<DownloadItem> downloads, HashSet<string> seen, DownloadArtifact artifact, string destination)
    {
        if (!seen.Add(destination)) return;
        downloads.Add(new DownloadItem(artifact, destination));
    }

    private async Task DownloadParallelAsync(
        List<DownloadItem> downloads,
        int parallelism,
        double percentFrom,
        double percentTo,
        string stage,
        IProgress<LaunchProgress> progress,
        CancellationToken ct)
    {
        var total = downloads.Count;
        if (total == 0) return;

        progress.Report(new LaunchProgress(percentFrom, stage, $"0 / {total}", null));

        var done = 0;
        await Parallel.ForEachAsync(downloads,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (item, token) =>
            {
                await _http.DownloadAsync(item.Artifact.Url, item.Destination, item.Artifact.Size,
                    item.Artifact.Sha1, null, token, verifySha1OnExisting: false);

                var current = Interlocked.Increment(ref done);
                if (current == total || current % 4 == 0)
                {
                    var percent = percentFrom + (percentTo - percentFrom) * current / total;
                    Report(progress, percent, stage, $"{current} / {total}");
                }
            });

        progress.Report(new LaunchProgress(percentTo, stage, $"Готово · {total} файлов", null));
    }

    private static string MavenPath(string name, string? classifierOverride = null)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return name.Replace(':', '/');

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = classifierOverride ?? (parts.Length > 3 ? parts[3] : null);
        var extension = parts.Length > 4 ? parts[4] : "jar";

        var file = $"{artifact}-{version}" + (string.IsNullOrWhiteSpace(classifier) ? "" : "-" + classifier) + "." + extension;
        return string.Join("/", group.Replace('.', '/'), artifact, version, file);
    }

    // ------------------------------------------------------------------ assets

    private async Task DownloadAssetsAsync(
        string assetsDirectory, List<AssetObject> objects, IProgress<LaunchProgress> progress, CancellationToken ct)
    {
        if (objects.Count == 0) return;

        progress.Report(new LaunchProgress(40, "Ресурсы", $"0 / {objects.Count}"));

        var done = 0;
        await Parallel.ForEachAsync(objects,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (asset, token) =>
            {
                var prefix = asset.Hash[..2];
                var destination = Path.Combine(assetsDirectory, "objects", prefix, asset.Hash);
                var url = $"{AssetBaseUrl}/{prefix}/{asset.Hash}";

                await _http.DownloadAsync(url, destination, asset.Size, asset.Hash, null, token,
                    verifySha1OnExisting: false);

                var current = Interlocked.Increment(ref done);
                if (current == objects.Count || current % 20 == 0)
                {
                    var percent = 40 + 50.0 * current / objects.Count;
                    Report(progress, percent, "Ресурсы", $"{current} / {objects.Count}");
                }
            });

        progress.Report(new LaunchProgress(90, "Ресурсы", $"Готово · {objects.Count} файлов"));
    }

    // ------------------------------------------------------------------ natives

    private static void ExtractNatives(List<NativeItem> natives, string nativesDirectory)
    {
        if (Directory.Exists(nativesDirectory)) Directory.Delete(nativesDirectory, recursive: true);
        Directory.CreateDirectory(nativesDirectory);

        var root = Path.GetFullPath(nativesDirectory) + Path.DirectorySeparatorChar;

        foreach (var native in natives)
        {
            if (!File.Exists(native.Path)) continue;

            try
            {
                using var archive = ZipFile.OpenRead(native.Path);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var entryName = entry.FullName.Replace('\\', '/');
                    if (entryName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (native.Exclude.Any(e => entryName.StartsWith(e.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var destination = Path.GetFullPath(Path.Combine(nativesDirectory, entryName));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Не удалось распаковать {Path.GetFileName(native.Path)}: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private void Report(IProgress<LaunchProgress> progress, double percent, string status, string? detail, bool force = false)
    {
        var now = Environment.TickCount64;
        lock (_reportGate)
        {
            if (!force && now - _lastReport < 55) return;
            _lastReport = now;
        }

        progress.Report(new LaunchProgress(percent, status, detail));
    }

    private sealed record DownloadItem(DownloadArtifact Artifact, string Destination);
    private sealed record NativeItem(string Path, List<string> Exclude);
}
