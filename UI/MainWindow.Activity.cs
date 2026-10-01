namespace RustedShpizhionStudio.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private void StartBusyIndicator(string baseText, bool updateResultSummary)
    {
        _busyVisibilityCts?.Cancel();
        _busyVisibilityCts?.Dispose();
        _busyVisibilityCts = new CancellationTokenSource();
        var token = _busyVisibilityCts.Token;

        _busyBaseText = baseText;
        _busyTick = 0;
        _busyIndicatorRunning = true;
        BusyOverlayText = baseText;
        OnPropertyChanged(nameof(BusyOverlayText));
        BusyOverlayVisible = false;
        if (updateResultSummary) ResultSummary = baseText;
        _busyTimer.Start();
        UpdateBusyIndicator();

        _ = ShowBusyOverlayDelayedAsync(baseText, token);
    }

    private async Task ShowBusyOverlayDelayedAsync(string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(140, token);
            if (token.IsCancellationRequested || !_busyIndicatorRunning) return;
            BusyOverlayText = text;
            OnPropertyChanged(nameof(BusyOverlayText));
            BusyOverlayVisible = true;
        }
        catch (OperationCanceledException) { }
    }

    private void SetBusyIndicatorPhase(string text)
    {
        _busyBaseText = text;
        BusyOverlayText = text;
        OnPropertyChanged(nameof(BusyOverlayText));
        if (BusyOverlayVisible)
        {
            ResultSummary = text;
            OnPropertyChanged(nameof(ResultSummary));
        }
    }

    private void StopBusyIndicator()
    {
        _busyIndicatorRunning = false;
        _busyVisibilityCts?.Cancel();
        _busyVisibilityCts?.Dispose();
        _busyVisibilityCts = null;
        _busyTimer.Stop();
        BusyOverlayVisible = false;
        ActivityIndicatorText = string.Empty;
        OnPropertyChanged(nameof(ActivityIndicatorText));
    }

    private void UpdateBusyIndicator()
    {
        if (!_busyIndicatorRunning) return;
        var text = Config.ShowAnimations
            ? new[] { "◐", "◓", "◑", "◒" }[_busyTick % 4]
            : _busyBaseText + new string('.', _busyTick % 3 + 1);
        ActivityIndicatorText = text;
        OnPropertyChanged(nameof(ActivityIndicatorText));
        if (_busyBaseText == "Поиск" && BusyOverlayVisible)
        {
            ResultSummary = text;
            OnPropertyChanged(nameof(ResultSummary));
        }
        _busyTick++;
    }

    private void OnPropertyChangedAllProgress()
    {
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(ProgressBarVisible));
        OnPropertyChanged(nameof(ActivityIndicatorText));
    }

    private void OnPropertyChangedAllDetails()
    {
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailSubtitle));
        OnPropertyChanged(nameof(DetailTypeText));
        OnPropertyChanged(nameof(DetailUsageText));
        OnPropertyChanged(nameof(DetailBitmap));
        OnPropertyChanged(nameof(DetailVisible));
        OnPropertyChanged(nameof(DetailPreviewHeight));
        OnPropertyChanged(nameof(DetailHasMissingReferences));
        OnPropertyChanged(nameof(DetailHasReferenceList));
        OnPropertyChanged(nameof(DetailReferencesToggleText));
        OnPropertyChanged(nameof(DetailCanOpenFolder));
        OnPropertyChanged(nameof(DetailCanCopyPath));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }
}
