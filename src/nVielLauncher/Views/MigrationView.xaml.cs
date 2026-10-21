using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

/// <summary>
/// In-launcher data migration page: finds other launchers, copies useful data,
/// creates an export archive, and opens official uninstallers after a backup.
/// </summary>
public partial class MigrationView : UserControl, IRefreshable
{
    private ObservableCollection<DetectedLauncher> _launchers = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _discovering;
    private bool _skipNextRefresh;

    public MigrationView()
    {
        InitializeComponent();
        LauncherList.ItemsSource = _launchers;
        ShowEmpty("Ищу старые лаунчеры...", "Проверяю известные лаунчеры и их папки.", showRetry: false);
    }

    public bool DataImported { get; private set; }

    public bool LaunchersRemoved { get; private set; }

    public event EventHandler? Finished;

    /// <summary>Uses an already discovered list (first-run path) instead of scanning twice.</summary>
    public void SetPreloaded(IReadOnlyList<DetectedLauncher> launchers)
    {
        ApplyLaunchers(launchers);
        _skipNextRefresh = true;
    }

    public void Refresh()
    {
        if (_skipNextRefresh)
        {
            _skipNextRefresh = false;
            return;
        }

        if (_busy || _discovering) return;
        _ = DiscoverAsync();
    }

    private async Task DiscoverAsync()
    {
        _discovering = true;
        LauncherList.Visibility = Visibility.Collapsed;
        ShowEmpty("Ищу старые лаунчеры...", "Проверяю известные лаунчеры и их папки.", showRetry: false);
        UpdateSelectionState();

        try
        {
            var found = await Task.Run(LauncherDiscovery.Discover);
            var actionable = found
                .Where(l => l.DataRoots.Count > 0 || l.CanUninstall)
                .ToList();
            ApplyLaunchers(actionable);
        }
        catch (Exception ex)
        {
            Log.Write("MIGRATION: поиск лаунчеров не удался: " + ex);
            ShowEmpty("Не удалось проверить лаунчеры",
                "Подробности в журнале. Попробуй ещё раз.", showRetry: true);
        }
        finally
        {
            _discovering = false;
            UpdateSelectionState();
        }
    }

    private void ApplyLaunchers(IReadOnlyList<DetectedLauncher> launchers)
    {
        foreach (var launcher in _launchers)
            launcher.PropertyChanged -= Launcher_PropertyChanged;

        _launchers = new ObservableCollection<DetectedLauncher>(launchers);
        foreach (var launcher in _launchers)
            launcher.PropertyChanged += Launcher_PropertyChanged;

        LauncherList.ItemsSource = _launchers;

        if (_launchers.Count == 0)
        {
            ShowEmpty("Старые лаунчеры с данными не найдены",
                "Если лаунчер появится позже, нажми «Проверить снова».", showRetry: true);
        }
        else
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            LauncherList.Visibility = Visibility.Visible;
        }

        UpdateSelectionState();
    }

    private void ShowEmpty(string title, string subtitle, bool showRetry)
    {
        EmptyTitle.Text = title;
        EmptySubtitle.Text = subtitle;
        RetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
    }

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
        var hasData = selected.Any(l => l.DataRoots.Count > 0);
        var canRemove = selected.Any(l => l.CanUninstall);

        ImportButton.IsEnabled = !_busy && !_discovering && hasData;
        ExportButton.IsEnabled = !_busy && !_discovering && hasData;
        RemoveButton.IsEnabled = !_busy && !_discovering && canRemove;
        RemoveButton.Visibility = canRemove ? Visibility.Visible : Visibility.Collapsed;
        CountBadge.Text = _launchers.Count.ToString();

        if (!_busy && !_discovering)
        {
            StatusText.Visibility = selected.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (selected.Count > 0)
                StatusText.Text = $"Выбрано: {selected.Count}";
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
        BackButton.IsEnabled = !busy;
        RetryButton.IsEnabled = !busy;
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
                selected, AppServices.Settings.Current.EffectiveGameDirectory,
                ReadOptions(), progress, _cts.Token);

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

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

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
        var answer = MessageBox.Show(Window.GetWindow(this),
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

    private void Retry_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Back_Click(object sender, RoutedEventArgs e) => Finish();

    private void Later_Click(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        if (_busy)
        {
            _cts?.Cancel();
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "Отмена...";
            return;
        }

        Finished?.Invoke(this, EventArgs.Empty);
    }
}
