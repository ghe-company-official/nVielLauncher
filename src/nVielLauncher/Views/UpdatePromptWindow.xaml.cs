using System.Windows;
using System.Windows.Input;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

/// <summary>
/// The only update UI in the launcher: asks for confirmation and then downloads,
/// verifies and applies the release. Repository and token details stay internal.
/// </summary>
public partial class UpdatePromptWindow : Window
{
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public UpdatePromptWindow(UpdateInfo update)
    {
        InitializeComponent();
        _update = update;
        VersionText.Text = "nViel Launcher " + update.Version;
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
        StatusText.Text = "Скачивание обновления...";

        try
        {
            var progress = new Progress<double>(value =>
                UpdateProgress.Value = Math.Clamp(value * 100, 0, 100));
            var stage = new Progress<string>(text => StatusText.Text = text);

            Log.Write("UPDATE: начинаю скачивание " + _update.Tag);
            var path = await AppServices.Updates.DownloadAsync(_update, progress, _cts.Token, stage);
            Log.Write("UPDATE: скачано и проверено " + path);

            UpdateProgress.Value = 100;
            StatusText.Text = "Установка и перезапуск...";
            await Task.Delay(400);

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
            StatusText.Text = "Не удалось установить обновление. Проверь интернет и попробуй ещё раз.";
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
