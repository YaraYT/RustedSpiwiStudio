namespace RustedShpizhionStudio.Services;

public static class DesktopShortcutService
{
    public readonly record struct ShortcutResult(bool Success, string Path, string Error);

    public static async Task<ShortcutResult> CreateAsync()
    {
        if (!OperatingSystem.IsWindows())
            return new(false, string.Empty, "Создание ярлыка поддерживается только в Windows.");

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            return new(false, string.Empty, "Windows не вернула путь к рабочему столу.");

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return new(false, string.Empty, "Не удалось определить путь к запущенному EXE.");

        Directory.CreateDirectory(desktop);
        var shortcutPath = Path.Combine(desktop, "Rusted Шпижион Студия.lnk");
        var workingDirectory = AppContext.BaseDirectory;
        var script = $@"
$ws = New-Object -ComObject WScript.Shell
$shortcut = $ws.CreateShortcut({PowerShellString(shortcutPath)})
$shortcut.TargetPath = {PowerShellString(executable)}
$shortcut.WorkingDirectory = {PowerShellString(workingDirectory)}
$shortcut.Description = 'Rusted Шпижион Студия'
$shortcut.IconLocation = {PowerShellString(executable + ",0")}
$shortcut.Save()
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(psi);
        if (process is null)
            return new(false, string.Empty, "Не удалось запустить Windows PowerShell.");

        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(shortcutPath))
            return new(false, shortcutPath, string.IsNullOrWhiteSpace(error) ? "Windows PowerShell не создал файл ярлыка." : error.Trim());

        AppLog.Info($"Desktop shortcut created: {shortcutPath}");
        return new(true, shortcutPath, string.Empty);
    }

    private static string PowerShellString(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
