using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7B, 0xE3, 0xA0)));
    private static readonly Brush IdleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x56, 0x60)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _monitorTimer;
    private readonly SystemMonitor _monitor = new();

    private bool _loading;
    private bool _javaChecked;
    private bool _started;
    private bool _updateChecked;
    private bool _updateBusy;
    private UpdateInfo? _availableUpdate;
    private CancellationTokenSource? _updateCts;

    public SettingsView()
    {
        _loading = true;
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            AppServices.Settings.Save();
            ShowToast();
        };

        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _monitorTimer.Tick += (_, _) => UpdateMonitor();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        AnimateCards();
        StartAmbientAnimations();
        StartMonitor();

        UpdateVersionText.Text = $"Версия {AppInfo.VersionLabel} · {AppServices.Updates.SourceLabel}";
        UpdateVersionText.ToolTip = AppServices.Updates.SourceLabel;
        AboutVersionText.Text = $"nViel Launcher {AppInfo.VersionLabel} · Fabric 1.21.4";

        if (!_updateChecked)
        {
            _updateChecked = true;
            _ = CheckUpdatesAsync(silent: true);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            UpdateRamAllocation();
            UpdateMonitor();
        }));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _monitorTimer.Stop();
        _saveTimer.Stop();
        _updateCts?.Cancel();
    }

    public void Refresh()
    {
        _loading = true;
        var settings = AppServices.Settings.Current;

        NickBox.Text = settings.Nickname;
        AvatarLetter.Text = string.IsNullOrWhiteSpace(settings.Nickname)
            ? "S"
            : settings.Nickname[..1].ToUpperInvariant();

        RamSlider.Value = Math.Clamp(settings.RamMb, (int)RamSlider.Minimum, (int)RamSlider.Maximum);
        RamValueText.Text = $"{settings.RamMb} МБ";

        GameDirText.Text = settings.EffectiveGameDirectory;

        RefreshUpdateSourceUi();

        CloseLauncherToggle.IsChecked = settings.CloseLauncherOnLaunch;
        FullscreenToggle.IsChecked = settings.Fullscreen;
        ShowResolutionToggle.IsChecked = settings.ShowResolution;
        ShowLogToggle.IsChecked = settings.ShowLogOnHome;
        AnimationsToggle.IsChecked = settings.AnimationsEnabled;
        BlurToggle.IsChecked = settings.BlurEnabled;
        GlowToggle.IsChecked = settings.BackgroundGlow;
        SplashToggle.IsChecked = settings.ShowSplash;
        HighlightAccent(settings.AccentColor);

        ResolutionWidthBox.Text = settings.ResolutionWidth.ToString();
        ResolutionHeightBox.Text = settings.ResolutionHeight.ToString();

        if (string.IsNullOrWhiteSpace(settings.JavaPath))
        {
            JavaPathText.Text = "не найдена — используем автоматический поиск";
            SetJavaStatus(false, "проверка");
        }
        else
        {
            JavaPathText.Text = settings.JavaPath;
            SetJavaStatus(true, "настроена");
        }

        _loading = false;

        UpdateResolutionState();
        UpdateRamHint();
        UpdateRamAllocation();

        if (!_javaChecked && string.IsNullOrWhiteSpace(settings.JavaPath))
            _ = FindJavaAsync(silent: true);
    }

    // ------------------------------------------------------------------ animation

    private void AnimateCards()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        for (var i = 0; i < CardsPanel.Children.Count; i++)
        {
            if (CardsPanel.Children[i] is not FrameworkElement card) continue;

            card.Opacity = 0;
            var transform = new TranslateTransform(0, 16);
            card.RenderTransform = transform;

            var begin = TimeSpan.FromMilliseconds(i * 55);

            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                BeginTime = begin,
                EasingFunction = ease
            });

            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(360))
            {
                BeginTime = begin,
                EasingFunction = ease
            });
        }
    }

    private void StartAmbientAnimations()
    {
        if (AboutRing.RenderTransform is not RotateTransform)
        {
            AboutRing.RenderTransformOrigin = new Point(0.5, 0.5);
            AboutRing.RenderTransform = new RotateTransform();
        }

        if (AboutRing.RenderTransform is RotateTransform rotate)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(26)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(720))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    // ------------------------------------------------------------------ monitor

    private void StartMonitor()
    {
        if (!_started)
        {
            _started = true;
            var totalGb = SystemMonitor.TotalPhysicalMemoryMb / 1024.0;
            CoresText.Text = totalGb > 0
                ? $"{Environment.ProcessorCount} логических ядер · {totalGb:0.0} ГБ ОЗУ · обновление раз в секунду"
                : $"{Environment.ProcessorCount} логических ядер · обновление раз в секунду";
        }

        if (!_monitorTimer.IsEnabled)
            _monitorTimer.Start();
    }

    private void UpdateMonitor()
    {
        try
        {
            var sample = _monitor.Sample();

            AnimateFill(CpuFill, CpuTrack, sample.CpuPercent);
            CpuText.Text = $"{sample.CpuPercent:0}%";

            var ramPercent = sample.TotalMemoryMb > 0
                ? sample.UsedMemoryMb * 100.0 / sample.TotalMemoryMb
                : 0;

            AnimateFill(RamFill, RamTrack, ramPercent);
            RamText.Text = sample.TotalMemoryMb > 0
                ? $"{sample.UsedMemoryMb / 1024.0:0.0} / {sample.TotalMemoryMb / 1024.0:0.0} ГБ"
                : "—";
        }
        catch
        {
            // never let the monitor break the page
        }
    }

    private void AnimateFill(FrameworkElement fill, FrameworkElement track, double percent)
    {
        if (track.ActualWidth <= 0) return;

        var target = Math.Clamp(track.ActualWidth * percent / 100.0, 0, track.ActualWidth);
        var animation = new DoubleAnimation(fill.ActualWidth, target, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        fill.BeginAnimation(WidthProperty, animation);
    }

    // ------------------------------------------------------------------ hints

    private void UpdateRamHint()
    {
        var total = SystemMonitor.TotalPhysicalMemoryMb;
        RamHintText.Text = total > 0
            ? $"Физическая память: ~{total / 1024.0:0.0} ГБ. Для Fabric 1.21.4 обычно хватает 4096–6144 МБ."
            : "Для Fabric 1.21.4 обычно хватает 4096–6144 МБ.";
    }

    private void UpdateRamAllocation()
    {
        if (RamAllocTrack is null || RamAllocFill is null) return;

        var ram = AppServices.Settings.Current.RamMb;
        var total = SystemMonitor.TotalPhysicalMemoryMb;

        RamAllocText.Text = total > 0 ? $"{ram} / {total} МБ" : $"{ram} МБ";

        var percent = total > 0 ? Math.Clamp(ram * 100.0 / total, 0, 100) : 0;
        AnimateFill(RamAllocFill, RamAllocTrack, percent);
    }

    private void UpdateResolutionState()
    {
        var enabled = ShowResolutionToggle.IsChecked == true;
        ResolutionPanel.IsEnabled = enabled;
        ResolutionPanel.Opacity = enabled ? 1 : 0.45;
    }

    private void SetJavaStatus(bool ok, string text)
    {
        JavaStatusDot.Fill = ok ? OkBrush : IdleBrush;
        JavaStatusText.Text = text;
    }

    private void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ShowToast(string text = "Сохранено")
    {
        ToastText.Text = text;

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1750))));
        ToastText.BeginAnimation(OpacityProperty, animation);
    }

    // ------------------------------------------------------------------ handlers

    private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        var value = (int)e.NewValue;
        RamValueText.Text = $"{value} МБ";
        AppServices.Settings.Current.RamMb = value;
        UpdateRamAllocation();
        ScheduleSave();
    }

    private void Behavior_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var settings = AppServices.Settings.Current;
        settings.CloseLauncherOnLaunch = CloseLauncherToggle.IsChecked == true;
        settings.Fullscreen = FullscreenToggle.IsChecked == true;
        settings.ShowResolution = ShowResolutionToggle.IsChecked == true;
        settings.ShowLogOnHome = ShowLogToggle.IsChecked == true;
        settings.AnimationsEnabled = AnimationsToggle.IsChecked == true;
        settings.BlurEnabled = BlurToggle.IsChecked == true;
        settings.BackgroundGlow = GlowToggle.IsChecked == true;
        settings.ShowSplash = SplashToggle.IsChecked == true;

        UpdateResolutionState();
        (Window.GetWindow(this) as MainWindow)?.ApplyAppearance();
        ScheduleSave();
    }

    private void Accent_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;

        AppServices.Settings.Current.AccentColor = hex;
        HighlightAccent(hex);
        (Window.GetWindow(this) as MainWindow)?.ApplyAppearance();
        ScheduleSave();
        ShowToast("Акцент обновлён");
    }

    private void HighlightAccent(string color)
    {
        var selected = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
        var idle = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        foreach (var swatch in new[] { Accent0, Accent1, Accent2, Accent3, Accent4 })
        {
            var isActive = string.Equals(swatch.Tag as string, color, StringComparison.OrdinalIgnoreCase);
            swatch.BorderThickness = new Thickness(isActive ? 3 : 1);
            swatch.BorderBrush = isActive ? selected : idle;
        }
    }

    private void ResolutionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        var settings = AppServices.Settings.Current;
        var box = sender as TextBox;

        if (int.TryParse(box?.Text, out var value))
        {
            if (ReferenceEquals(box, ResolutionWidthBox))
                settings.ResolutionWidth = Math.Clamp(value, 854, 3840);
            else
                settings.ResolutionHeight = Math.Clamp(value, 480, 2160);

            ScheduleSave();
        }
    }

    private void ResolutionBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Settings.Current;
        ResolutionWidthBox.Text = settings.ResolutionWidth.ToString();
        ResolutionHeightBox.Text = settings.ResolutionHeight.ToString();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var nickname = NickBox.Text.Trim();
        if (!LauncherService.IsValidNickname(nickname))
        {
            ShowToast("Ник: 3–16 символов, A-Z, 0-9, _");
            NickBox.Text = AppServices.Settings.Current.Nickname;
            return;
        }

        var settings = AppServices.Settings.Current;
        settings.Nickname = nickname;
        settings.CloseLauncherOnLaunch = CloseLauncherToggle.IsChecked == true;
        settings.Fullscreen = FullscreenToggle.IsChecked == true;
        settings.ShowResolution = ShowResolutionToggle.IsChecked == true;
        settings.ShowLogOnHome = ShowLogToggle.IsChecked == true;

        AppServices.Settings.Save();
        ShowToast();

        (Window.GetWindow(this) as MainWindow)?.RefreshProfile();
    }

    private async void FindJava_Click(object sender, RoutedEventArgs e) => await FindJavaAsync(silent: false);

    private async Task FindJavaAsync(bool silent)
    {
        _javaChecked = true;

        if (!silent)
        {
            JavaPathText.Text = "ищем...";
            JavaInfoText.Text = "Сканируем систему на наличие Java 21...";
            SetJavaStatus(false, "поиск...");
        }

        var info = await Task.Run(() => AppServices.Java.DetectAsync(21, CancellationToken.None));

        if (info is null)
        {
            JavaPathText.Text = "не найдена — скачаем при первом запуске";
            JavaInfoText.Text = "Java 21 (Temurin JRE) будет скачана автоматически при запуске игры.";
            SetJavaStatus(false, "не найдена");
            if (!silent) ShowToast("Java 21 не найдена");
            return;
        }

        AppServices.Settings.Current.JavaPath = info.JavaExe;
        AppServices.Settings.Save();

        JavaPathText.Text = info.JavaExe;
        JavaInfoText.Text = $"Java {info.Major} · {info.VersionString}";
        SetJavaStatus(true, $"Java {info.Major}");
        if (!silent) ShowToast("Java найдена");
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери java.exe",
            Filter = "java.exe|java.exe|Все приложения (*.exe)|*.exe"
        };

        if (dialog.ShowDialog() != true) return;

        var info = JavaService.Probe(dialog.FileName);
        if (info is null)
        {
            ShowToast("Это не похоже на Java");
            return;
        }

        AppServices.Settings.Current.JavaPath = info.JavaExe;
        AppServices.Settings.Save();

        JavaPathText.Text = info.JavaExe;
        JavaInfoText.Text = $"Java {info.Major} · {info.VersionString}";
        SetJavaStatus(true, $"Java {info.Major}");
        ShowToast($"Java {info.Major} сохранена");
    }

    private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Settings.Current;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка игры",
            InitialDirectory = Directory.Exists(settings.EffectiveGameDirectory)
                ? settings.EffectiveGameDirectory
                : AppPaths.DefaultGameDirectory
        };

        if (dialog.ShowDialog() != true) return;

        settings.GameDirectory = dialog.FolderName;
        AppServices.Settings.Save();

        GameDirText.Text = settings.EffectiveGameDirectory;
        ShowToast();
    }

    private void OpenGameDir_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(AppServices.Settings.Current.EffectiveGameDirectory);

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(AppServices.Mods.BundledDirectory);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => ShellHelper.OpenFolder(AppPaths.LogsDirectory);

    private void ReplayTour_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.StartTour();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Settings.Current;
        settings.RamMb = SettingsService.DefaultRamMb;
        settings.CloseLauncherOnLaunch = false;
        settings.Fullscreen = false;
        settings.ShowResolution = false;
        settings.ShowLogOnHome = true;
        settings.AnimationsEnabled = true;
        settings.BlurEnabled = true;
        settings.BackgroundGlow = true;
        settings.ShowSplash = true;
        settings.AccentColor = "#FFFFFFFF";
        settings.ResolutionWidth = 1280;
        settings.ResolutionHeight = 720;
        settings.GameDirectory = AppPaths.DefaultGameDirectory;

        AppServices.Settings.Save();
        Refresh();
        (Window.GetWindow(this) as MainWindow)?.ApplyAppearance();
        ShowToast("Настройки сброшены");
    }

    // ------------------------------------------------------------------ updates / recovery

    private void OpenRe_Click(object sender, RoutedEventArgs e) => ReLauncher.OpenRe();

    private void RefreshUpdateSourceUi()
    {
        var settings = AppServices.Settings.Current;
        UpdateRepoBox.Text = settings.UpdateRepository;
        UpdateTokenBox.Clear();
        UpdateSourceStatus.Text = BuildUpdateSourceStatus();
        UpdateVersionText.Text = $"Версия {AppInfo.VersionLabel} · {AppServices.Updates.SourceLabel}";
        UpdateVersionText.ToolTip = AppServices.Updates.SourceLabel;
    }

    private static string BuildUpdateSourceStatus()
    {
        var configured = AppServices.Settings.Current.UpdateRepository;
        if (!string.IsNullOrWhiteSpace(configured) && !AppServices.Updates.HasCustomSource)
            return "Некорректный репозиторий — используется репозиторий релизов по умолчанию.";

        var repo = AppServices.Updates.SourceLabel;
        var token = AppServices.Updates.TokenSourceLabel;

        return AppServices.Updates.HasCustomSource
            ? $"Свой репозиторий: {repo} · {token}"
            : $"Репозиторий релизов: {repo} · {token}";
    }

    private void SaveUpdateSource_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
        {
            ShowToast("Дождись завершения текущей операции");
            return;
        }

        var repoText = UpdateRepoBox.Text.Trim();
        var normalizedRepo = "";

        if (!string.IsNullOrWhiteSpace(repoText))
        {
            if (!UpdateService.TryParseRepository(repoText, out var owner, out var repository))
            {
                UpdateSourceStatus.Text = "Неверный формат. Укажи owner/repo.";
                ShowToast("Формат: owner/repo");
                return;
            }

            normalizedRepo = owner + "/" + repository;
        }

        var token = UpdateTokenBox.Password.Trim();
        string? protectedToken = null;

        if (!string.IsNullOrEmpty(token))
        {
            protectedToken = SecretProtector.Protect(token);
            if (string.IsNullOrEmpty(protectedToken))
            {
                UpdateSourceStatus.Text = "Не удалось зашифровать токен.";
                ShowToast("Ошибка шифрования токена");
                return;
            }
        }

        var settings = AppServices.Settings.Current;
        settings.UpdateRepository = normalizedRepo;
        if (protectedToken is not null)
            settings.UpdateTokenProtected = protectedToken;

        AppServices.Settings.Save();
        RefreshUpdateSourceUi();
        ShowToast("Источник обновлений сохранён");

        _availableUpdate = null;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateNotesText.Visibility = Visibility.Collapsed;
        _updateChecked = true;
        _ = CheckUpdatesAsync(silent: true, force: true);
    }

    private void ClearUpdateToken_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
        {
            ShowToast("Дождись завершения текущей операции");
            return;
        }

        var settings = AppServices.Settings.Current;
        if (string.IsNullOrWhiteSpace(settings.UpdateTokenProtected))
        {
            if (AppServices.Updates.TokenSourceLabel == "токен из Git Credential Manager")
            {
                UpdateSourceStatus.Text = "Токен берётся из Git Credential Manager; здесь его удалить нельзя.";
                ShowToast("Токен из Git Credential Manager");
            }
            else
            {
                UpdateSourceStatus.Text = "Токен не задан.";
                ShowToast("Токен не задан");
            }
            return;
        }

        settings.UpdateTokenProtected = "";
        AppServices.Settings.Save();
        RefreshUpdateSourceUi();
        ShowToast("Токен удалён");

        _availableUpdate = null;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateNotesText.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "нужна проверка";
        UpdateDot.Fill = IdleBrush;
        UpdateStepText.Visibility = Visibility.Collapsed;
        UpdateStepText.Text = "";
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => await CheckUpdatesAsync(silent: false);

    private async Task CheckUpdatesAsync(bool silent, bool force = false)
    {
        if (_updateBusy) return;

        _updateBusy = true;
        _updateChecked = true;
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateNotesText.Visibility = Visibility.Collapsed;
        UpdateStepText.Visibility = Visibility.Collapsed;
        UpdateStepText.Text = "";
        UpdateStatusText.Text = "проверяем...";
        UpdateDot.Fill = IdleBrush;

        try
        {
            var result = await AppServices.Updates.CheckAsync(CancellationToken.None, force: force || !silent);

            switch (result.Status)
            {
                case UpdateStatus.Available when result.Update is not null:
                    _availableUpdate = result.Update;
                    UpdateDot.Fill = OkBrush;
                    UpdateStatusText.Text = "доступно обновление";
                    UpdateNotesText.Text = BuildNotes(result.Update.Notes);
                    UpdateNotesText.Visibility = string.IsNullOrWhiteSpace(UpdateNotesText.Text)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    InstallUpdateButton.Content = "Обновить до " + result.Update.Version;
                    InstallUpdateButton.Visibility = Visibility.Visible;
                    UpdateStepText.Visibility = Visibility.Collapsed;
                    if (!silent) ShowToast("Доступно обновление " + result.Update.Version);
                    break;

                case UpdateStatus.UpToDate:
                    _availableUpdate = null;
                    UpdateDot.Fill = OkBrush;
                    UpdateStatusText.Text = "актуальная версия";
                    UpdateStepText.Visibility = Visibility.Collapsed;
                    if (!silent) ShowToast("Обновлений нет");
                    break;

                case UpdateStatus.NoReleases:
                    _availableUpdate = null;
                    UpdateDot.Fill = IdleBrush;
                    UpdateStatusText.Text = "нет релизов / доступа";
                    UpdateStepText.Text = result.Message;
                    UpdateStepText.Visibility = Visibility.Visible;
                    if (!silent) ShowToast("Нет релизов или доступа к репозиторию");
                    break;

                default:
                    _availableUpdate = null;
                    UpdateDot.Fill = ErrorBrush;
                    UpdateStatusText.Text = "ошибка проверки";
                    UpdateStepText.Text = result.Message;
                    UpdateStepText.Visibility = Visibility.Visible;
                    if (!silent) ShowToast("Не удалось проверить обновления");
                    break;
            }
        }
        catch (Exception ex)
        {
            _availableUpdate = null;
            UpdateDot.Fill = ErrorBrush;
            UpdateStatusText.Text = "не удалось проверить";
            UpdateStepText.Text = "Проверь подключение к интернету и повтори попытку.";
            UpdateStepText.Visibility = Visibility.Visible;
            Log.Write("UPDATE: " + ex);
            if (!silent) ShowToast("Не удалось проверить обновления");
        }
        finally
        {
            _updateBusy = false;
            CheckUpdateButton.IsEnabled = true;
            UpdateSourceStatus.Text = BuildUpdateSourceStatus();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _updateBusy) return;

        var update = _availableUpdate;
        _updateBusy = true;
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateNotesText.Visibility = Visibility.Collapsed;
        UpdateStepText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "скачивание...";
        UpdateDot.Fill = IdleBrush;
        _updateCts = new CancellationTokenSource();

        try
        {
            UpdateStepText.Text = "1 / 3 · Скачивание обновления";
            var progress = new Progress<double>(p =>
            {
                if (p >= 0) UpdateProgress.Value = Math.Clamp(p * 100, 0, 100);
            });

            var stage = new Progress<string>(text =>
            {
                UpdateStepText.Text = text;
                UpdateStatusText.Text = text.Contains("Проверка", StringComparison.OrdinalIgnoreCase)
                    ? "проверка..."
                    : "скачивание...";
            });

            var path = await AppServices.Updates.DownloadAsync(update, progress, _updateCts.Token, stage);

            UpdateProgress.Value = 100;
            UpdateStatusText.Text = "установка...";
            UpdateStepText.Text = "3 / 3 · Установка и перезапуск лаунчера";
            await Task.Delay(500);

            AppServices.Updates.ApplyAndRestart(path);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "отменено";
            UpdateStepText.Text = "Обновление отменено.";
            UpdateDot.Fill = IdleBrush;
            InstallUpdateButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Write("UPDATE: установка не удалась: " + ex);
            UpdateStatusText.Text = "ошибка обновления";
            UpdateStepText.Text = ex.Message;
            UpdateDot.Fill = ErrorBrush;
            UpdateProgress.Visibility = Visibility.Collapsed;
            InstallUpdateButton.IsEnabled = true;
            Notify.Show("Обновление не установлено", ex.Message, "error");
        }
        finally
        {
            _updateBusy = false;
            CheckUpdateButton.IsEnabled = true;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private static string BuildNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";

        var text = notes.Replace("\r", "").Trim();
        if (text.Length > 280) text = text[..280].TrimEnd() + "…";
        return text;
    }
}
