namespace RustedShpizhionStudio.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public string VersionText => UpdateService.CurrentVersion;
    public string WindowTitle => $"Rusted Шпижион Студия {VersionText}";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        UiAnimationService.PrepareWindow(this);
        KeyDown += MainWindow_KeyDown;
        _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _busyTimer.Tick += (_, _) => UpdateBusyIndicator();
        _thumbnailTimer = new DispatcherTimer();
        _thumbnailTimer.Tick += ThumbnailTimer_Tick;
        Opened += async (_, _) => await InitializeAsync();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private bool _allowClose;

    internal void CloseForUpdate()
    {
        _allowClose = true;
        _scanCts?.Cancel();
        Close();
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        var text = IsScanning
            ? "Сканирование ещё выполняется. Выйти из программы? Текущий процесс будет остановлен."
            : "Выйти из Rusted Шпижион Студия?";
        var ok = await DialogWindow.AskAsync(this, text, "Выйти", "Остаться");
        if (!ok) return;
        _allowClose = true;
        _scanCts?.Cancel();
        Close();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        try { _scanCts?.Cancel(); _scanCts?.Dispose(); } catch { }
        try { _searchDebounceCts?.Cancel(); _searchDebounceCts?.Dispose(); } catch { }
        try { _detailCts?.Cancel(); _detailCts?.Dispose(); } catch { }
        try { _thumbnailCts?.Cancel(); _thumbnailCts?.Dispose(); } catch { }
        try { _busyVisibilityCts?.Cancel(); _busyVisibilityCts?.Dispose(); } catch { }
        try { _cardDeliveryCts?.Cancel(); _cardDeliveryCts?.Dispose(); } catch { }
        try { _scrollDebounceCts?.Cancel(); _scrollDebounceCts?.Dispose(); } catch { }
        try { _predictionCts?.Cancel(); _predictionCts?.Dispose(); } catch { }
        try { _detailPredictionCts?.Cancel(); _detailPredictionCts?.Dispose(); } catch { }
        try { foreach (var vm in Items) vm.Dispose(); } catch { }
        try { DetailBitmap = null; } catch { }
        try { foreach (var bitmap in _predictedPreviewCache.Values) bitmap.Dispose(); _predictedPreviewCache.Clear(); } catch { }
        try { _busyTimer.Stop(); _thumbnailTimer.Stop(); } catch { }
    }
}
