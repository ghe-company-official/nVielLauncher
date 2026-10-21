using System.IO;
using System.Text.Json;

namespace nVielLauncher.Services;

public enum RepairLogKind
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record RepairLogEntry(string Message, RepairLogKind Kind = RepairLogKind.Info);

public sealed class RepairReport
{
    public bool LauncherOk { get; set; }
    public bool JavaOk { get; set; }
    public bool FilesOk { get; set; }
    public bool ModsOk { get; set; }
    public int Errors { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>
/// Full integrity pass used by nVielRE: Java, Minecraft/Fabric files, core mods and
/// launcher state. Every step is isolated so one failure does not stop the others.
/// </summary>
public static class RepairService
{
    public static async Task<RepairReport> RunAsync(
        string gameDirectory,
        IProgress<LaunchProgress> progress,
        IProgress<RepairLogEntry> log,
        CancellationToken ct)
    {
        var report = new RepairReport();
        var runtimeDirectory = AppServices.Settings.Current.SharedRuntimeDirectory;

        void Write(string message, RepairLogKind kind = RepairLogKind.Info) =>
            log.Report(new RepairLogEntry(message, kind));

        progress.Report(new LaunchProgress(0, "Подготовка", "nVielRE · полная проверка"));
        Write("nVielRE запущен. Режим: полное восстановление.", RepairLogKind.Success);
        Write("Игровая папка: " + gameDirectory);

        // 1. game folder
        try
        {
            Directory.CreateDirectory(gameDirectory);
            var probe = Path.Combine(gameDirectory, ".nviel-repair.tmp");
            await File.WriteAllTextAsync(probe, "ok", ct);
            File.Delete(probe);

            report.LauncherOk = true;
            Write("Папка игры доступна для записи.", RepairLogKind.Success);
        }
        catch (Exception ex)
        {
            report.Errors++;
            Write("Папка игры недоступна для записи: " + ex.Message, RepairLogKind.Error);
        }

        // 2. launcher state files
        try
        {
            Write("Проверяем файлы настроек лаунчера...");
            ValidateJson(AppPaths.SettingsFile);
            ValidateJson(AppPaths.ModrinthStateFile);
            Write("Настройки и состояние Modrinth читаются корректно.", RepairLogKind.Success);
        }
        catch (Exception ex)
        {
            report.Errors++;
            report.LauncherOk = false;
            Write("Повреждён файл настроек: " + ex.Message, RepairLogKind.Error);
        }

        // 3. Java
        progress.Report(new LaunchProgress(4, "Java", "Поиск Java 21"));
        try
        {
            Write("Ищем Java 21...");
            var java = await AppServices.Java.DetectAsync(21, ct);

            if (java is null)
            {
                Write("Java 21 не найдена — скачиваем Temurin JRE.", RepairLogKind.Warning);
                java = await AppServices.Java.EnsureJavaAsync(21,
                    Map(progress, 4, 12, "Java"), ct);
            }

            AppServices.Settings.Current.JavaPath = java.JavaExe;
            AppServices.Settings.Save();

            report.JavaOk = true;
            Write($"Java {java.Major}: {java.JavaExe}", RepairLogKind.Success);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            report.Errors++;
            Write("Java: " + ex.Message, RepairLogKind.Error);
        }

        // 4. Minecraft + Fabric + assets
        progress.Report(new LaunchProgress(13, "Minecraft", "Проверка файлов игры"));
        try
        {
            Write("Проверяем клиент, библиотеки, Fabric и ресурсы по SHA-1. Это может занять пару минут...");
            await AppServices.Installer.EnsureAsync(gameDirectory, runtimeDirectory,
                Map(progress, 13, 80, "Minecraft"), ct);

            var versionJson = Path.Combine(runtimeDirectory, "versions", MinecraftInstaller.MinecraftVersion,
                MinecraftInstaller.MinecraftVersion + ".json");

            if (!File.Exists(versionJson))
                throw new IOException("Не найден файл версии Minecraft.");

            report.FilesOk = true;
            Write("Файлы Minecraft и Fabric в порядке.", RepairLogKind.Success);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            report.Errors++;
            Write("Файлы игры: " + ex.Message, RepairLogKind.Error);
        }

        // 5. core mods
        progress.Report(new LaunchProgress(83, "Моды", "Восстановление обязательных модов"));
        try
        {
            Write("Восстанавливаем обязательные моды...");
            await AppServices.Mods.EnsureCoreModsAsync(gameDirectory, ct);
            await AppServices.Mods.SyncAsync(gameDirectory, null, ct);

            var status = AppServices.Mods.GetCoreModStatus(gameDirectory);
            report.ModsOk = status.Total == 0 || status.Installed == status.Total;

            Write($"Обязательные моды: {status.Installed} / {status.Total}.",
                report.ModsOk ? RepairLogKind.Success : RepairLogKind.Warning);

            if (!report.ModsOk)
                report.Errors++;

            var broken = CountBrokenJars(Path.Combine(gameDirectory, "mods"));
            if (broken > 0)
            {
                Write($"Повреждённых .jar в папке mods: {broken}. Переустанови их через Modrinth.",
                    RepairLogKind.Warning);
                report.Errors++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            report.Errors++;
            Write("Моды: " + ex.Message, RepairLogKind.Error);
        }

        // 6. free disk space
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(gameDirectory));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                var free = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                Write($"Свободно на диске {drive.Name}: {free:0.0} ГБ.");

                if (free < 2)
                {
                    Write("Мало свободного места — игре может не хватить для ресурсов и миров.",
                        RepairLogKind.Warning);
                }
            }
        }
        catch
        {
            // disk information is optional
        }

        progress.Report(new LaunchProgress(100, "Проверка завершена",
            report.Errors == 0 ? "Проблем не найдено" : $"Проблем: {report.Errors}"));

        report.Summary = report.Errors == 0
            ? "Всё в порядке — можно запускать игру."
            : $"Проверка завершена, найдено проблем: {report.Errors}.";

        Write(report.Summary, report.Errors == 0 ? RepairLogKind.Success : RepairLogKind.Error);
        return report;
    }

    private static IProgress<LaunchProgress> Map(
        IProgress<LaunchProgress> target, double from, double to, string prefix)
    {
        return new Progress<LaunchProgress>(p =>
        {
            var percent = from + Math.Clamp(p.Percent, 0, 100) / 100d * (to - from);
            target.Report(new LaunchProgress(percent, prefix + ": " + p.Status, p.Detail));
        });
    }

    private static void ValidateJson(string path)
    {
        if (!File.Exists(path)) return;

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return;

        using var _ = JsonDocument.Parse(text);
    }

    private static int CountBrokenJars(string modsDirectory)
    {
        try
        {
            if (!Directory.Exists(modsDirectory)) return 0;

            return Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly)
                .Count(path =>
                {
                    try { return new FileInfo(path).Length == 0; }
                    catch { return false; }
                });
        }
        catch
        {
            return 0;
        }
    }
}
