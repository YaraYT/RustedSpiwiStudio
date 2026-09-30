namespace RustedShpizhionStudio.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    // Последний известный результат проверки обновлений.
    private UpdateInfo? _lastUpdateInfo;

    private async Task InitializeAsync()
    {
        if (_loaded) return;
        _loaded = true;
        CrashManager.SetStage("MainWindow initialization");
        StartBusyIndicator("Загрузка индекса", false);
        StatusText = "Загрузка…";
        OnPropertyChanged(nameof(StatusText));

        // ── Логируем старт ──────────────────────────────────────────────────
        AppLog.TrimIfLarge(10_000);
        AppLog.Info($"=== Запуск Rusted Шпижион Студия {UpdateService.CurrentVersion} " +
                    $"на {Environment.OSVersion} ({RuntimeInformation.ProcessArchitecture}) ===");

        CrashManager.SetStage("loading configuration");
        var loaded = await _configService.LoadAsync();
        Config = loaded.Config;
        _configPath = loaded.ActivePath;
        ApplyCardLayout();
        InitializeResourceFilters();
        UiAnimationService.SetEnabled(Config.ShowAnimations);
        UiAnimationService.PrepareOpacityTransition(BusyOverlay, Config.ShowAnimations, UiAnimationService.ControlFadeDuration);
        ApplyWindowMode(Config.WindowMode);
        // Конструирование схемы и проверка совместимости индекса могут быть тяжёлыми.
        // Не делаем это на UI-потоке.
        CrashManager.SetStage("initializing SQLite index");
        (_db, _indexer) = await Task.Run(() =>
        {
            var db = new IndexDatabase(_configService.IndexPath);
            return (db, new IndexerService(db, _configService.DocsDbPath));
        });

        CrashManager.SetStage("loading indexed data");
        if (!string.IsNullOrWhiteSpace(Config.ModsRoot) && Directory.Exists(Config.ModsRoot))
        {
            await RefreshModsAsync();
            await RefreshStatsAsync();
            if (HasIndexedData) await SearchAsync(true);
        }
        else
        {
            IndexStateText = "Источник не настроен";
            OnPropertyChanged(nameof(IndexStateText));
            StopBusyIndicator();
            CrashManager.MarkHealthy();
            await ShowSettingsAsync();
            await CheckUpdatesOnStartupAsync();
            return;
        }

        StopBusyIndicator();
        CrashManager.MarkHealthy();
        StatusText = HasIndexedData ? "Готово" : "Индекс отсутствует";
        OnPropertyChanged(nameof(StatusText));
        await CheckUpdatesOnStartupAsync();
    }

    private async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            var info = await UpdateService.CheckAsync();
            _lastUpdateInfo = info;
            if (info?.IsNewer != true) return;
            AppLog.Info($"Доступна новая версия: {info.TagName}");
            if (!info.CanInstall)
            {
                AppLog.Warn($"Релиз {info.TagName} не имеет подходящего EXE с SHA-256. Автообновление пропущено.");
                return;
            }
            if (_updatePromptShown) return;
            _updatePromptShown = true;
            var prompt = new UpdatePromptWindow(info);
            var accepted = await prompt.ShowAndWaitAsync(this);
            if (accepted && info.CanInstall) CloseForUpdate();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Startup update prompt failed: {ex.Message}");
        }
    }

    // ИСПРАВЛЕНО: убрано ключевое слово async — методы не содержат await,
    // поэтому компилятор генерировал лишний state-machine (CS1998).
    // Когда здесь появятся реальные await (например, async DB), вернуть async.
    private async Task RefreshStatsAsync()
    {
        if (_db is null) return;
        var db = _db;
        var s = await Task.Run(db.GetStats);
        AppLog.Info($"Stats: mods={s.Mods} files={s.Files} images={s.Images} refs={s.References} missing={s.Missing}");
        HasIndexedData = s.HasIndexedData;
        _missingReferencesCount = s.Missing;
        StatsText = $"Модов: {s.Mods}\nФайлов: {s.Files}\nИзображений: {s.Images}\nКарт: {s.Maps}\nРабочих ссылок: {s.References}\nНеиспользуемых: {s.Unused}\nБитых: {s.Missing}";
        IndexStateText = s.HasIndexedData ? "Индекс готов к просмотру" : "Индекс отсутствует";
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(IndexStateText));
        OnPropertyChanged(nameof(IsBrokenFilterVisible));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(IsIndexEmptyStateVisible));
        OnPropertyChanged(nameof(IsNoResultsStateVisible));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    private async Task RefreshModsAsync()
    {
        ModFilters.Clear();
        if (_db is null) return;
        var db = _db;
        var mods = await Task.Run(db.GetMods);
        foreach (var m in mods)
        {
            var item = new ModFilterItem(m.Id, m.Name, false);
            item.PropertyChanged += ModFilterChanged;
            ModFilters.Add(item);
        }
    }
    // ── Кнопка версии → окно «Что нового» ──────────────────────────────────
    private async void Version_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var win = new ChangelogWindow(_lastUpdateInfo);
            await win.ShowDialog(this);
        }
        catch (Exception ex) { AppLog.Error("Version_Click", ex); }
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var info = await UpdateService.CheckAsync();
            _lastUpdateInfo = info;
            if (info?.IsNewer == true)
            {
                if (!info.CanInstall)
                {
                    await DialogWindow.ShowAsync(this, $"Доступна версия {info.TagName}, но GitHub не предоставил подходящий EXE с SHA-256. Обновление пропущено.");
                    return;
                }
                var prompt = new UpdatePromptWindow(info);
                var accepted = await prompt.ShowAndWaitAsync(this);
                if (accepted && info.CanInstall) CloseForUpdate();
                return;
            }
            await DialogWindow.ShowAsync(this, "Новых опубликованных версий не найдено.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Manual update check failed: {ex.Message}");
        }
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (IsScanning)
            {
                await DialogWindow.ShowAsync(this, "Настройки нельзя изменять во время индексации. Остановите индексацию или дождитесь её завершения.");
                return;
            }
            await ShowSettingsAsync();
        }
        catch (Exception ex) { AppLog.Error("Settings_Click", ex); }
    }

    private async Task DeleteIndexAsync()
    {
        try
        {
            ClearItemsAndThumbnails();
            _thumbnailTimer.Stop();
            _db = null;
            _indexer = null;
            await Task.Run(() => IndexDatabase.DeletePortableDatabase(_configService.IndexPath));
            _db = new IndexDatabase(_configService.IndexPath);
            _indexer = new IndexerService(_db, _configService.DocsDbPath);
            HasIndexedData = false;
            _offset = 0;
            ResultSummary = string.Empty;
            await RefreshModsAsync();
            await RefreshStatsAsync();
            OnPropertyChanged(nameof(ResultSummary));
            AppLog.Info("Portable index database deleted by user.");
            StatusText = "База индекса удалена";
            OnPropertyChanged(nameof(StatusText));
        }
        catch (Exception ex)
        {
            AppLog.Error("Delete index database failed", ex);
            await DialogWindow.ShowAsync(this, $"Не удалось удалить базу индекса:\n\n{ex.Message}", "Закрыть");
        }
    }

    private async Task ShowSettingsAsync()
    {
        var dialog = new SettingsWindow(Config);
        await dialog.ShowDialog(this);
        if (dialog.DeleteDatabaseRequested)
        {
            await DeleteIndexAsync();
            return;
        }
        if (dialog.Result is null) return;
        Config = dialog.Result;
        _configPath = await _configService.SaveAsync(Config);
        UiAnimationService.SetEnabled(Config.ShowAnimations);
        UiAnimationService.PrepareOpacityTransition(BusyOverlay, Config.ShowAnimations, UiAnimationService.ControlFadeDuration);
        ApplyWindowMode(Config.WindowMode);
        ApplyCardLayout();
        await RefreshModsAsync();
        await RefreshStatsAsync();
        StatusText = HasIndexedData ? "Готово" : "Индекс отсутствует";
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FullscreenButtonText));
        if (HasIndexedData) await SearchAsync(true);
    }
}
