namespace RustedShpizhionStudio;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CrashManager.Initialize(args);
        CrashManager.InstallGlobalHandlers();
        try
        {
            AppLog.TrimIfLarge();
            AppLog.Info($"=== Application starting {UpdateService.CurrentVersion} ===");
            CrashManager.SetStage("managed bootstrap");
            CrashManager.Preflight();
            CrashManager.SetStage("Avalonia startup");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            AppLog.Info("=== Application exiting normally ===");
        }
        catch (Exception ex)
        {
            CrashManager.HandleFatal("Program.Main", ex);
        }
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}