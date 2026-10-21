using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7B, 0xE3, 0xA0)));
    private static readonly Brush IdleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x56, 0x60)));

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _monitorTimer;
    private readonly SystemMonitor _monitor = new();

    private bool _loading;
    private bool _javaChecked;
    private bool _started;

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

        AboutVersionText.Text = $"{AppInfo.DisplayName} · Fabric 1.21.4";

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

        StorageModeText.Text = AppPaths.IsPortable
            ? "Portable · данные рядом с exe"
            : "Локальные настройки · %LOCALAPPDATA%";
        StorageDetailText.Text = AppPaths.IsPortable
            ? "Данные хранятся в папке data рядом с exe. Настройки можно перенести между компьютерами одним JSON-файлом."
            : "Настройки можно перенести на другой компьютер одним JSON-файлом или включить portable-режим.";

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
                ? $"{Environment.ProcessorCount} логических ядер · {totalGb:0.0} ГБ ОЗУ · опрос раз в секунду"
                : $"{Environment.ProcessorCount} логических ядер · опрос раз в секунду";
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

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт настроек nViel Launcher",
            FileName = $"nViel-settings-{DateTime.Now:yyyyMMdd}.json",
            Filter = "JSON (*.json)|*.json",
            AddExtension = true,
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            AppServices.Settings.Save();
            AppServices.Settings.ExportTo(dialog.FileName);
            ShowToast("Настройки экспортированы");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Не удалось экспортировать настройки:\n" + ex.Message,
                "nViel Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт настроек nViel Launcher",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var answer = MessageBox.Show(Window.GetWindow(this),
            "Импортировать настройки? Текущие настройки будут заменены.\n" +
            "Ник, профили, серверы, RAM и параметры окна будут взяты из файла.",
            "nViel Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            AppServices.Settings.ImportFrom(dialog.FileName);
            Refresh();
            (Window.GetWindow(this) as MainWindow)?.ApplyAppearance();
            ShowToast("Настройки импортированы");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Не удалось импортировать настройки:\n" + ex.Message,
                "nViel Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Portable_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("NVIEL_PORTABLE"), "1", StringComparison.Ordinal))
        {
            MessageBox.Show(Window.GetWindow(this),
                "Portable-режим задан переменной окружения NVIEL_PORTABLE и здесь не переключается.",
                "nViel Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (File.Exists(AppPaths.PortableFlagFile))
            {
                File.Delete(AppPaths.PortableFlagFile);
                MessageBox.Show(Window.GetWindow(this),
                    "Portable-режим выключен. Перезапусти лаунчер, чтобы вернуться к локальным настройкам.",
                    "nViel Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                File.WriteAllText(AppPaths.PortableFlagFile, "portable" + Environment.NewLine);
                MessageBox.Show(Window.GetWindow(this),
                    "Portable-режим включён. После перезапуска данные будут храниться в папке data рядом с exe.\n\n" +
                    "Текущие настройки можно перенести через «Экспорт» и «Импорт».",
                    "nViel Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this),
                "Не удалось изменить portable-режим. Папка лаунчера должна быть доступна для записи.\n" + ex.Message,
                "nViel Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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

    // ------------------------------------------------------------------ recovery

    private void OpenRe_Click(object sender, RoutedEventArgs e) => ReLauncher.OpenRe();
}
