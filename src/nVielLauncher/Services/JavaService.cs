using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace nVielLauncher.Services;

public sealed record JavaInfo(string JavaExe, string JavawExe, int Major, string VersionString)
{
    public string Home => Path.GetDirectoryName(Path.GetDirectoryName(JavaExe)!) ?? "";
}

public sealed class JavaService
{
    private const string AdoptiumUrl =
        "https://api.adoptium.net/v3/binary/latest/{0}/ga/windows/x64/jre/hotspot/normal/eclipse";

    private readonly HttpService _http;
    private readonly SettingsService _settings;

    public JavaService(HttpService http, SettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public Task<JavaInfo?> DetectAsync(int minMajor, CancellationToken ct)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(_settings.Current.JavaPath))
            candidates.Add(_settings.Current.JavaPath);

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(folder.Trim(), "java.exe");
                if (File.Exists(exe)) candidates.Add(exe);
            }
            catch
            {
                // ignored
            }
        }

        candidates.AddRange(FindInRegistry());
        candidates.AddRange(FindInKnownFolders());

        var probed = new List<JavaInfo>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var info = Probe(candidate);
            if (info is not null) probed.Add(info);
        }

        var best = probed
            .Where(i => i.Major >= minMajor)
            .OrderByDescending(i => i.Major)
            .FirstOrDefault();

        if (best is not null)
        {
            _settings.Current.JavaPath = best.JavaExe;
            return Task.FromResult<JavaInfo?>(best);
        }

        return Task.FromResult<JavaInfo?>(null);
    }

    public async Task<JavaInfo> EnsureJavaAsync(int minMajor, IProgress<LaunchProgress> progress, CancellationToken ct)
    {
        progress.Report(new LaunchProgress(0, "Поиск Java " + minMajor, "Проверяем установленные версии"));
        var found = await DetectAsync(minMajor, ct);
        if (found is not null)
        {
            progress.Report(new LaunchProgress(100, "Java найдена", $"Java {found.Major} · {found.Home}"));
            return found;
        }

        Log.Write($"Java {minMajor}+ не найдена, скачиваем Temurin JRE {minMajor}.");
        return await DownloadJavaAsync(minMajor, progress, ct);
    }

    public async Task<JavaInfo> DownloadJavaAsync(int major, IProgress<LaunchProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.JavaDirectory);
        var zip = Path.Combine(AppPaths.JavaDirectory, $"temurin-{major}.zip");
        var target = Path.Combine(AppPaths.JavaDirectory, $"temurin-{major}");
        var url = string.Format(AdoptiumUrl, major);

        progress.Report(new LaunchProgress(0, "Скачивание Java " + major, "Temurin JRE · ~50 МБ"));

        var downloadProgress = new Progress<double>(p =>
        {
            if (p < 0)
                progress.Report(new LaunchProgress(30, "Скачивание Java " + major, "Получаем пакет..."));
            else
                progress.Report(new LaunchProgress(Math.Clamp(p, 0, 1) * 80, "Скачивание Java " + major,
                    $"{p * 100:0}% · Temurin JRE {major}"));
        });

        try
        {
            await _http.DownloadAsync(url, zip, 0, null, downloadProgress, ct, verifySha1OnExisting: false);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Не удалось скачать Java {major}. Установи JDK {major} вручную и укажи путь в настройках. ({ex.Message})", ex);
        }

        progress.Report(new LaunchProgress(84, "Распаковка Java", "Это может занять минуту"));

        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            throw new IOException("Не удалось распаковать Java: " + ex.Message, ex);
        }

        TryDelete(zip);

        var javaw = Directory.EnumerateFiles(target, "javaw.exe", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();

        if (javaw is null)
            throw new IOException("В скачанном архиве Java не найден javaw.exe.");

        var info = Probe(javaw);
        if (info is null || info.Major < major)
            throw new IOException("Скачанная Java не запускается или версия ниже требуемой.");

        _settings.Current.JavaPath = info.JavaExe;
        _settings.Save();

        progress.Report(new LaunchProgress(100, "Java готова", $"Java {info.Major} · {info.Home}"));
        return info;
    }

    public static JavaInfo? Probe(string javaExe)
    {
        try
        {
            if (!File.Exists(javaExe)) return null;

            var psi = new ProcessStartInfo(javaExe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stderr = process.StandardError.ReadToEnd();
            var stdout = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(8000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            var text = stderr + Environment.NewLine + stdout;
            var major = ParseMajor(text);
            if (major <= 0) return null;

            var javaw = Path.Combine(Path.GetDirectoryName(javaExe)!, "javaw.exe");
            var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

            return new JavaInfo(javaExe, File.Exists(javaw) ? javaw : javaExe, major, firstLine);
        }
        catch
        {
            return null;
        }
    }

    private static int ParseMajor(string versionOutput)
    {
        var match = Regex.Match(versionOutput, "version\\s+\"([^\"]+)\"");
        if (!match.Success) return 0;

        var parts = match.Groups[1].Value.Split('.');
        if (parts.Length == 0) return 0;

        if (parts[0] == "1" && parts.Length > 1)
            return int.TryParse(parts[1], out var legacy) ? legacy : 0;

        return int.TryParse(parts[0], out var major) ? major : 0;
    }

    private static IEnumerable<string> FindInRegistry()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new[]
        {
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\Eclipse Adoptium\JDK",
            @"SOFTWARE\Eclipse Adoptium\JRE",
            @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\Azul Systems\Zulu",
            @"SOFTWARE\BellSoft\LibericaJDK"
        };

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var keyPath in keys)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(keyPath);
                        if (key is null) continue;

                        foreach (var sub in key.GetSubKeyNames())
                        {
                            using var versionKey = key.OpenSubKey(sub);
                            var home = versionKey?.GetValue("JavaHome") as string;
                            if (string.IsNullOrWhiteSpace(home)) continue;

                            var exe = Path.Combine(home, "bin", "java.exe");
                            if (File.Exists(exe)) results.Add(exe);
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        return results;
    }

    private static IEnumerable<string> FindInKnownFolders()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks")
        };

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;

                foreach (var exe in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories))
                {
                    if (exe.Contains(Path.Combine("bin", "java.exe"), StringComparison.OrdinalIgnoreCase))
                        results.Add(exe);
                    if (results.Count > 64) return results;
                }
            }
            catch
            {
                // ignored
            }
        }

        return results;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }
}
