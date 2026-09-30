namespace RustedShpizhionStudio.Core;

internal static class CrashManager
{
    private const string RestartArgumentPrefix = "--crash-restart-count=";
    private const int MaxRestartAttempts = 2;
    private const uint MbOk = 0x00000000;
    private const uint MbRetryCancel = 0x00000005;
    private const uint MbIconError = 0x00000010;
    private const int IdRetry = 4;
    private static int _handling;
    private static int _restartCount;
    private static string _stage = "bootstrap";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Initialize(string[] args)
    {
        _restartCount = ParseRestartCount(args);
        AppLog.Info("=== CrashManager initialized ===");
        AppLog.Info($"Process: pid={Environment.ProcessId}, path={Environment.ProcessPath ?? "unknown"}");
        AppLog.Info($"Base: {AppContext.BaseDirectory}");
        AppLog.Info($"Current: {Environment.CurrentDirectory}");
        AppLog.Info($"OS: {Environment.OSVersion} / {RuntimeInformation.OSDescription}");
        AppLog.Info($"Runtime: {RuntimeInformation.FrameworkDescription}");
        AppLog.Info($"Arch: {RuntimeInformation.ProcessArchitecture}; 64-bit={Environment.Is64BitProcess}");
        AppLog.Info($"Restart chain: {_restartCount}/{MaxRestartAttempts}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { AppLog.Info("=== ProcessExit ==="); } catch { } };
    }

    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleFatal("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Необработанное исключение Task (fire-and-forget)", e.Exception);
            e.SetObserved();
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            HandleFatal("Avalonia.Dispatcher.UnhandledException", e.Exception);
        };
    }

    public static void SetStage(string stage)
    {
        _stage = string.IsNullOrWhiteSpace(stage) ? "unknown" : stage.Trim();
        AppLog.Info($"[checkpoint] {_stage}");
    }

    public static void MarkHealthy()
    {
        if (_restartCount > 0)
            AppLog.Info($"Healthy startup reached; reset crash chain {_restartCount} -> 0.");
        _restartCount = 0;
        SetStage("healthy startup");
    }

    public static void Preflight()
    {
        SetStage("preflight checks");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rusted Шпижион Студия предназначена только для Windows.");
        if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Для этой версии требуется Windows x64.");
        TryLoad("user32.dll", "системное окно Windows");
        TryLoad("winsqlite3.dll", "локальная SQLite-база индекса");
        AppLog.Info("Preflight checks passed.");
    }

    private static void TryLoad(string name, string purpose)
    {
        if (!NativeLibrary.TryLoad(name, out var handle))
            throw new DllNotFoundException($"Не удалось загрузить обязательную Windows native-библиотеку '{name}'. Назначение: {purpose}.");
        try { NativeLibrary.Free(handle); } catch (Exception ex) { AppLog.Warn($"NativeLibrary.Free('{name}') failed: {ex.Message}"); }
    }

    public static void HandleFatal(string stage, Exception? exception, string? additionalMessage = null)
    {
        if (Interlocked.Exchange(ref _handling, 1) != 0) return;
        try { SetStage(stage); } catch { }

        var crashId = $"CRASH-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0x10000):X4}";
        try { AppLog.Error($"КРИТИЧЕСКАЯ ОШИБКА [{crashId}]", exception); } catch { }

        var report = BuildReport(crashId, stage, exception, additionalMessage);
        string logPath;
        try { logPath = AppLog.TryWriteEmergency(report); } catch { logPath = "<журнал недоступен>"; }

        var canRestart = _restartCount < MaxRestartAttempts;
        if (ShowMessageBox(crashId, stage, exception, additionalMessage, logPath, canRestart) && canRestart)
        {
            var next = _restartCount + 1;
            if (TryRestart(next))
            {
                Environment.Exit(0);
                return;
            }
        }
        Environment.Exit(1);
    }

    private static bool ShowMessageBox(string id, string stage, Exception? exception, string? extra, string logPath, bool canRestart)
    {
        var type = exception?.GetType().FullName ?? "Неизвестный тип исключения";
        var message = string.IsNullOrWhiteSpace(exception?.Message) ? "Сообщение отсутствует." : exception!.Message;
        var text = new StringBuilder();
        text.AppendLine("Rusted Шпижион Студия столкнулась с критической ошибкой и не может продолжить работу.");
        text.AppendLine();
        text.AppendLine($"Этап: {stage}");
        text.AppendLine($"Исключение: {type}");
        text.AppendLine($"Сообщение: {message}");

        var hint = DependencyHint(exception);
        if (!string.IsNullOrWhiteSpace(hint)) { text.AppendLine(); text.AppendLine(hint); }
        if (!string.IsNullOrWhiteSpace(extra)) { text.AppendLine(); text.AppendLine(extra); }

        text.AppendLine();
        text.AppendLine($"ID ошибки: {id}");
        text.AppendLine($"Журнал: {logPath}");
        text.AppendLine();

        if (canRestart)
        {
            text.AppendLine($"Аварийных перезапусков: {_restartCount}/{MaxRestartAttempts}.");
            text.AppendLine("«Повторить» перезапустит программу. «Отмена» завершит её.");
        }
        else
        {
            text.AppendLine("Программа уже несколько раз подряд не смогла нормально запуститься.");
            text.AppendLine("Цепочка аварийных перезапусков остановлена.");
        }

        try
        {
            var flags = (canRestart ? MbRetryCancel : MbOk) | MbIconError;
            return canRestart && MessageBoxW(IntPtr.Zero, text.ToString(), "Rusted Шпижион Студия — критическая ошибка", flags) == IdRetry;
        }
        catch (Exception ex)
        {
            try { AppLog.Error("Не удалось показать системное окно критической ошибки", ex); } catch { }
            try { Console.Error.WriteLine(text); } catch { }
            return false;
        }
    }

    private static string DependencyHint(Exception? exception)
    {
        if (exception is not (DllNotFoundException or BadImageFormatException)) return string.Empty;
        var details = exception.ToString();
        if (details.Contains("winsqlite3", StringComparison.OrdinalIgnoreCase))
            return "Не удалось загрузить winsqlite3.dll. Требуется совместимая Windows 10/11 x64.";
        if (details.Contains("vcruntime", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("msvcp", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("ucrtbase", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("api-ms-win-crt", StringComparison.OrdinalIgnoreCase))
            return "Похоже, отсутствует или повреждена зависимость Microsoft Visual C++. Проверьте Microsoft Visual C++ Redistributable x64.";
        return "Обнаружена проблема с native-библиотекой. Полное имя библиотеки и трассировка сохранены в журнале.";
    }

    private static string BuildReport(string id, string stage, Exception? exception, string? extra)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Rusted Шпижион Студия crash report ===");
        sb.AppendLine($"Crash ID: {id}");
        sb.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
        sb.AppendLine($"Stage: {stage}");
        sb.AppendLine($"Version: {UpdateService.CurrentVersion}");
        sb.AppendLine($"PID: {Environment.ProcessId}");
        sb.AppendLine($"Process path: {Environment.ProcessPath ?? "unknown"}");
        sb.AppendLine($"Base directory: {AppContext.BaseDirectory}");
        sb.AppendLine($"Current directory: {Environment.CurrentDirectory}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"64-bit process: {Environment.Is64BitProcess}");
        sb.AppendLine($"Restart chain: {_restartCount}/{MaxRestartAttempts}");
        if (!string.IsNullOrWhiteSpace(extra)) { sb.AppendLine(); sb.AppendLine("Additional information:"); sb.AppendLine(extra); }
        sb.AppendLine(); sb.AppendLine("Exception:"); sb.AppendLine(exception?.ToString() ?? "<none>");
        return sb.ToString();
    }

    private static bool TryRestart(int nextAttempt)
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                AppLog.Error("Критический перезапуск невозможен: путь текущего EXE не найден.");
                return false;
            }

            var start = new ProcessStartInfo { FileName = path, UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                if (!arg.StartsWith(RestartArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(arg);

            start.ArgumentList.Add($"{RestartArgumentPrefix}{nextAttempt}");
            AppLog.Info($"Starting emergency restart {nextAttempt}/{MaxRestartAttempts}: {path}");
            Process.Start(start);
            return true;
        }
        catch (Exception ex) { AppLog.Error("Не удалось запустить аварийный перезапуск", ex); return false; }
    }

    private static int ParseRestartCount(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith(RestartArgumentPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(arg[RestartArgumentPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return Math.Clamp(value, 0, MaxRestartAttempts);
        }
        return 0;
    }
}