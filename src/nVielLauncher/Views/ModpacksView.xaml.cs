using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public sealed class ModpackRow
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Meta { get; init; } = "";
}

public sealed class InstalledPackRow
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
}

public partial class ModpacksView : UserControl, IRefreshable
{
    private const string StatusHint =
        "Сборка установится в папку игры: моды, конфиги и ресурсы — автоматически";

    private readonly ObservableCollection<ModpackRow> _results = new();
    private readonly ObservableCollection<InstalledPackRow> _installed = new();
    private bool _busy;

    public ModpacksView()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;
        InstalledList.ItemsSource = _installed;
    }

    public void Refresh()
    {
        _installed.Clear();
        foreach (var pack in AppServices.Modrinth.Modpacks)
        {
            var version = string.IsNullOrWhiteSpace(pack.Version) ? "" : "v" + pack.Version + " · ";
            _installed.Add(new InstalledPackRow
            {
                Title = pack.Name,
                Subtitle = version + pack.InstalledUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            });
        }

        InstalledEmpty.Visibility = _installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountBadge.Text = _installed.Count.ToString();
    }

    // ------------------------------------------------------------------ search

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search_Click(sender, e);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        SearchBox.IsEnabled = false;
        StatusText.Text = "Поиск сборок на Modrinth...";

        try
        {
            var hits = await AppServices.Modrinth.SearchAsync(query, "modpack", CancellationToken.None);

            _results.Clear();
            foreach (var hit in hits)
            {
                _results.Add(new ModpackRow
                {
                    ProjectId = hit.ProjectId,
                    Title = hit.Title,
                    Description = hit.Description,
                    Meta = $"{FormatDownloads(hit.Downloads)} загрузок" +
                           (string.IsNullOrWhiteSpace(hit.Author) ? "" : " · " + hit.Author)
                });
            }

            ResultsEmpty.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _results.Count == 0
                ? "Ничего не найдено для Fabric 1.21.4."
                : $"Найдено: {_results.Count}.";

            ResultsList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось выполнить поиск: " + ex.Message;
        }
        finally
        {
            SearchBox.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ install

    private async void InstallResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModpackRow row } button) return;
        if (_busy) return;

        var original = button.Content;
        button.IsEnabled = false;
        button.Content = "Установка...";

        try
        {
            var versions = await AppServices.Modrinth.GetVersionsAsync(row.ProjectId, CancellationToken.None);
            var best = versions.FirstOrDefault();
            var file = best?.Files.FirstOrDefault(f => f.Primary) ?? best?.Files.FirstOrDefault();

            if (file is null)
            {
                StatusText.Text = $"«{row.Title}»: нет подходящей версии для Fabric {ModrinthService.GameVersion}.";
                return;
            }

            await InstallAsync(file.Url, row.Title, button);
            if (Equals(button.Content, "Готово")) return;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка установки: " + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
            if (!Equals(button.Content, "Готово")) button.Content = original;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери сборку Modrinth",
            Filter = "Сборки Modrinth (*.mrpack)|*.mrpack|Все файлы (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        ImportButton.IsEnabled = false;
        var original = ImportButton.Content;
        ImportButton.Content = "Установка...";

        try
        {
            await InstallAsync(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), ImportButton);
            if (Equals(ImportButton.Content, "Готово")) return;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка установки: " + ex.Message;
        }
        finally
        {
            ImportButton.IsEnabled = true;
            if (!Equals(ImportButton.Content, "Готово")) ImportButton.Content = original;
        }
    }

    private async Task InstallAsync(string source, string? displayName, Button? button)
    {
        if (_busy) return;
        _busy = true;

        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.Value = 0;

        try
        {
            var gameDirectory = AppServices.Settings.Current.EffectiveGameDirectory;
            var progress = new Progress<LaunchProgress>(p =>
            {
                StatusText.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + " · " + p.Detail;
                InstallProgress.Value = p.Percent;
            });

            var pack = await AppServices.Modrinth.InstallModpackAsync(
                source, gameDirectory, displayName, progress, CancellationToken.None);

            StatusText.Text = $"Сборка «{pack.Name}» установлена. Запусти игру.";
            Notify.Show("Сборка установлена", pack.Name, "success");
            if (button is not null) button.Content = "Готово";
            Refresh();
        }
        finally
        {
            _busy = false;
            InstallProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatDownloads(long value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.#} млн";
        if (value >= 1_000) return $"{value / 1_000d:0.#} тыс";
        return value.ToString();
    }
}
