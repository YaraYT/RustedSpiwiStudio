using Avalonia.Controls.Primitives;

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
                _predictionCts?.Cancel();
                _predictionCts?.Dispose();
                _predictionCts = null;
            }
            StopBusyIndicator();
            StartBusyIndicator("Поиск", true);
            try
            {
                var selected = ModFilters.Where(x => x.IsSelected).Select(x => x.Id).ToList();
                var resourceTypes = ResourceTypeFilters.Where(x => x.IsSelected).Select(x => x.Code).ToList();
                var resourceSubtypes = ResourceSubtypeFilters.Where(x => x.IsSelected).Select(x => x.Code).ToList();
                var usageFilter = (ResourceUsageFilter)Math.Clamp(DisplayFilterIndex, 0, 2);
                var deliveryToken = _cardDeliveryCts?.Token ?? CancellationToken.None;
                var result = await _db.SearchImagesAsync(SearchQuery, resourceTypes, resourceSubtypes, Config.ImagePageSize, _offset, selected, Config.HideBroken, usageFilter, BrokenOnly, deliveryToken);
                if (generation != _searchGeneration) return;
                var vms = result.Rows.Select(x =>
                {
                    var vm = new ImageItemViewModel(x);
                    vm.ApplyLayout(Config);
                    return vm;
                }).ToList();
                await AppendCardsBatchedAsync(vms, deliveryToken);
                if (generation != _searchGeneration)
                {
                    foreach (var vm in vms) vm.Dispose();
                    return;
                }
                HasMore = result.HasMore;
                _offset += result.Rows.Count;

                if (reset && !Config.PredictionEnabled && Config.AggressiveCardLoading && HasMore && Items.Count > 0)
                {
                    SetBusyIndicatorPhase("Подготовка карточек");
                    await PrepareAggressiveCardsAsync(generation, resourceTypes, resourceSubtypes, selected, usageFilter, deliveryToken);
                }

                if (!Config.PredictionEnabled && Config.AggressivePhotoLoading && Items.Count > 0)
                {
                    SetBusyIndicatorPhase("Подготовка изображений");
                    await PrepareAggressiveThumbnailsAsync(generation, deliveryToken);
                }

                ReleaseOffscreenThumbnails();
                QueueVisibleThumbnails();
                SchedulePrediction();
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
        var delay = Config.ShowAnimations && !Config.AggressivePhotoLoading ? 24 : 0;

        for (var i = 0; i < vms.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, vms.Count - i);
            for (var j = 0; j < count; j++)
                Items.Add(vms[i + j]);
            OnPropertyChanged(nameof(IsEmptyStateVisible));
            OnPropertyChanged(nameof(IsNoResultsStateVisible));
            if (i + count < vms.Count)
            {
                if (delay > 0) await Task.Delay(delay, ct);
                else await Task.Yield();
            }
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
        _predictionQueue.Clear();
        _predictionQueued.Clear();
        _predictionCts?.Cancel();
        foreach (var bitmap in _predictedPreviewCache.Values) bitmap.Dispose();
        _predictedPreviewCache.Clear();
        _detailPredictionCts?.Cancel();
    }

    private void RestartThumbnailLoading()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        _thumbnailQueue.Clear();
        _thumbnailQueued.Clear();
        _predictionQueue.Clear();
        _predictionQueued.Clear();
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
        if (Math.Abs(e.OffsetDelta.Y) > 0.1) _scrollDirection = e.OffsetDelta.Y >= 0 ? 1 : -1;
        if (IsDetailPanelExpanded && Math.Abs(e.OffsetDelta.Y) > 0.1) CollapseDetailPanel();
        QueueVisibleThumbnails();
        _scrollDebounceCts?.Cancel();
        _scrollDebounceCts?.Dispose();
        _scrollDebounceCts = new CancellationTokenSource();
        var token = _scrollDebounceCts.Token;
        _ = Task.Delay(120, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ReleaseOffscreenThumbnails();
                QueueVisibleThumbnails();
                SchedulePrediction();
            });
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
        while (batch.Count < batchSize && _thumbnailQueue.Count == 0 && _predictionQueue.Count > 0)
        {
            var vm = _predictionQueue.Dequeue();
            _predictionQueued.Remove(vm);
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
        var prefetchRows = Config.PredictionEnabled ? 1 : (Config.AggressivePhotoLoading ? 6 : 3);
        var firstRow = Math.Max(0, (int)Math.Floor(viewportTop / rowHeight) - prefetchRows);
        var lastRow = Math.Max(firstRow, (int)Math.Ceiling(viewportBottom / rowHeight) + prefetchRows);
        var firstIndex = firstRow * columns;
        var lastIndex = Math.Min(Items.Count - 1, ((lastRow + 1) * columns) - 1);
        for (var i = firstIndex; i <= lastIndex; i++) QueueThumbnail(Items[i]);
        if (!_thumbnailTimer.IsEnabled && _thumbnailQueue.Count > 0) _thumbnailTimer.Start();
    }

    private void ReleaseOffscreenThumbnails()
    {
        if (Config.AggressivePhotoLoading || Items.Count == 0) return;
        var columns = GetVisibleColumnCount();
        var rowHeight = Math.Max(120, CardItemHeight + 10);
        var viewportTop = Math.Max(0, CardScrollViewer.Offset.Y);
        var viewportBottom = viewportTop + Math.Max(1, CardScrollViewer.Bounds.Height);
        var keepRows = Config.PredictionEnabled ? Config.PredictionDepth + 1 : 4;
        var firstKeepRow = Math.Max(0, (int)Math.Floor(viewportTop / rowHeight) - keepRows);
        var lastKeepRow = Math.Max(firstKeepRow, (int)Math.Ceiling(viewportBottom / rowHeight) + keepRows);
        var firstKeep = firstKeepRow * columns;
        var lastKeep = Math.Min(Items.Count - 1, ((lastKeepRow + 1) * columns) - 1);

        for (var i = 0; i < Items.Count; i++)
        {
            if (i >= firstKeep && i <= lastKeep) continue;
            Items[i].UnloadThumbnail();
        }
    }

    private void SchedulePrediction()
    {
        if (!Config.PredictionEnabled || Items.Count == 0) return;
        _predictionCts?.Cancel();
        _predictionCts?.Dispose();
        _predictionCts = new CancellationTokenSource();
        var token = _predictionCts.Token;
        var delay = Config.BackgroundPrediction ? 140 : 15;
        _ = PredictAheadAsync(token, delay);
    }

    private async Task PredictAheadAsync(CancellationToken ct, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                QueuePredictedThumbnails();
                ScheduleDetailPrediction();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn($"Prediction scheduling failed: {ex.Message}"); }
    }

    private void QueuePredictedThumbnail(ImageItemViewModel vm)
    {
        if (vm.Thumbnail is not null || _thumbnailQueued.Contains(vm) || !_predictionQueued.Add(vm)) return;
        _predictionQueue.Enqueue(vm);
        if (!_thumbnailTimer.IsEnabled) _thumbnailTimer.Start();
    }

    private void QueuePredictedThumbnails()
    {
        if (!Config.PredictionEnabled || Items.Count == 0) return;
        var depth = Math.Clamp(Config.PredictionDepth, 1, 5);
        var columns = GetVisibleColumnCount();
        var rowHeight = Math.Max(120, CardItemHeight + 10);
        var viewportTop = Math.Max(0, CardScrollViewer.Offset.Y);
        var viewportBottom = viewportTop + Math.Max(1, CardScrollViewer.Bounds.Height);
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(viewportTop / rowHeight));
        var lastVisibleRow = Math.Max(firstVisibleRow, (int)Math.Ceiling(viewportBottom / rowHeight));
        var firstPredictedRow = _scrollDirection >= 0 ? lastVisibleRow + 1 : Math.Max(0, firstVisibleRow - depth);
        var lastPredictedRow = _scrollDirection >= 0 ? lastVisibleRow + depth : firstVisibleRow - 1;

        if (_scrollDirection >= 0)
        {
            for (var row = firstPredictedRow; row <= lastPredictedRow; row++)
            {
                var start = row * columns;
                var end = Math.Min(Items.Count - 1, start + columns - 1);
                if (start >= Items.Count) break;
                for (var i = start; i <= end; i++) QueuePredictedThumbnail(Items[i]);
            }
        }
        else
        {
            for (var row = Math.Max(0, lastPredictedRow); row >= firstPredictedRow; row--)
            {
                var start = row * columns;
                var end = Math.Min(Items.Count - 1, start + columns - 1);
                if (start >= Items.Count) continue;
                for (var i = end; i >= start; i--) QueuePredictedThumbnail(Items[i]);
            }
        }
    }

    private void ScheduleDetailPrediction(long? preferredId = null)
    {
        if (!Config.PredictionEnabled || Items.Count == 0) return;
        if (preferredId is null && _currentRoute.Kind == DetailRouteKind.Image) preferredId = _currentRoute.Id;
        if (preferredId is null) return;

        var currentIndex = Items.IndexOf(Items.FirstOrDefault(x => x.Row.Id == preferredId.Value));
        if (currentIndex < 0) return;
        _detailPredictionCts?.Cancel();
        _detailPredictionCts?.Dispose();
        _detailPredictionCts = new CancellationTokenSource();
        var token = _detailPredictionCts.Token;
        var depth = Math.Clamp(Config.PredictionDepth, 1, 5);
        var direction = _scrollDirection >= 0 ? 1 : -1;
        var candidates = new List<ImageRow>(depth * 2);
        for (var step = 1; step <= depth; step++)
        {
            var index = currentIndex + direction * step;
            if (index >= 0 && index < Items.Count) candidates.Add(Items[index].Row);
        }
        if (currentIndex - direction >= 0 && currentIndex - direction < Items.Count && candidates.Count < depth * 2)
            candidates.Add(Items[currentIndex - direction].Row);
        _ = PredictDetailPreviewsAsync(candidates, token, Config.BackgroundPrediction ? 180 : 20);
    }

    private async Task PredictDetailPreviewsAsync(IReadOnlyList<ImageRow> rows, CancellationToken ct, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var alreadyCached = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => _predictedPreviewCache.ContainsKey(row.Id),
                    Avalonia.Threading.DispatcherPriority.Background);
                if (alreadyCached) continue;

                var bitmap = await ImageItemViewModel.LoadBitmapAsync(row, 900, ct).ConfigureAwait(false);
                if (bitmap is null) continue;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) bitmap.Dispose();
                    else StorePredictedPreview(row.Id, bitmap);
                }, Avalonia.Threading.DispatcherPriority.Background);
                if (ct.IsCancellationRequested) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn($"Detail prediction failed: {ex.Message}"); }
    }

    private void StorePredictedPreview(long id, Bitmap bitmap)
    {
        if (_predictedPreviewCache.TryGetValue(id, out var existing))
        {
            existing.Dispose();
            _predictedPreviewCache[id] = bitmap;
            return;
        }
        _predictedPreviewCache[id] = bitmap;
        while (_predictedPreviewCache.Count > Math.Clamp(Config.PredictionDepth, 1, 5) * 2)
        {
            var first = _predictedPreviewCache.Keys.First();
            if (_predictedPreviewCache.Remove(first, out var removed)) removed.Dispose();
        }
    }

    private async Task PrepareAggressiveThumbnailsAsync(int generation, CancellationToken ct)
    {
        if (!Config.AggressivePhotoLoading || Items.Count == 0) return;

        var budgetBytes = (long)Math.Clamp(Config.PhotoMemoryLimitMb, 1024, 2048) * 1024L * 1024L;
        var safeBudget = (long)(budgetBytes * 0.85);
        var reservedBytes = Items
            .Where(x => x.Thumbnail is not null)
            .Sum(x => x.EstimateThumbnailBytes(Math.Max(160, (int)Math.Ceiling(x.CardWidth))));
        var prepared = Items.Count(x => x.Thumbnail is not null);
        var considered = prepared;
        const int batchSize = 12;
        var batch = new List<ImageItemViewModel>(batchSize);

        for (var index = 0; index < Items.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (generation != _searchGeneration) return;

            var vm = Items[index];
            var width = Math.Max(160, (int)Math.Ceiling(vm.CardWidth));
            if (vm.Thumbnail is null)
            {
                var estimate = vm.EstimateThumbnailBytes(width);
                if (reservedBytes + estimate > safeBudget)
                {
                    AppLog.Info($"Aggressive photo preload stopped at {prepared} images: estimated memory budget reached ({Config.PhotoMemoryLimitMb} MiB).");
                    break;
                }
                reservedBytes += estimate;
                batch.Add(vm);
            }
            considered++;

            if (batch.Count < batchSize && index != Items.Count - 1) continue;
            if (batch.Count > 0)
            {
                await Task.WhenAll(batch.Select(x => x.LoadThumbnailAsync(Math.Max(160, (int)Math.Ceiling(x.CardWidth)), ct)));
                prepared += batch.Count(x => x.Thumbnail is not null);
                batch.Clear();
            }

            BusyOverlayText = $"Подготовка изображений · {prepared}/{Math.Max(considered, 1)}";
            OnPropertyChanged(nameof(BusyOverlayText));
        }

        if (batch.Count > 0)
        {
            await Task.WhenAll(batch.Select(x => x.LoadThumbnailAsync(Math.Max(160, (int)Math.Ceiling(x.CardWidth)), ct)));
            prepared += batch.Count(x => x.Thumbnail is not null);
        }

        BusyOverlayText = prepared >= Items.Count
            ? $"Изображения подготовлены · {prepared}"
            : $"Подготовлено изображений · {prepared} из {Items.Count}";
        OnPropertyChanged(nameof(BusyOverlayText));
    }

    private async Task PrepareAggressiveCardsAsync(
        int generation,
        IReadOnlyList<string> resourceTypes,
        IReadOnlyList<string> resourceSubtypes,
        IReadOnlyList<long> modIds,
        ResourceUsageFilter usageFilter,
        CancellationToken ct)
    {
        if (!Config.AggressiveCardLoading || _db is null || !HasMore) return;

        var budgetBytes = (long)Math.Clamp(Config.CardMemoryLimitMb, 512, 1024) * 1024L * 1024L;
        var softBudget = (long)(budgetBytes * 0.88);
        var reservedBytes = Items.Sum(x => x.EstimateCardMemoryBytes());
        var loaded = Items.Count;
        var page = 0;

        while (HasMore)
        {
            ct.ThrowIfCancellationRequested();
            if (generation != _searchGeneration) return;

            var result = await _db.SearchImagesAsync(
                SearchQuery, resourceTypes, resourceSubtypes, Config.ImagePageSize, _offset,
                modIds, Config.HideBroken, usageFilter, BrokenOnly, ct);

            if (generation != _searchGeneration) return;
            if (result.Rows.Count == 0)
            {
                HasMore = false;
                break;
            }

            var accepted = new List<ImageItemViewModel>(result.Rows.Count);
            foreach (var row in result.Rows)
            {
                var vm = new ImageItemViewModel(row);
                vm.ApplyLayout(Config);
                var estimate = vm.EstimateCardMemoryBytes();
                if (reservedBytes + estimate > softBudget)
                {
                    vm.Dispose();
                    foreach (var preparedVm in accepted) preparedVm.Dispose();
                    HasMore = true;
                    AppLog.Info($"Aggressive card preload stopped at {loaded} cards: estimated memory budget reached ({Config.CardMemoryLimitMb} MiB).");
                    OnPropertyChanged(nameof(ResultSummary));
                    return;
                }

                reservedBytes += estimate;
                accepted.Add(vm);
            }

            foreach (var vm in accepted) Items.Add(vm);
            loaded += accepted.Count;
            _offset += accepted.Count;
            HasMore = result.HasMore;
            page++;
            OnPropertyChanged(nameof(IsEmptyStateVisible));
            OnPropertyChanged(nameof(IsNoResultsStateVisible));
            OnPropertyChanged(nameof(EndOfListText));
            BusyOverlayText = $"Подготовка карточек · {loaded}";
            OnPropertyChanged(nameof(BusyOverlayText));

            if (accepted.Count == 0 || !result.HasMore) break;
            if ((page & 1) == 0) await Task.Yield();
        }

        BusyOverlayText = $"Карточки подготовлены · {loaded}";
        OnPropertyChanged(nameof(BusyOverlayText));
    }

    private void ModFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModFilterItem.IsSelected)) return;
        OnPropertyChanged(nameof(ModsFilterSummary));
        if (_loaded && !IsScanning) _ = DebouncedSearchAsync();
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
            UiAnimationService.PrepareOpacityTransition(image, Config.ShowAnimations && !Config.AggressivePhotoLoading, UiAnimationService.ThumbnailRevealDuration);
            QueueThumbnail(vm);
        }
    }

    private void CardRoot_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;

        // Карточка всегда остаётся видимой. Раньше CardOpacity хранился во ViewModel
        // и мог застрять на 0 при рециклинге ItemsRepeater, оставляя пустую серую карточку.
        border.Opacity = 1;

        if (!Config.ShowAnimations || Config.AggressiveCardLoading)
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

    private void ResetFilters_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in ModFilters) item.IsSelected = true;
        foreach (var item in ResourceTypeFilters) item.IsSelected = true;
        foreach (var item in ResourceSubtypeFilters) item.IsSelected = true;
        BrokenOnly = false;
        _config.HideBroken = false;
        HideBrokenFilterBox.IsChecked = false;
        _ = _configService.SaveAsync(_config);
        OnPropertyChanged(nameof(ModsFilterSummary));
        OnPropertyChanged(nameof(ResourceTypesFilterSummary));
        OnPropertyChanged(nameof(ResourceSubtypesFilterSummary));
        DisplayFilterIndex = 0;
        if (!string.IsNullOrWhiteSpace(SearchQuery)) SearchQuery = string.Empty;
        if (_loaded && !IsScanning) _ = DebouncedSearchAsync();
    }

    private void ResetModsFilters_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in ModFilters) item.IsSelected = true;
        OnPropertyChanged(nameof(ModsFilterSummary));
        if (_loaded && !IsScanning) _ = DebouncedSearchAsync();
    }

    private void ResetResourceTypesFilters_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in ResourceTypeFilters) item.IsSelected = true;
        OnPropertyChanged(nameof(ResourceTypesFilterSummary));
        if (_loaded && !IsScanning) _ = DebouncedSearchAsync();
    }

    private void ResetResourceSubtypesFilters_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in ResourceSubtypeFilters) item.IsSelected = true;
        OnPropertyChanged(nameof(ResourceSubtypesFilterSummary));
        if (_loaded && !IsScanning) _ = DebouncedSearchAsync();
    }

    private void ResetStateFilter_Click(object? sender, RoutedEventArgs e)
    {
        BrokenOnly = false;
        _config.HideBroken = false;
        HideBrokenFilterBox.IsChecked = false;
        OnPropertyChanged(nameof(Config));
        _ = _configService.SaveAsync(_config);
        if (_loaded && !IsScanning) _ = DebouncedSearchAsync();
    }

    private void WorkspaceGrid_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateDetailPanelOverlayBounds();
        if (IsDetailPanelExpanded && DetailVisible) SetDetailPanelWidth(CalculateExpandedDetailWidth());
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
        if (sender is CheckBox toggle)
        {
            _config.HideBroken = toggle.IsChecked == true;
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

}
