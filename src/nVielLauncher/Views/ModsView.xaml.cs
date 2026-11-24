using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public sealed class ModRow
{
    public string Name { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string State { get; init; } = "";
    public Brush StateBackground { get; init; } = Brushes.Transparent;
    public Brush StateForeground { get; init; } = Brushes.White;
}

public sealed class ModrinthRow
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Meta { get; init; } = "";
}

public sealed class ManagedRow
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string ToggleLabel => Enabled ? "Выключить" : "Включить";
    public Visibility BadgeVisibility => Enabled ? Visibility.Collapsed : Visibility.Visible;
}

public partial class ModsView : UserControl, IRefreshable
{
    private const string SearchHint = "Поиск и установка модов прямо с Modrinth — зависимости подтянутся автоматически";

    private readonly ObservableCollection<ModRow> _rows = new();
    private readonly ObservableCollection<ModrinthRow> _results = new();
    private readonly ObservableCollection<ManagedRow> _managed = new();
    private Storyboard? _ambientStoryboard;
    private bool _ambientStarted;

    public ModsView()
    {
        InitializeComponent();
        ModsList.ItemsSource = _rows;
        ResultsList.ItemsSource = _results;
        ManagedList.ItemsSource = _managed;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncAmbientAnimations();
        Refresh();
        AnimateEntrance();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _ambientStoryboard?.Stop(this);
        _ambientStoryboard = null;
        _ambientStarted = false;
    }

    private void SyncAmbientAnimations()
    {
        if (!AppServices.Settings.Current.AnimationsEnabled)
        {
            _ambientStoryboard?.Stop(this);
            _ambientStoryboard = null;
            _ambientStarted = false;
            return;
        }

        if (_ambientStarted) return;
        _ambientStarted = true;
        if (Resources["ModsAmbient"] is Storyboard storyboard)
        {
            _ambientStoryboard = storyboard;
            storyboard.Begin(this, true);
        }
    }

