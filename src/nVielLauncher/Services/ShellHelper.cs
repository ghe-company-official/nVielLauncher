using System.Diagnostics;
using System.IO;

namespace nVielLauncher.Services;

public static class ShellHelper
{
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write("Не удалось открыть папку: " + ex.Message);
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Не удалось открыть ссылку: " + ex.Message);
        }
    }
}
