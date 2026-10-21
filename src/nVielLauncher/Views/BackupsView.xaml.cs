using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public sealed class BackupRow
{
    public BackupInfo Backup { get; init; } = null!;
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string FileName { get; init; } = "";
}

public partial class BackupsView : UserControl, IRefreshable
{
    private readonly ObservableCollection<BackupRow> _rows = new();
    private bool _busy;
    private bool _loading;
    private BackupInfo? _pending;
    private string? _pendingInstanceId;
    private string _pendingAction = "";

    public BackupsView()
    {
        InitializeComponent();
        BackupList.ItemsSource = _rows;
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
        try
        {
            var instance = AppServices.Instances.Active;
            if (instance is null) return;

            var settings = AppServices.Settings.Current;
            _loading = true;
            AutoBackupToggle.IsChecked = settings.AutoBackupBeforeLaunch;
            _loading = false;

            InstanceText.Text = instance.Name;
            SubHeader.Text = "Миры и конфиги инстанса «" + instance.Name + "»";
            RetentionText.Text = $"Хранятся последние {settings.BackupRetention} копий · saves, config, options.txt и servers.dat";

            var list = await Task.Run(() => AppServices.Backups.List(instance.Id));
            _rows.Clear();
            foreach (var backup in list)
                _rows.Add(Map(backup));

            BackupList.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var last = instance.LastBackupUtc?.ToLocalTime() ?? list.FirstOrDefault()?.CreatedUtc.ToLocalTime();
            LastBackupText.Text = last is null
                ? "Копий пока нет"
                : "Последняя: " + last.Value.ToString("dd.MM.yyyy, HH:mm");
            StatusText.Text = _rows.Count == 0 ? "" : _rows.Count + " " + Plural(_rows.Count, "копия", "копии", "копий");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось прочитать копии: " + ex.Message;
            Log.Write("BACKUP: " + ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private static BackupRow Map(BackupInfo backup)
    {
        var reason = backup.Reason switch
        {
            "auto" => "перед запуском",
            "manual" => "вручную",
            "before-restore" => "перед восстановлением",
            _ => backup.Reason
        };

        return new BackupRow
        {
            Backup = backup,
            Title = backup.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy, HH:mm"),
            Meta = FormatSize(backup.Size) + " · " + reason,
            FileName = backup.FileName
        };
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var instance = AppServices.Instances.Active;
        if (instance is null || _busy) return;

        SetBusy(true, "Создаём резервную копию...");
        BackupProgress.Value = 0;

        try
        {
            var gameDirectory = AppServices.Instances.GetDirectory(instance);
            var settings = AppServices.Settings.Current;
            var progress = new Progress<LaunchProgress>(p =>
            {
                BackupProgress.Value = Math.Clamp(p.Percent, 0, 100);
                StatusText.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + " · " + p.Detail;
            });

            var result = await AppServices.Backups.CreateAsync(
                gameDirectory, instance.Id, "manual", settings.BackupRetention, progress, CancellationToken.None);

            if (result is null)
            {
                Notify.Show("Нечего сохранять", "В инстансе пока нет миров или конфигов.", "info");
            }
            else
            {
                instance.LastBackupUtc = DateTime.UtcNow;
                AppServices.Settings.Save();
                Notify.Show("Резервная копия создана", result.FileName, "success");
            }
        }
        catch (Exception ex)
        {
            Notify.Show("Не удалось создать копию", ex.Message, "error");
            Log.Write("BACKUP: " + ex);
        }
        finally
        {
            SetBusy(false, "");
        }

        Refresh();
    }

    private void AutoBackup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.Current.AutoBackupBeforeLaunch = AutoBackupToggle.IsChecked == true;
        AppServices.Settings.Save();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BackupRow row } || _busy) return;
        _pending = row.Backup;
        _pendingInstanceId = AppServices.Instances.Active?.Id;
        _pendingAction = "restore";
        ConfirmTitle.Text = "Восстановить копию?";
        ConfirmText.Text = row.Title + " · " + row.Meta + "\nФайлы миров и конфигов будут перезаписаны.";
        ConfirmWarning.Text = "Перед восстановлением лаунчер создаст страховочную копию.";
        ConfirmWarning.Visibility = Visibility.Visible;
        ConfirmButton.Content = "Восстановить";
        ShowConfirm();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BackupRow row } || _busy) return;
        _pending = row.Backup;
        _pendingInstanceId = AppServices.Instances.Active?.Id;
        _pendingAction = "delete";
        ConfirmTitle.Text = "Удалить копию?";
        ConfirmText.Text = row.FileName;
        ConfirmWarning.Text = "Файл будет удалён без возможности отмены.";
        ConfirmWarning.Visibility = Visibility.Visible;
        ConfirmButton.Content = "Удалить";
        ShowConfirm();
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is null || _busy) return;
        var backup = _pending;
        var action = _pendingAction;
        ConfirmLayer.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = false;

        if (action == "delete")
        {
            AppServices.Backups.Delete(backup);
            Notify.Show("Копия удалена", backup.FileName, "info");
            _pending = null;
            _pendingInstanceId = null;
            ConfirmButton.IsEnabled = true;
            Refresh();
            return;
        }

        var instance = AppServices.Instances.All.FirstOrDefault(i =>
            string.Equals(i.Id, _pendingInstanceId, StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            Notify.Show("Инстанс не найден", "Копия относится к удалённому инстансу.", "error");
            _pending = null;
            _pendingInstanceId = null;
            ConfirmButton.IsEnabled = true;
            return;
        }

        SetBusy(true, "Восстанавливаем...");
        BackupProgress.Value = 0;

        try
        {
            var gameDirectory = AppServices.Instances.GetDirectory(instance);
            var settings = AppServices.Settings.Current;
            var safetyProgress = new Progress<LaunchProgress>(p =>
                StatusText.Text = "Страховочная копия · " + p.Status);
            await AppServices.Backups.CreateAsync(
                gameDirectory, instance.Id, "before-restore", settings.BackupRetention,
                safetyProgress, CancellationToken.None);

            var progress = new Progress<LaunchProgress>(p =>
            {
                BackupProgress.Value = Math.Clamp(p.Percent, 0, 100);
                StatusText.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Status : p.Status + " · " + p.Detail;
            });
            await AppServices.Backups.RestoreAsync(backup, gameDirectory, progress, CancellationToken.None);

            instance.LastBackupUtc = DateTime.UtcNow;
            AppServices.Settings.Save();
            Notify.Show("Копия восстановлена", backup.FileName, "success");
        }
        catch (Exception ex)
        {
            Notify.Show("Не удалось восстановить копию", ex.Message, "error");
            Log.Write("BACKUP: " + ex);
        }
        finally
        {
            _pending = null;
            _pendingInstanceId = null;
            SetBusy(false, "");
        }

        Refresh();
    }

    private void CloseConfirm_Click(object sender, RoutedEventArgs e)
    {
        ConfirmLayer.Visibility = Visibility.Collapsed;
        _pending = null;
        _pendingInstanceId = null;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var instance = AppServices.Instances.Active;
        if (instance is null) return;

        var directory = AppServices.Backups.DirectoryFor(instance.Id);
        Directory.CreateDirectory(directory);
        ShellHelper.OpenFolder(directory);
    }

    private void ShowConfirm()
    {
        ConfirmLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => ConfirmButton.Focus());
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        CreateButton.IsEnabled = !busy;
        ConfirmButton.IsEnabled = !busy;
        AutoBackupToggle.IsEnabled = !busy;
        BackupProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024d / 1024 / 1024).ToString("0.00") + " ГБ";
        if (bytes >= 1024L * 1024) return (bytes / 1024d / 1024).ToString("0.0") + " МБ";
        if (bytes >= 1024) return (bytes / 1024d).ToString("0") + " КБ";
        return bytes + " Б";
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
}
