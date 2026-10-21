using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public partial class HomeView : UserControl, IRefreshable
{
    private bool _running;
    private bool _ringsStarted;
    private bool _javaChecked;
    private int _logLines;

    public HomeView()
    {
        InitializeComponent();

        Log.Message += OnLogMessage;
        AppServices.Launcher.GameStarted += OnGameStarted;
        AppServices.Launcher.GameExited += OnGameExited;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartRings();
        Refresh();

        if (_logLines == 0)
            AppendLog("Добро пожаловать в nViel Launcher. Нажми «Играть» — файлы загрузятся автоматически.");
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

    private void StartRings()
    {
        if (_ringsStarted) return;
        _ringsStarted = true;

        DashRing.RenderTransformOrigin = new Point(0.5, 0.5);
        var dashRotate = new RotateTransform();
        DashRing.RenderTransform = dashRotate;
        dashRotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(46)) { RepeatBehavior = RepeatBehavior.Forever });

        BusyRing.RenderTransformOrigin = new Point(0.5, 0.5);
        BusyRing.RenderTransform = new RotateTransform();
    }

    public void Refresh()
    {
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
        StatMods.Text = CountMods(settings.EffectiveGameDirectory).ToString();
        ChipMods.Text = AppServices.Mods.CountBundled() + " модов";
        LogCard.Visibility = settings.ShowLogOnHome ? Visibility.Visible : Visibility.Collapsed;

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
            Progress.Value = 0;
            PercentText.Text = "0%";
            StatusText.Text = "Готов к запуску";
            DetailText.Text = "Файлы проверятся автоматически при запуске";
            PillText.Text = "ГОТОВ К ЗАПУСКУ";
        }

        if (!_javaChecked)
            _ = DetectJavaAsync();
    }

    private async Task DetectJavaAsync()
    {
        _javaChecked = true;
        try
        {
            var info = await Task.Run(() => AppServices.Java.DetectAsync(21, CancellationToken.None));
            if (info is null)
            {
                StatJava.Text = "не найдена";
                ChipJava.Text = "Java —";
            }
            else
            {
                StatJava.Text = $"Java {info.Major}";
                ChipJava.Text = $"Java {info.Major}";
            }
        }
        catch
        {
            StatJava.Text = "не найдена";
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
        PlayIcon.Visibility = running ? Visibility.Visible : Visibility.Visible;

        if (running)
        {
            PillText.Text = "ИГРАЕТ";
            StatusText.Text = "Игра запущена";
            PercentText.Text = "100%";
            Progress.Value = 100;
            DetailText.Text = "Minecraft работает — можно свернуть лаунчер";
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("Запрошена остановка игры...");
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
            AppendLog($"Minecraft закрыт (код {code}).");
            Progress.Value = 0;
            PercentText.Text = "0%";
            StatusText.Text = "Готов к запуску";
            DetailText.Text = "Можно запускать снова";
            PillText.Text = "ГОТОВ К ЗАПУСКУ";
            SetRunningUi(false);
            PlayButton.IsEnabled = true;
            PlayIcon.Visibility = Visibility.Visible;
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

    // ------------------------------------------------------------------ log

    private void OnLogMessage(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        AppendLog(line);
    }

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        LogBox.AppendText(line + Environment.NewLine);
        _logLines++;

        if (_logLines > 700)
        {
            var lines = LogBox.Text.Replace("\r\n", "\n").Split('\n');
            LogBox.Text = string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 350)));
            _logLines = 350;
        }

        LogBox.ScrollToEnd();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
        }
        catch
        {
            // ignored
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        _logLines = 0;
    }

    // ------------------------------------------------------------------ actions

    private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = AppServices.Settings.Current.EffectiveGameDirectory;
        Directory.CreateDirectory(directory);
        ShellHelper.OpenFolder(directory);
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(AppServices.Mods.BundledDirectory);

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(AppPaths.LogsDirectory);

    private void Diagnose_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowDiagnostics();

    private void ChangeNick_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowProfiles();
}
