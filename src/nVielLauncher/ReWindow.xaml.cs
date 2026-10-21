using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using nVielLauncher.Services;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace nVielLauncher;

/// <summary>
/// nVielRE — a separate recovery edition of the launcher. It shares the visual language
/// and settings with the main window, but never starts Minecraft.
/// </summary>
public partial class ReWindow : Window
{
    private static readonly Brush OkBrush = Freeze(Color.FromRgb(0x7B, 0xE3, 0xA0));
    private static readonly Brush WarnBrush = Freeze(Color.FromRgb(0xF0, 0xC0, 0x6B));
    private static readonly Brush ErrorBrush = Freeze(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush IdleBrush = Freeze(Color.FromRgb(0x56, 0x56, 0x60));

    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _preflighting;

    public ReWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimateWindowIn();
        StartAmbientAnimations();

        BuildText.Text = $"nVielRE 2.0 · core {AppInfo.DisplayVersion} · Fabric 1.21.4";
        LauncherDetail.Text = "nViel Launcher " + AppInfo.DisplayVersion;

        AppendLog("nVielRE 2.0 · recovery edition", RepairLogKind.Success);
        AppendLog($"Ядро лаунчера {AppInfo.DisplayVersion} · {Environment.OSVersion.VersionString}");
        AppendLog("Проверяем окружение...");

        _ = PreflightAsync();
    }

    // ------------------------------------------------------------------ startup / status

    private async Task PreflightAsync(bool writeLog = true)
    {
        if (_running || _preflighting) return;
        _preflighting = true;

        try
        {
            var gameDirectory = AppServices.Settings.Current.EffectiveGameDirectory;

            var java = await Task.Run(() => AppServices.Java.DetectAsync(21, CancellationToken.None));
            if (java is null)
            {
                SetDot(JavaDot, WarnBrush);
                JavaBadge.Text = "НЕТ";
                JavaDetail.Text = "будет скачана при восстановлении";
            }
            else
            {
                SetDot(JavaDot, OkBrush);
                JavaBadge.Text = "21";
                JavaDetail.Text = $"Java {java.Major} · {java.Home}";
            }

            var clientJar = Path.Combine(gameDirectory, "versions", "1.21.4", "1.21.4.jar");
            var indexDirectory = Path.Combine(gameDirectory, "assets", "indexes");
            var fabricCache = Path.Combine(gameDirectory, "versions", "fabric-loader", "loader.txt");

            var hasClient = File.Exists(clientJar) && new FileInfo(clientJar).Length > 0;
            var hasAssets = Directory.Exists(indexDirectory) &&
                            Directory.EnumerateFiles(indexDirectory, "*.json").Any();
            var hasFabric = File.Exists(fabricCache);
            var filesReady = hasClient && hasAssets && hasFabric;

            if (filesReady)
            {
                SetDot(GameDot, OkBrush);
                GameBadge.Text = "OK";
                GameDetail.Text = "клиент, ресурсы и Fabric на месте";
            }
            else if (!Directory.Exists(gameDirectory))
            {
                SetDot(GameDot, IdleBrush);
                GameBadge.Text = "—";
                GameDetail.Text = "игра ещё не установлена";
            }
            else
            {
                SetDot(GameDot, WarnBrush);
                GameBadge.Text = "НЕТ";
                GameDetail.Text = "файлы будут скачаны при запуске";
            }

            var (installed, total) = AppServices.Mods.GetCoreModStatus(gameDirectory);
            if (total == 0)
            {
                SetDot(ModsDot, IdleBrush);
                ModsBadge.Text = "—";
                ModsDetail.Text = "встроенных модов нет в этой сборке";
            }
            else if (installed == total)
            {
                SetDot(ModsDot, OkBrush);
                ModsBadge.Text = $"{installed}/{total}";
                ModsDetail.Text = "все обязательные моды на месте";
            }
            else
            {
                SetDot(ModsDot, WarnBrush);
                ModsBadge.Text = $"{installed}/{total}";
                ModsDetail.Text = "требуется восстановление";
            }

            var launcherOk = true;
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    using var _ = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
                }
            }
            catch
            {
                launcherOk = false;
            }

