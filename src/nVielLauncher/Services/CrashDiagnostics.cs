using System.IO;
using System.Text;

namespace nVielLauncher.Services;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Critical
}

public sealed record DiagnosticFinding(DiagnosticSeverity Severity, string Title, string Detail, string Suggestion);

/// <summary>Reads the newest crash report / latest.log and turns common failures into plain-language advice.</summary>
public static class CrashDiagnostics
{
    private const int TailLines = 500;

    public static string CrashReportsDirectory(string gameDirectory) =>
        Path.Combine(gameDirectory, "crash-reports");

    public static string LatestLogPath(string gameDirectory) =>
        Path.Combine(gameDirectory, "logs", "latest.log");

    public static string? LatestCrashReport(string gameDirectory)
    {
        try
        {
            var directory = CrashReportsDirectory(gameDirectory);
            if (!Directory.Exists(directory)) return null;

            return Directory.EnumerateFiles(directory, "*.txt")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static List<DiagnosticFinding> Analyze(string gameDirectory)
    {
        var findings = new List<DiagnosticFinding>();
        var text = new StringBuilder();

        var crashReport = LatestCrashReport(gameDirectory);
        if (crashReport is not null)
        {
            text.AppendLine(ReadTail(crashReport, 240));
        }

        text.AppendLine(ReadTail(LatestLogPath(gameDirectory), TailLines));

        var haystack = text.ToString();

        if (haystack.Length == 0)
        {
            findings.Add(new DiagnosticFinding(
                DiagnosticSeverity.Info,
                "Нет данных для анализа",
                "Не найдено ни crash-report, ни logs/latest.log.",
                "Запусти игру хотя бы один раз — лаунчер сохранит журнал рядом с игрой."));
            return findings;
        }

        void Add(bool matched, DiagnosticFinding finding)
        {
            if (matched) findings.Add(finding);
        }

        Add(Contains(haystack, "OutOfMemoryError", "Java heap space", "GC overhead limit exceeded"),
            new DiagnosticFinding(
                DiagnosticSeverity.Critical,
                "Не хватает оперативной памяти",
                "Игра не смогла выделить память под моды.",
                "Открой «Настройки» и увеличь объём ОЗУ (например, 6144–8192 МБ), затем запусти снова."));

        Add(Contains(haystack, "UnsupportedClassVersionError", "class file version"),
            new DiagnosticFinding(
                DiagnosticSeverity.Critical,
                "Неподходящая версия Java",
                "Мод или игра собраны под другую версию Java.",
                "В «Настройках» нажми «Найти Java» — лаунчеру нужна Java 21."));

        Add(Contains(haystack, "Incompatible mod set", "Mod resolution failed", "Unmet dependency",
                "requires", "net.fabricmc.loader.impl.FormattedException"),
            new DiagnosticFinding(
                DiagnosticSeverity.Critical,
                "Конфликт или отсутствие модов",
                "Fabric Loader не смог собрать набор модов.",
                "Открой раздел «Моды» и проверь установленные версии: убери лишний мод либо добавь отсутствующую зависимость через Modrinth."));

        Add(Contains(haystack, "GLFW error", "Failed to create window", "WGL", "Pixel format",
                "OpenGL", "The driver does not appear to support OpenGL"),
            new DiagnosticFinding(
                DiagnosticSeverity.Warning,
                "Проблема с графикой / драйверами",
                "Видеодрайвер не позволил создать окно или контекст OpenGL.",
                "Обнови драйверы видеокарты и убедись, что игра запускается не через удалённый рабочий стол."));

        Add(Contains(haystack, "NoClassDefFoundError", "ClassNotFoundException"),
            new DiagnosticFinding(
                DiagnosticSeverity.Warning,
                "Повреждённые файлы установки",
                "Игре не хватает одного из библиотечных файлов.",
                "Удали папку libraries в каталоге игры — лаунчер скачает файлы заново при следующем запуске."));

        Add(Contains(haystack, "Failed to download", "UnknownHostException", "SocketTimeoutException",
                "Connection reset", "SSLException"),
            new DiagnosticFinding(
                DiagnosticSeverity.Warning,
                "Сбой загрузки из сети",
                "Не удалось скачать один из файлов.",
                "Проверь интернет и запусти игру снова — загрузка продолжится автоматически."));

        if (findings.Count == 0)
        {
            findings.Add(new DiagnosticFinding(
                DiagnosticSeverity.Info,
                "Явных проблем не найдено",
                "Ошибка не совпала с известными шаблонами.",
                "Открой logs/latest.log и найди строку с «Exception» — это поможет понять причину."));
        }

        return findings;
    }

    private static bool Contains(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string ReadTail(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path)) return "";

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);

            var lines = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines) lines.Dequeue();
            }

            return string.Join('\n', lines);
        }
        catch
        {
            return "";
        }
    }
}
