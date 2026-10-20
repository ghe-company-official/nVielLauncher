using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace nVielLauncher.Services;

public sealed class LauncherService
{
    public const string VersionLabel = "Fabric 1.21.4";

    private static readonly Regex NicknameRegex = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly Dictionary<string, bool> Features = new(StringComparer.OrdinalIgnoreCase)
    {
        ["is_demo_user"] = false,
        ["has_custom_resolution"] = false,
        ["has_quick_plays_support"] = false,
        ["is_quick_play_singleplayer"] = false,
        ["is_quick_play_multiplayer"] = false,
        ["is_quick_play_realms"] = false
    };

    private readonly SettingsService _settings;
    private readonly JavaService _java;
    private readonly MinecraftInstaller _installer;
    private readonly ModsService _mods;
    private readonly ModrinthService _modrinth;

    private Process? _process;

    public LauncherService(SettingsService settings, JavaService java, MinecraftInstaller installer, ModsService mods, ModrinthService modrinth)
    {
        _settings = settings;
        _java = java;
        _installer = installer;
        _mods = mods;
        _modrinth = modrinth;
    }

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch
            {
                return false;
            }
        }
    }

    public event Action<int>? GameExited;
    public event Action? GameStarted;

    public static bool IsValidNickname(string nickname) => NicknameRegex.IsMatch(nickname);

    public async Task LaunchAsync(IProgress<LaunchProgress> progress, CancellationToken ct)
    {
        if (IsRunning) throw new InvalidOperationException("Игра уже запущена.");

        var settings = _settings.Current;
        var nickname = settings.Nickname.Trim();

        if (!IsValidNickname(nickname))
            throw new InvalidOperationException("Ник должен быть от 3 до 16 символов: A-Z, 0-9 и _.");

        var gameDirectory = settings.EffectiveGameDirectory;
        Directory.CreateDirectory(gameDirectory);

        progress.Report(new LaunchProgress(0, "Подготовка", "nViel Launcher · " + VersionLabel));

        // 1. Java 21
        var javaProgress = new Progress<LaunchProgress>(p =>
            progress.Report(p with { Percent = 2 + p.Percent * 0.08 }));
        var java = await _java.EnsureJavaAsync(21, javaProgress, ct);

        // 2. Minecraft + Fabric + assets
        var installProgress = new Progress<LaunchProgress>(p =>
            progress.Report(p with { Percent = 10 + p.Percent * 0.85 }));
        var plan = await _installer.EnsureAsync(gameDirectory, installProgress, ct);

        // 3. Bundled mods
        var modsProgress = new Progress<LaunchProgress>(p =>
            progress.Report(p with { Percent = 95 + p.Percent * 0.03 }));
        await _mods.SyncAsync(gameDirectory, modsProgress, ct);
        await _mods.EnsureCoreModsAsync(gameDirectory, ct);

        // framework mods required by the built-in set (downloaded silently, never surfaced in the UI)
        await _modrinth.EnsureSupportModsAsync(gameDirectory, ct);

        // 4. Build and start
        progress.Report(new LaunchProgress(98, "Запуск Minecraft", $"Java {java.Major} · {settings.RamMb} МБ RAM"));

        var arguments = BuildArguments(plan, java, settings, nickname);
        Log.Write($"Запуск: {java.JavawExe} ({arguments.Count} аргументов)");
        Log.Write($"Игровая папка: {gameDirectory}");

        var startInfo = new ProcessStartInfo
        {
            FileName = java.JavawExe,
            WorkingDirectory = gameDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            var previous = _process;
            if (previous is not null)
            {
                try { previous.Dispose(); } catch { }
            }

            _process = Process.Start(startInfo)
                       ?? throw new InvalidOperationException("Windows не разрешил запустить процесс Java.");

            Log.Write($"Minecraft запущен. PID {_process.Id}.");
            progress.Report(new LaunchProgress(100, "Игра запущена", "Приятной игры!"));

            _settings.Current.LastLaunchUtc = DateTime.UtcNow;
            if (_settings.Current.QuickJoinHost is not null)
                _settings.Current.QuickJoinHost = null;
            _settings.Save();

            var process = _process;
            GameStarted?.Invoke();

            _ = Task.Run(async () =>
            {
                try { await process.WaitForExitAsync(); } catch { }

                var code = -1;
                try { code = process.ExitCode; } catch { }

                Log.Write($"Minecraft завершился. Код: {code}.");
                GameExited?.Invoke(code);
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось запустить Minecraft: " + ex.Message, ex);
        }
    }

    public void KillGame()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                Log.Write("Остановка Minecraft по запросу лаунчера.");
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Не удалось остановить игру: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ arguments

    private static List<string> BuildArguments(
        LaunchPlan plan, JavaInfo java, LauncherSettings settings, string nickname)
    {
        var classpath = string.Join(';', plan.Classpath);
        var offlineUuid = OfflineUuid(nickname);

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = nickname,
            ["version_name"] = plan.VersionName,
            ["game_directory"] = plan.GameDirectory,
            ["assets_root"] = plan.AssetsDirectory,
            ["assets_index_name"] = plan.AssetIndexName,
            ["auth_uuid"] = offlineUuid,
            ["auth_access_token"] = "0",
            ["auth_session"] = "0",
            ["user_type"] = "legacy",
            ["version_type"] = "release",
            ["natives_directory"] = plan.NativesDirectory,
            ["launcher_name"] = "nViel",
            ["launcher_version"] = "1.0.0",
            ["classpath"] = classpath,
            ["classpath_separator"] = ";",
            ["library_directory"] = Path.Combine(plan.GameDirectory, "libraries"),
            ["user_properties"] = "{}",
            ["clientid"] = "0",
            ["auth_xuid"] = "0",
            ["resolution_width"] = "1280",
            ["resolution_height"] = "720",
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = "",
            ["quickPlayRealms"] = ""
        };

        var jvm = new List<string>
        {
            $"-Xmx{settings.RamMb}M",
            $"-Xms{Math.Min(settings.RamMb, 1024)}M",
            "-XX:+UseG1GC",
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:G1NewSizePercent=20",
            "-XX:G1ReservePercent=20",
            "-XX:MaxGCPauseMillis=50",
            "-Dfile.encoding=UTF-8",
            "-Dminecraft.launcher.brand=nViel",
            "-Dminecraft.launcher.version=1.0.0"
        };

        if (!string.IsNullOrWhiteSpace(plan.LogConfigPath))
            jvm.Add("-Dlog4j.configurationFile=" + plan.LogConfigPath);

        jvm.AddRange(EvaluateArguments(plan.BaseVersion.Arguments?.Jvm, replacements));
        jvm.AddRange(EvaluateArguments(plan.FabricProfile.Arguments?.Jvm, replacements));

        if (!jvm.Contains("-cp") && !jvm.Contains("-classpath"))
        {
            jvm.Add("-cp");
            jvm.Add(classpath);
        }

        var game = new List<string>();
        game.AddRange(EvaluateArguments(plan.BaseVersion.Arguments?.Game, replacements));
        game.AddRange(EvaluateArguments(plan.FabricProfile.Arguments?.Game, replacements));

        if (settings.ShowResolution)
        {
            game.Add("--width");
            game.Add(Math.Clamp(settings.ResolutionWidth, 854, 3840).ToString());
            game.Add("--height");
            game.Add(Math.Clamp(settings.ResolutionHeight, 480, 2160).ToString());
        }

        if (settings.Fullscreen)
            game.Add("--fullscreen");

        if (!string.IsNullOrWhiteSpace(settings.QuickJoinHost))
        {
            game.Add("--quickPlayMultiplayer");
            game.Add(settings.QuickJoinHost + ":" + (settings.QuickJoinPort > 0 ? settings.QuickJoinPort : 25565));
        }

        var mainClass = string.IsNullOrWhiteSpace(plan.FabricProfile.MainClass)
            ? plan.BaseVersion.MainClass
            : plan.FabricProfile.MainClass;

        var result = new List<string>(jvm);
        if (!string.IsNullOrWhiteSpace(mainClass)) result.Add(mainClass);
        result.AddRange(game);
        return result;
    }

    private static IEnumerable<string> EvaluateArguments(
        List<JsonElement>? arguments, Dictionary<string, string> replacements)
    {
        if (arguments is null) yield break;

        foreach (var element in arguments)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                yield return ReplacePlaceholders(element.GetString() ?? "", replacements);
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object) continue;

            if (element.TryGetProperty("rules", out var rulesElement))
            {
                var rules = rulesElement.Deserialize<List<RuleInfo>>(HttpService.JsonOptions);
                if (!IsAllowed(rules)) continue;
            }

            if (!element.TryGetProperty("value", out var value)) continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                yield return ReplacePlaceholders(value.GetString() ?? "", replacements);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        yield return ReplacePlaceholders(item.GetString() ?? "", replacements);
                }
            }
        }
    }

    private static bool IsAllowed(List<RuleInfo>? rules)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            var matches = true;

            if (rule.Os is not null)
            {
                if (!string.IsNullOrWhiteSpace(rule.Os.Name) &&
                    !rule.Os.Name.Equals("windows", StringComparison.OrdinalIgnoreCase))
                    matches = false;

                if (matches && !string.IsNullOrWhiteSpace(rule.Os.Arch))
                {
                    var actual = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
                    if (rule.Os.Arch.Equals("x86", StringComparison.OrdinalIgnoreCase) && actual == "x86_64")
                    {
                        // x86 is compatible with x64
                    }
                    else if (!rule.Os.Arch.Equals(actual, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                    }
                }
            }

            if (matches && rule.Features is not null)
            {
                foreach (var feature in rule.Features)
                {
                    var actual = Features.TryGetValue(feature.Key, out var enabled) && enabled;
                    if (actual != feature.Value) matches = false;
                }
            }

            if (matches)
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static string ReplacePlaceholders(string input, Dictionary<string, string> replacements)
    {
        if (!input.Contains("${", StringComparison.Ordinal)) return input;
        return PlaceholderRegex.Replace(input, match =>
            replacements.TryGetValue(match.Groups[1].Value, out var value) ? value : "");
    }

    /// <summary>Java-compatible offline UUID (md5 of "OfflinePlayer:nickname").</summary>
    public static string OfflineUuid(string nickname)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + nickname));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
