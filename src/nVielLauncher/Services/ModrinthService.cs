using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nVielLauncher.Services;

// ------------------------------------------------------------------ Modrinth models

public sealed class ModrinthHit
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    public long Downloads { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    public string? Author { get; set; }
}

public sealed class ModrinthSearchResponse
{
    public List<ModrinthHit>? Hits { get; set; }
}

public sealed class ModrinthVersion
{
    public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "release";
    [JsonPropertyName("date_published")] public DateTimeOffset DatePublished { get; set; }
    public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    public List<ModrinthDependency> Dependencies { get; set; } = new();
    public List<ModrinthFile> Files { get; set; } = new();
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "";
}

public sealed class ModrinthFile
{
    public string Filename { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public bool Primary { get; set; }
    public ModrinthHashes? Hashes { get; set; }
}

public sealed class ModrinthHashes
{
    public string? Sha1 { get; set; }
    public string? Sha512 { get; set; }
}

// ------------------------------------------------------------------ .mrpack models

public sealed class MrpackIndex
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = "";
    public List<MrpackFile> Files { get; set; } = new();
    public Dictionary<string, string>? Dependencies { get; set; }
}

public sealed class MrpackFile
{
    public string Path { get; set; } = "";
    public Dictionary<string, string>? Hashes { get; set; }
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
    public List<string> Downloads { get; set; } = new();
    public MrpackEnvironment? Env { get; set; }
}

public sealed class MrpackEnvironment
{
    public string? Client { get; set; }
    public string? Server { get; set; }
}

// ------------------------------------------------------------------ local state

public sealed class InstalledModInfo
{
    public string FileName { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string VersionNumber { get; set; } = "";
}

public sealed class InstalledModpack
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public DateTime InstalledUtc { get; set; }
}

public sealed class ModrinthState
{
    public Dictionary<string, InstalledModInfo> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<InstalledModpack> Modpacks { get; set; } = new();
}

// ------------------------------------------------------------------ service

public sealed class ModrinthService
{
    public const string GameVersion = "1.21.4";
    public const string FabricApiProjectId = "P7dR8mSH";

    private const string ApiBase = "https://api.modrinth.com/v2";
    private static readonly JsonSerializerOptions Json = HttpService.JsonOptions;

    private readonly HttpService _http;
    private readonly object _gate = new();
    private ModrinthState _state;

    public ModrinthService(HttpService http)
    {
        _http = http;
        _state = LoadState();
    }

    public IReadOnlyList<InstalledModpack> Modpacks
    {
        get
        {
            lock (_gate) return _state.Modpacks.ToList();
        }
    }

    // ------------------------------------------------------------------ search / versions

    public async Task<List<ModrinthHit>> SearchAsync(string query, string projectType, CancellationToken ct)
    {
        var facets = "[[\"project_type:" + projectType + "\"],[\"categories:fabric\"],[\"versions:" + GameVersion + "\"]]";
        var url = $"{ApiBase}/search?limit=24&index=relevance&query={Uri.EscapeDataString(query ?? "")}" +
                  $"&facets={Uri.EscapeDataString(facets)}";

        var response = await _http.GetJsonAsync<ModrinthSearchResponse>(url, ct);
        return response?.Hits ?? new List<ModrinthHit>();
    }

