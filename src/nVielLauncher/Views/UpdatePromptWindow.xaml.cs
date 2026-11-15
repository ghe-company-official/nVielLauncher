using System.Windows;
using System.Windows.Input;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

/// <summary>
/// The only update UI in the launcher: shows what changed in the launcher and the
/// client mod, then downloads, verifies and applies both. Repository and token
/// details stay internal.
/// </summary>
public partial class UpdatePromptWindow : Window
{
    private readonly UpdateInfo? _update;
    private readonly ClientModInfo? _mod;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public UpdatePromptWindow(UpdateInfo? update, ClientModInfo? mod)
    {
        InitializeComponent();

        _update = update;
        _mod = mod;

        VersionText.Text = update is not null
            ? AppInfo.ProductName + " " + update.Version + " " + AppInfo.Edition
            : "Клиентский мод nViel " + (mod?.Version ?? "");

        NotesText.Text = update?.Notes ?? "";
        NotesPanel.Visibility = string.IsNullOrWhiteSpace(update?.Notes)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (mod is not null)
        {
            ModPanel.Visibility = Visibility.Visible;
            ModTitle.Text = "Клиентский мод nViel " + mod.Version;
            ModMeta.Text = FormatSize(mod.Size) + " · SHA-256 проверяется перед установкой";
            ModNotes.Text = string.IsNullOrWhiteSpace(mod.Notes)
                ? "Мод будет установлен во все инстансы, старая версия сохранится для отката."
                : mod.Notes;
        }

        InstallButton.Content = update is not null && mod is not null
            ? "Обновить всё"
            : update is null
                ? "Обновить мод"
                : "Установить";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024) return (bytes / 1024d / 1024).ToString("0.0") + " МБ";
        if (bytes >= 1024) return (bytes / 1024d).ToString("0") + " КБ";
        return Math.Max(0, bytes) + " Б";
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
            CancelDownload();
            return;
        }

        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            CancelDownload();
            return;
        }

        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (_busy)
            CancelDownload();
        else
            Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_busy) return;

        e.Cancel = true;
        CancelDownload();
    }

    private void CancelDownload()
    {
        if (!_busy) return;

        Log.Write("UPDATE: отмена скачивания");
        _cts?.Cancel();
        LaterButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "Отмена...";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        _cts = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        LaterButton.Content = "Отмена";
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "Подготовка...";

        try
        {
            var progress = new Progress<double>(value =>
                UpdateProgress.Value = Math.Clamp(value * 100, 0, 100));
            IProgress<double> totalProgress = progress;
            var stage = new Progress<string>(text => StatusText.Text = text);

            if (_mod is not null)
            {
                if (AppServices.Launcher.IsRunning)
                    throw new InvalidOperationException("Сначала закрой Minecraft — файлы мода заняты игрой.");

                Log.Write("UPDATE: скачиваю клиентский мод " + _mod.Version);
                var modProgress = new Progress<double>(value => totalProgress.Report(value * 0.4));
                var result = await AppServices.ClientMods.InstallAsync(
                    _mod, modProgress, stage, _cts.Token);

                Log.Write($"UPDATE: мод {result.Version} установлен в {result.Instances} инстанс(ов)");
                totalProgress.Report(0.4);
            }

            if (_update is null)
            {
                UpdateProgress.Value = 100;
                StatusText.Text = "Клиентский мод обновлён. Перезапуск не требуется.";
                InstallButton.Content = "Готово";
                InstallButton.IsEnabled = false;
                CloseButton.IsEnabled = true;
                LaterButton.Content = "Закрыть";
                LaterButton.IsEnabled = true;
                _busy = false;
                return;
            }

            var launcherProgress = new Progress<double>(value => totalProgress.Report(0.4 + value * 0.6));
            Log.Write("UPDATE: начинаю скачивание " + _update.Tag);
            var path = await AppServices.Updates.DownloadAsync(_update, launcherProgress, _cts.Token, stage);
            Log.Write("UPDATE: скачано и проверено " + path);

            UpdateProgress.Value = 100;
            StatusText.Text = "Установка и перезапуск...";
            await Task.Delay(400);
            _cts.Token.ThrowIfCancellationRequested();

            // ApplyAndRestart shuts the application down; allow the window to close.
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            Log.Write("UPDATE: запускаю установку и перезапуск");
            AppServices.Updates.ApplyAndRestart(path);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Обновление отменено.";
            ResetButtons("Повторить");
        }
        catch (Exception ex)
        {
            Log.Write("UPDATE: установка не удалась: " + ex);
            StatusText.Text = "Не удалось установить обновление: " + ex.Message;
            ResetButtons("Повторить");
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ResetButtons(string installText)
    {
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateProgress.Value = 0;
        InstallButton.IsEnabled = true;
        InstallButton.Content = installText;
        CloseButton.IsEnabled = true;
        LaterButton.IsEnabled = true;
        LaterButton.Content = "Закрыть";
    }
}
