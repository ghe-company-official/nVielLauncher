using System.IO;
using System.Text.Json;

namespace nVielLauncher.Services;

public sealed class LauncherSettings
{
    public string Nickname { get; set; } = "";
    public int RamMb { get; set; } = SettingsService.DefaultRamMb;
    public string? JavaPath { get; set; }
    public string GameDirectory { get; set; } = AppPaths.DefaultGameDirectory;
    public bool CloseLauncherOnLaunch { get; set; }
    public bool Fullscreen { get; set; }
    public bool ShowResolution { get; set; }
    public int ResolutionWidth { get; set; } = 1280;
    public int ResolutionHeight { get; set; } = 720;
    public bool ShowLogOnHome { get; set; } = true;
    public bool AnimationsEnabled { get; set; } = true;
    public bool BlurEnabled { get; set; } = true;
    public bool ShowSplash { get; set; } = true;
    public string AccentColor { get; set; } = "#FFFFFFFF";
    public bool BackgroundGlow { get; set; } = true;
    public List<ServerEntry> Servers { get; set; } = new();
    public string? QuickJoinHost { get; set; }
    public int QuickJoinPort { get; set; }
    public List<PlayerProfile> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }
    public bool HasSeenTour { get; set; }
    public bool SetupCompleted { get; set; }
    public DateTime? LastLaunchUtc { get; set; }

    public string EffectiveGameDirectory =>
        string.IsNullOrWhiteSpace(GameDirectory) ? AppPaths.DefaultGameDirectory : GameDirectory;
}

public sealed class ServerEntry
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25565;
    public DateTime? LastPlayedUtc { get; set; }

    public string Address => Host + ":" + (Port > 0 ? Port : 25565);
}

public sealed class PlayerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Nickname { get; set; } = "";
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();

    public LauncherSettings Current { get; private set; } = new();

    public event Action? Changed;

    public static int DefaultRamMb
    {
        get
        {
            try
            {
                var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                var half = (int)(bytes / 1024 / 1024 / 2);
                return Math.Clamp(half, 2048, 8192);
            }
            catch
            {
                return 4096;
            }
        }
    }

    public SettingsService()
    {
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions);
                if (loaded is not null) Current = loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Не удалось прочитать настройки: " + ex.Message);
        }

        if (Current.RamMb <= 0) Current.RamMb = DefaultRamMb;
        if (string.IsNullOrWhiteSpace(Current.GameDirectory)) Current.GameDirectory = AppPaths.DefaultGameDirectory;
    }

    public void Save()
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(AppPaths.AppRoot);
                var json = JsonSerializer.Serialize(Current, JsonOptions);
                var temp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.SettingsFile, overwrite: true);
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("Не удалось сохранить настройки: " + ex.Message);
        }
    }
}
