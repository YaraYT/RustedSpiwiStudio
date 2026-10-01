namespace RustedShpizhionStudio.UI;


public sealed class ResourceFilterItem : INotifyPropertyChanged
{
    public string Code { get; }
    public string Name { get; }
    public string Group { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Group) ? Name : $"{Group} · {Name}";
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public ResourceFilterItem(string code, string name, string group = "") { Code = code; Name = name; Group = group; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ModFilterItem : INotifyPropertyChanged
{
    public long Id { get; }
    public string Name { get; }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected==value)return;_isSelected=value;PropertyChanged?.Invoke(this,new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ModFilterItem(long id,string name,bool selected=false){Id=id;Name=name;_isSelected=selected;}
}

public sealed class ImageItemViewModel : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim ThumbnailSemaphore = new(6, 6);
    private Task? _thumbnailTask;
    private CancellationTokenSource? _thumbnailLoadCts;
    public ImageRow Row { get; }
    public string Filename => Row.Filename;
    public string PathAndMod => $"{Row.ModName} · {Row.RelativePath}";
    public string DimensionsText => Row.Width is int w && Row.Height is int h ? $"{w} × {h}" : FormatSize(Row.Size);
    public string ResourceType => Row.ResourceType switch { "building"=>"Здание", "unit"=>"Юнит", "map"=>"Карта", _=>"Прочее" };
    public string ResourceTypeDisplay => $"{ResourceType} · {ResourceSubtype}";
    public string ResourceSubtype => Row.ResourceSubtype switch { "unit:tower"=>"Башня", "unit:body"=>"Тело", "unit:chassis"=>"Шасси", "unit:projectile"=>"Снаряды", "unit:active_ability"=>"Активные способности", "unit:effect"=>"Эффекты", "unit:corpse"=>"Трупы", "unit:other"=>"Другое", "building:body"=>"Основное тело", "building:tower"=>"Башня", "building:weapon"=>"Оружие", "building:ammunition"=>"Боеприпасы", "building:active_ability"=>"Активные способности", "building:effect"=>"Эффекты", "building:other"=>"Прочее", "map"=>"Карта", _=>"Другое" };
    public string FullPath => Path.GetFullPath(Path.Combine(Row.RootPath, Row.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

    public bool IsThumbnailLoading => _thumbnailTask is { IsCompleted: false };

    public long EstimateCardMemoryBytes()
    {
        var stringChars = (long)Row.Filename.Length
                        + Row.ModName.Length
                        + Row.RelativePath.Length
                        + Row.RootPath.Length
                        + Row.ResourceType.Length
                        + Row.ResourceSubtype.Length
                        + Row.Sha256.Length;

        // Conservative soft estimate for the VM, its ImageRow record, string references
        // and the small amount of UI metadata kept with a virtualized card. It is a
        // budgeting heuristic, not a hard process-memory cap.
        return 8192L + stringChars * sizeof(char);
    }

    public long EstimateThumbnailBytes(int width)
    {
        width = Math.Clamp(width, 160, 900);
        var sourceWidth = Row.Width.GetValueOrDefault(width);
        var sourceHeight = Row.Height.GetValueOrDefault(width);
        if (sourceWidth <= 0) sourceWidth = width;
        if (sourceHeight <= 0) sourceHeight = width;
        var scale = Math.Min(1d, (double)width / sourceWidth);
        var decodedWidth = Math.Max(1L, (long)Math.Round(sourceWidth * scale));
        var decodedHeight = Math.Max(1L, (long)Math.Round(sourceHeight * scale));
        var pixels = Math.Min(long.MaxValue / 4, decodedWidth * decodedHeight);
        return Math.Min(long.MaxValue / 2, pixels * 4);
    }
    private Bitmap? _thumbnail;
    private bool _thumbnailFailed;
    private bool _disposed;
    private double _cardOpacity = 1;
    private double _cardWidth = 250;
    private double _previewHeight = 190;
    private double _cardHeight = 285;
    public double CardWidth { get=>_cardWidth; private set { if(Math.Abs(_cardWidth-value)<0.1)return;_cardWidth=value;PropertyChanged?.Invoke(this,new(nameof(CardWidth))); } }
    public double PreviewHeight { get=>_previewHeight; private set { if(Math.Abs(_previewHeight-value)<0.1)return;_previewHeight=value;PropertyChanged?.Invoke(this,new(nameof(PreviewHeight))); } }
    public double CardHeight { get=>_cardHeight; private set { if(Math.Abs(_cardHeight-value)<0.1)return;_cardHeight=value;PropertyChanged?.Invoke(this,new(nameof(CardHeight))); } }
    public double PreviewPadding { get; private set; }
    public Bitmap? Thumbnail { get => _thumbnail; private set
    {
        if (_disposed) { value?.Dispose(); return; }
        if (ReferenceEquals(_thumbnail, value)) return;
        var old = _thumbnail;
        _thumbnail = value;
        old?.Dispose();
        PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
        PropertyChanged?.Invoke(this, new(nameof(IsThumbnailPlaceholderVisible)));
        PropertyChanged?.Invoke(this, new(nameof(IsThumbnailErrorVisible)));
        PropertyChanged?.Invoke(this, new(nameof(ThumbnailOpacity)));
    }
    }
    public double CardOpacity { get => _cardOpacity; private set { if (Math.Abs(_cardOpacity - value) < 0.01) return; _cardOpacity = value; PropertyChanged?.Invoke(this, new(nameof(CardOpacity))); } }
    public double ThumbnailOpacity => Thumbnail is null ? 0 : 1;
    public bool IsThumbnailPlaceholderVisible => Thumbnail is null && !_thumbnailFailed;
    public bool IsThumbnailErrorVisible => Thumbnail is null && _thumbnailFailed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ImageItemViewModel(ImageRow row)=>Row=row;

    public void ApplyLayout(AppConfig config, double? resolvedWidth = null)
    {
        var preferredWidth = config.CardLayout switch
        {
            "grid-2" => 330d,
            "grid-3" => 280d,
            "grid-4" => 215d,
            "grid-5" => 185d,
            "list" => 650d,
            _ => 250d
        };
        var preferredPreviewHeight = config.CardLayout switch
        {
            "grid-2" => 205d,
            "grid-3" => 195d,
            "grid-4" => 180d,
            "grid-5" => 160d,
            "list" => 120d,
            _ => 190d
        };
        CardWidth = resolvedWidth is > 0 ? resolvedWidth.Value : preferredWidth;
        PreviewHeight = preferredPreviewHeight;
        CardHeight = config.CardLayout switch
        {
            "grid-2" => 315,
            "grid-3" => 305,
            "grid-4" => 285,
            "grid-5" => 270,
            "list" => 235,
            _ => 300
        };
        PreviewPadding = config.NormalizeImages ? Math.Round(PreviewHeight * config.NormalizePadding / 100d) : 0;
        CardOpacity = 1;
        PropertyChanged?.Invoke(this,new(nameof(PreviewPadding)));
        PropertyChanged?.Invoke(this,new(nameof(ThumbnailOpacity)));
    }

    // ИСПРАВЛЕНО: добавлен CancellationToken, чтобы WaitAsync не блокировал вечно
    // когда список карточек обновляется (например, новый поиск) — старые задачи
    // теперь могут быть отменены. Также убран тихий catch (теперь логируем).
    public Task LoadThumbnailAsync(int width = 512, CancellationToken ct = default)
    {
        if (_disposed || Thumbnail is not null) return Task.CompletedTask;
        if (_thumbnailTask is { IsCompleted: false }) return _thumbnailTask;

        _thumbnailLoadCts?.Cancel();
        _thumbnailLoadCts?.Dispose();
        _thumbnailLoadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _thumbnailTask = LoadThumbnailCoreAsync(Math.Clamp(width, 160, 900), _thumbnailLoadCts.Token, _thumbnailLoadCts);
        return _thumbnailTask;
    }

    private async Task LoadThumbnailCoreAsync(int width, CancellationToken ct, CancellationTokenSource ownerCts)
    {
        try
        {
            if (!File.Exists(FullPath))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || ct.IsCancellationRequested) return;
                    _thumbnailFailed = true;
                    PropertyChanged?.Invoke(this, new(nameof(IsThumbnailPlaceholderVisible)));
                    PropertyChanged?.Invoke(this, new(nameof(IsThumbnailErrorVisible)));
                });
                return;
            }

            await ThumbnailSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Exception? last = null;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var path = FullPath;
                        var bitmap = await Task.Run(() =>
                        {
                            ct.ThrowIfCancellationRequested();
                            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: false);
                            return Bitmap.DecodeToWidth(fs, width, BitmapInterpolationMode.LowQuality);
                        }, ct).ConfigureAwait(false);

                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (_disposed || ct.IsCancellationRequested)
                            {
                                bitmap.Dispose();
                                return;
                            }
                            _thumbnailFailed = false;
                            Thumbnail = bitmap;
                        });
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (attempt == 0)
                    {
                        last = ex;
                        await Task.Delay(35, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }

                if (last is not null && !ct.IsCancellationRequested)
                {
                    AppLog.Warn($"Thumbnail decode failed for '{FullPath}': {last.Message}");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || ct.IsCancellationRequested) return;
                        _thumbnailFailed = true;
                        PropertyChanged?.Invoke(this, new(nameof(IsThumbnailPlaceholderVisible)));
                        PropertyChanged?.Invoke(this, new(nameof(IsThumbnailErrorVisible)));
                    });
                }
            }
            finally { ThumbnailSemaphore.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn($"Thumbnail load failed for '{FullPath}': {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_thumbnailLoadCts, ownerCts))
            {
                _thumbnailLoadCts = null;
                _thumbnailTask = null;
            }
            ownerCts.Dispose();
        }
    }

    public void UnloadThumbnail()
    {
        if (_disposed) return;
        _thumbnailLoadCts?.Cancel();
        var thumbnail = Interlocked.Exchange(ref _thumbnail, null);
        try { thumbnail?.Dispose(); } catch { }
        if (thumbnail is not null)
        {
            _thumbnailFailed = false;
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new(nameof(IsThumbnailPlaceholderVisible)));
            PropertyChanged?.Invoke(this, new(nameof(IsThumbnailErrorVisible)));
            PropertyChanged?.Invoke(this, new(nameof(ThumbnailOpacity)));
        }
    }

    public void HideCardForAnimation() => CardOpacity = 0;
    public void RevealCard() => CardOpacity = 1;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thumbnailLoadCts?.Cancel();
        if (_thumbnailTask is null)
        {
            try { _thumbnailLoadCts?.Dispose(); } catch { }
            _thumbnailLoadCts = null;
        }
        var thumbnail = Interlocked.Exchange(ref _thumbnail, null);
        try { thumbnail?.Dispose(); } catch { }
        _thumbnailTask = null;
    }

    public static async Task<Bitmap?> LoadBitmapAsync(ImageRow row, int width, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(Path.Combine(row.RootPath, row.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) return null;
        await ThumbnailSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: false);
                ct.ThrowIfCancellationRequested();
                return Bitmap.DecodeToWidth(fs, Math.Clamp(width, 320, 1800), BitmapInterpolationMode.HighQuality);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { AppLog.Warn($"Preview decode failed for '{path}': {ex.Message}"); return null; }
        finally { ThumbnailSemaphore.Release(); }
    }

    private static string FormatSize(long bytes)=>bytes switch{>=1024*1024=>$"{bytes/1024d/1024d:0.##} MiB",>=1024=>$"{bytes/1024d:0.##} KiB",_=>$"{bytes} B"};
}

public sealed class ReferenceItemViewModel
{
    public string KeyText { get; }
    public string ValueText { get; }
    public string StatusText { get; }
    public long? UnitId { get; }
    public long? ImageId { get; }
    public bool CanOpenUnit => UnitId.HasValue;
    public bool CanOpenImage => ImageId.HasValue;

    public ReferenceItemViewModel(ReferenceRow row)
    {
        KeyText=$"{row.KeyName} · {row.RelationType}";
        ValueText=row.ResolvedPath is null ? row.RawValue : row.ResolvedPath;
        StatusText=row.Status=="ok" ? (row.IsAnimation?"Анимационная ссылка":"Ссылка найдена") : $"Состояние: {row.Status}";
        UnitId=row.UnitId;ImageId=row.ImageId;
    }

    public ReferenceItemViewModel(UnitResource row)
    {
        KeyText=$"{row.KeyName} · {row.RelationType}";
        ValueText=row.ResolvedPath is null ? row.RawValue : row.ResolvedPath;
        StatusText=row.Status=="ok" ? (row.IsAnimation?"Анимационная ссылка":"Ссылка найдена") : $"Состояние: {row.Status}";
        UnitId=null;ImageId=row.ImageId;
    }
}
