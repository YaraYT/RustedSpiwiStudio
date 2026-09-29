namespace RustedShpizhionStudio.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private async void Scan_Click(object? sender, RoutedEventArgs e)
    {
        try { await ScanAsync(); }
        catch (Exception ex) { AppLog.Error("Scan_Click", ex); }
    }

    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(Config.ModsRoot)) { await ShowSettingsAsync(); return; }
        var indexer = _indexer;
        if (indexer is null) return;
        AppLog.Info($"Scan started: ModsRoot={Config.ModsRoot}");
        IsScanning = true;
        ProgressValue = 0;
        ModProgressValue = 0;
        ProgressText = "Подготовка…";
        ModProgressText = string.Empty;
        StatusText = "Сканирование…";
        _currentModIndex = 0;
        _totalMods = 0;
        StartBusyIndicator("Сканирование", false);
        OnPropertyChangedAllProgress();
        _scanCts = new CancellationTokenSource();
        try
        {
            if (Config.DangerousMode)
                AppLog.Warn("Dangerous indexing mode enabled: no CPU headroom is reserved for Windows/other applications.");
            var summary = await Task.Run(() => indexer.ScanAsync(new IndexerOptions
            {
                ModsRoot = Config.ModsRoot,
                CacheRoot = _configService.CachePath,
                IncludeRwmod = Config.RwmodExtraction,
                EnableParallelism = Config.ParallelIndexing,
                IndexParallelism = Config.IndexParallelism,
                ComputeParallelism = Config.ComputeParallelism,
                DangerousMode = Config.DangerousMode,
                CancellationToken = _scanCts.Token,
                Progress = p => Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleProgress(p)),
                AskRwmodAsync = paths => AskAsync($"Найдено .rwmod: {paths.Count}.\n\nРазрешить распаковку и индексацию всех новых архивов?", "Разрешить", "Пропустить"),
                AskCancelAsync = summary => AskAsync($"Сканирование отменено.\nОбработано модов: {summary.Mods}, файлов: {summary.Files}, изображений: {summary.Images}.\n\nСохранить частичный результат?", "Сохранить", "Откатить")
            }));
            if (!summary.Cancelled)
            {
                AppLog.Info($"Scan complete: mods={summary.Mods} files={summary.Files} images={summary.Images} refs={summary.References} missing={summary.Missing}");
                Config.LastScanAt = DateTimeOffset.Now.ToString("O");
                await _configService.SaveAsync(Config);
                StatusText = "Сканирование завершено";
                ProgressValue = 100;
                ModProgressValue = 100;
                ProgressText = "100%";
            }
            else
            {
                AppLog.Info($"Scan cancelled: saved={summary.Saved} mods={summary.Mods}");
                StatusText = summary.Saved ? "Частичный результат сохранён" : "Изменения сканирования откатаны";
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Scan failed", ex);
            await DialogWindow.ShowAsync(this, $"Сканирование завершилось ошибкой:\n\n{ex.Message}", "Закрыть");
            StatusText = "Ошибка сканирования";
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            StopBusyIndicator();
            IsScanning = false;
            await RefreshModsAsync();
            await RefreshStatsAsync();
            OnPropertyChangedAllProgress();
            if (HasIndexedData) await SearchAsync(true);
        }
    }

    private async Task<bool> AskAsync(string text, string yes, string no)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) return await DialogWindow.AskAsync(this, text, yes, no);
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => DialogWindow.AskAsync(this, text, yes, no));
    }

    private void HandleProgress(ScanProgress p)
    {
        if (p.Kind == "mods-count")
        {
            _totalMods = Math.Max(1, p.Total);
            ActivityIndicatorText = Config.ShowAnimations ? "◐" : "Сканирование…";
        }
        else if (p.Kind == "mod-start")
        {
            _currentModIndex = p.Current;
            _totalMods = Math.Max(1, p.Total);
            ModProgressValue = 0;
            ModProgressText = $"Мод {_currentModIndex}/{_totalMods}: {p.Message}";
            ProgressValue = Math.Max(0, (_currentModIndex - 1) * 100d / _totalMods);
        }
        else if (p.Kind is "file" or "compute" or "image")
        {
            var phase = p.Total > 0 ? Math.Clamp(p.Current * 100d / p.Total, 0, 100) : 0;
            ModProgressValue = phase;
            ModProgressText = $"Мод {_currentModIndex}/{Math.Max(1, _totalMods)} · {phase:0}% · {p.Message}";
            ProgressValue = Math.Clamp(((_currentModIndex - 1) + phase / 100d) * 100d / Math.Max(1, _totalMods), 0, 100);
        }
        else if (p.Kind == "complete")
        {
            ProgressValue = 100;
            ModProgressValue = 100;
            ModProgressText = "Все модификации обработаны";
        }

        StatusText = p.Message;
        ProgressText = Config.ShowAnimations ? p.Message : $"{ProgressValue:0}%";
        OnPropertyChangedAllProgress();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        StatusText = "Запрос отмены принят…";
        OnPropertyChanged(nameof(StatusText));
    }
}
