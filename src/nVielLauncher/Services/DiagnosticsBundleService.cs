using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace nVielLauncher.Services;

public sealed record DiagnosticsBundle(string Path, long Size);

/// <summary>
/// Creates a privacy-aware support archive: launcher/game logs, crash reports,
/// redacted settings and system information. Upload is opt-in and only happens
/// when the user confirms it in nVielRE.
/// </summary>
public static class DiagnosticsBundleService
{
    private const long MaxFileBytes = 8 * 1024 * 1024;

    public static string? UploadEndpoint =>
        (Environment.GetEnvironmentVariable("NVIEL_REPORT_ENDPOINT") ?? "").Trim();

    public static async Task<DiagnosticsBundle> CreateAsync(
        string gameDirectory,
        CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.ReportsDirectory);
        var path = Path.Combine(AppPaths.ReportsDirectory,
            $"nViel-report-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");

        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

            AddText(archive, "system.txt", BuildSystemInfo(gameDirectory));
            AddText(archive, "launcher-info.txt",
                $"Product: {AppInfo.DisplayName}\r\n" +
                $"Version: {AppInfo.Version}\r\n" +
                $"Edition: {AppInfo.Edition}\r\n" +
                $"Portable: {AppPaths.IsPortable}\r\n" +
                $"AppRoot: {RedactPath(AppPaths.AppRoot)}\r\n" +
                $"Executable: {RedactPath(AppInfo.ExecutablePath)}\r\n" +
                $"Update channel: {AppInfo.UpdateChannel}\r\n" +
                $"Update relay: {(string.IsNullOrWhiteSpace(AppInfo.UpdateProxyUrl) ? "no" : "yes")}\r\n" +
                $"Created: {DateTimeOffset.Now:O}\r\n");

            AddSettings(archive);

            foreach (var log in SafeFiles(AppPaths.LogsDirectory, "launcher-*.log").TakeLast(10))
                AddFile(archive, "launcher-logs/" + Path.GetFileName(log), log, ct);

            var latestLog = CrashDiagnostics.LatestLogPath(gameDirectory);
            if (File.Exists(latestLog))
                AddFile(archive, "game/latest.log", latestLog, ct);

            foreach (var crash in SafeFiles(CrashDiagnostics.CrashReportsDirectory(gameDirectory), "*.txt")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(3))
                AddFile(archive, "game/crash-reports/" + Path.GetFileName(crash), crash, ct);

            AddText(archive, "mods.txt", BuildModsInfo(gameDirectory));
            AddText(archive, "diagnostics.txt",
                string.Join(Environment.NewLine, CrashDiagnostics.Analyze(gameDirectory).Select(f =>
                    $"[{f.Severity}] {f.Title}\r\n  {f.Detail}\r\n  Рекомендация: {f.Suggestion}")));
        }, ct);

        var info = new FileInfo(path);
        Log.Write($"REPORT: создан {path} ({info.Length} байт).");
        return new DiagnosticsBundle(path, info.Length);
    }

    public static async Task<string> UploadAsync(string path, CancellationToken ct)
    {
        var endpoint = UploadEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("Сервер отчётов не настроен.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttps && !endpointUri.IsLoopback))
            throw new InvalidOperationException("Сервер отчётов должен использовать HTTPS.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("nVielLauncher/" + AppInfo.VersionLabel);

        await using var stream = File.OpenRead(path);
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(file, "report", Path.GetFileName(path));

        using var response = await http.PostAsync(endpoint, content, ct);
        response.EnsureSuccessStatusCode();
        return $"HTTP {(int)response.StatusCode}";
    }

    private static string BuildSystemInfo(string gameDirectory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("nViel Launcher support report");
        sb.AppendLine("=============================");
        sb.AppendLine($"Version: {AppInfo.DisplayName}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"CPU threads: {Environment.ProcessorCount}");
        sb.AppendLine($"Physical memory: {SystemMonitor.TotalPhysicalMemoryMb} MB");
        sb.AppendLine($"Runtime: {Environment.Version}");
        sb.AppendLine($"Game directory: {RedactPath(gameDirectory)}");
        sb.AppendLine($"Launcher directory: {RedactPath(AppPaths.ExecutableDirectory)}");
        sb.AppendLine($"Portable mode: {AppPaths.IsPortable}");
        sb.AppendLine();

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(gameDirectory));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                sb.AppendLine($"Disk {drive.Name}: {drive.AvailableFreeSpace / 1024d / 1024d / 1024d:0.0} GB free");
            }
        }
        catch
        {
            // optional
        }

        var javaPath = AppServices.Settings.Current.JavaPath;
        if (!string.IsNullOrWhiteSpace(javaPath))
        {
            var java = JavaService.Probe(javaPath);
            sb.AppendLine(java is null
                ? "Java: configured but not responding"
                : $"Java: {java.Major} · {java.VersionString} · {RedactPath(java.Home)}");
        }
        else
        {
            sb.AppendLine("Java: auto-detect");
        }

        return sb.ToString();
    }

    private static string BuildModsInfo(string gameDirectory)
    {
        try
        {
            var mods = Path.Combine(gameDirectory, "mods");
            if (!Directory.Exists(mods)) return "Mods: folder not found";
            var files = Directory.EnumerateFiles(mods, "*.jar", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(mods, "*.jar.disabled", SearchOption.TopDirectoryOnly))
                .ToList();
            return "Mods: " + files.Count + " files in the game folder (names omitted for privacy)";
        }
        catch (Exception ex)
        {
            return "Mods: " + ex.Message;
        }
    }

    private static void AddSettings(ZipArchive archive)
    {
        if (!File.Exists(AppPaths.SettingsFile)) return;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile)) as JsonObject;
            if (node is null) return;

            Redact(node, "Nickname");
            Redact(node, "GameDirectory");
            Redact(node, "JavaPath");
            Redact(node, "QuickJoinHost");

            if (node["Profiles"] is JsonArray profiles)
            {
                foreach (var profile in profiles.OfType<JsonObject>())
                {
                    Redact(profile, "Name");
                    Redact(profile, "Nickname");
                }
            }

            if (node["Servers"] is JsonArray servers)
            {
                foreach (var server in servers.OfType<JsonObject>())
                {
                    Redact(server, "Name");
                    Redact(server, "Host");
                }
            }

            AddText(archive, "settings.redacted.json",
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A corrupted settings file is already visible in the logs.
        }
    }

    private static void Redact(JsonObject node, string property)
    {
        if (node[property] is not null) node[property] = "***";
    }

    private static IEnumerable<string> SafeFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToList()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void AddFile(ZipArchive archive, string entryName, string path, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var target = entry.Open();
            using var source = File.OpenRead(path);

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while (written < MaxFileBytes && (read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxFileBytes - written))) > 0)
            {
                ct.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
                written += read;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // one unreadable log must not break the report
        }
    }

    private static void AddText(ZipArchive archive, string entryName, string text)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string RedactPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return path
            .Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(Environment.UserName, "%USER%", StringComparison.OrdinalIgnoreCase);
    }
}