            SetDot(LauncherDot, launcherOk ? OkBrush : ErrorBrush);
            LauncherBadge.Text = launcherOk ? "OK" : "ERR";
            LauncherDetail.Text = launcherOk
                ? $"настройки в порядке · {AppInfo.DisplayVersion}"
                : "файл настроек повреждён";

            var ram = SystemMonitor.TotalPhysicalMemoryMb / 1024.0;
            var free = 0d;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(gameDirectory));
                if (!string.IsNullOrWhiteSpace(root))
                    free = new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d;
            }
            catch
            {
                // optional
            }

            SystemDetail.Text =
                $"{Environment.ProcessorCount} логических ядер · {ram:0.0} ГБ ОЗУ\n" +
                (free > 0 ? $"свободно {free:0.0} ГБ на диске\n" : "") +
                gameDirectory;

            if (writeLog)
            {
                AppendLog(java is null ? "Java 21 не найдена." : $"Java {java.Major} найдена.",
                    java is null ? RepairLogKind.Warning : RepairLogKind.Success);
                AppendLog(filesReady ? "Файлы Minecraft готовы." : "Файлы Minecraft требуют установки.",
                    filesReady ? RepairLogKind.Success : RepairLogKind.Warning);
            }

            SetHeader(launcherOk);
        }
        catch (Exception ex)
        {
            AppendLog("Проверка окружения: " + ex.Message, RepairLogKind.Error);
            SetHeader(false);
        }
        finally
        {
            _preflighting = false;
        }
    }

    private void SetHeader(bool ok)
    {
        HeaderDot.Fill = ok ? OkBrush : ErrorBrush;
        HeaderStatus.Text = ok ? "SYSTEM READY" : "ATTENTION";
    }

    private static void SetDot(Ellipse dot, Brush brush) => dot.Fill = brush;

    // ------------------------------------------------------------------ repair

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        await RunRepairAsync();
    }

    private async Task RunRepairAsync()
    {
        if (_running) return;

        BeginRun("REPAIRING...");
        _cts = new CancellationTokenSource();

        var progress = new Progress<LaunchProgress>(p =>
        {
            RepairProgress.Value = Math.Clamp(p.Percent, 0, 100);
            RepairPercent.Text = $"{Math.Clamp(p.Percent, 0, 100):0}%";

            var text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + " · " + p.Detail;
            if (!string.IsNullOrWhiteSpace(text)) RepairStatus.Text = text;
        });

        var log = new Progress<RepairLogEntry>(entry => AppendLog(entry.Message, entry.Kind));

        try
        {
            var report = await RepairService.RunAsync(
                AppServices.Settings.Current.EffectiveGameDirectory,
                progress, log, _cts.Token);

            RepairStatus.Text = report.Summary;
            RepairSubtitle.Text = report.Errors == 0
                ? "Проверка завершена — проблем не найдено."
                : $"Проверка завершена, проблем: {report.Errors}.";

            SetDot(JavaDot, report.JavaOk ? OkBrush : ErrorBrush);
            SetDot(GameDot, report.FilesOk ? OkBrush : ErrorBrush);
            SetDot(ModsDot, report.ModsOk ? OkBrush : WarnBrush);
            SetDot(LauncherDot, report.LauncherOk ? OkBrush : ErrorBrush);

            HeaderDot.Fill = report.Errors == 0 ? OkBrush : WarnBrush;
            HeaderStatus.Text = report.Errors == 0 ? "SYSTEM READY" : "ATTENTION";
        }
        catch (OperationCanceledException)
        {
            RepairStatus.Text = "Проверка остановлена.";
            AppendLog("Проверка остановлена пользователем.", RepairLogKind.Warning);
            SetHeader(true);
        }
        catch (Exception ex)
        {
            RepairStatus.Text = "Ошибка восстановления.";
            AppendLog("Критическая ошибка: " + ex.Message, RepairLogKind.Error);
            SetHeader(false);
        }
        finally
        {
            EndRun();
        }

        await PreflightAsync(writeLog: false);
    }

    private async void QuickJava_Click(object sender, RoutedEventArgs e)
    {
        await RunQuickAsync("Java", async ct =>
        {
            AppendLog("Проверяем Java 21...");
            var java = await AppServices.Java.DetectAsync(21, ct);

            if (java is null)
            {
                AppendLog("Java 21 не найдена — скачиваем Temurin JRE.", RepairLogKind.Warning);
                java = await AppServices.Java.EnsureJavaAsync(21,
                    UiProgress(0, 100, "Java"), ct);
            }

            AppServices.Settings.Current.JavaPath = java.JavaExe;
            AppServices.Settings.Save();

            SetDot(JavaDot, OkBrush);
            JavaBadge.Text = "21";
            JavaDetail.Text = $"Java {java.Major} · {java.Home}";
            AppendLog($"Java {java.Major}: {java.JavaExe}", RepairLogKind.Success);
        });
    }

    private async void QuickMods_Click(object sender, RoutedEventArgs e)
    {
        await RunQuickAsync("Моды", async ct =>
        {
            var gameDirectory = AppServices.Settings.Current.EffectiveGameDirectory;
            AppendLog("Восстанавливаем обязательные и пользовательские моды...");

            await AppServices.Mods.EnsureCoreModsAsync(gameDirectory, ct);
            await AppServices.Mods.SyncAsync(gameDirectory, null, ct);

            var (installed, total) = AppServices.Mods.GetCoreModStatus(gameDirectory);
            SetDot(ModsDot, total > 0 && installed == total ? OkBrush : WarnBrush);
            ModsBadge.Text = total > 0 ? $"{installed}/{total}" : "—";

            AppendLog($"Обязательные моды: {installed} / {total}.",
                total > 0 && installed == total ? RepairLogKind.Success : RepairLogKind.Warning);
        });
    }

    private async void QuickFabric_Click(object sender, RoutedEventArgs e)
    {
        await RunQuickAsync("Fabric", async ct =>
        {
            AppendLog("Проверяем клиент, библиотеки, ресурсы и Fabric...");
            await AppServices.Installer.EnsureAsync(AppServices.Settings.Current.EffectiveGameDirectory,
                UiProgress(0, 100, "Minecraft"), ct);
            AppendLog("Файлы Minecraft и Fabric проверены.", RepairLogKind.Success);
        });
    }

    private async Task RunQuickAsync(string title, Func<CancellationToken, Task> action)
    {
        if (_running) return;

        BeginRun(title.ToUpperInvariant() + "...");
        _cts = new CancellationTokenSource();

        try
        {
            await action(_cts.Token);
            RepairStatus.Text = title + ": готово.";
        }
        catch (OperationCanceledException)
        {
            AppendLog(title + ": остановлено.", RepairLogKind.Warning);
            RepairStatus.Text = "Остановлено.";
        }
        catch (Exception ex)
        {
            AppendLog(title + ": " + ex.Message, RepairLogKind.Error);
            RepairStatus.Text = "Ошибка.";
            SetHeader(false);
        }
        finally
        {
            EndRun();
        }

        await PreflightAsync(writeLog: false);
    }

    private void BeginRun(string status)
    {
        _running = true;
        RepairButton.IsEnabled = false;
        RepairButton.Content = "REPAIRING...";
        StopButton.Visibility = Visibility.Visible;
        RepairProgress.Value = 0;
        RepairPercent.Text = "0%";
        RepairStatus.Text = status;
        RepairSubtitle.Text = "Java, клиент, библиотеки, ресурсы, Fabric и обязательные моды.";
        HeaderStatus.Text = "REPAIR IN PROGRESS";
        HeaderDot.Fill = WarnBrush;
    }

    private void EndRun()
    {
        _running = false;
        RepairButton.IsEnabled = true;
        RepairButton.Content = "REPAIR";
        StopButton.Visibility = Visibility.Collapsed;
        _cts?.Dispose();
        _cts = null;
    }

    private IProgress<LaunchProgress> UiProgress(double from, double to, string prefix)
    {
        return new Progress<LaunchProgress>(p =>
        {
            var percent = from + Math.Clamp(p.Percent, 0, 100) / 100d * (to - from);
            RepairProgress.Value = percent;
            RepairPercent.Text = $"{percent:0}%";

            var text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + " · " + p.Detail;
            if (!string.IsNullOrWhiteSpace(text)) RepairStatus.Text = prefix + ": " + text;
        });
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;

        AppendLog("Останавливаем...", RepairLogKind.Warning);
        _cts.Cancel();
    }

    // ------------------------------------------------------------------ console

    private void AppendLog(string message, RepairLogKind kind = RepairLogKind.Info)
    {
        try
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var color = kind switch
            {
                RepairLogKind.Success => OkBrush,
                RepairLogKind.Warning => WarnBrush,
                RepairLogKind.Error => ErrorBrush,
                _ => (Brush)FindResource("TextSecondaryBrush")
            };

            ConsolePanel.Children.Add(new TextBlock
            {
                Text = $"[{time}]  {message}",
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11.5,
                Foreground = color,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 2)
            });

            while (ConsolePanel.Children.Count > 400)
                ConsolePanel.Children.RemoveAt(0);

            ConsoleScroll.ScrollToEnd();
        }
        catch
        {
            // the console must never break a repair
        }
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        ConsolePanel.Children.Clear();
        AppendLog("Консоль очищена.");
    }

    private void CopyConsole_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var builder = new StringBuilder();
            foreach (var child in ConsolePanel.Children)
            {
                if (child is TextBlock text)
                    builder.AppendLine(text.Text);
            }

            if (builder.Length == 0) return;

            Clipboard.SetText(builder.ToString());
            AppendLog("Консоль скопирована в буфер обмена.", RepairLogKind.Success);
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось скопировать консоль: " + ex.Message, RepairLogKind.Error);
        }
    }

    private async void BuildReport_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Собрать архив диагностики?\n\n" +
            "Внутри: логи лаунчера и игры, последние crash-reports, версия, Java и обезличенные настройки. " +
            "Имена профилей, серверов и пути пользователя маскируются.",
            "Отчёт nVielRE", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            AppendLog("Собираем архив диагностики...");
            var bundle = await DiagnosticsBundleService.CreateAsync(
                AppServices.Settings.Current.EffectiveGameDirectory,
                CancellationToken.None);

            AppendLog($"Отчёт готов: {bundle.Path} ({bundle.Size / 1024d:0.0} КБ).", RepairLogKind.Success);

            if (!string.IsNullOrWhiteSpace(DiagnosticsBundleService.UploadEndpoint))
            {
                var upload = MessageBox.Show(
                    "Архив создан. Отправить его на сервер поддержки?\n\n" +
                    "Отправка добровольная: без согласия архив останется только на этом компьютере.",
                    "Отправка отчёта", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (upload == MessageBoxResult.Yes)
                {
                    AppendLog("Отправляем отчёт на сервер поддержки...");
                    var result = await DiagnosticsBundleService.UploadAsync(bundle.Path, CancellationToken.None);
                    AppendLog("Отчёт отправлен: " + result, RepairLogKind.Success);
                }
            }

            ShellHelper.OpenFolder(Path.GetDirectoryName(bundle.Path)!);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Сбор отчёта отменён.", RepairLogKind.Warning);
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось собрать отчёт: " + ex.Message, RepairLogKind.Error);
        }
    }

    // ------------------------------------------------------------------ folders

    private void OpenGame_Click(object sender, RoutedEventArgs e)
    {
        var directory = AppServices.Settings.Current.EffectiveGameDirectory;
        Directory.CreateDirectory(directory);
        ShellHelper.OpenFolder(directory);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "logs");
        Directory.CreateDirectory(directory);
        ShellHelper.OpenFolder(directory);
    }

    private void OpenCrash_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "crash-reports");
        Directory.CreateDirectory(directory);
        ShellHelper.OpenFolder(directory);
    }

    private void BackToLauncher_Click(object sender, RoutedEventArgs e) => ReLauncher.OpenLauncher();

    // ------------------------------------------------------------------ chrome / animation

    private void AnimateWindowIn()
    {
        RootGrid.Opacity = 0;
        RootScale.ScaleX = 0.985;
        RootScale.ScaleY = 0.985;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootGrid.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
    }

    private void StartAmbientAnimations()
    {
        HeaderDot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });

        ConsoleDot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(1100))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch
        {
            // ignored
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);

        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
