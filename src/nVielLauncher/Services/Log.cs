using System.IO;
using System.Text;

namespace nVielLauncher.Services;

public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static event Action<string>? Message;

    public static string CurrentLogFile
    {
        get
        {
            if (_file is not null) return _file;
            lock (Gate)
            {
                _file ??= Path.Combine(AppPaths.LogsDirectory, $"launcher-{DateTime.Now:yyyy-MM-dd}.log");
                return _file;
            }
        }
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never crash the launcher
        }

        Message?.Invoke(line);
    }
}
