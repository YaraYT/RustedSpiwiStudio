namespace RustedShpizhionStudio;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.TrimIfLarge();
        AppLog.Info($"=== Application starting (args: {string.Join(' ', args)}) ===");

        // Перехватываем все необработанные исключения, чтобы они попали в portable app.log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Необработанное исключение AppDomain", e.ExceptionObject as Exception);

        // Перехватываем исключения из fire-and-forget Task, которые были забыты
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Необработанное исключение Task (fire-and-forget)", e.Exception);
            e.SetObserved(); // не крашим процесс
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // В том числе сюда попадает ошибка AvaloniaXamlLoader.Load(), если
            // опубликованная сборка потеряла AXAML-ресурс.
            AppLog.Error("Критическая ошибка при запуске Avalonia", ex);
            throw;
        }

        AppLog.Info("=== Application exiting normally ===");
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
