using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public partial class HomeView : UserControl, IRefreshable
{
    private bool _running;
    private bool _ambientStarted;
    private Storyboard? _ambient;

    public HomeView()
    {
        InitializeComponent();

        AppServices.Launcher.GameStarted += OnGameStarted;
        AppServices.Launcher.GameExited += OnGameExited;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncAmbient();
        Refresh();
    }

    private static string Plural(int count, string one, string few, string many)
    {
        var mod100 = Math.Abs(count) % 100;
        var mod10 = mod100 % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    private static int CountMods(string gameDirectory)
    {
        try
        {
            var modsDirectory = Path.Combine(gameDirectory, "mods");
            if (!Directory.Exists(modsDirectory)) return 0;
            return Directory.EnumerateFiles(modsDirectory).Count(file =>
                file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Runs the ambient vector animation unless the user disabled animations.</summary>
    private void SyncAmbient()
    {
        if (!AppServices.Settings.Current.AnimationsEnabled)
        {
            _ambient?.Stop(this);
            _ambient = null;
            _ambientStarted = false;
            return;
        }

        if (_ambientStarted) return;
        _ambientStarted = true;

        if (Resources["HomeAmbient"] is Storyboard storyboard)
        {
            _ambient = storyboard;
            storyboard.Begin(this, true);
        }
    }

    public void Refresh()
    {
        SyncAmbient();

        var settings = AppServices.Settings.Current;
        var nickname = string.IsNullOrWhiteSpace(settings.Nickname) ? "Steve" : settings.Nickname;

        NicknameText.Text = nickname;
        NickLine.Text = "Игрок: " + nickname;
        var instanceName = settings.ActiveInstance?.Name ?? "Основная";
        SubHeader.Text = $"{LauncherService.VersionLabel} · {instanceName} · профиль {nickname}";
        AvatarLetter.Text = nickname[..1].ToUpperInvariant();

        ChipRam.Text = $"{settings.RamMb} МБ";
        StatInstance.Text = instanceName;
        StatInstance.ToolTip = settings.EffectiveGameDirectory;
        var mods = CountMods(settings.EffectiveGameDirectory);
        ChipMods.Text = mods + " " + Plural(mods, "мод", "мода", "модов");

        if (settings.LastLaunchUtc is { } lastUtc)
        {
            var local = lastUtc.ToLocalTime();
            StatLastLaunch.Text = local.Date == DateTime.Now.Date
                ? "сегодня, " + local.ToString("HH:mm")
                : local.ToString("dd.MM, HH:mm");
        }
        else
        {
            StatLastLaunch.Text = "ещё не было";
        }

        if (!_running)
        {
            Progress.Visibility = Visibility.Collapsed;
            Progress.Value = 0;
            PercentText.Text = "0%";
            StatusText.Text = "Готов к запуску";
            DetailText.Text = "Файлы проверятся автоматически при запуске";
            PillText.Text = "ГОТОВ К ЗАПУСКУ";
            PlayLabel.Text = "Играть";
        }
    }

    // ------------------------------------------------------------------ launch

    /// <summary>Starts the game (used by "quick join" from the Servers page).</summary>
    public void BeginLaunch() => (Window.GetWindow(this) as MainWindow)?.StartLaunch();

    private void Play_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.StartLaunch();

    private void Instances_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.NavigateTo("instances");

    private void SetRunningUi(bool running)
    {
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.IsEnabled = !running;
        PlayOrb.IsEnabled = !running;
        PlayLabel.Text = running ? "В игре" : "Играть";

        if (running)
        {
            PillText.Text = "ИГРАЕТ";
            StatusText.Text = "Игра запущена";
            PercentText.Text = "100%";
            Progress.Visibility = Visibility.Visible;
            Progress.Value = 100;
            DetailText.Text = "Minecraft работает — можно свернуть лаунчер";
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Launcher.KillGame();
    }

    private void OnGameStarted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _running = true;
            SetRunningUi(true);
            if ((Window.GetWindow(this) as MainWindow)?.IsPageActive("launch") != true)
                Notify.Show("Minecraft", "Игра запущена. Приятной игры!", "success");
        });
    }

    private void OnGameExited(int code)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _running = false;
            Progress.Visibility = Visibility.Collapsed;
            Progress.Value = 0;
            PercentText.Text = "0%";
            StatusText.Text = "Готов к запуску";
            DetailText.Text = "Можно запускать снова";
            PillText.Text = "ГОТОВ К ЗАПУСКУ";
            SetRunningUi(false);
            PlayButton.IsEnabled = true;
            PlayOrb.IsEnabled = true;
            PlayPercent.Visibility = Visibility.Collapsed;

            var window = Window.GetWindow(this) as MainWindow;
            if (window?.IsPageActive("launch") == true) return;

            if (code == 0)
                Notify.Show("Minecraft", "Игровой сеанс завершён.", "info");
            else if (code != -1)
            {
                Notify.Show("Игра завершилась с ошибкой", $"Код выхода {code}. Открываю диагностику.", "error");
                window?.ShowDiagnostics();
            }
        });
    }

    // ------------------------------------------------------------------ actions

    private void ChangeNick_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowProfiles();
}
