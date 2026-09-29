namespace RustedShpizhionStudio.Core;

/// <summary>
/// Простой потокобезопасный файловый логгер.
/// Пишет в app.log рядом с exe и дублирует в Debug.WriteLine.
/// </summary>
public static class AppLog
{
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "app.log");
    private static readonly object FileLock = new();

    public static void Debug(string message) => Write("DEBUG", message, null);
    public static void Info(string  message) => Write("INFO ", message, null);
    public static void Warn(string  message) => Write("WARN ", message, null);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", message, ex);

    /// <summary>Открывает app.log в блокноте / дефолтном текстовом редакторе.</summary>
    public static void OpenLogFile()
    {
        try
        {
            if (!File.Exists(LogPath))
                File.WriteAllText(LogPath, $"# app.log — Rusted Шпижион Студия{Environment.NewLine}", Encoding.UTF8);

            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{LogPath}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"-t \"{LogPath}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{LogPath}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { Warn($"OpenLogFile failed: {ex.Message}"); }
    }

    /// <summary>Возвращает полный путь к файлу лога.</summary>
    public static string LogFilePath => LogPath;

    private static void Write(string level, string message, Exception? ex)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        var line  = $"[{stamp}] [{level}] {message}";
        if (ex is not null)
            line += $"\n  Exception: {ex.GetType().Name}: {ex.Message}\n  StackTrace:\n{ex.StackTrace}";

        System.Diagnostics.Debug.WriteLine(line);

        try
        {
            lock (FileLock)
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* если не смогли записать файл — не падаем */ }
    }

    /// <summary>Обрезает лог до lastLines строк, если файл вырос.</summary>
    public static void TrimIfLarge(int maxLines = 10_000)
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var lines = File.ReadAllLines(LogPath);
            if (lines.Length <= maxLines) return;
            var kept = lines.Skip(lines.Length - maxLines).ToArray();
            lock (FileLock)
                File.WriteAllLines(LogPath, kept, Encoding.UTF8);
        }
        catch { }
    }
}
