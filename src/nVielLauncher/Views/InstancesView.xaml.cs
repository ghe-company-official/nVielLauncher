using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public sealed class InstanceRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Stats { get; init; } = "";
    public bool IsDefault { get; init; }
    public Visibility ActivateVisibility { get; init; }
    public Visibility ActiveBadgeVisibility { get; init; }
    public Visibility DefaultBadgeVisibility { get; init; }
    public Visibility DeleteVisibility { get; init; }
    public Brush CardBackground { get; init; } = Brushes.Transparent;
    public Brush CardBorder { get; init; } = Brushes.Transparent;
}

public partial class InstancesView : UserControl, IRefreshable
{
    private static readonly Brush ActiveBackground = Freeze(new LinearGradientBrush(
        Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
        Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 90));
    private static readonly Brush IdleBackground = Freeze(new LinearGradientBrush(
        Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        Color.FromArgb(0x05, 0xFF, 0xFF, 0xFF), 90));
    private static readonly Brush ActiveBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush IdleBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)));

    private readonly ObservableCollection<InstanceRow> _rows = new();
    private bool _busy;
    private bool _loading;
    private string? _editingId;
    private string? _deletingId;
    private string? _editingSettingsId;

    public InstancesView()
    {
        InitializeComponent();
        InstanceList.ItemsSource = _rows;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        if (_busy || _loading) return;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        if (_rows.Count == 0)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            InstanceList.Visibility = Visibility.Collapsed;
        }

        try
        {
            var rows = await Task.Run(() =>
            {
                var activeId = AppServices.Settings.Current.ActiveInstanceId;
                return AppServices.Instances.All
                    .Select(instance =>
                    {
                        var stats = AppServices.Instances.GetStats(instance);
                        var active = string.Equals(instance.Id, activeId, StringComparison.OrdinalIgnoreCase);
                        return new InstanceRow
                        {
                            Id = instance.Id,
                            Name = instance.Name,
                            Path = AppServices.Instances.GetDirectory(instance),
                            Stats = BuildStats(stats, instance),
                            IsDefault = instance.IsDefault,
                            ActivateVisibility = active ? Visibility.Collapsed : Visibility.Visible,
                            ActiveBadgeVisibility = active ? Visibility.Visible : Visibility.Collapsed,
                            DefaultBadgeVisibility = instance.IsDefault ? Visibility.Visible : Visibility.Collapsed,
                            DeleteVisibility = instance.IsDefault ? Visibility.Collapsed : Visibility.Visible,
                            CardBackground = active ? ActiveBackground : IdleBackground,
                            CardBorder = active ? ActiveBorder : IdleBorder
                        };
                    })
                    .ToList();
            });

            _rows.Clear();
            foreach (var row in rows)
                _rows.Add(row);

            ApplyActiveCard();
            CountText.Text = rows.Count + " " + Plural(rows.Count, "инстанс", "инстанса", "инстансов");
            EmptyState.Visibility = rows.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;
            ListHint.Visibility = rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            InstanceList.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Write("INSTANCE: не удалось обновить список: " + ex.Message);
            LoadingPanel.Visibility = Visibility.Collapsed;
            InstanceList.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyActiveCard()
    {
        var active = AppServices.Instances.Active;
        if (active is null)
        {
            ActiveName.Text = "Нет инстанса";
            ActivePath.Text = "";
            ActiveStats.Text = "";
            DeleteActiveButton.Visibility = Visibility.Collapsed;
            return;
        }

        var stats = AppServices.Instances.GetStats(active);
        ActiveName.Text = active.Name;
        ActivePath.Text = AppServices.Instances.GetDirectory(active);
        ActiveStats.Text = BuildStats(stats, active);
        DeleteActiveButton.Visibility = active.IsDefault ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string BuildStats(InstanceStats stats, GameInstance instance)
    {
        var parts = new List<string>
        {
            stats.Mods + " " + Plural(stats.Mods, "мод", "мода", "модов"),
            stats.Worlds + " " + Plural(stats.Worlds, "мир", "мира", "миров"),
            FormatSize(stats.SizeBytes)
        };

        if (instance.LastPlayedUtc is { } last)
            parts.Add("запуск " + last.ToLocalTime().ToString("dd.MM, HH:mm"));

        return string.Join(" · ", parts);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024d / 1024 / 1024).ToString("0.0") + " ГБ";
        if (bytes >= 1024L * 1024) return (bytes / 1024d / 1024).ToString("0") + " МБ";
        return Math.Max(0, bytes / 1024) + " КБ";
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

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _editingId = null;
        EditorTitle.Text = "Новый инстанс";
        InstanceNameBox.Text = "Новый инстанс";
        InstanceNameBox.SelectAll();
        CopyModsCheck.Visibility = Visibility.Visible;
        CopyModsCheck.IsChecked = true;
        EditorConfirmButton.Content = "Создать";
        EditorError.Visibility = Visibility.Collapsed;
        EditorBusy.Visibility = Visibility.Collapsed;
        ShowLayer(EditorLayer, InstanceNameBox);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var active = AppServices.Instances.Active;
        if (active is null || _busy) return;

        _editingId = active.Id;
        EditorTitle.Text = "Переименовать инстанс";
        InstanceNameBox.Text = active.Name;
        InstanceNameBox.SelectAll();
        CopyModsCheck.Visibility = Visibility.Collapsed;
        EditorConfirmButton.Content = "Сохранить";
        EditorError.Visibility = Visibility.Collapsed;
        EditorBusy.Visibility = Visibility.Collapsed;
        ShowLayer(EditorLayer, InstanceNameBox);
    }

    // ------------------------------------------------------------------ per-instance settings

    private void SettingsActive_Click(object sender, RoutedEventArgs e)
    {
        var active = AppServices.Instances.Active;
        if (active is null || _busy) return;
        OpenInstanceSettings(active);
    }

    private void OpenInstanceSettings(GameInstance instance)
    {
        _editingSettingsId = instance.Id;
        var settings = AppServices.Settings.Current;

        SettingsTitle.Text = "Настройки · " + instance.Name;

        InstanceNickBox.Text = instance.Nickname ?? "";
        NickHint.Text = string.IsNullOrWhiteSpace(instance.Nickname)
            ? "Сейчас игра получит ник из общих настроек: «" + settings.NicknameFor(instance) + "»."
            : "Игра получит ник «" + instance.Nickname.Trim() + "».";

        var hasRam = instance.RamMb > 0;
        RamOverrideCheck.IsChecked = hasRam;
        InstanceRamSlider.Value = hasRam ? instance.RamMb : settings.RamMb;
        UpdateRamUi();

        InstanceJavaBox.Text = instance.JavaPath ?? "";

        ScreenOverrideCheck.IsChecked = instance.Fullscreen is not null || instance.ShowResolution is not null;
        InstanceFullscreenCheck.IsChecked = instance.Fullscreen ?? settings.Fullscreen;
        InstanceResolutionCheck.IsChecked = instance.ShowResolution ?? settings.ShowResolution;
        InstanceWidthBox.Text = (instance.ResolutionWidth > 0 ? instance.ResolutionWidth : settings.ResolutionWidth).ToString();
        InstanceHeightBox.Text = (instance.ResolutionHeight > 0 ? instance.ResolutionHeight : settings.ResolutionHeight).ToString();
        UpdateScreenUi();

        SettingsError.Visibility = Visibility.Collapsed;
        ShowLayer(SettingsLayer, InstanceNickBox);
    }

    private void RamOverride_ValueChanged(object sender, RoutedEventArgs e) => UpdateRamUi();

    private void InstanceRamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateRamUi();

    private void UpdateRamUi()
    {
        if (RamPanel is null || RamValueText is null || RamHint is null || RamOverrideCheck is null)
            return;

        var hasOverride = RamOverrideCheck.IsChecked == true;
        RamPanel.Visibility = hasOverride ? Visibility.Visible : Visibility.Collapsed;
        var value = hasOverride ? (int)InstanceRamSlider.Value : AppServices.Settings.Current.RamMb;
        RamValueText.Text = value + " МБ";
        RamHint.Text = hasOverride
            ? "Отдельное значение для этого инстанса"
            : "Используются общие настройки";
    }

    private void ScreenOverride_ValueChanged(object sender, RoutedEventArgs e) => UpdateScreenUi();

    private void InstanceResolution_ValueChanged(object sender, RoutedEventArgs e) => UpdateScreenUi();

    private void UpdateScreenUi()
    {
        if (ScreenPanel is null || ResolutionPanel is null || ScreenOverrideCheck is null)
            return;

        var on = ScreenOverrideCheck.IsChecked == true;
        ScreenPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ResolutionPanel.Visibility = on && InstanceResolutionCheck.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SettingsConfirm_Click(object sender, RoutedEventArgs e)
    {
        var instance = AppServices.Instances.All.FirstOrDefault(i =>
            string.Equals(i.Id, _editingSettingsId, StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            SettingsLayer.Visibility = Visibility.Collapsed;
            return;
        }

        var nickname = (InstanceNickBox.Text ?? "").Trim();
        if (nickname.Length > 0 && !LauncherService.IsValidNickname(nickname))
        {
            ShowSettingsError("Ник должен быть от 3 до 16 символов: A-Z, 0-9 и _.");
            return;
        }

        var settings = AppServices.Settings.Current;
        var effectiveNickname = nickname.Length > 0 ? nickname : settings.NicknameFor(null);
        if (!LauncherService.IsValidNickname(effectiveNickname))
        {
            ShowSettingsError("Сначала укажи корректный ник в общих настройках — инстанс его наследует.");
            return;
        }

        var javaPath = (InstanceJavaBox.Text ?? "").Trim();
        if (javaPath.Length > 0 && !File.Exists(javaPath))
        {
            ShowSettingsError("Файл Java не найден: " + javaPath);
            return;
        }

        var ram = RamOverrideCheck.IsChecked == true ? (int)InstanceRamSlider.Value : 0;

        var overrideScreen = ScreenOverrideCheck.IsChecked == true;
        bool? fullscreen = overrideScreen ? InstanceFullscreenCheck.IsChecked : null;
        bool? showResolution = overrideScreen ? InstanceResolutionCheck.IsChecked : null;
        int width = 0;
        int height = 0;
        if (overrideScreen && showResolution == true)
        {
            var widthText = (InstanceWidthBox.Text ?? "").Trim();
            var heightText = (InstanceHeightBox.Text ?? "").Trim();
            var widthOk = int.TryParse(widthText, out width) && width is >= 854 and <= 3840;
            var heightOk = int.TryParse(heightText, out height) && height is >= 480 and <= 2160;
            if (!widthOk || !heightOk)
            {
                ShowSettingsError("Размер окна: от 854×480 до 3840×2160.");
                return;
            }
        }

        instance.Nickname = nickname.Length > 0 ? nickname : null;
        instance.RamMb = ram;
        instance.JavaPath = javaPath.Length > 0 ? javaPath : null;
        instance.Fullscreen = fullscreen;
        instance.ShowResolution = showResolution;
        instance.ResolutionWidth = width;
        instance.ResolutionHeight = height;
        AppServices.Settings.Save();

        SettingsLayer.Visibility = Visibility.Collapsed;
        _editingSettingsId = null;
        (Window.GetWindow(this) as MainWindow)?.OnInstanceChanged();
        Notify.Show("Настройки сохранены", instance.Name, "success");
        Refresh();
    }

    private void ResetInstanceSettings_Click(object sender, RoutedEventArgs e)
    {
        var instance = AppServices.Instances.All.FirstOrDefault(i =>
            string.Equals(i.Id, _editingSettingsId, StringComparison.OrdinalIgnoreCase));
        if (instance is null) return;

        instance.Nickname = null;
        instance.RamMb = 0;
        instance.JavaPath = null;
        instance.Fullscreen = null;
        instance.ShowResolution = null;
        instance.ResolutionWidth = 0;
        instance.ResolutionHeight = 0;
        AppServices.Settings.Save();

        OpenInstanceSettings(instance);
        Notify.Show("Настройки сброшены", "Инстанс снова использует общие настройки.", "info");
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsLayer.Visibility = Visibility.Collapsed;
        _editingSettingsId = null;
    }

    private void ShowSettingsError(string message)
    {
        SettingsError.Text = message;
        SettingsError.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ cloning

    private void CloneActive_Click(object sender, RoutedEventArgs e)
    {
        var active = AppServices.Instances.Active;
        if (active is null || _busy) return;
        _ = CloneAsync(active);
    }

    private void CloneRow_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id }) return;
        var instance = AppServices.Instances.All.FirstOrDefault(i =>
            string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (instance is null) return;
        _ = CloneAsync(instance);
    }

    private async Task CloneAsync(GameInstance source)
    {
        SetBusy(true);
        Notify.Show("Копируем инстанс", "Копия «" + source.Name + "» создаётся — большие папки займут время.");

        try
        {
            var clone = await Task.Run(() => AppServices.Instances.Clone(
                source.Id, new Progress<LaunchProgress>(_ => { }), CancellationToken.None));

            (Window.GetWindow(this) as MainWindow)?.OnInstanceChanged();
            Notify.Show("Инстанс скопирован", "«" + clone.Name + "» готов и стал активным.", "success");
        }
        catch (Exception ex)
        {
            Notify.Show("Не удалось скопировать инстанс", ex.Message, "error");
        }
        finally
        {
            SetBusy(false);
            Refresh();
        }
    }

    private async void EditorConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var name = InstanceNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowEditorError("Введи название инстанса.");
            return;
        }

        SetBusy(true);
        EditorConfirmButton.IsEnabled = false;
        EditorError.Visibility = Visibility.Collapsed;
        EditorBusy.Text = _editingId is null ? "Создаём инстанс..." : "Сохраняем...";
        EditorBusy.Visibility = Visibility.Visible;
        var changed = false;

        try
        {
            if (_editingId is null)
            {
                var copy = CopyModsCheck.IsChecked == true;
                var progress = new Progress<LaunchProgress>(p =>
                    EditorBusy.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + " · " + p.Detail);

                await Task.Run(() => AppServices.Instances.Create(name, copy, progress, CancellationToken.None));
                Notify.Show("Инстанс создан", "«" + name + "» теперь активен.", "success");
            }
            else
            {
                AppServices.Instances.Rename(_editingId, name);
                Notify.Show("Инстанс переименован", name, "success");
            }

            EditorLayer.Visibility = Visibility.Collapsed;
            (Window.GetWindow(this) as MainWindow)?.OnInstanceChanged();
            changed = true;
        }
        catch (Exception ex)
        {
            ShowEditorError(ex.Message);
        }
        finally
        {
            SetBusy(false);
            EditorConfirmButton.IsEnabled = true;
            EditorBusy.Visibility = Visibility.Collapsed;
            if (changed) Refresh();
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id }) return;

        try
        {
            AppServices.Instances.SetActive(id);
            (Window.GetWindow(this) as MainWindow)?.OnInstanceChanged();
            Notify.Show("Инстанс выбран", AppServices.Instances.Active?.Name ?? "", "success");
            Refresh();
        }
        catch (Exception ex)
        {
            Notify.Show("Не удалось переключить инстанс", ex.Message, "error");
        }
    }

    private void OpenActiveFolder_Click(object sender, RoutedEventArgs e)
    {
        var active = AppServices.Instances.Active;
        if (active is null) return;

        var directory = AppServices.Instances.GetDirectory(active);
        Directory.CreateDirectory(directory);
        ShellHelper.OpenFolder(directory);
    }

    private void DeleteActive_Click(object sender, RoutedEventArgs e)
    {
        var active = AppServices.Instances.Active;
        if (active is null || active.IsDefault) return;
        AskDelete(active);
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var instance = AppServices.Instances.All.FirstOrDefault(i =>
            string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (instance is null || instance.IsDefault) return;
        AskDelete(instance);
    }

    private void AskDelete(GameInstance instance)
    {
        if (_busy) return;
        _deletingId = instance.Id;
        DeleteText.Text = "«" + instance.Name + "» будет удалён.";
        DeleteError.Visibility = Visibility.Collapsed;
        ShowLayer(ConfirmLayer, DeleteConfirmButton);
    }

    private async void DeleteConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _deletingId is null) return;

        SetBusy(true);
        DeleteConfirmButton.IsEnabled = false;
        DeleteError.Visibility = Visibility.Collapsed;
        var changed = false;

        try
        {
            var id = _deletingId;
            var deleted = await Task.Run(() => AppServices.Instances.Delete(id));
            if (!deleted)
                throw new InvalidOperationException("Не удалось удалить папку инстанса. Возможно, файлы заняты игрой.");

            ConfirmLayer.Visibility = Visibility.Collapsed;
            _deletingId = null;
            (Window.GetWindow(this) as MainWindow)?.OnInstanceChanged();
            Notify.Show("Инстанс удалён", "Активным стал другой инстанс.", "info");
            changed = true;
        }
        catch (Exception ex)
        {
            DeleteError.Text = ex.Message;
            DeleteError.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
            DeleteConfirmButton.IsEnabled = true;
            if (changed) Refresh();
        }
    }

    private void CloseEditor_Click(object sender, RoutedEventArgs e) => EditorLayer.Visibility = Visibility.Collapsed;

    private void CloseDelete_Click(object sender, RoutedEventArgs e) => ConfirmLayer.Visibility = Visibility.Collapsed;

    private void InstanceNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EditorConfirm_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EditorLayer.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void ShowEditorError(string message)
    {
        EditorError.Text = message;
        EditorError.Visibility = Visibility.Visible;
    }

    private void ShowLayer(Grid layer, Control focus)
    {
        layer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => focus.Focus());
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CreateButtonState(busy);
    }

    private void CreateButtonState(bool busy)
    {
        CreateButton.IsEnabled = !busy;
        EditorConfirmButton.IsEnabled = !busy;
        DeleteConfirmButton.IsEnabled = !busy;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
