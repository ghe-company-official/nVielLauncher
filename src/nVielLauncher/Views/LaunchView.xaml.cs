using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using nVielLauncher.Services;
using WpfPath = System.Windows.Shapes.Path;

namespace nVielLauncher.Views;

public partial class LaunchView : UserControl, IRefreshable
{
    private static readonly string[] StageNames = { "Проверка", "Java", "Файлы игры", "Моды", "Запуск" };

    private static readonly Brush PendingBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ActiveBorder = Freeze(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush DoneBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush PendingBackground = Brushes.Transparent;
    private static readonly Brush ActiveBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush DoneBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A)));

    private readonly List<StageUi> _stages = new();
    private readonly List<Border> _connectors = new();
    private bool _busy;
    private bool _running;
    private bool _started;
    private bool _gameExited;
    private bool _canRetry;
    private bool _showDiagnostics;
    private int _logLines;
    private CancellationTokenSource? _launchCts;

    public LaunchView()
    {
        InitializeComponent();
        BuildStages();

        Log.Message += OnLogMessage;
        AppServices.Launcher.GameStarted += OnGameStarted;
        AppServices.Launcher.GameExited += OnGameExited;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var settings = AppServices.Settings.Current;
        var instance = settings.ActiveInstance;
        var name = instance?.Name ?? "Основная";
        var directory = settings.EffectiveGameDirectory;

        InstanceText.Text = "Инстанс: " + name;
        SubHeader.Text = $"{LauncherService.VersionLabel} · {name}";
        InstancePath.Text = directory;

        if (!_started && !_busy && !_running)
        {
            StateTitle.Text = "Готов к запуску";
            StateDetail.Text = "Нажми «Играть» на главной или «Повторить» ниже.";
            PercentText.Text = "0%";
            LaunchProgressBar.Value = 0;
            UpdateStage(0);
            UpdateButtons();
        }
    }

    public void BeginLaunch()
    {
        if (_busy || _running || AppServices.Launcher.IsRunning) return;
        _ = BeginLaunchAsync();
    }

    private async Task BeginLaunchAsync()
    {
        _busy = true;
        _running = false;
        _started = true;
        _gameExited = false;
        _canRetry = false;
        _showDiagnostics = false;
        _launchCts = new CancellationTokenSource();

        ResetUi();
        Refresh();

        var settings = AppServices.Settings.Current;
        var instance = settings.ActiveInstance;
        if (instance is null)
        {
            ShowError("Активный инстанс не найден.");
            _busy = false;
            UpdateButtons();
            return;
        }

        try
        {
            if (AppServices.Backups.ShouldAutoBackup(instance))
            {
                StateTitle.Text = "Резервная копия";
                StateDetail.Text = "Сохраняем миры и конфиги перед запуском";
                UpdateStage(0);
                AppendLog("=== Резервная копия перед запуском ===");

                try
                {
                    var backupProgress = new Progress<LaunchProgress>(p =>
                    {
                        LaunchProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
                        PercentText.Text = $"{p.Percent:0}%";
                        StateDetail.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + " · " + p.Detail;
                    });

                    var backup = await AppServices.Backups.CreateAsync(
                        AppServices.Instances.GetDirectory(instance),
                        instance.Id,
                        "auto",
                        settings.BackupRetention,
                        backupProgress,
                        _launchCts.Token);

                    if (backup is not null)
                    {
                        instance.LastBackupUtc = DateTime.UtcNow;
                        AppServices.Settings.Save();
                        AppendLog("Копия: " + backup.FileName);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog("Не удалось сделать копию: " + ex.Message);
                }
                finally
                {
                    LaunchProgressBar.Value = 0;
                    PercentText.Text = "0%";
                }
            }

            StateTitle.Text = "Проверка модов";
            StateDetail.Text = "Читаем fabric.mod.json и ищем конфликты";
            var gameDirectory = AppServices.Instances.GetDirectory(instance);
            try
            {
                var report = await ModConflictService.ScanAsync(gameDirectory, _launchCts.Token);
                AppendLog($"Моды: активно {report.EnabledCount}, ошибок {report.ErrorCount}, предупреждений {report.WarningCount}.");
                if (report.ErrorCount > 0)
                {
                    StateDetail.Text = $"Найдено конфликтов: {report.ErrorCount}. Проверь раздел «Моды → Конфликты».";
                    Notify.Show("Конфликты модов", $"Найдено ошибок: {report.ErrorCount}. Запуск продолжится.", "error");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog("Проверка модов пропущена: " + ex.Message);
            }

            AppendLog($"=== Запуск {LauncherService.VersionLabel} ===");
            var progress = new Progress<LaunchProgress>(OnProgress);
            await AppServices.Launcher.LaunchAsync(progress, _launchCts.Token);

            if (_gameExited)
                return;

            _running = true;
            ShowSuccess();
        }
        catch (OperationCanceledException)
        {
            ShowCancelled();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            Log.Write("LAUNCH: " + ex);
        }
        finally
        {
            _busy = false;
            _launchCts?.Dispose();
            _launchCts = null;
            UpdateButtons();
        }

        if (_running && !_gameExited && settings.CloseLauncherOnLaunch)
        {
            AppendLog("Лаунчер закроется через пару секунд...");
            await Task.Delay(900);
            Application.Current.Shutdown();
        }
    }

    private void ResetUi()
    {
        ClearLog();
        LaunchProgressBar.Value = 0;
        PercentText.Text = "0%";
        StateTitle.Text = "Подготовка";
        StateDetail.Text = "Проверяем настройки и активный инстанс";
        PillText.Text = "ЗАПУСК";
        SetStateIcon(StateIconKind.Play);
        UpdateStage(0);
        UpdateButtons();
    }

    private void OnProgress(LaunchProgress progress)
    {
        LaunchProgressBar.Value = Math.Clamp(progress.Percent, 0, 100);
        PercentText.Text = $"{progress.Percent:0}%";
        StateTitle.Text = progress.Status;
        if (!string.IsNullOrWhiteSpace(progress.Detail))
            StateDetail.Text = progress.Detail;

        UpdateStage(progress.Percent);

        if (!string.IsNullOrWhiteSpace(progress.Log))
            AppendLog(progress.Log);
    }

    private void ShowSuccess()
    {
        _running = true;
        _busy = false;
        _canRetry = false;
        _showDiagnostics = false;
        UpdateStage(100);
        LaunchProgressBar.Value = 100;
        PercentText.Text = "100%";
        StateTitle.Text = "Minecraft запущен";
        StateDetail.Text = "Игра работает. Можно свернуть лаунчер и вернуться в игру.";
        PillText.Text = "ИГРАЕТ";
        SetStateIcon(StateIconKind.Success);
        AppendLog("Игра запущена.");
        UpdateButtons();
        Notify.Show("Minecraft", "Игра запущена. Приятной игры!", "success");
    }

    private void ShowError(string message)
    {
        _running = false;
        _canRetry = true;
        _showDiagnostics = true;
        StateTitle.Text = "Ошибка запуска";
        StateDetail.Text = message;
        PillText.Text = "ОШИБКА";
        SetStateIcon(StateIconKind.Error);
        AppendLog("ОШИБКА: " + message);
        UpdateButtons();
    }

    private void ShowCancelled()
    {
        _running = false;
        _canRetry = true;
        _showDiagnostics = false;
        StateTitle.Text = "Запуск отменён";
        StateDetail.Text = "Файлы и настройки не изменены. Можно запустить снова.";
        PillText.Text = "ОТМЕНЕНО";
        SetStateIcon(StateIconKind.Play);
        AppendLog("Запуск отменён.");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _busy;
        RetryButton.Visibility = !_busy && _canRetry ? Visibility.Visible : Visibility.Collapsed;
        MinimizeButton.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsButton.Visibility = !_busy && _showDiagnostics ? Visibility.Visible : Visibility.Collapsed;
    }

    private enum StateIconKind
    {
        Play,
        Success,
        Error
    }

    private void SetStateIcon(StateIconKind kind)
    {
        var primary = (Brush)FindResource("TextPrimaryBrush");
        switch (kind)
        {
            case StateIconKind.Success:
                StateIcon.Data = (Geometry)FindResource("IconCheck");
                StateIcon.Fill = Brushes.Transparent;
                StateIcon.Stroke = primary;
                StateIcon.StrokeThickness = 1.8;
                StateIcon.StrokeStartLineCap = PenLineCap.Round;
                StateIcon.StrokeEndLineCap = PenLineCap.Round;
                break;

            case StateIconKind.Error:
                StateIcon.Data = (Geometry)FindResource("IconWarning");
                StateIcon.Fill = Brushes.Transparent;
                StateIcon.Stroke = ErrorBrush;
                StateIcon.StrokeThickness = 1.6;
                StateIcon.StrokeLineJoin = PenLineJoin.Round;
                break;

            default:
                StateIcon.Data = (Geometry)FindResource("IconPlay");
                StateIcon.Fill = primary;
                StateIcon.Stroke = Brushes.Transparent;
                StateIcon.StrokeThickness = 0;
                break;
        }
    }

    private void BuildStages()
    {
        var primary = (Brush)FindResource("TextPrimaryBrush");
        var muted = (Brush)FindResource("TextMutedBrush");

        for (var i = 0; i < StageNames.Length; i++)
        {
            if (i > 0)
            {
                var connector = new Border
                {
                    Width = 26,
                    Height = 1,
                    Background = PendingBorder,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(2, 14, 2, 0)
                };
                _connectors.Add(connector);
                StageList.Children.Add(connector);
            }

            var holder = new StackPanel { Width = 104 };
            var dotGrid = new Grid { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center };
            var ring = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Background = PendingBackground,
                BorderBrush = PendingBorder
            };

            var inner = new Grid();
            var number = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var check = new WpfPath
            {
                Data = (Geometry)FindResource("IconCheck"),
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                Stroke = primary,
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            inner.Children.Add(number);
            inner.Children.Add(check);
            ring.Child = inner;
            dotGrid.Children.Add(ring);

            var label = new TextBlock
            {
                Text = StageNames[i],
                FontSize = 10.5,
                Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 7, 0, 0)
            };

            holder.Children.Add(dotGrid);
            holder.Children.Add(label);
            StageList.Children.Add(holder);

            _stages.Add(new StageUi(ring, number, check, label));
        }
    }

    private void UpdateStage(double percent)
    {
        var active = percent switch
        {
            < 2 => 0,
            < 10 => 1,
            < 95 => 2,
            < 98 => 3,
            < 100 => 4,
            _ => StageNames.Length
        };

        var primary = (Brush)FindResource("TextPrimaryBrush");
        var muted = (Brush)FindResource("TextMutedBrush");

        for (var i = 0; i < _stages.Count; i++)
        {
            var stage = _stages[i];
            var done = active >= _stages.Count || i < active;
            var current = !done && i == active;

            stage.Ring.Background = done ? DoneBackground : current ? ActiveBackground : PendingBackground;
            stage.Ring.BorderBrush = done ? DoneBorder : current ? ActiveBorder : PendingBorder;
            stage.Number.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
            stage.Number.Foreground = current ? primary : muted;
            stage.Check.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
            stage.Label.Foreground = done || current ? primary : muted;
        }

        for (var i = 0; i < _connectors.Count; i++)
            _connectors[i].Background = i < active ? DoneBorder : PendingBorder;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _launchCts?.Cancel();
        CancelButton.IsEnabled = false;
        StateTitle.Text = "Отмена...";
        StateDetail.Text = "Останавливаем текущий этап";
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => BeginLaunch();

    private void Home_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.NavigateTo("home");

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
            window.WindowState = WindowState.Minimized;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("Запрошена остановка игры...");
        AppServices.Launcher.KillGame();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.ShowDiagnostics();

    private void Backups_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.OpenBackups("home");

    private void OnGameStarted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _running = true;
            UpdateButtons();
        });
    }

    private void OnGameExited(int code)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _busy = false;
            _running = false;
            _gameExited = true;
            _canRetry = true;
            _showDiagnostics = code != 0;
            _launchCts?.Dispose();
            _launchCts = null;

            StateTitle.Text = code == 0 ? "Игра завершена" : "Игра закрылась с ошибкой";
            StateDetail.Text = code == 0
                ? "Игровой сеанс завершён. Можно запустить снова."
                : $"Код выхода {code}. Открой диагностику, если ошибка повторяется.";
            PillText.Text = code == 0 ? "ЗАВЕРШЕНО" : "ОШИБКА";
            SetStateIcon(code == 0 ? StateIconKind.Play : StateIconKind.Error);
            AppendLog($"Minecraft закрыт (код {code}).");
            UpdateButtons();

            if (code == 0)
                Notify.Show("Minecraft", "Игровой сеанс завершён.", "info");
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

    private void ClearLog()
    {
        LogBox.Clear();
        _logLines = 0;
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
        }
        catch
        {
            // clipboard is sometimes locked by another app
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ClearLog();

    private sealed record StageUi(Border Ring, TextBlock Number, WpfPath Check, TextBlock Label);

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
