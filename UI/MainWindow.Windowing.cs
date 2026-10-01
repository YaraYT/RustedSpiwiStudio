namespace RustedShpizhionStudio.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DetailVisible)
        {
            _ = CloseDetailAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            ApplyWindowMode(Config.WindowMode == "fullscreen" ? "windowed" : Config.WindowMode);
            e.Handled = true;
        }
    }

    private void ToggleFullscreen_Click(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
            ApplyWindowMode(Config.WindowMode == "fullscreen" ? "windowed" : Config.WindowMode);
        else
        {
            WindowDecorations = WindowDecorations.None;
            WindowState = WindowState.FullScreen;
            OnPropertyChanged(nameof(FullscreenButtonText));
        }
    }

    private void ApplyWindowMode(string mode)
    {
        mode = mode is "windowed" or "borderless" or "fullscreen" ? mode : "windowed";
        switch (mode)
        {
            case "fullscreen":
                WindowDecorations = WindowDecorations.None;
                WindowState = WindowState.FullScreen;
                break;
            case "borderless":
                WindowState = WindowState.Maximized;
                WindowDecorations = WindowDecorations.None;
                break;
            default:
                WindowDecorations = WindowDecorations.Full;
                WindowState = WindowState.Maximized;
                break;
        }
        OnPropertyChanged(nameof(FullscreenButtonText));
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();
    private static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Warn($"OpenFolder failed for '{path}': {ex.Message}"); }
    }
}