    private void AnimateEntrance()
    {
        if (!AppServices.Settings.Current.AnimationsEnabled)
        {
            ModsHeroCard.Opacity = 1;
            ModsHeroCard.RenderTransform = Transform.Identity;
            return;
        }

        ModsHeroCard.Opacity = 0;
        ModsHeroCard.RenderTransform = new TranslateTransform(0, 8);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ModsHeroCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = ease
        });
        ((TranslateTransform)ModsHeroCard.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
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

    public void Refresh()
    {
        var gameDirectory = AppServices.Settings.Current.EffectiveGameDirectory;
        var source = AppServices.Mods.BundledDirectory;

        SourcePathText.Text = source;

        var upToDateBackground = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        var pendingBackground = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
        var primary = (Brush)FindResource("TextPrimaryBrush");
        var secondary = (Brush)FindResource("TextSecondaryBrush");

        _rows.Clear();

        foreach (var entry in AppServices.Mods.GetEntries(gameDirectory))
        {
            var state = entry.UpToDate ? "УСТАНОВЛЕН" : entry.Installed ? "ОБНОВИТСЯ" : "НОВЫЙ";
            _rows.Add(new ModRow
            {
                Name = entry.FileName,
                SizeText = FormatSize(entry.Size),
                State = state,
                StateBackground = entry.UpToDate ? upToDateBackground : pendingBackground,
                StateForeground = entry.UpToDate ? primary : secondary
            });
        }

        CountBadge.Text = _rows.Count + " " + Plural(_rows.Count, "мод", "мода", "модов");

        var installed = _rows.Count(r => r.State == "УСТАНОВЛЕН");
        TotalModsText.Text = _rows.Count.ToString();
        InstalledModsText.Text = installed.ToString();
        PendingModsText.Text = (_rows.Count - installed).ToString();

        SubHeader.Text = _rows.Count == 0
            ? "Собери игру под себя — моды, обновления и зависимости в одном месте."
            : $"{_rows.Count} {Plural(_rows.Count, "мод готов", "мода готовы", "модов готовы")} к установке";

        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AnimateFadeIn(ModsList, 260);
    }

    private void AnimateFadeIn(UIElement element, int durationMs)
    {
        if (!AppServices.Settings.Current.AnimationsEnabled)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / 1024d / 1024d:0.0} МБ";
        if (bytes >= 1024) return $"{bytes / 1024d:0} КБ";
        return bytes + " Б";
    }

    // ------------------------------------------------------------------ Modrinth

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) Search_Click(sender, e);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        SearchBox.IsEnabled = false;
        SearchStatus.Text = "Поиск на Modrinth...";

        try
        {
            var hits = await AppServices.Modrinth.SearchAsync(query, "mod", CancellationToken.None);

            _results.Clear();
            foreach (var hit in hits)
            {
                _results.Add(new ModrinthRow
                {
                    ProjectId = hit.ProjectId,
                    Title = hit.Title,
                    Description = hit.Description,
                    Meta = $"{FormatDownloads(hit.Downloads)} загрузок" +
                           (string.IsNullOrWhiteSpace(hit.Author) ? "" : " · " + hit.Author)
                });
            }

            ShowPane("results");
            ResultsEmpty.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchStatus.Text = _results.Count == 0
                ? "Ничего не найдено. Попробуй другое название."
                : $"Найдено: {_results.Count}. Нажми «Установить».";

            AnimateFadeIn(ResultsList, 240);
        }
        catch (Exception ex)
        {
            SearchStatus.Text = "Не удалось выполнить поиск: " + ex.Message;
        }
        finally
        {
            SearchBox.IsEnabled = true;
        }
    }

    private void BackToLocal_Click(object sender, RoutedEventArgs e)
    {
        ShowPane("local");
        SearchStatus.Text = SearchHint;
    }

    public void SearchFor(string query)
    {
        SearchBox.Text = query;
        Search_Click(this, new RoutedEventArgs());
    }

    private void Conflicts_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.OpenModConflicts();

    private async void Managed_Click(object sender, RoutedEventArgs e)
    {
        ShowPane("managed");
        await LoadManagedAsync();
    }

    private async void CheckDependencies_Click(object sender, RoutedEventArgs e)
    {
        ShowPane("managed");
        await LoadManagedAsync();

        DependenciesButton.IsEnabled = false;
        DependenciesButton.Content = "Проверка...";
        SearchStatus.Text = "Проверяем обязательные зависимости установленных модов...";

        try
        {
            var modsDirectory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "mods");
            var missing = await AppServices.Modrinth.FindMissingDependenciesAsync(modsDirectory, CancellationToken.None);

            if (missing.Count == 0)
            {
                SearchStatus.Text = "Все обязательные зависимости на месте.";
                Notify.Show("Зависимости в порядке", "Нечего исправлять.", "success");
                return;
            }

            var summary = string.Join(", ", missing.Select(m => m.ProjectId).Take(6)) +
                          (missing.Count > 6 ? "…" : "");
            var answer = MessageBox.Show(
                $"Найдено зависимостей: {missing.Count}.\n{summary}\n\nУстановить их сейчас?",
                "Зависимости модов", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            DependenciesButton.Content = "Установка...";
            var progress = new Progress<LaunchProgress>(p =>
                SearchStatus.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + ": " + p.Detail);

            var changed = await AppServices.Modrinth.EnsureDependenciesAsync(
                modsDirectory, progress, CancellationToken.None);

            SearchStatus.Text = $"Зависимости обработаны: {changed}.";
            Notify.Show("Зависимости обновлены", $"Изменено: {changed}", "success");
            Refresh();
            await LoadManagedAsync();
        }
        catch (Exception ex)
        {
            SearchStatus.Text = "Не удалось проверить зависимости: " + ex.Message;
            Notify.Show("Ошибка зависимостей", ex.Message, "error");
        }
        finally
        {
            DependenciesButton.IsEnabled = true;
            DependenciesButton.Content = "Зависимости";
        }
    }

    private async Task LoadManagedAsync()
    {
        var modsDirectory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "mods");
        var installed = AppServices.Modrinth.GetInstalledMods(modsDirectory);

        _managed.Clear();
        foreach (var mod in installed)
        {
            var version = string.IsNullOrWhiteSpace(mod.VersionNumber) ? "не указана" : mod.VersionNumber;
            _managed.Add(new ManagedRow
            {
                ProjectId = mod.ProjectId,
                Title = mod.Title,
                Meta = "версия " + version + (mod.Enabled ? "" : " · выключен"),
                Enabled = mod.Enabled
            });
        }

        ManagedEmpty.Visibility = _managed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchStatus.Text = _managed.Count == 0
            ? "Установленных модов нет. Найди мод на Modrinth — он появится здесь."
            : $"Установлено: {_managed.Count}. Можно включать, обновлять и удалять.";

        await Task.CompletedTask;
    }

    private async void ToggleManaged_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ManagedRow row } button) return;

        var modsDirectory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "mods");
        button.IsEnabled = false;

        try
        {
            var enable = !row.Enabled;
            if (AppServices.Modrinth.SetEnabled(row.ProjectId, modsDirectory, enable))
            {
                Notify.Show(enable ? "Мод включён" : "Мод выключен", row.Title, "success");

                if (enable)
                {
                    SearchStatus.Text = "Проверяем зависимости включённого мода...";
                    var progress = new Progress<LaunchProgress>(p =>
                        SearchStatus.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + ": " + p.Detail);
                    var changed = await AppServices.Modrinth.EnsureDependenciesAsync(
                        modsDirectory, progress, CancellationToken.None);

                    if (changed > 0)
                        Notify.Show("Зависимости добавлены", $"Изменено: {changed}", "success");
                }
            }
            else
            {
                Notify.Show("Не удалось переключить мод", row.Title, "error");
            }
        }
        finally
        {
            button.IsEnabled = true;
            await LoadManagedAsync();
        }
    }

    private async void UpdateManaged_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ManagedRow row } button) return;

        var modsDirectory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "mods");
        var original = button.Content;
        button.IsEnabled = false;
        button.Content = "Проверка...";

        try
        {
            var update = await AppServices.Modrinth.FindUpdateAsync(row.ProjectId, CancellationToken.None);

            if (update is null)
            {
                SearchStatus.Text = $"«{row.Title}» уже актуальной версии.";
                Notify.Show("Обновление не нужно", $"«{row.Title}» уже актуален.");
            }
            else
            {
                button.Content = "Обновление...";
                var progress = new Progress<LaunchProgress>(p =>
                    SearchStatus.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + ": " + p.Detail);

                await AppServices.Modrinth.InstallVersionAsync(update, modsDirectory, progress, CancellationToken.None);

                SearchStatus.Text = $"«{row.Title}» обновлён до {update.VersionNumber}.";
                Notify.Show("Мод обновлён", $"{row.Title} → {update.VersionNumber}", "success");
                Refresh();
            }
        }
        catch (Exception ex)
        {
            SearchStatus.Text = "Ошибка обновления: " + ex.Message;
            Notify.Show("Ошибка обновления", ex.Message, "error");
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = original;
            await LoadManagedAsync();
        }
    }

    private async void RemoveManaged_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ManagedRow row }) return;

        var modsDirectory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "mods");

        if (AppServices.Modrinth.Uninstall(row.ProjectId, modsDirectory))
        {
            Notify.Show("Мод удалён", row.Title);
            Refresh();
            await LoadManagedAsync();
        }
    }

    private async void InstallResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModrinthRow row } button) return;

        var original = button.Content;
        button.IsEnabled = false;
        button.Content = "Установка...";

        try
        {
            var modsDirectory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "mods");
            var versions = await AppServices.Modrinth.GetVersionsAsync(row.ProjectId, CancellationToken.None);
            var best = versions.FirstOrDefault();

            if (best is null)
            {
                SearchStatus.Text = $"«{row.Title}»: нет версии под Fabric {ModrinthService.GameVersion}.";
                return;
            }

            var progress = new Progress<LaunchProgress>(p =>
                SearchStatus.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + ": " + p.Detail);

            await AppServices.Modrinth.InstallVersionAsync(best, modsDirectory, progress, CancellationToken.None);

            SearchStatus.Text = $"«{row.Title}» установлен и появится в игре при следующем запуске.";
            Notify.Show("Мод установлен", row.Title, "success");
            button.Content = "Готово";
            Refresh();
        }
        catch (Exception ex)
        {
            SearchStatus.Text = "Ошибка установки: " + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
            if (!Equals(button.Content, "Готово")) button.Content = original;
        }
    }

    private void ShowPane(string pane)
    {
        LocalPane.Visibility = pane == "local" ? Visibility.Visible : Visibility.Collapsed;
        ResultsPane.Visibility = pane == "results" ? Visibility.Visible : Visibility.Collapsed;
        ManagedPane.Visibility = pane == "managed" ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = pane == "local" ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatDownloads(long value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.#} млн";
        if (value >= 1_000) return $"{value / 1_000d:0.#} тыс";
        return value.ToString();
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(AppServices.Mods.BundledDirectory);

    private void OpenGame_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(AppServices.Settings.Current.EffectiveGameDirectory, "mods");
        Directory.CreateDirectory(directory);
        ShellHelper.OpenFolder(directory);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
