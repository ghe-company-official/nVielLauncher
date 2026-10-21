using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace nVielLauncher.Services;

public enum ModIssueSeverity
{
    Info,
    Warning,
    Error
}

public enum ModIssueAction
{
    None,
    Disable,
    Enable,
    Search,
    Open
}

public sealed record ModIssue(
    string Code,
    ModIssueSeverity Severity,
    string Title,
    string Detail,
    string? FileName = null,
    ModIssueAction Action = ModIssueAction.None,
    string? ActionValue = null);

public sealed record ScannedMod(
    string FileName,
    string? Id,
    string? Name,
    string? Version,
    bool Enabled,
    IReadOnlyDictionary<string, string[]> Depends,
    IReadOnlyDictionary<string, string[]> Breaks,
    string? Error);

public sealed class ModConflictReport
{
    public IReadOnlyList<ScannedMod> Mods { get; init; } = Array.Empty<ScannedMod>();
    public IReadOnlyList<ModIssue> Issues { get; init; } = Array.Empty<ModIssue>();

    public int EnabledCount => Mods.Count(m => m.Enabled);
    public int DisabledCount => Mods.Count(m => !m.Enabled);
    public int ErrorCount => Issues.Count(i => i.Severity == ModIssueSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == ModIssueSeverity.Warning);
    public bool HasErrors => ErrorCount > 0;
}

/// <summary>
/// Reads fabric.mod.json directly from every installed jar. This catches problems
/// that Modrinth metadata cannot see: two downloads of the same mod, jars made for
/// another Minecraft version, missing required libraries and broken archives.
/// </summary>
public static class ModConflictService
{
    private const long MaxMetadataSize = 2 * 1024 * 1024;