    public async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId, CancellationToken ct)
    {
        var loaders = Uri.EscapeDataString("[\"fabric\"]");
        var versions = Uri.EscapeDataString("[\"" + GameVersion + "\"]");
        var url = $"{ApiBase}/project/{projectId}/version?loaders={loaders}&game_versions={versions}";

        var list = await _http.GetJsonAsync<List<ModrinthVersion>>(url, ct) ?? new List<ModrinthVersion>();
        return list
            .OrderByDescending(v => v.VersionType == "release")
            .ThenByDescending(v => v.DatePublished)
            .ToList();
    }

    // ------------------------------------------------------------------ mod install

    /// <summary>Ensures a mod and all its required dependencies are installed into the game's mods folder.</summary>
    public async Task<bool> EnsureProjectAsync(
        string projectId,
        string modsDirectory,
        IProgress<LaunchProgress>? progress,
        CancellationToken ct,
        HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(projectId)) return false;

        var versions = await GetVersionsAsync(projectId, ct);
        var best = versions.FirstOrDefault();
        if (best is null) return false;

        var file = best.Files.FirstOrDefault(f => f.Primary) ?? best.Files.FirstOrDefault();
        if (file is null) return false;

        lock (_gate)
        {
            if (_state.Mods.TryGetValue(projectId, out var info) &&
                info.VersionId == best.Id &&
                File.Exists(Path.Combine(modsDirectory, info.FileName)))
                return true;
        }

        return await InstallVersionAsync(best, modsDirectory, progress, ct, visited);
    }

    /// <summary>Installs the primary file of a version and recursively installs required dependencies.</summary>
    public async Task<bool> InstallVersionAsync(
        ModrinthVersion version,
        string modsDirectory,
        IProgress<LaunchProgress>? progress,
        CancellationToken ct,
        HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
        if (file is null) return false;

        Directory.CreateDirectory(modsDirectory);
        var destination = Path.Combine(modsDirectory, file.Filename);

        progress?.Report(new LaunchProgress(0, "Установка мода", version.Name));

        await _http.DownloadAsync(file.Url, destination, file.Size, file.Hashes?.Sha1, null, ct,
            verifySha1OnExisting: false);

        if (!string.IsNullOrWhiteSpace(version.ProjectId))
        {
            lock (_gate)
            {
                _state.Mods[version.ProjectId] = new InstalledModInfo
                {
                    FileName = file.Filename,
                    VersionId = version.Id,
                    Title = version.Name,
                    VersionNumber = version.VersionNumber
                };
                SaveState();
            }
        }

        // required dependencies
        foreach (var dependency in version.Dependencies)
        {
            if (!string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(dependency.ProjectId)) continue;
            if (visited.Contains(dependency.ProjectId)) continue;

            try
            {
                progress?.Report(new LaunchProgress(0, "Зависимость", "Загружаем " + dependency.ProjectId));
                await EnsureProjectAsync(dependency.ProjectId, modsDirectory, progress, ct, visited);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // a single missing dependency must not abort the whole install
            }
        }

        return true;
    }

    /// <summary>
    /// Silently makes sure framework mods required by the built-in set (Fabric API) are present.
    /// Never throws — a launch must not fail because of network problems.
    /// </summary>
    public async Task EnsureSupportModsAsync(string gameDirectory, CancellationToken ct)
    {
        try
        {
            var modsDirectory = Path.Combine(gameDirectory, "mods");
            Directory.CreateDirectory(modsDirectory);
            await EnsureProjectAsync(FabricApiProjectId, modsDirectory, null, ct);
        }
        catch (OperationCanceledException)
        {
            // caller cancelled
        }
        catch
        {
            // offline or API unreachable
        }
    }

    // ------------------------------------------------------------------ managed mods

    public sealed record ManagedMod(string ProjectId, string Title, string VersionNumber, string FileName);

    public IReadOnlyList<ManagedMod> GetInstalledMods()
    {
        lock (_gate)
        {
            return _state.Mods
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.FileName))
                .Where(kv => !string.Equals(kv.Key, FabricApiProjectId, StringComparison.OrdinalIgnoreCase))
                .Select(kv => new ManagedMod(
                    kv.Key,
                    string.IsNullOrWhiteSpace(kv.Value.Title) ? kv.Key : kv.Value.Title,
                    kv.Value.VersionNumber,
                    kv.Value.FileName))
                .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public bool Uninstall(string projectId, string modsDirectory)
    {
        InstalledModInfo? info;
        lock (_gate)
        {
            if (!_state.Mods.TryGetValue(projectId, out info)) return false;
            _state.Mods.Remove(projectId);
            SaveState();
        }

        try
        {
            var path = Path.Combine(modsDirectory, info.FileName);
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // ignore file-level errors
        }

        return true;
    }

    /// <summary>Returns the newest version if an update is available, otherwise null.</summary>
    public async Task<ModrinthVersion?> FindUpdateAsync(string projectId, CancellationToken ct)
    {
        var versions = await GetVersionsAsync(projectId, ct);
        var best = versions.FirstOrDefault();
        if (best is null) return null;

        lock (_gate)
        {
            if (_state.Mods.TryGetValue(projectId, out var info) && info.VersionId == best.Id)
                return null;
        }

        return best;
    }

    // ------------------------------------------------------------------ modpacks

    /// <summary>Installs a .mrpack from a URL or a local file path.</summary>
    public async Task<InstalledModpack> InstallModpackAsync(
        string source,
        string gameDirectory,
        string? displayName,
        IProgress<LaunchProgress> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(gameDirectory);

        var localFile = source;
        var temporary = false;

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            temporary = true;
            localFile = Path.Combine(Path.GetTempPath(), "nviel-" + Guid.NewGuid().ToString("N") + ".mrpack");
            progress.Report(new LaunchProgress(2, "Скачивание сборки", displayName ?? Path.GetFileName(uri.LocalPath)));
            await _http.DownloadAsync(source, localFile, 0, null, null, ct, verifySha1OnExisting: false);
        }

        try
        {
            return await InstallModpackFromArchiveAsync(localFile, gameDirectory, displayName, progress, ct);
        }
        finally
        {
            if (temporary)
            {
                try { File.Delete(localFile); } catch { }
            }
        }
    }

    private async Task<InstalledModpack> InstallModpackFromArchiveAsync(
        string archivePath,
        string gameDirectory,
        string? displayName,
        IProgress<LaunchProgress> progress,
        CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var indexEntry = archive.GetEntry("modrinth.index.json")
                         ?? throw new InvalidOperationException("В файле нет modrinth.index.json — это не .mrpack.");

        MrpackIndex index;
        await using (var stream = indexEntry.Open())
        {
            index = await JsonSerializer.DeserializeAsync<MrpackIndex>(stream, Json) ?? new MrpackIndex();
        }

        var name = !string.IsNullOrWhiteSpace(displayName) ? displayName!
                   : !string.IsNullOrWhiteSpace(index.Name) ? index.Name
                   : Path.GetFileNameWithoutExtension(archivePath);

        progress.Report(new LaunchProgress(5, "Установка сборки", name));

        // 1. files listed in the index
        var files = index.Files
            .Where(f => f.Downloads.Count > 0)
            .Where(f => !string.Equals(f.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var done = 0;
        var work = files.Select(f => new
        {
            File = f,
            Destination = Path.Combine(gameDirectory, f.Path.Replace('/', Path.DirectorySeparatorChar))
        }).ToList();

        foreach (var item in work)
        {
            ct.ThrowIfCancellationRequested();

            var artifact = new DownloadArtifact
            {
                Url = item.File.Downloads[0],
                Size = item.File.FileSize,
                Sha1 = item.File.Hashes is not null && item.File.Hashes.TryGetValue("sha1", out var sha) ? sha : null
            };

            try
            {
                await _http.DownloadAsync(artifact.Url, item.Destination, artifact.Size, artifact.Sha1, null, ct,
                    verifySha1OnExisting: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Write("Пропущен файл сборки: " + ex.Message);
            }

            done++;
            progress.Report(new LaunchProgress(5 + 80.0 * done / Math.Max(1, work.Count),
                "Скачивание файлов сборки", $"{done} / {work.Count}"));
        }

        // 2. overrides
        progress.Report(new LaunchProgress(88, "Распаковка сборки", "Дополнительные файлы"));

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            string? relative = null;
            if (entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                relative = entry.FullName["overrides/".Length..];
            else if (entry.FullName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
                relative = entry.FullName["client-overrides/".Length..];

            if (string.IsNullOrEmpty(relative) || string.IsNullOrEmpty(entry.Name)) continue;

            var destination = Path.Combine(gameDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

            // never overwrite the launcher's own mods folder collisions silently
            if (File.Exists(destination)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        var modpack = new InstalledModpack
        {
            Name = name,
            Version = index.VersionId,
            ProjectId = "",
            InstalledUtc = DateTime.UtcNow
        };

        lock (_gate)
        {
            _state.Modpacks.RemoveAll(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _state.Modpacks.Add(modpack);
            SaveState();
        }

        progress.Report(new LaunchProgress(100, "Сборка установлена", name));

        // modpack files may have written read-only jars; nothing else to do
        await Task.CompletedTask;
        return modpack;
    }

    // ------------------------------------------------------------------ state

    private static ModrinthState LoadState()
    {
        try
        {
            if (File.Exists(AppPaths.ModrinthStateFile))
            {
                var json = File.ReadAllText(AppPaths.ModrinthStateFile);
                var state = JsonSerializer.Deserialize<ModrinthState>(json, Json);
                if (state is not null)
                {
                    state.Mods = new Dictionary<string, InstalledModInfo>(state.Mods, StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch
        {
            // ignored
        }

        return new ModrinthState();
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppRoot);
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.ModrinthStateFile, json);
        }
        catch (Exception ex)
        {
            Log.Write("Не удалось сохранить состояние Modrinth: " + ex.Message);
        }
    }
}
