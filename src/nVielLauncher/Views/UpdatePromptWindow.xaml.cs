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
            : "РљР»РёРµРЅС‚СЃРєРёР№ РјРѕРґ nViel " + (mod?.Version ?? "");

        NotesText.Text = update?.Notes ?? "";
        NotesPanel.Visibility = string.IsNullOrWhiteSpace(update?.Notes)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (mod is not null)
        {
            ModPanel.Visibility = Visibility.Visible;
            ModTitle.Text = "РљР»РёРµРЅС‚СЃРєРёР№ РјРѕРґ nViel " + mod.Version;
            ModMeta.Text = FormatSize(mod.Size) + " В· SHA-256 РїСЂРѕРІРµСЂСЏРµС‚СЃСЏ РїРµСЂРµРґ СѓСЃС‚Р°РЅРѕРІРєРѕР№";
            ModNotes.Text = string.IsNullOrWhiteSpace(mod.Notes)
                ? "РњРѕРґ Р±СѓРґРµС‚ СѓСЃС‚Р°РЅРѕРІР»РµРЅ РІРѕ РІСЃРµ РёРЅСЃС‚Р°РЅСЃС‹, СЃС‚Р°СЂР°СЏ РІРµСЂСЃРёСЏ СЃРѕС…СЂР°РЅРёС‚СЃСЏ РґР»СЏ РѕС‚РєР°С‚Р°."
                : mod.Notes;
        }

        InstallButton.Content = update is not null && mod is not null
            ? "РћР±РЅРѕРІРёС‚СЊ РІСЃС‘"
            : update is null
                ? "РћР±РЅРѕРІРёС‚СЊ РјРѕРґ"
                : "РЈСЃС‚Р°РЅРѕРІРёС‚СЊ";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024) return (bytes / 1024d / 1024).ToString("0.0") + " РњР‘";
        if (bytes >= 1024) return (bytes / 1024d).ToString("0") + " РљР‘";
        return Math.Max(0, bytes) + " Р‘";
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

        Log.Write("UPDATE: РѕС‚РјРµРЅР° СЃРєР°С‡РёРІР°РЅРёСЏ");
        _cts?.Cancel();
        LaterButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "РћС‚РјРµРЅР°...";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        _cts = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        LaterButton.Content = "РћС‚РјРµРЅР°";
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "РџРѕРґРіРѕС‚РѕРІРєР°...";

        try
        {
            var progress = new Progress<double>(value =>
                UpdateProgress.Value = Math.Clamp(value * 100, 0, 100));
            IProgress<double> totalProgress = progress;
            var stage = new Progress<string>(text => StatusText.Text = text);

            if (_mod is not null)
            {
                if (AppServices.Launcher.IsRunning)
                    throw new InvalidOperationException("РЎРЅР°С‡Р°Р»Р° Р·Р°РєСЂРѕР№ Minecraft вЂ” С„Р°Р№Р»С‹ РјРѕРґР° Р·Р°РЅСЏС‚С‹ РёРіСЂРѕР№.");

                Log.Write("UPDATE: СЃРєР°С‡РёРІР°СЋ РєР»РёРµРЅС‚СЃРєРёР№ РјРѕРґ " + _mod.Version);
                var modProgress = new Progress<double>(value => totalProgress.Report(value * 0.4));
                var result = await AppServices.ClientMods.InstallAsync(
                    _mod, modProgress, stage, _cts.Token);

                Log.Write($"UPDATE: РјРѕРґ {result.Version} СѓСЃС‚Р°РЅРѕРІР»РµРЅ РІ {result.Instances} РёРЅСЃС‚Р°РЅСЃ(РѕРІ)");
                totalProgress.Report(0.4);
            }

            if (_update is null)
            {
                UpdateProgress.Value = 100;
                StatusText.Text = "РљР»РёРµРЅС‚СЃРєРёР№ РјРѕРґ РѕР±РЅРѕРІР»С‘РЅ. РџРµСЂРµР·Р°РїСѓСЃРє РЅРµ С‚СЂРµР±СѓРµС‚СЃСЏ.";
                InstallButton.Content = "Р“РѕС‚РѕРІРѕ";
                InstallButton.IsEnabled = false;
                CloseButton.IsEnabled = true;
                LaterButton.Content = "Р—Р°РєСЂС‹С‚СЊ";
                LaterButton.IsEnabled = true;
                _busy = false;
                return;
            }

            var launcherProgress = new Progress<double>(value => totalProgress.Report(0.4 + value * 0.6));
            Log.Write("UPDATE: РЅР°С‡РёРЅР°СЋ СЃРєР°С‡РёРІР°РЅРёРµ " + _update.Tag);
            var path = await AppServices.Updates.DownloadAsync(_update, launcherProgress, _cts.Token, stage);
            Log.Write("UPDATE: СЃРєР°С‡Р°РЅРѕ Рё РїСЂРѕРІРµСЂРµРЅРѕ " + path);

            UpdateProgress.Value = 100;
            StatusText.Text = "РЈСЃС‚Р°РЅРѕРІРєР° Рё РїРµСЂРµР·Р°РїСѓСЃРє...";
            await Task.Delay(400);
            _cts.Token.ThrowIfCancellationRequested();

            // ApplyAndRestart shuts the application down; allow the window to close.
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            Log.Write("UPDATE: Р·Р°РїСѓСЃРєР°СЋ СѓСЃС‚Р°РЅРѕРІРєСѓ Рё РїРµСЂРµР·Р°РїСѓСЃРє");
            AppServices.Updates.ApplyAndRestart(path);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "РћР±РЅРѕРІР»РµРЅРёРµ РѕС‚РјРµРЅРµРЅРѕ.";
            ResetButtons("РџРѕРІС‚РѕСЂРёС‚СЊ");
        }
        catch (Exception ex)
        {
            Log.Write("UPDATE: СѓСЃС‚Р°РЅРѕРІРєР° РЅРµ СѓРґР°Р»Р°СЃСЊ: " + ex);
            StatusText.Text = "РќРµ СѓРґР°Р»РѕСЃСЊ СѓСЃС‚Р°РЅРѕРІРёС‚СЊ РѕР±РЅРѕРІР»РµРЅРёРµ: " + ex.Message;
            ResetButtons("РџРѕРІС‚РѕСЂРёС‚СЊ");
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
        LaterButton.Content = "Р—Р°РєСЂС‹С‚СЊ";
    }
}