    private static readonly HashSet<string> BuiltinDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "fabric_loader",
        "quilt_loader", "quilt-loader"
    };

    public static string GetModsDirectory(string gameDirectory) => Path.Combine(gameDirectory, "mods");

    public static Task<ModConflictReport> ScanAsync(string gameDirectory, CancellationToken ct) =>
        Task.Run(() => Scan(gameDirectory, ct), ct);

    public static ModConflictReport Scan(string gameDirectory, CancellationToken ct)
    {
        var modsDirectory = GetModsDirectory(gameDirectory);
        var mods = new List<ScannedMod>();

        if (Directory.Exists(modsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(modsDirectory))
            {
                ct.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(path);
                var enabled = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                if (!enabled && !fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                    continue;

                var metadata = ReadMetadata(path);
                mods.Add(new ScannedMod(
                    fileName,
                    metadata.Id,
                    metadata.Name,
                    metadata.Version,
                    enabled,
                    metadata.Depends,
                    metadata.Breaks,
                    metadata.Error));
            }
        }

        var issues = new List<ModIssue>();
        var enabledById = mods
            .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var disabledById = mods
            .Where(m => !m.Enabled && !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Duplicate mod IDs: keep the newest version, flag the rest.
        foreach (var group in mods
                     .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Id))
                     .GroupBy(m => m.Id!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var ordered = group
                .OrderByDescending(m => VersionPredicate.ParseVersion(m.Version) ?? new Version(0, 0))
                .ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var keeper = ordered[0];

            foreach (var duplicate in ordered.Skip(1))
            {
                issues.Add(new ModIssue(
                    "duplicate",
                    ModIssueSeverity.Error,
                    "Дубликат мода: " + group.Key,
                    $"«{keeper.Name ?? keeper.FileName}» уже установлен файлом {keeper.FileName}. " +
                    $"Этот файл — лишняя копия, её лучше отключить.",
                    duplicate.FileName,
                    ModIssueAction.Disable,
                    duplicate.FileName));
            }
        }

        // Missing/disabled dependencies and version predicates.
        foreach (var mod in mods.Where(m => m.Enabled))
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(mod.Error))
            {
                issues.Add(new ModIssue(
                    "metadata",
                    ModIssueSeverity.Warning,
                    "Не удалось прочитать мод",
                    $"{mod.FileName}: {mod.Error}. Возможно, это не Fabric-мод или файл повреждён.",
                    mod.FileName,
                    ModIssueAction.Open));
            }

            foreach (var (dependencyId, predicates) in mod.Depends)
            {
                if (BuiltinDependencies.Contains(dependencyId))
                {
                    if (dependencyId.Equals("minecraft", StringComparison.OrdinalIgnoreCase) &&
                        !VersionPredicate.MatchesAny(predicates, MinecraftInstaller.MinecraftVersion))
                    {
                        issues.Add(new ModIssue(
                            "minecraft-version",
                            ModIssueSeverity.Warning,
                            "Мод для другой версии Minecraft",
                            $"{mod.Name ?? mod.FileName} требует Minecraft {string.Join(" / ", predicates)}, " +
                            $"а лаунчер использует {MinecraftInstaller.MinecraftVersion}.",
                            mod.FileName,
                            ModIssueAction.Disable,
                            mod.FileName));
                    }

                    continue;
                }

                if (enabledById.TryGetValue(dependencyId, out var installed))
                {
                    if (predicates.Length > 0 &&
                        VersionPredicate.ParseVersion(installed.Version) is not null &&
                        !VersionPredicate.MatchesAny(predicates, installed.Version))
                    {
                        issues.Add(new ModIssue(
                            "dependency-version",
                            ModIssueSeverity.Warning,
                            "Неподходящая версия зависимости",
                            $"{mod.Name ?? mod.FileName} требует {dependencyId} {string.Join(" / ", predicates)}, " +
                            $"установлено {installed.Version} ({installed.FileName}).",
                            mod.FileName,
                            ModIssueAction.Search,
                            dependencyId));
                    }

                    continue;
                }

                if (disabledById.TryGetValue(dependencyId, out var disabled))
                {
                    issues.Add(new ModIssue(
                        "dependency-disabled",
                        ModIssueSeverity.Warning,
                        "Зависимость отключена",
                        $"{mod.Name ?? mod.FileName} требует {dependencyId}, но файл {disabled.FileName} отключён.",
                        disabled.FileName,
                        ModIssueAction.Enable,
                        disabled.FileName));
                }
                else
                {
                    issues.Add(new ModIssue(
                        "dependency-missing",
                        ModIssueSeverity.Error,
                        "Не хватает зависимости: " + dependencyId,
                        $"Нужна для {mod.Name ?? mod.FileName}" +
                        (predicates.Length > 0 ? $" ({string.Join(" / ", predicates)})" : "") +
                        ". Найди её в Modrinth или положи .jar в папку mods.",
                        mod.FileName,
                        ModIssueAction.Search,
                        dependencyId));
                }
            }

            foreach (var (brokenId, predicates) in mod.Breaks)
            {
                if (brokenId.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    if (VersionPredicate.MatchesAny(predicates, MinecraftInstaller.MinecraftVersion))
                    {
                        issues.Add(new ModIssue(
                            "minecraft-break",
                            ModIssueSeverity.Warning,
                            "Мод несовместим с этой версией Minecraft",
                            $"{mod.Name ?? mod.FileName} явно не поддерживает Minecraft {MinecraftInstaller.MinecraftVersion}.",
                            mod.FileName,
                            ModIssueAction.Disable,
                            mod.FileName));
                    }

                    continue;
                }

                if (!enabledById.TryGetValue(brokenId, out var other)) continue;
                if (predicates.Length > 0 && !VersionPredicate.MatchesAny(predicates, other.Version)) continue;

                issues.Add(new ModIssue(
                    "incompatible",
                    ModIssueSeverity.Error,
                    "Моды несовместимы",
                    $"{mod.Name ?? mod.FileName} сообщает о несовместимости с {other.Name ?? other.FileName}" +
                    (predicates.Length > 0 ? $" {string.Join(" / ", predicates)}" : "") + ".",
                    mod.FileName,
                    ModIssueAction.Disable,
                    mod.FileName));
            }
        }

        var orderedIssues = issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new ModConflictReport { Mods = mods, Issues = orderedIssues };
    }

    public static bool SetEnabled(string modsDirectory, string fileName, bool enabled)
    {
        var path = Path.Combine(modsDirectory, fileName);
        if (!File.Exists(path)) return false;

        if (enabled)
        {
            if (!fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return false;
            var targetName = fileName[..^".disabled".Length];
            if (targetName.Length == 0) return false;
            File.Move(path, Path.Combine(modsDirectory, targetName), overwrite: false);
            return true;
        }

        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return false;
        File.Move(path, path + ".disabled", overwrite: false);
        return true;
    }

    private static Metadata ReadMetadata(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
                return Metadata.Empty("нет fabric.mod.json");

            if (entry.Length > MaxMetadataSize)
                return Metadata.Empty("fabric.mod.json слишком большой");

            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            var id = ReadString(root, "id");
            var name = ReadString(root, "name") ?? id;
            var version = ReadString(root, "version");
            var depends = ReadPredicates(root, "depends");
            var breaks = ReadPredicates(root, "breaks");
            foreach (var pair in ReadPredicates(root, "conflicts"))
                breaks[pair.Key] = pair.Value;

            return new Metadata(id, name, version, depends, breaks, null);
        }
        catch (Exception ex)
        {
            return Metadata.Empty(ex is InvalidDataException
                ? "файл не похож на zip/jar"
                : ex.Message);
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static Dictionary<string, string[]> ReadPredicates(JsonElement root, string name)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in value.EnumerateObject())
        {
            var predicates = new List<string>();
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var text = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) predicates.Add(text);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) predicates.Add(text);
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Number)
            {
                predicates.Add(property.Value.GetRawText());
            }

            if (predicates.Count > 0)
                result[property.Name] = predicates.ToArray();
        }

        return result;
    }

    private sealed record Metadata(
        string? Id,
        string? Name,
        string? Version,
        Dictionary<string, string[]> Depends,
        Dictionary<string, string[]> Breaks,
        string? Error)
    {
        public static Metadata Empty(string error) => new(
            null, null, null,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            error);
    }
}

