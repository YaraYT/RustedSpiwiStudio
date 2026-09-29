namespace RustedShpizhionStudio;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new RustedShpizhionStudio.UI.MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
