using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

/// <summary>
/// First-run import prompt: copies useful data from other launchers, can create
/// an export archive, and only opens the official uninstallers after a backup.
/// </summary>
public partial class MigrationWindow : Window
{
    private readonly ObservableCollection<DetectedLauncher> _launchers;
    private readonly string _targetDirectory;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public MigrationWindow(IReadOnlyList<DetectedLauncher> launchers, string targetDirectory)
    {
        InitializeComponent();

        _targetDirectory = targetDirectory;
        _launchers = new ObservableCollection<DetectedLauncher>(launchers);

        foreach (var launcher in _launchers)
            launcher.PropertyChanged += Launcher_PropertyChanged;

        LauncherList.ItemsSource = _launchers;
        EmptyPanel.Visibility = _launchers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionState();
    }

    public bool DataImported { get; private set; }

    public bool LaunchersRemoved { get; private set; }

    private void Launcher_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetectedLauncher.IsSelected))
            UpdateSelectionState();
    }

    private IReadOnlyList<DetectedLauncher> Selected
        => _launchers.Where(l => l.IsSelected).ToList();

    private void UpdateSelectionState()
    {
        var selected = Selected;
        var count = selected.Count;
        var hasData = selected.Any(l => l.DataRoots.Count > 0);

        ImportButton.IsEnabled = !_busy && hasData;
        ExportButton.IsEnabled = !_busy && hasData;
        RemoveButton.IsEnabled = !_busy && selected.Any(l => l.CanUninstall);

        if (!_busy)
        {
            StatusText.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (count > 0)
                StatusText.Text = $"Выбрано: {count}";
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        LauncherList.IsEnabled = !busy;
        WorldsCheck.IsEnabled = !busy;
        ModsCheck.IsEnabled = !busy;
        ResourcePacksCheck.IsEnabled = !busy;
        ShaderPacksCheck.IsEnabled = !busy;
        ConfigsCheck.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        LaterButton.Content = busy ? "Отмена" : "Позже";
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (busy)
        {
            ImportButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
        }
        else
        {
            UpdateSelectionState();
        }

        StatusText.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = status;
    }

    private MigrationOptions ReadOptions() => new()
    {
        Worlds = WorldsCheck.IsChecked == true,
        Mods = ModsCheck.IsChecked == true,
        ResourcePacks = ResourcePacksCheck.IsChecked == true,
        ShaderPacks = ShaderPacksCheck.IsChecked == true,
        Configs = ConfigsCheck.IsChecked == true
    };

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var selected = Selected;
        if (selected.Count == 0)
        {
            SetBusy(false, "Выбери хотя бы один лаунчер.");
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true, "Копирую миры и моды...");

        try
        {
            var progress = new Progress<string>(text => StatusText.Text = text);
            var summary = await LauncherMigrationService.ImportAsync(
                selected, _targetDirectory, ReadOptions(), progress, _cts.Token);

            var total = summary.Worlds + summary.Mods + summary.ResourcePacks +
                        summary.ShaderPacks + summary.ConfigFiles;
            DataImported = total > 0;

            StatusText.Text = total == 0
                ? "Данные не найдены. Проверь, что выбранный лаунчер уже запускался."
                : $"Готово: миров {summary.Worlds}, модов {summary.Mods}, " +
                  $"ресурспаков {summary.ResourcePacks}, шейдеров {summary.ShaderPacks}, " +
                  $"настроек {summary.ConfigFiles}.";

            if (summary.Skipped > 0)
                StatusText.Text += $" Пропущено уже существующих: {summary.Skipped}.";
            if (summary.Errors > 0)
                StatusText.Text += $" Ошибок: {summary.Errors} — подробности в журнале.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Импорт отменён.";
        }
        catch (Exception ex)
        {
            Log.Write("MIGRATION: импорт не удался: " + ex);
            StatusText.Text = "Не удалось импортировать данные. Подробности в журнале.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false, StatusText.Text);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var selected = Selected;
        if (selected.Count == 0)
        {
            SetBusy(false, "Выбери хотя бы один лаунчер.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Экспорт данных старых лаунчеров",
            FileName = $"nViel-launcher-data-{DateTime.Now:yyyyMMdd}.zip",
            Filter = "ZIP (*.zip)|*.zip",
            AddExtension = true,
            DefaultExt = ".zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dialog.ShowDialog(this) != true) return;

        _cts = new CancellationTokenSource();
        SetBusy(true, "Создаю архив...");

        try
        {
            var progress = new Progress<string>(text => StatusText.Text = text);
            var path = await LauncherMigrationService.ExportAsync(
                selected, dialog.FileName, progress, _cts.Token);

            StatusText.Text = "Архив сохранён: " + path;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Экспорт отменён.";
        }
        catch (Exception ex)
        {
            Log.Write("MIGRATION: экспорт не удался: " + ex);
            StatusText.Text = "Не удалось создать архив. Подробности в журнале.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false, StatusText.Text);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var removable = Selected.Where(l => l.CanUninstall).ToList();
        if (removable.Count == 0)
        {
            SetBusy(false,
                "У выбранных лаунчеров нет автоматического деинсталлятора. nViel не удаляет папки вручную.");
            return;
        }

        var names = string.Join("\n", removable.Select(l => "• " + l.Name));
        var answer = MessageBox.Show(this,
            "Удалить выбранные лаунчеры?\n\n" + names + "\n\n" +
            "Сначала будет создана резервная копия их данных. Затем откроются штатные " +
            "деинсталляторы — подтверди удаление в каждом окне. nViel не удаляет файлы напрямую.",
            "nViel Launcher", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetBusy(true, "Создаю резервную копию перед удалением...");

        try
        {
            var progress = new Progress<string>(text => StatusText.Text = text);
            var withData = removable.Where(l => l.DataRoots.Count > 0).ToList();
            string? backup = null;
            if (withData.Count > 0)
            {
                backup = await LauncherMigrationService.BackupBeforeRemovalAsync(
                    withData, progress, _cts.Token);
            }

            var started = LauncherMigrationService.LaunchUninstallers(removable);
            LaunchersRemoved = started.Count > 0;

            if (started.Count == 0)
            {
                StatusText.Text = backup is null
                    ? "Данных для копии нет, но деинсталляторы не запустились."
                    : "Резервная копия создана, но деинсталляторы не запустились: " + backup;
            }
            else
            {
                StatusText.Text = backup is null
                    ? $"Данных для копии нет. Открыто деинсталляторов: {started.Count}. Подтверди удаление в них."
                    : $"Резервная копия: {backup}\nОткрыто деинсталляторов: {started.Count}. Подтверди удаление в них.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Удаление отменено, резервная копия не создана.";
        }
        catch (Exception ex)
        {
            Log.Write("MIGRATION: удаление не запущено: " + ex);
            StatusText.Text = "Не удалось создать резервную копию, поэтому удаление отменено. " +
                              "Подробности в журнале.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false, StatusText.Text);
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "Отмена...";
            return;
        }

        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (_busy)
        {
            _cts?.Cancel();
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "Отмена...";
        }
        else
        {
            Close();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;

        e.Cancel = true;
        _cts?.Cancel();
    }
}