/// <summary>
/// Minimal SemVer predicate support for Fabric metadata: >=, >, &lt;=, &lt;,
/// exact values, x-ranges, ~, ^ and space-separated constraints.
/// </summary>
public static class VersionPredicate
{
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        var cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut > 0) text = text[..cut];

        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out var version)) return null;

        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    public static bool MatchesAny(IReadOnlyList<string>? predicates, string? version)
    {
        if (predicates is null || predicates.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(version)) return true;

        var parsed = ParseVersion(version);
        if (parsed is null) return true;

        return predicates.Any(p => Matches(p, parsed));

        static bool Matches(string predicate, Version parsed)
        {
            if (string.IsNullOrWhiteSpace(predicate)) return true;

            foreach (var alternative in predicate.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (MatchesAll(alternative, parsed)) return true;
            }

            return false;
        }

        static bool MatchesAll(string expression, Version parsed)
        {
            var tokens = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) return true;

            foreach (var token in tokens)
            {
                if (!MatchesToken(token, parsed)) return false;
            }

            return true;
        }

        static Version CaretUpperBound(Version target)
        {
            if (target.Major > 0) return new Version(target.Major + 1, 0, 0, 0);
            if (target.Minor > 0) return new Version(0, target.Minor + 1, 0, 0);
            return new Version(0, 0, target.Build + 1, 0);
        }

        static bool MatchesToken(string raw, Version parsed)
        {
            var token = raw.Trim();
            if (token.Length == 0) return true;
            if (token is "*" or "x" or "X") return true;

            // "1.20-1.21" is an inclusive shorthand used by a few mods.
            if (token.Count(c => c == '-') == 1)
            {
                var parts = token.Split('-');
                var low = ParseVersion(parts[0]);
                var high = ParseVersion(parts[1]);
                if (low is not null && high is not null)
                    return parsed >= low && parsed <= high;
            }

            var op = "";
            foreach (var candidate in new[] { ">=", "<=", ">", "<", "=", "~", "^" })
            {
                if (!token.StartsWith(candidate, StringComparison.Ordinal)) continue;
                op = candidate;
                token = token[candidate.Length..].Trim();
                break;
            }

            if (token.EndsWith(".x", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith(".*", StringComparison.OrdinalIgnoreCase))
            {
                var baseVersion = ParseVersion(token[..^2]);
                if (baseVersion is null) return true;
                var upper = new Version(baseVersion.Major, baseVersion.Minor + 1, 0, 0);
                return parsed >= baseVersion && parsed < upper;
            }

            var target = ParseVersion(token);
            if (target is null) return true;

            return op switch
            {
                ">=" => parsed >= target,
                ">" => parsed > target,
                "<=" => parsed <= target,
                "<" => parsed < target,
                "~" => parsed >= target && parsed < new Version(target.Major, target.Minor + 1, 0, 0),
                "^" => parsed >= target && parsed < CaretUpperBound(target),
                _ => parsed == target
            };
        }
    }
}
