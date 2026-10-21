using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nVielLauncher.Services;

public sealed class GameInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новый инстанс";
    public string Directory { get; set; } = "";
    public bool IsDefault { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedUtc { get; set; }
    public DateTime? LastBackupUtc { get; set; }
}

public sealed class LauncherSettings
{
    public string Nickname { get; set; } = "";
    public int RamMb { get; set; } = SettingsService.DefaultRamMb;
    public string? JavaPath { get; set; }
    public string GameDirectory { get; set; } = AppPaths.DefaultGameDirectory;
    public List<GameInstance> Instances { get; set; } = new();
    public string? ActiveInstanceId { get; set; }
    public bool AutoBackupBeforeLaunch { get; set; } = true;
    public int BackupRetention { get; set; } = 5;
    public int AutoBackupIntervalHours { get; set; } = 12;
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
    public bool LauncherMigrationOffered { get; set; }
    public DateTime? LastLaunchUtc { get; set; }

    [JsonIgnore]
    public GameInstance? ActiveInstance =>
        Instances?.FirstOrDefault(i => i.Id == ActiveInstanceId) ?? Instances?.FirstOrDefault();

    [JsonIgnore]
    public string EffectiveGameDirectory
    {
        get
        {
            var active = ActiveInstance;
            if (active is { IsDefault: true } && !string.IsNullOrWhiteSpace(GameDirectory))
                return GameDirectory;

            if (active is not null && !string.IsNullOrWhiteSpace(active.Directory))
                return active.Directory;

            return string.IsNullOrWhiteSpace(GameDirectory)
                ? AppPaths.DefaultGameDirectory
                : GameDirectory;
        }
    }

    /// <summary>
    /// Versions, libraries, assets and natives are shared by all instances. Keeping
    /// them in the legacy game folder means existing installs do not download again.
    /// </summary>
    [JsonIgnore]
    public string SharedRuntimeDirectory =>
        string.IsNullOrWhiteSpace(GameDirectory) ? AppPaths.DefaultGameDirectory : GameDirectory;

    public void EnsureInstances()
    {
        Instances ??= new List<GameInstance>();

        if (Instances.Count == 0)
        {
            Instances.Add(new GameInstance
            {
                Id = SettingsService.DefaultInstanceId,
                Name = "Основная",
                Directory = SharedRuntimeDirectory,
                IsDefault = true
            });
        }

        foreach (var instance in Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Id))
                instance.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(instance.Name))
                instance.Name = instance.IsDefault ? "Основная" : "Инстанс";
            if (string.IsNullOrWhiteSpace(instance.Directory))
            {
                instance.Directory = instance.IsDefault
                    ? SharedRuntimeDirectory
                    : Path.Combine(AppPaths.InstancesDirectory, instance.Id);
            }

            instance.Directory = NormalizePath(instance.Directory);
        }

        if (!Instances.Any(i => i.IsDefault))
            Instances[0].IsDefault = true;

        if (string.IsNullOrWhiteSpace(ActiveInstanceId) ||
            Instances.All(i => !string.Equals(i.Id, ActiveInstanceId, StringComparison.OrdinalIgnoreCase)))
        {
            ActiveInstanceId = (Instances.FirstOrDefault(i => i.IsDefault) ?? Instances[0]).Id;
        }

        if (BackupRetention < 1) BackupRetention = 5;
        if (AutoBackupIntervalHours < 1) AutoBackupIntervalHours = 12;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
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
    public const string DefaultInstanceId = "default";

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
        Current.Servers ??= new List<ServerEntry>();
        Current.Profiles ??= new List<PlayerProfile>();
        Current.Nickname ??= "";
        if (string.IsNullOrWhiteSpace(Current.AccentColor)) Current.AccentColor = "#FFFFFFFF";
        Current.EnsureInstances();
    }

    public void Save()
    {
        try
        {
            lock (_gate)
            {
                Current.EnsureInstances();
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

    public void ExportTo(string path)
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
    }

    /// <summary>Replaces settings with a validated export and saves atomically.</summary>
    public void ImportFrom(string path)
    {
        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions)
                     ?? throw new InvalidOperationException("Файл настроек пуст.");

        if (string.IsNullOrWhiteSpace(loaded.GameDirectory))
            loaded.GameDirectory = AppPaths.DefaultGameDirectory;
        loaded.Servers ??= new List<ServerEntry>();
        loaded.Profiles ??= new List<PlayerProfile>();
        loaded.Nickname ??= "";
        if (string.IsNullOrWhiteSpace(loaded.AccentColor))
            loaded.AccentColor = "#FFFFFFFF";
        if (loaded.RamMb <= 0)
            loaded.RamMb = DefaultRamMb;
        if (loaded.ResolutionWidth < 854)
            loaded.ResolutionWidth = 1280;
        if (loaded.ResolutionHeight < 480)
            loaded.ResolutionHeight = 720;

        loaded.EnsureInstances();
        Current = loaded;
        Save();
    }
}
