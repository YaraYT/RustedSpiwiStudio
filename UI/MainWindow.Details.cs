namespace RustedShpizhionStudio.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private async Task<Bitmap?> LoadPreviewAsync(long? imageId, int width = 1200, CancellationToken ct = default)
    {
        if (imageId is null || _db is null) return null;
        var row = _db.GetImage(imageId.GetValueOrDefault())?.Image;
        if (row is null) return null;
        return await ImageItemViewModel.LoadBitmapAsync(row, width, ct);
    }
    private async Task NavigateToDetailAsync(DetailRoute route)
    {
        if (route == _currentRoute)
        {
            await CloseDetailAsync();
            return;
        }
        if (!await RenderDetailAsync(route)) return;
        PushLimited(_backHistory, _currentRoute);
        _forwardHistory.Clear();
        _currentRoute = route;
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }

    private async Task CloseDetailAsync()
    {
        if (_currentRoute == DetailRoute.Empty) return;
        if (!await RenderDetailAsync(DetailRoute.Empty)) return;
        _currentRoute = DetailRoute.Empty;
        _backHistory.Clear();
        _forwardHistory.Clear();
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }

    private async Task NavigateBackAsync()
    {
        if (_backHistory.Count == 0) return;
        var target = _backHistory.Pop();
        PushLimited(_forwardHistory, _currentRoute);
        if (!await RenderDetailAsync(target)) return;
        _currentRoute = target;
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }

    private async Task NavigateForwardAsync()
    {
        if (_forwardHistory.Count == 0) return;
        var target = _forwardHistory.Pop();
        PushLimited(_backHistory, _currentRoute);
        if (!await RenderDetailAsync(target)) return;
        _currentRoute = target;
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }

    private static void PushLimited(Stack<DetailRoute> stack, DetailRoute route)
    {
        stack.Push(route);
        while (stack.Count > 50) stack.RemoveBottom();
    }

    private async Task<bool> RenderDetailAsync(DetailRoute route)
    {
        if (_db is null) return false;
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = new CancellationTokenSource();
        var detailToken = _detailCts.Token;
        DetailBitmap = null;
        DetailVisible = route.Kind != DetailRouteKind.None;
        DetailTitle = route.Kind == DetailRouteKind.None ? "Выберите изображение или юнит" : "Загрузка…";
        DetailSubtitle = route.Kind == DetailRouteKind.None ? "Здесь появятся пути, тип ресурса и связанные данные." : string.Empty;
        DetailTypeText = string.Empty;
        DetailUsageText = string.Empty;
        _detailFolder = string.Empty;
        _selectedItem = null;
        _selectedUnit = null;
        DetailReferences.Clear();
        DetailMissingReferences.Clear();
        DetailReferencesExpanded = false;
        OnPropertyChangedAllDetails();

        switch (route.Kind)
        {
            case DetailRouteKind.None:
                return true;
            case DetailRouteKind.Image:
                return await RenderImageDetailAsync(route.Id, detailToken);
            case DetailRouteKind.Missing:
                return RenderMissingDetail(route.Id);
            case DetailRouteKind.Unit:
                return await RenderUnitDetailAsync(route.Id, detailToken);
            default:
                return false;
        }
    }

    private async Task<bool> RenderImageDetailAsync(long id, CancellationToken detailToken)
    {
        var details = _db!.GetImage(id);
        if (details is null) return false;
        _selectedItem = Items.FirstOrDefault(x => x.Row.Id == id);
        var row = details.Image;
        _detailFolder = _selectedItem is null ? Path.GetDirectoryName(Path.GetFullPath(Path.Combine(row.RootPath, row.RelativePath.Replace('/', Path.DirectorySeparatorChar)))) ?? string.Empty : Path.GetDirectoryName(_selectedItem.FullPath) ?? string.Empty;
        // ИСПРАВЛЕНО: Width/Height могут быть null (изображение не прочитано),
        // что давало "null × null ·" в строке подзаголовка.
        var dimsText = row.Width is int w && row.Height is int h ? $"{w} × {h} · " : "";
        DetailVisible = true;
        DetailTitle = DisplayImageName(row.Filename);
        DetailSubtitle = $"{row.ModName} · {row.RelativePath}\n{dimsText}{row.Size} bytes\nИспользований: {row.UsageCount}";
        DetailTypeText = $"Тип: {ResourceTypeText(row.ResourceType)} · {ResourceSubtypeText(row.ResourceSubtype)}";
        DetailUsageText = row.IsUsed ? "Статус: используется" : "Статус: не используется";
        DetailBitmap = await LoadPreviewAsync(id, ct: detailToken);
        foreach (var r in details.References) DetailReferences.Add(new ReferenceItemViewModel(r));
        foreach (var r in details.MissingReferences) DetailMissingReferences.Add(new ReferenceItemViewModel(new ReferenceRow(r.Id, r.KeyName, r.RawValue, r.ResolvedPath, "missing", null, r.Status, false, null, null)));
        OnPropertyChangedAllDetails();
        return true;
    }

    private bool RenderMissingDetail(long id)
    {
        var missing = _db!.GetMissingReference(id);
        if (missing is null) return false;
        _detailFolder = string.Empty;
        DetailVisible = true;
        DetailTitle = "Битая ссылка";
        DetailSubtitle = $"{missing.ModName} · {missing.RelativeFile} · [{missing.SectionName}]";
        DetailTypeText = "Тип: Битая ссылка";
        DetailUsageText = string.Empty;
        DetailReferences.Add(new ReferenceItemViewModel(new ReferenceRow(missing.Id, missing.KeyName, missing.RawValue, missing.ResolvedPath, "missing", null, missing.Status, false, null, null)));
        OnPropertyChangedAllDetails();
        return true;
    }

    private async Task<bool> RenderUnitDetailAsync(long id, CancellationToken detailToken)
    {
        var unit = _db!.GetUnit(id);
        if (unit is null) return false;
        _selectedUnit = unit;
        var iniFull = Path.GetFullPath(Path.Combine(unit.RootPath, unit.IniPath.Replace('/', Path.DirectorySeparatorChar)));
        _detailFolder = Path.GetDirectoryName(iniFull) ?? string.Empty;
        var visible = unit.Resources.ToList();
        DetailVisible = true;
        DetailTitle = unit.DisplayName;
        DetailSubtitle = $"Юнит: {unit.TechnicalName}\n{unit.ModName} · {unit.IniPath}\nВизуальных ссылок: {visible.Count}";
        DetailTypeText = "Тип: Юнит";
        DetailUsageText = $"Ресурсов: {visible.Count}";
        DetailBitmap = await LoadPreviewAsync(visible.FirstOrDefault(r => r.ImageId.HasValue)?.ImageId, ct: detailToken);
        foreach (var r in visible) DetailReferences.Add(new ReferenceItemViewModel(r));
        OnPropertyChangedAllDetails();
        return true;
    }

    private static string DisplayImageName(string filename) => Path.GetFileNameWithoutExtension(filename);
    private static string ResourceTypeText(string resourceType) => resourceType switch
    {
        "building" => "Здание",
        "unit" => "Юнит",
        "map" => "Карта",
        _ => "Не определён"
    };

    private static string ResourceSubtypeText(string subtype) => subtype switch
    {
        "unit:tower" or "building:tower" => "Башня",
        "unit:body" => "Тело",
        "unit:chassis" => "Шасси",
        "unit:projectile" => "Снаряды",
        "unit:active_ability" or "building:active_ability" => "Активные способности",
        "unit:effect" or "building:effect" => "Эффекты",
        "unit:corpse" => "Труп",
        "building:body" => "Основное тело",
        "building:weapon" => "Оружие",
        "building:ammunition" => "Боеприпасы",
        "building:other" => "Прочее",
        "map" => "Карта",
        _ => "Другое"
    };

    private async void ReferenceUnit_Click(object? sender, RoutedEventArgs e)
    {
        try { if (sender is Button b && b.Tag is ReferenceItemViewModel vm && vm.UnitId is long id) await NavigateToDetailAsync(new DetailRoute(DetailRouteKind.Unit, id)); }
        catch (Exception ex) { AppLog.Error("ReferenceUnit_Click", ex); }
        e.Handled = true;
    }

    private async void ReferenceImage_Click(object? sender, RoutedEventArgs e)
    {
        try { if (sender is Button b && b.Tag is ReferenceItemViewModel vm && vm.ImageId is long id) await NavigateToDetailAsync(new DetailRoute(DetailRouteKind.Image, id)); }
        catch (Exception ex) { AppLog.Error("ReferenceImage_Click", ex); }
        e.Handled = true;
    }

    private async void Back_Click(object? sender, RoutedEventArgs e)
    {
        try { await NavigateBackAsync(); }
        catch (Exception ex) { AppLog.Error("Back_Click", ex); }
    }
    private async void Forward_Click(object? sender, RoutedEventArgs e)
    {
        try { await NavigateForwardAsync(); }
        catch (Exception ex) { AppLog.Error("Forward_Click", ex); }
    }

    private void OpenDetailFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_detailFolder)) OpenFolder(_detailFolder);
    }

    private async void CopyDetailPath_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_selectedItem.FullPath);
    }
}
