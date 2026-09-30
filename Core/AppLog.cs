namespace RustedShpizhionStudio.Core;

/// <summary>Потокобезопасный логгер с fallback в %TEMP% при недоступности portable-каталога.</summary>
public static class AppLog
{
    private static readonly object FileLock = new();
    private static string? _activeLogPath;

    public static void Debug(string message) => Write("DEBUG", message, null);
    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);
    public static string LogFilePath => GetLogPath();

    public static string OpenLogFile()
    {
        var path = GetLogPath();
        try
        {
            if (!File.Exists(path)) Write("INFO ", "Создан новый журнал приложения.", null);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { try { Write("WARN ", $"OpenLogFile failed: {ex.Message}", null); } catch { } }
        return path;
    }

    public static string TryWriteEmergency(string report)
    {
        try
        {
            var path = GetLogPath();
            Append(path, report + Environment.NewLine);
            return path;
        }
        catch
        {
            try
            {
                var fallback = BuildFallbackPath();
                lock (FileLock) _activeLogPath = fallback;
                Append(fallback, report + Environment.NewLine);
                return fallback;
            }
            catch { return "<журнал недоступен>"; }
        }
    }

    public static void TrimIfLarge(int maxLines = 10_000)
    {
        try
        {
            var path = GetLogPath();
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            if (lines.Length <= maxLines) return;
            var kept = lines.Skip(lines.Length - maxLines).ToArray();
            lock (FileLock) File.WriteAllLines(path, kept, Encoding.UTF8);
        }
        catch { }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        var line = $"[{stamp}] [{level}] {message}";
        if (ex is not null)
        {
            line += $"\n  Exception: {ex.GetType().FullName}: {ex.Message}";
            line += $"\n  HResult: 0x{ex.HResult:X8}";
            line += $"\n  StackTrace:\n{ex.StackTrace}";
            if (ex.InnerException is not null) line += $"\n  InnerException:\n{ex.InnerException}";
        }
        System.Diagnostics.Debug.WriteLine(line);
        try { Append(GetLogPath(), line + Environment.NewLine); }
        catch
        {
            try
            {
                var fallback = BuildFallbackPath();
                lock (FileLock) _activeLogPath = fallback;
                Append(fallback, line + Environment.NewLine);
            }
            catch { }
        }
    }

    private static string GetLogPath()
    {
        lock (FileLock)
        {
            if (!string.IsNullOrWhiteSpace(_activeLogPath)) return _activeLogPath!;
            var primary = Path.Combine(AppContext.BaseDirectory, "app.log");
            _activeLogPath = CanWrite(primary) ? primary : BuildFallbackPath();
            return _activeLogPath;
        }
    }

    private static bool CanWrite(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir)) return false;
            Directory.CreateDirectory(dir);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            stream.Seek(0, SeekOrigin.End);
            return true;
        }
        catch { return false; }
    }

    private static string BuildFallbackPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RustedShpizhionStudio");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "app.log");
    }

    private static void Append(string path, string content)
    {
        lock (FileLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 16 * 1024, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
    }
}