namespace RustedShpizhionStudio.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private int _searchGeneration;

    private async Task SearchAsync(bool reset)
    {
        if (_db is null || IsScanning) return;
        await _searchGate.WaitAsync();
        try
        {
            var generation = Interlocked.Increment(ref _searchGeneration);
            if (reset)
            {
                _cardDeliveryCts?.Cancel();
                _cardDeliveryCts?.Dispose();
                _cardDeliveryCts = new CancellationTokenSource();
                _offset = 0;
                ClearItemsAndThumbnails();
                RestartThumbnailLoading();
            }
            StopBusyIndicator();
            StartBusyIndicator("Поиск", true);
            try
            {
                var selected = ModFilters.Where(x => x.IsSelected).Select(x => x.Id).ToList();
                var resourceTypes = ResourceTypeFilters.Where(x => x.IsSelected).Select(x => x.Code).ToList();
                var resourceSubtypes = ResourceSubtypeFilters.Where(x => x.IsSelected).Select(x => x.Code).ToList();
                var usageFilter = (ResourceUsageFilter)Math.Clamp(DisplayFilterIndex, 0, 2);
                var result = await Task.Run(() => _db.SearchImages(SearchQuery, resourceTypes, resourceSubtypes, Config.ImagePageSize, _offset, selected, Config.HideBroken, usageFilter, BrokenOnly));
                if (generation != _searchGeneration) return;
                var vms = result.Rows.Select(x =>
                {
                    var vm = new ImageItemViewModel(x);
                    vm.ApplyLayout(Config);
                    return vm;
                }).ToList();
                var deliveryToken = _cardDeliveryCts?.Token ?? CancellationToken.None;
                await AppendCardsBatchedAsync(vms, deliveryToken);
                if (generation != _searchGeneration)
                {
                    foreach (var vm in vms) vm.Dispose();
                    return;
                }
                HasMore = result.HasMore;
                _offset += result.Rows.Count;
                QueueVisibleThumbnails();
                ResultSummary = result.Rows.Count == 0 && reset ? "0 результатов" : $"Показано {Items.Count}{(HasMore ? "+" : "")}";
                OnPropertyChanged(nameof(ResultSummary));
                OnPropertyChanged(nameof(IsEmptyStateVisible));
                OnPropertyChanged(nameof(IsIndexEmptyStateVisible));
                OnPropertyChanged(nameof(IsNoResultsStateVisible));
                OnPropertyChanged(nameof(EndOfListText));
            }
            finally
            {
                StopBusyIndicator();
            }
        }
        finally
        {
            _searchGate.Release();
        }
    }

    private async Task AppendCardsBatchedAsync(IReadOnlyList<ImageItemViewModel> vms, CancellationToken ct)
    {
        if (vms.Count == 0) return;
        var batchSize = Config.CardLayout switch
        {
            "grid-2" => 16,
            "grid-3" => 24,
            "grid-4" => 28,
            "grid-5" => 36,
            "list" => 24,
            _ => 24
        };
        var delay = Config.ShowAnimations ? 24 : 0;

        for (var i = 0; i < vms.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, vms.Count - i);
            for (var j = 0; j < count; j++)
                Items.Add(vms[i + j]);
            OnPropertyChanged(nameof(IsEmptyStateVisible));
            OnPropertyChanged(nameof(IsNoResultsStateVisible));
            if (i + count < vms.Count)
                await Task.Delay(delay, ct);
        }
    }

    private async Task DebouncedSearchAsync(int delayMs = 70)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();
        var token = _searchDebounceCts.Token;
        try
        {
            await Task.Delay(delayMs, token);
            await SearchAsync(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Error("Ошибка фильтрации", ex); }
    }

    private void ClearItemsAndThumbnails()
    {
        _thumbnailCts?.Cancel();
        foreach (var vm in Items) vm.Dispose();
        Items.Clear();
        _thumbnailQueue.Clear();
        _thumbnailQueued.Clear();
    }

    private void RestartThumbnailLoading()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        _thumbnailQueue.Clear();
        _thumbnailQueued.Clear();
        _thumbnailTimer.Interval = TimeSpan.FromMilliseconds(20);
        _thumbnailTimer.Start();
    }

    // Карточки в ItemsRepeater виртуализированы, поэтому миниатюры не стоит
    // заранее читать для всего результата. На старте и при прокрутке в очередь
    // попадает только видимый диапазон с небольшим упреждением.
    private CancellationTokenSource? _scrollDebounceCts;

    private void CardScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta == Vector.Zero) return;
        QueueVisibleThumbnails();
        _scrollDebounceCts?.Cancel();
        _scrollDebounceCts?.Dispose();
        _scrollDebounceCts = new CancellationTokenSource();
        var token = _scrollDebounceCts.Token;
        _ = Task.Delay(120, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(QueueVisibleThumbnails);
        }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void QueueThumbnail(ImageItemViewModel vm)
    {
        if (vm.Thumbnail is not null || !_thumbnailQueued.Add(vm)) return;
        _thumbnailQueue.Enqueue(vm);
        if (!_thumbnailTimer.IsEnabled) _thumbnailTimer.Start();
    }

    private async void ThumbnailTimer_Tick(object? sender, EventArgs e)
    {
        if (_thumbnailBatchRunning) return;
        if (_thumbnailQueue.Count == 0)
        {
            _thumbnailTimer.Stop();
            return;
        }
        if (_thumbnailCts is null) return;
        var ct = _thumbnailCts.Token;
        const int batchSize = 10;
        var batch = new List<ImageItemViewModel>(batchSize);
        while (batch.Count < batchSize && _thumbnailQueue.Count > 0)
        {
            var vm = _thumbnailQueue.Dequeue();
            _thumbnailQueued.Remove(vm);
            if (vm.Thumbnail is null) batch.Add(vm);
        }
        if (batch.Count == 0) return;
        _thumbnailBatchRunning = true;
        try { await Task.WhenAll(batch.Select(vm => vm.LoadThumbnailAsync(Math.Max(160, (int)Math.Ceiling(vm.CardWidth)), ct))); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn($"Thumbnail batch failed: {ex.Message}"); }
        finally { _thumbnailBatchRunning = false; }
    }

    private void ApplyCardLayout()
    {
        if (CardRepeater.Layout is not Avalonia.Layout.UniformGridLayout layout)
        {
            foreach (var item in Items) item.ApplyLayout(Config);
            OnPropertyChanged(nameof(CardItemWidth));
            OnPropertyChanged(nameof(CardItemHeight));
            return;
        }

        var width = Math.Max(240, CardScrollViewer.Bounds.Width);
        var columns = Config.CardLayout switch
        {
            "grid-2" => 2,
            "grid-3" => 3,
            "grid-4" => 4,
            "grid-5" => 5,
            "list" => 1,
            _ => 3
        };
        const double gap = 10d;
        var itemWidth = columns == 1
            ? Math.Max(320, width)
            : Math.Max(120, (width - gap * (columns - 1)) / columns);

        layout.MinItemWidth = itemWidth;
        layout.MinItemHeight = CardItemHeight;
        foreach (var item in Items) item.ApplyLayout(Config, itemWidth);

        OnPropertyChanged(nameof(CardItemWidth));
        OnPropertyChanged(nameof(CardItemHeight));
        QueueVisibleThumbnails();
    }

    private void CardScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyCardLayout();
    }

    private int GetVisibleColumnCount() => Config.CardLayout switch
    {
        "grid-2" => 2,
        "grid-3" => 3,
        "grid-4" => 4,
        "grid-5" => 5,
        "list" => 1,
        _ => 3
    };

    private void QueueVisibleThumbnails()
    {
        if (Items.Count == 0) return;
        var columns = GetVisibleColumnCount();
        var rowHeight = Math.Max(120, CardItemHeight + 10);
        var viewportTop = Math.Max(0, CardScrollViewer.Offset.Y);
        var viewportBottom = viewportTop + Math.Max(1, CardScrollViewer.Bounds.Height);
        var firstRow = Math.Max(0, (int)Math.Floor(viewportTop / rowHeight) - 1);
        var lastRow = Math.Max(firstRow, (int)Math.Ceiling(viewportBottom / rowHeight) + 1);
        var firstIndex = firstRow * columns;
        var lastIndex = Math.Min(Items.Count - 1, ((lastRow + 1) * columns) - 1);
        for (var i = firstIndex; i <= lastIndex; i++) QueueThumbnail(Items[i]);
        if (!_thumbnailTimer.IsEnabled && _thumbnailQueue.Count > 0) _thumbnailTimer.Start();
    }

    private void ModFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModFilterItem.IsSelected) && _loaded && !IsScanning) _ = DebouncedSearchAsync();
    }

    private void Card_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ImageItemViewModel vm) _ = NavigateToDetailAsync(new DetailRoute(DetailRouteKind.Image, vm.Row.Id));
        e.Handled = true;
    }

    private void Thumbnail_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Image image && image.DataContext is ImageItemViewModel vm)
        {
            UiAnimationService.PrepareOpacityTransition(image, Config.ShowAnimations, UiAnimationService.ThumbnailRevealDuration);
            QueueThumbnail(vm);
        }
    }

    private void CardRoot_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;

        // Карточка всегда остаётся видимой. Раньше CardOpacity хранился во ViewModel
        // и мог застрять на 0 при рециклинге ItemsRepeater, оставляя пустую серую карточку.
        border.Opacity = 1;

        if (!Config.ShowAnimations)
        {
            UiAnimationService.PrepareOpacityTransition(border, false);
            return;
        }

        UiAnimationService.PrepareOpacityTransition(border, true, UiAnimationService.CardRevealDuration);
        border.Opacity = 0;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => border.Opacity = 1,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void ToggleFilters_Click(object? sender, RoutedEventArgs e)
    {
        IsFiltersPanelVisible = !IsFiltersPanelVisible;
    }

    private void ToggleDetailReferences_Click(object? sender, RoutedEventArgs e)
    {
        DetailReferencesExpanded = !DetailReferencesExpanded;
    }

    private async void Search_Click(object? sender, RoutedEventArgs e)
    {
        try { await SearchAsync(true); }
        catch (Exception ex) { AppLog.Error("Search_Click", ex); }
    }
    private async void Rescan_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await DialogWindow.AskAsync(this, "Пересканировать источник и заменить текущий индекс?\n\nТекущие результаты будут заменены результатами нового сканирования.", "Пересканировать", "Отмена");
            if (ok) await ScanAsync();
        }
        catch (Exception ex) { AppLog.Error("Rescan_Click", ex); }
    }
    private async void LoadMore_Click(object? sender, RoutedEventArgs e)
    {
        try { await SearchAsync(false); }
        catch (Exception ex) { AppLog.Error("LoadMore_Click", ex); }
    }

    private async void FilterChanged_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Content is string text && text.Contains("Скрывать", StringComparison.OrdinalIgnoreCase))
        {
            _config.HideBroken = cb.IsChecked == true;
            await _configService.SaveAsync(_config);
            await SearchAsync(true);
        }
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchAsync(true);
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        SearchQuery = string.Empty;
        _ = SearchAsync(true);
    }

    // ИСПРАВЛЕНО: убран Java-стиль Task.Run + InvokeAsync.
    // В C#/Avalonia event handler уже на UI-потоке, поэтому достаточно
    // сделать метод async void и напрямую await Task.Delay — после await
    // SynchronizationContext вернёт нас обратно на UI-поток автоматически.
    private async void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_loaded || IsScanning) return;
        await DebouncedSearchAsync(150);
    }
    private void AllResourceTypes_Click(object? sender, RoutedEventArgs e) { foreach (var x in ResourceTypeFilters) x.IsSelected = true; _ = DebouncedSearchAsync(); }
    private void NoneResourceTypes_Click(object? sender, RoutedEventArgs e) { foreach (var x in ResourceTypeFilters) x.IsSelected = false; _ = DebouncedSearchAsync(); }
    private void AllResourceSubtypes_Click(object? sender, RoutedEventArgs e) { foreach (var x in ResourceSubtypeFilters) x.IsSelected = true; _ = DebouncedSearchAsync(); }
    private void NoneResourceSubtypes_Click(object? sender, RoutedEventArgs e) { foreach (var x in ResourceSubtypeFilters) x.IsSelected = false; _ = DebouncedSearchAsync(); }

    private void AllMods_Click(object? sender, RoutedEventArgs e) { foreach (var m in ModFilters) m.IsSelected = true; _ = DebouncedSearchAsync(); }
    private void NoMods_Click(object? sender, RoutedEventArgs e) { foreach (var m in ModFilters) m.IsSelected = false; _ = DebouncedSearchAsync(); }
}
