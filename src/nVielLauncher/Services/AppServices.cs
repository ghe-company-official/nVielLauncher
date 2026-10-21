using System.IO;

namespace nVielLauncher.Services;

public static class AppServices
{
    private static bool _initialized;

    public static SettingsService Settings { get; private set; } = null!;
    public static InstanceService Instances { get; private set; } = null!;
    public static BackupService Backups { get; private set; } = null!;
    public static HttpService Http { get; private set; } = null!;
    public static JavaService Java { get; private set; } = null!;
    public static ModsService Mods { get; private set; } = null!;
    public static ModrinthService Modrinth { get; private set; } = null!;
    public static MinecraftInstaller Installer { get; private set; } = null!;
    public static LauncherService Launcher { get; private set; } = null!;
    public static UpdateService Updates { get; private set; } = null!;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        AppPaths.EnsureDirectories();
        Log.Write("nViel Launcher запущен. " + AppPaths.AppRoot);

        Settings = new SettingsService();
        Instances = new InstanceService(Settings);
        Backups = new BackupService();
        Http = new HttpService();
        Java = new JavaService(Http, Settings);
        Mods = new ModsService();
        Modrinth = new ModrinthService(Http);
        Installer = new MinecraftInstaller(Http);
        Launcher = new LauncherService(Settings, Java, Installer, Mods, Modrinth);
        Updates = new UpdateService();
    }
}
