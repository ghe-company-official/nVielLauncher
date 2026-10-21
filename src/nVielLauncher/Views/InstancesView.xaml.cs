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
