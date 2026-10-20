using System.Diagnostics;
using System.IO;
using System.Windows;

namespace nVielLauncher.Services;

/// <summary>Starts the normal launcher or the nVielRE recovery edition and closes the current process.</summary>
public static class ReLauncher
{
    public static void OpenRe() => Launch("--re");

    public static void OpenLauncher() => Launch(null);

    private static void Launch(string? argument)
    {
        var window = Application.Current?.MainWindow;
        var hidden = false;

        try
        {
            var executable = AppInfo.ExecutablePath;
            var directory = Path.GetDirectoryName(executable);

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory
            };

            if (argument is not null)
                startInfo.Arguments = argument;

            if (window is not null && window.IsVisible)
            {
                window.Hide();
                hidden = true;
            }

            Process.Start(startInfo);
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            if (hidden && window is not null)
                window.Show();

            MessageBox.Show(
                "Не удалось переключить режим:\n\n" + ex.Message,
                "nViel Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
