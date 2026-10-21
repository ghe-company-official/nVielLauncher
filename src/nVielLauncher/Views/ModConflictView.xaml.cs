using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public sealed class ConflictIssueRow
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string File { get; init; } = "";
    public string ActionLabel { get; init; } = "";
    public string ActionPayload { get; init; } = "";
    public ModIssueAction Action { get; init; }
    public Visibility ActionVisibility { get; init; }
    public Brush Accent { get; init; } = Brushes.White;
    public Brush Background { get; init; } = Brushes.Transparent;
    public Brush Border { get; init; } = Brushes.Transparent;
}

public partial class ModConflictView : UserControl, IRefreshable
{
    private static readonly Brush ErrorAccent = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)));
    private static readonly Brush WarningAccent = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD4, 0x79)));
    private static readonly Brush ErrorBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0x8A, 0x8A)));
    private static readonly Brush WarningBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x0B, 0xFF, 0xD4, 0x79)));
    private static readonly Brush ErrorBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0x8A, 0x8A)));
    private static readonly Brush WarningBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xD4, 0x79)));

    private readonly ObservableCollection<ConflictIssueRow> _issues = new();
    private bool _scanning;

    public ModConflictView()
    {
        InitializeComponent();
        IssueList.ItemsSource = _issues;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        if (_scanning) return;
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        _scanning = true;
        RefreshButton.IsEnabled = false;
        DependenciesButton.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        ScanStatus.Text = "Сканируем fabric.mod.json...";
        IssueList.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        _issues.Clear();

        try
        {
            var settings = AppServices.Settings.Current;
            var gameDirectory = settings.EffectiveGameDirectory;
            var instanceName = settings.ActiveInstance?.Name ?? "Основная";
            SubHeader.Text = $"Инстанс «{instanceName}» · {gameDirectory}";

            var report = await ModConflictService.ScanAsync(gameDirectory, CancellationToken.None);

            ErrorCount.Text = report.ErrorCount.ToString();
            WarningCount.Text = report.WarningCount.ToString();
            CheckedCount.Text = report.EnabledCount.ToString();
            CheckedHint.Text = report.EnabledCount == 1 ? "активный jar" : "активных jar";
            DisabledCount.Text = report.DisabledCount.ToString();

            foreach (var issue in report.Issues)
                _issues.Add(Map(issue));

            var hasIssues = _issues.Count > 0;
            IssueList.Visibility = hasIssues ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = hasIssues ? Visibility.Collapsed : Visibility.Visible;
            EmptyHint.Text = report.EnabledCount == 0
                ? "В папке mods пока нет активных модов."
                : $"Проверено {report.EnabledCount} модов, обязательные зависимости на месте.";

            LastScanText.Text = "Проверено " + DateTime.Now.ToString("HH:mm:ss");
            ScanStatus.Text = hasIssues
                ? $"Найдено {_issues.Count} " + Plural(_issues.Count, "замечание", "замечания", "замечаний")
                : "Проблем не найдено";
        }
        catch (Exception ex)
        {
            ScanStatus.Text = "Ошибка проверки: " + ex.Message;
            Log.Write("CONFLICTS: " + ex);
        }
        finally
        {
            _scanning = false;
            RefreshButton.IsEnabled = true;
            DependenciesButton.IsEnabled = true;
            ScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static ConflictIssueRow Map(ModIssue issue)
    {
        var isError = issue.Severity == ModIssueSeverity.Error;
        var label = issue.Action switch
        {
            ModIssueAction.Disable => "Отключить",
            ModIssueAction.Enable => "Включить",
            ModIssueAction.Search => "Найти",
            ModIssueAction.Open => "Папка",
            _ => ""
        };

        return new ConflictIssueRow
        {
            Title = issue.Title,
            Detail = issue.Detail,
            File = string.IsNullOrWhiteSpace(issue.FileName) ? "" : "Файл: " + issue.FileName,
            Action = issue.Action,
            ActionLabel = label,
            ActionPayload = issue.ActionValue ?? issue.FileName ?? "",
            ActionVisibility = issue.Action == ModIssueAction.None ? Visibility.Collapsed : Visibility.Visible,
            Accent = isError ? ErrorAccent : WarningAccent,
            Background = isError ? ErrorBackground : WarningBackground,
            Border = isError ? ErrorBorder : WarningBorder
        };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = ModConflictService.GetModsDirectory(AppServices.Settings.Current.EffectiveGameDirectory);
        Directory.CreateDirectory(directory);
        ShellHelper.OpenFolder(directory);
    }

    private async void Dependencies_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;

        _scanning = true;
        RefreshButton.IsEnabled = false;
        DependenciesButton.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        ScanStatus.Text = "Ищем обязательные зависимости в Modrinth...";

        try
        {
            var gameDirectory = AppServices.Settings.Current.EffectiveGameDirectory;
            var progress = new Progress<LaunchProgress>(p =>
                ScanStatus.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + " · " + p.Detail);
            await AppServices.Modrinth.EnsureRequiredDependenciesAsync(gameDirectory, progress, CancellationToken.None);
            Notify.Show("Зависимости", "Проверка завершена. Пересканируем моды.", "success");
        }
        catch (OperationCanceledException)
        {
            // page is closing or user started another scan
        }
        catch (Exception ex)
        {
            Notify.Show("Не удалось проверить зависимости", ex.Message, "error");
        }
        finally
        {
            _scanning = false;
            RefreshButton.IsEnabled = true;
            DependenciesButton.IsEnabled = true;
            ScanProgress.Visibility = Visibility.Collapsed;
        }

        Refresh();
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConflictIssueRow row }) return;

        var gameDirectory = AppServices.Settings.Current.EffectiveGameDirectory;
        var modsDirectory = ModConflictService.GetModsDirectory(gameDirectory);

        try
        {
            switch (row.Action)
            {
                case ModIssueAction.Disable:
                    if (ModConflictService.SetEnabled(modsDirectory, row.ActionPayload, enabled: false))
                    {
                        Log.Write("CONFLICTS: отключён " + row.ActionPayload);
                        Refresh();
                    }
                    break;

                case ModIssueAction.Enable:
                    if (ModConflictService.SetEnabled(modsDirectory, row.ActionPayload, enabled: true))
                    {
                        Log.Write("CONFLICTS: включён " + row.ActionPayload);
                        Refresh();
                    }
                    break;

                case ModIssueAction.Search:
                    (Window.GetWindow(this) as MainWindow)?.OpenModsSearch(row.ActionPayload);
                    break;

                case ModIssueAction.Open:
                    Directory.CreateDirectory(modsDirectory);
                    ShellHelper.OpenFolder(modsDirectory);
                    break;
            }
        }
        catch (Exception ex)
        {
            Notify.Show("Не удалось выполнить действие", ex.Message, "error");
        }
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

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
