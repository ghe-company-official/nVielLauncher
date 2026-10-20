using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace nVielLauncher.Services;

/// <summary>
/// Turns the downloaded single-file build into an installed launcher:
/// it copies itself to %APPDATA%\.nviel\app, starts that copy, creates shortcuts
/// and removes the original file when it was launched from the Downloads folder.
/// </summary>
public static class SelfInstaller
{
    private const string ExeName = "nVielLauncher.exe";
    private const string ShortcutName = "nViel Launcher.lnk";

    /// <summary>Returns true when the current process should exit because the installed copy was started.</summary>
    public static bool TryInstall(string[]? args = null)
    {
        try
        {
            if (args is not null && args.Any(a => string.Equals(a, "--no-install", StringComparison.OrdinalIgnoreCase)))
                return false;

            var source = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(source)) return false;
            source = Path.GetFullPath(source);

            // A regular dev build has nVielLauncher.dll next to the exe — never self-install from bin/.
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "nVielLauncher.dll")))
                return false;

            var installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".nviel", "app");
            var target = Path.Combine(installDir, ExeName);

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return false;

            Directory.CreateDirectory(installDir);

            var sourceVersion = GetVersion(source);
            var targetVersion = File.Exists(target) ? GetVersion(target) : null;

            if (targetVersion is null || sourceVersion >= targetVersion)
            {
                ReplaceTarget(source, target);
                TryDeleteZoneIdentifier(target);
            }

            EnsureShortcuts(target);
            StartInstalled(target, installDir, args);

            if (IsInDownloads(source))
                ScheduleDelete(source);

            Log.Write($"SELF-INSTALL: установлено в {installDir}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("SELF-INSTALL: " + ex);
            return false;
        }
    }

    private static void ReplaceTarget(string source, string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        var temp = target + ".new";

        foreach (var stale in Directory.EnumerateFiles(directory, "*.new"))
            TryDelete(stale);

        File.Copy(source, temp, overwrite: true);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The previously installed copy may still be running — close it and retry.
                CloseInstalled(target);
                Thread.Sleep(400);
            }
        }

        TryDelete(temp);
        throw new IOException("Не удалось обновить установленную копию лаунчера.");
    }

    private static void CloseInstalled(string target)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;

                var path = process.MainModule?.FileName;
                if (path is not null && string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1500))
                        process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Another user's process or already gone — nothing to do.
            }
        }
    }

    private static void StartInstalled(string target, string installDir, string[]? args)
    {
        var start = new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            WorkingDirectory = installDir
        };

        if (args is { Length: > 0 })
            start.Arguments = string.Join(' ', args.Select(Quote));

        Process.Start(start);
    }

    private static void EnsureShortcuts(string target)
    {
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName), target);

        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName), target);
    }

    private static void CreateShortcut(string linkPath, string target)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "nViel Launcher — Minecraft Fabric 1.21.4";
            link.Save();
        }
        catch (Exception ex)
        {
            Log.Write("SELF-INSTALL: ярлык не создан: " + ex.Message);
        }
    }

    /// <summary>Deletes the original file after this process has exited (Windows locks running exes).</summary>
    private static void ScheduleDelete(string path)
    {
        var script =
            $"for /l %i in (1,1,15) do @(ping 127.0.0.1 -n 2 >nul & del /f /q \"{path}\" >nul 2>&1 & if not exist \"{path}\" exit /b 0)";

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + script)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex)
        {
            Log.Write("SELF-INSTALL: файл не удалён: " + ex.Message);
        }
    }

    private static bool IsInDownloads(string path)
    {
        foreach (var folder in GetDownloadFolders())
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;

            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> GetDownloadFolders()
    {
        var known = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (!string.IsNullOrWhiteSpace(known))
            yield return known;

        string? fromRegistry = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            fromRegistry = key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;

            if (!string.IsNullOrWhiteSpace(fromRegistry))
                fromRegistry = Environment.ExpandEnvironmentVariables(fromRegistry);
        }
        catch
        {
            // Registry unavailable — the default folder above is enough.
        }

        if (!string.IsNullOrWhiteSpace(fromRegistry) &&
            !string.Equals(fromRegistry, known, StringComparison.OrdinalIgnoreCase))
        {
            yield return fromRegistry;
        }
    }

    private static Version GetVersion(string path)
    {
        try
        {
            var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return Version.TryParse(text, out var version) ? version : new Version(0, 0);
        }
        catch
        {
            return new Version(0, 0);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }

    private static void TryDeleteZoneIdentifier(string path)
    {
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch
        {
            // no mark of the web — fine
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('"') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
}
