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
    private bool _checkingUpdates;
    private Storyboard? _ambientStoryboard;
    private bool _ambientStarted;
    private readonly Dictionary<string, Border> _iconTiles = new();
    private string _iconKey = "";

    public SettingsView()
    {
        _loading = true;
        InitializeComponent();

        BuildAccentSwatches();
        BuildThemeModeChips();
        BuildBaseSwatches();
        BuildIconTiles();
        AccentPicker.ColorChanged += AccentPicker_ColorChanged;
        BasePicker.ColorChanged += BasePicker_ColorChanged;

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
        SyncAmbientAnimations();
        StartMonitor();

        AboutVersionText.Text = $"{AppInfo.DisplayName} · {AppInfo.Codename} · Fabric 1.21.4";

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
        _ambientStoryboard?.Stop(this);
        _ambientStoryboard = null;
        _ambientStarted = false;
        AboutRingRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        AboutRingRotate.Angle = 0;
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
        var activeInstance = settings.ActiveInstance;
        GameDirCaptionText.Text = activeInstance is null
            ? "Миры, моды и конфиги активного инстанса."
            : $"Инстанс «{activeInstance.Name}» · миры, моды и конфиги. Общие файлы игры хранятся отдельно.";

        CloseLauncherToggle.IsChecked = settings.CloseLauncherOnLaunch;
        FullscreenToggle.IsChecked = settings.Fullscreen;
        ShowResolutionToggle.IsChecked = settings.ShowResolution;
        AnimationsToggle.IsChecked = settings.AnimationsEnabled;
        BlurToggle.IsChecked = settings.BlurEnabled;
        GlowToggle.IsChecked = settings.BackgroundGlow;
        SplashToggle.IsChecked = settings.ShowSplash;
        HighlightAccent(settings.AccentColor);
        RefreshThemeSelection();
        HighlightBase(settings.BaseColor);

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
        if (!AppServices.Settings.Current.AnimationsEnabled)
        {
            foreach (var child in CardsPanel.Children)
            {
                if (child is FrameworkElement element)
                {
                    element.BeginAnimation(OpacityProperty, null);
                    element.Opacity = 1;
                    element.RenderTransform = Transform.Identity;
                }
            }
            return;
        }

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

    private void SyncAmbientAnimations()
    {
        if (!AppServices.Settings.Current.AnimationsEnabled)
        {
            _ambientStoryboard?.Stop(this);
            _ambientStoryboard = null;
            _ambientStarted = false;
            LiveDot.BeginAnimation(OpacityProperty, null);
            LiveDot.Opacity = 1;
            AboutRingRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            AboutRingRotate.Angle = 0;
            return;
        }

        if (_ambientStarted) return;
        _ambientStarted = true;
        if (Resources["SettingsAmbient"] is Storyboard storyboard)
        {
            _ambientStoryboard = storyboard;
            storyboard.Begin(this, true);
        }

        AboutRingRotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(26)) { RepeatBehavior = RepeatBehavior.Forever });
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
        settings.AnimationsEnabled = AnimationsToggle.IsChecked == true;
        settings.BlurEnabled = BlurToggle.IsChecked == true;
        settings.BackgroundGlow = GlowToggle.IsChecked == true;
        settings.ShowSplash = SplashToggle.IsChecked == true;

        UpdateResolutionState();
        SyncAmbientAnimations();
        (Window.GetWindow(this) as MainWindow)?.ApplyAppearance();
        ScheduleSave();
    }

    private void Accent_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;
        ApplyAccent(hex, toast: true);
    }

    private void AccentPicker_ColorChanged(Color color)
    {
        ApplyAccent(ToHex(color), toast: false);
    }

    private void CustomAccent_Click(object sender, RoutedEventArgs e)
    {
        var show = CustomAccentPanel.Visibility != Visibility.Visible;
        CustomAccentPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CustomAccentButton.Content = show ? "Скрыть цветовой круг" : "Свой цвет…";

        if (!show) return;

        if (TryParseAccent(AppServices.Settings.Current.AccentColor, out var color))
            AccentPicker.SetColor(color);

        CustomAccentPanel.BringIntoView();
    }

    private void ApplyAccent(string hex, bool toast)
    {
        AppServices.Settings.Current.AccentColor = hex;
        HighlightAccent(hex);
        (Window.GetWindow(this) as MainWindow)?.ApplyAppearance();
        ScheduleSave();

        if (toast) ShowToast("Акцент обновлён");
    }

    private void BuildAccentSwatches()
    {
        AccentSwatches.Children.Clear();

        foreach (var hex in AccentPalette.Presets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = brush,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 10),
                Tag = hex
            };

            swatch.MouseLeftButtonUp += Accent_Click;
            AccentSwatches.Children.Add(swatch);
        }
    }

    private void HighlightAccent(string color)
    {
        var selected = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
        var idle = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var hasColor = TryParseAccent(color, out var activeColor);

        foreach (var child in AccentSwatches.Children)
        {
            if (child is not Border swatch) continue;

            var isActive = hasColor && swatch.Tag is string tag &&
                           TryParseAccent(tag, out var swatchColor) &&
                           ColorsEqual(swatchColor, activeColor);

            swatch.BorderThickness = new Thickness(isActive ? 3 : 1);
            swatch.BorderBrush = isActive ? selected : idle;
        }

        if (hasColor && !ColorsEqual(activeColor, AccentPicker.SelectedColor))
            AccentPicker.SetColor(activeColor);
    }

    // ------------------------------------------------------------------ theme

    private void BuildThemeModeChips()
    {
        ThemeModePanel.Children.Clear();
        AddThemeChip(MaterialTheme.ModeMaterial, "Material You");
        AddThemeChip(MaterialTheme.ModeNviel, "nViel");
    }

    private void AddThemeChip(string mode, string label)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        var chip = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(15, 7, 15, 7),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Tag = mode,
            Child = text
        };

        chip.MouseLeftButtonUp += ThemeChip_Click;
        ThemeModePanel.Children.Add(chip);
    }

    private void ThemeChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string mode }) return;
        if (MaterialTheme.NormalizeMode(AppServices.Settings.Current.ThemeMode) == mode) return;

        AppServices.Settings.Current.ThemeMode = mode;
        ApplyThemeChange();
        ShowToast("Тема обновлена");
    }

    private void RefreshThemeSelection()
    {
        var mode = MaterialTheme.NormalizeMode(AppServices.Settings.Current.ThemeMode);

        foreach (var child in ThemeModePanel.Children)
        {
            if (child is not Border chip) continue;

            var selected = chip.Tag is string tag && tag == mode;
            chip.SetResourceReference(Border.BackgroundProperty, selected ? "AccentBrush" : "OverlaySoftBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "BorderSubtleBrush");

            if (chip.Child is TextBlock text)
            {
                text.SetResourceReference(TextBlock.ForegroundProperty,
                    selected ? "OnAccentBrush" : "TextSecondaryBrush");
                text.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        var material = MaterialTheme.IsMaterial(mode);
        BaseColorSection.Opacity = material ? 1 : 0.45;
        BaseColorSection.IsEnabled = material;
    }

    private void BuildBaseSwatches()
    {
        BaseSwatches.Children.Clear();

        foreach (var hex in AccentPalette.BasePresets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = brush,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 10),
                Tag = hex
            };

            swatch.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            swatch.MouseLeftButtonUp += Base_Click;
            BaseSwatches.Children.Add(swatch);
        }
    }

    private void Base_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;
        ApplyBase(hex, toast: true);
    }

    private void BasePicker_ColorChanged(Color color) => ApplyBase(ToHex(color), toast: false);

    private void CustomBase_Click(object sender, RoutedEventArgs e)
    {
        var show = CustomBasePanel.Visibility != Visibility.Visible;
        CustomBasePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CustomBaseButton.Content = show ? "Скрыть цветовой круг" : "Свой цвет…";

        if (!show) return;

        if (TryParseAccent(AppServices.Settings.Current.BaseColor, out var color))
            BasePicker.SetColor(color);

        CustomBasePanel.BringIntoView();
    }

    private void ApplyBase(string hex, bool toast)
    {
        AppServices.Settings.Current.BaseColor = hex;
        HighlightBase(hex);
        ApplyThemeChange();

        if (toast) ShowToast("Основной цвет обновлён");
    }

    private void HighlightBase(string color)
    {
        var hasColor = TryParseAccent(color, out var activeColor);

        foreach (var child in BaseSwatches.Children)
        {
            if (child is not Border swatch) continue;

            var isActive = hasColor && swatch.Tag is string tag &&
                           TryParseAccent(tag, out var swatchColor) &&
                           ColorsEqual(swatchColor, activeColor);

            swatch.BorderThickness = new Thickness(isActive ? 3 : 1);
            swatch.SetResourceReference(Border.BorderBrushProperty,
                isActive ? "TextPrimaryBrush" : "BorderSubtleBrush");
        }

        if (hasColor && !ColorsEqual(activeColor, BasePicker.SelectedColor))
            BasePicker.SetColor(activeColor);
    }

    private void ApplyThemeChange()
    {
        (Window.GetWindow(this) as MainWindow)?.ApplyAppearance();
        RefreshThemeSelection();
        ScheduleSave();
    }

    // ------------------------------------------------------------------ icons

    private void BuildIconTiles()
    {
        IconGrid.Children.Clear();
        _iconTiles.Clear();

        foreach (var icon in IconCatalog.All)
        {
            var preview = new System.Windows.Shapes.Path
            {
                Data = IconCatalog.CreateGeometry(icon.Key),
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            preview.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondaryBrush");

            var tile = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Tag = icon.Key,
                ToolTip = icon.Label,
                Child = preview
            };

            tile.SetResourceReference(Border.BackgroundProperty, "OverlaySoftBrush");
            tile.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            tile.MouseLeftButtonUp += IconTile_Click;

            _iconTiles[icon.Key] = tile;
            IconGrid.Children.Add(tile);
        }
    }

    private void IconTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string key }) return;
        SelectIcon(key);
    }

    private void SelectIcon(string key)
    {
        var icon = IconCatalog.Find(key);
        if (icon is null) return;

        _iconKey = key;
        IconEditor.Visibility = Visibility.Visible;
        IconEditorTitle.Text = icon.Label;
        IconErrorText.Text = "";

        var custom = AppServices.Settings.Current.IconOverrides.TryGetValue(key, out var stored) &&
                     !string.IsNullOrWhiteSpace(stored)
            ? stored
            : icon.PathData;

        IconPathBox.Text = custom;
        IconEditorPreview.Data = IconTheme.TryParse(custom, out var geometry)
            ? geometry
            : IconCatalog.CreateGeometry(key);

        foreach (var pair in _iconTiles)
        {
            var selected = pair.Key == key;
            pair.Value.BorderThickness = new Thickness(selected ? 2 : 1);
            pair.Value.SetResourceReference(Border.BorderBrushProperty,
                selected ? "AccentBrush" : "BorderSubtleBrush");
        }

        IconEditor.BringIntoView();
    }

    private void ApplyIcon_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_iconKey)) return;

        var text = (IconPathBox.Text ?? "").Trim();
        if (!IconTheme.TryParse(text, out var geometry))
        {
            IconErrorText.Text = "Некорректный SVG-путь";
            return;
        }

        var settings = AppServices.Settings.Current;
        var icon = IconCatalog.Find(_iconKey);

        if (icon is not null && string.Equals(text, icon.PathData, StringComparison.Ordinal))
            settings.IconOverrides.Remove(_iconKey);
        else
            settings.IconOverrides[_iconKey] = text;

        IconErrorText.Text = "";
        IconEditorPreview.Data = geometry;
        ApplyThemeChange();
        ShowToast("Иконка обновлена");
    }

    private void ResetIcon_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_iconKey)) return;

        var icon = IconCatalog.Find(_iconKey);
        AppServices.Settings.Current.IconOverrides.Remove(_iconKey);

        if (icon is not null)
        {
            IconPathBox.Text = icon.PathData;
            IconEditorPreview.Data = IconCatalog.CreateGeometry(_iconKey);
        }

        IconErrorText.Text = "";
        ApplyThemeChange();
        ShowToast("Иконка сброшена");
    }

    private void ResetAllIcons_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Settings.Current;
        if (settings.IconOverrides.Count == 0)
        {
            ShowToast("Своих иконок нет");
            return;
        }

        settings.IconOverrides.Clear();
        if (!string.IsNullOrEmpty(_iconKey)) SelectIcon(_iconKey);
        ApplyThemeChange();
        ShowToast("Все иконки сброшены");
    }

    private static bool TryParseAccent(string? text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            color = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool ColorsEqual(Color a, Color b) =>
        a.R == b.R && a.G == b.G && a.B == b.B;

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

        var instance = settings.ActiveInstance;
        if (instance is null || instance.IsDefault)
        {
            settings.GameDirectory = dialog.FolderName;
            if (instance is not null)
                instance.Directory = dialog.FolderName;
        }
        else
        {
            instance.Directory = dialog.FolderName;
        }

        AppServices.Settings.Save();
        (Window.GetWindow(this) as MainWindow)?.OnInstanceChanged();

        GameDirText.Text = settings.EffectiveGameDirectory;
        ShowToast();
    }

    private void OpenGameDir_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(AppServices.Settings.Current.EffectiveGameDirectory);

    private void OpenInstances_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo("instances");

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OpenBackups("settings");

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "mods");
        Directory.CreateDirectory(directory);
        ShellHelper.OpenFolder(directory);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => ShellHelper.OpenFolder(AppPaths.LogsDirectory);

    private void Diagnose_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.ShowDiagnostics();

    private void OpenMigration_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OpenMigration("settings");

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
        settings.AnimationsEnabled = true;
        settings.BlurEnabled = true;
        settings.BackgroundGlow = true;
        settings.ShowSplash = true;
        settings.AccentColor = MaterialTheme.DefaultSeedHex;
        settings.ThemeMode = MaterialTheme.ModeMaterial;
        settings.BaseColor = MaterialTheme.DefaultBaseHex;
        settings.IconOverrides.Clear();
        settings.ResolutionWidth = 1280;
        settings.ResolutionHeight = 720;
        settings.GameDirectory = AppPaths.DefaultGameDirectory;
        settings.EnsureInstances();
        if (settings.Instances.FirstOrDefault(i => i.IsDefault) is { } defaultInstance)
            defaultInstance.Directory = AppPaths.DefaultGameDirectory;

        AppServices.Settings.Save();
        Refresh();
        (Window.GetWindow(this) as MainWindow)?.ApplyAppearance();
        ShowToast("Настройки сброшены");
    }

    // ------------------------------------------------------------------ updates

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdates) return;
        if (Window.GetWindow(this) is not MainWindow main) return;

        _checkingUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "Проверяю обновления…";

        try
        {
            var result = await main.CheckForUpdatesManuallyAsync();

            if (result is null)
            {
                UpdateStatusText.Text = "Проверка уже выполняется.";
                return;
            }

            var mod = result.ClientMod;
            var modPending = mod is not null && AppServices.ClientMods.NeedsUpdate(mod);

            UpdateStatusText.Text = result.Status switch
            {
                UpdateStatus.Available when result.Update is not null =>
                    "Доступно обновление " + result.Update.Version + ".",
                UpdateStatus.Available when modPending =>
                    "Доступен клиентский мод " + mod!.Version + ".",
                UpdateStatus.Available => "Клиентский мод обновлён.",
                UpdateStatus.UpToDate => "Установлена актуальная версия.",
                _ => result.Message
            };
        }
        catch (Exception ex)
        {
            Log.Write("UPDATE: ручная проверка не удалась: " + ex);
            UpdateStatusText.Text = "Не удалось проверить обновления: " + ex.Message;
        }
        finally
        {
            _checkingUpdates = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ recovery

    private void OpenRe_Click(object sender, RoutedEventArgs e) => ReLauncher.OpenRe();
}
