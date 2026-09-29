namespace RustedShpizhionStudio.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ConfigService _configService = new();
    private AppConfig _config = new();
    private string _configPath = string.Empty;
    private IndexDatabase? _db;
    private IndexerService? _indexer;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _detailCts;
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private CancellationTokenSource? _thumbnailCts;
    private readonly Queue<ImageItemViewModel> _thumbnailQueue = new();
    private readonly HashSet<ImageItemViewModel> _thumbnailQueued = [];
    private bool _thumbnailBatchRunning;
    private readonly DispatcherTimer _thumbnailTimer;
    private bool _brokenOnly;
    private bool _isFiltersPanelVisible;
    private bool _detailReferencesExpanded;
    private int _displayFilterIndex;
    private string _searchQuery = string.Empty;
    private bool _isScanning;
    private bool _hasMore;
    private bool _hasIndexedData;
    private int _offset;
    private bool _loaded;
    private ImageItemViewModel? _selectedItem;
    private UnitDetails? _selectedUnit;
    private string _detailFolder = string.Empty;
    private int _currentModIndex;
    private int _missingReferencesCount;
    private int _totalMods;
    private double _modProgressValue;
    private string _modProgressText = string.Empty;
    private bool _busyIndicatorRunning;
    private string _busyBaseText = string.Empty;
    private int _busyTick;
    private readonly DispatcherTimer _busyTimer;
    private CancellationTokenSource? _busyVisibilityCts;
    private CancellationTokenSource? _cardDeliveryCts;
    private readonly Stack<DetailRoute> _backHistory = new();
    private readonly Stack<DetailRoute> _forwardHistory = new();
    private DetailRoute _currentRoute = DetailRoute.Empty;
    private bool _updatePromptShown;

    public ObservableCollection<ImageItemViewModel> Items { get; } = [];
    public ObservableCollection<ModFilterItem> ModFilters { get; } = [];
    public ObservableCollection<ResourceFilterItem> ResourceTypeFilters { get; } = [];
    public ObservableCollection<ResourceFilterItem> ResourceSubtypeFilters { get; } = [];
    public ObservableCollection<ReferenceItemViewModel> DetailReferences { get; } = [];
    public ObservableCollection<ReferenceItemViewModel> DetailMissingReferences { get; } = [];

    public AppConfig Config { get => _config; private set { _config = value; OnPropertyChanged(); OnPropertyChanged(nameof(CardItemWidth)); OnPropertyChanged(nameof(CardItemHeight)); OnPropertyChanged(nameof(ProgressBarVisible)); } }
    public string SearchQuery { get => _searchQuery; set { _searchQuery = value; OnPropertyChanged(); } }
    public bool IsFiltersPanelVisible
    {
        get => _isFiltersPanelVisible;
        private set
        {
            if (_isFiltersPanelVisible == value) return;
            _isFiltersPanelVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FiltersButtonText));
        }
    }
    public string FiltersButtonText => IsFiltersPanelVisible ? "Скрыть фильтры" : "Фильтры";
    public bool DetailReferencesExpanded
    {
        get => _detailReferencesExpanded;
        private set
        {
            if (_detailReferencesExpanded == value) return;
            _detailReferencesExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailReferencesToggleText));
        }
    }
    public string DetailReferencesToggleText => DetailReferencesExpanded ? "Скрыть список" : "Развернуть список";
    public bool DetailHasReferenceList => DetailReferences.Count > 0 || DetailMissingReferences.Count > 0;

    public bool BrokenOnly { get => _brokenOnly; set { if (_brokenOnly == value) return; _brokenOnly = value; OnPropertyChanged(); if (_loaded) _ = DebouncedSearchAsync(); } }
    public int DisplayFilterIndex { get => _displayFilterIndex; set { if (_displayFilterIndex == value) return; _displayFilterIndex = value; OnPropertyChanged(); if (_loaded) _ = DebouncedSearchAsync(); } }
    public bool IsScanning { get => _isScanning; private set { _isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanPanelVisible)); OnPropertyChanged(nameof(RescanVisible)); OnPropertyChanged(nameof(ScanButtonVisible)); OnPropertyChanged(nameof(ProgressBarVisible)); OnPropertyChanged(nameof(IsEmptyStateVisible)); OnPropertyChanged(nameof(IsIndexEmptyStateVisible)); OnPropertyChanged(nameof(IsNoResultsStateVisible)); } }
    public bool HasMore { get => _hasMore; private set { _hasMore = value; OnPropertyChanged(); OnPropertyChanged(nameof(EndOfListText)); } }
    public bool HasIndexedData { get => _hasIndexedData; private set { _hasIndexedData = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanPanelVisible)); OnPropertyChanged(nameof(RescanVisible)); OnPropertyChanged(nameof(ScanButtonVisible)); OnPropertyChanged(nameof(IsEmptyStateVisible)); OnPropertyChanged(nameof(IsIndexEmptyStateVisible)); OnPropertyChanged(nameof(IsNoResultsStateVisible)); OnPropertyChanged(nameof(EmptyStateTitle)); OnPropertyChanged(nameof(EmptyStateText)); } }
    public bool ScanPanelVisible => IsScanning;
    public bool RescanVisible => HasIndexedData && !IsScanning;
    public bool ScanButtonVisible => false;
    public bool ProgressBarVisible => IsScanning;
    public double ProgressValue { get; private set; }
    public double ModProgressValue { get => _modProgressValue; private set { _modProgressValue = value; OnPropertyChanged(); } }
    public string ProgressText { get; private set; } = string.Empty;
    public string ModProgressText { get => _modProgressText; private set { _modProgressText = value; OnPropertyChanged(); } }
    public string StatusText { get; private set; } = "Готово";
    public string ActivityIndicatorText { get; private set; } = string.Empty;
    private bool _busyOverlayVisible;
    public bool BusyOverlayVisible
    {
        get => _busyOverlayVisible;
        private set
        {
            if (_busyOverlayVisible == value) return;
            _busyOverlayVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BusyOverlayOpacity));
        }
    }
    public double BusyOverlayOpacity => BusyOverlayVisible ? 1 : 0;
    public string BusyOverlayText { get; private set; } = "Загрузка…";
    public string IndexStateText { get; private set; } = "Индекс не проверен";
    public string StatsText { get; private set; } = string.Empty;
    public string ResultSummary { get; private set; } = string.Empty;
    public bool IsEmptyStateVisible => !IsScanning && Items.Count == 0;
    public bool IsIndexEmptyStateVisible => IsEmptyStateVisible && !HasIndexedData;
    public bool IsNoResultsStateVisible => IsEmptyStateVisible && HasIndexedData;
    public string EmptyStateTitle => !HasIndexedData ? "Индекс ещё не создан" : "По этому запросу ничего нет";
    public string EmptyStateText => !HasIndexedData
        ? "Сканирование ещё не запускалось. Выберите папку mods/units и создайте индекс, после чего здесь появится галерея ресурсов."
        : "Проверьте строку поиска, тип ресурса и выбранные моды. Также можно пересканировать источник, если индекс устарел.";
    public string EndOfListText => Items.Count > 0 && !HasMore && HasIndexedData ? $"Конец списка · загружено {Items.Count} элементов" : string.Empty;
    public string DetailTitle { get; private set; } = "Выберите изображение или юнит";
    public string DetailSubtitle { get; private set; } = "Здесь появятся пути, тип ресурса и связанные данные.";
    public string DetailTypeText { get; private set; } = string.Empty;
    public string DetailUsageText { get; private set; } = string.Empty;
    private Bitmap? _detailBitmap;
    public Bitmap? DetailBitmap { get => _detailBitmap; private set
    {
        if (ReferenceEquals(_detailBitmap, value)) return;
        var old = _detailBitmap;
        _detailBitmap = value;
        old?.Dispose();
    }
    }
    
    public bool DetailVisible { get; private set; }
    public bool DetailCanOpenFolder => !string.IsNullOrWhiteSpace(_detailFolder);
    public bool DetailCanCopyPath => _selectedItem is not null;
    public bool DetailHasMissingReferences => DetailMissingReferences.Count > 0;
    public bool IsBrokenFilterVisible => HasIndexedData && _missingReferencesCount > 0;
    public bool CanNavigateBack => _backHistory.Count > 0;
    public bool CanNavigateForward => _forwardHistory.Count > 0;
    public double CardItemWidth => Config.CardLayout switch
    {
        "grid-2" => 330,
        "grid-3" => 280,
        "grid-4" => 215,
        "grid-5" => 185,
        "list" => 650,
        _ => 250
    };
    public double CardItemHeight => Config.CardLayout switch
    {
        "grid-2" => 315,
        "grid-3" => 305,
        "grid-4" => 285,
        "grid-5" => 270,
        "list" => 235,
        _ => 300
    };
    public string FullscreenButtonText => WindowState == WindowState.FullScreen ? "Выйти из полноэкранного" : "Полный экран";

    private readonly record struct DetailRoute(DetailRouteKind Kind, long Id)
    {
        public static DetailRoute Empty => new(DetailRouteKind.None, 0);
    }

    private enum DetailRouteKind { None, Image, Unit, Missing }

    private void InitializeResourceFilters()
    {
        ResourceTypeFilters.Clear();
        ResourceSubtypeFilters.Clear();
        ResourceTypeFilters.Add(new("unit", "Юнит"));
        ResourceTypeFilters.Add(new("building", "Здание"));
        ResourceTypeFilters.Add(new("map", "Карта"));
        ResourceTypeFilters.Add(new("other", "Не определён (другое)"));
        foreach (var item in ResourceTypeFilters) { item.IsSelected = true; item.PropertyChanged += ResourceFilterChanged; }

        void Add(string code, string name, string group)
        {
            var item = new ResourceFilterItem(code, name, group) { IsSelected = true };
            item.PropertyChanged += ResourceFilterChanged;
            ResourceSubtypeFilters.Add(item);
        }
        Add("unit:tower", "Башня", "Юниты");
        Add("unit:body", "Тело", "Юниты");
        Add("unit:chassis", "Шасси", "Юниты");
        Add("unit:projectile", "Снаряды", "Юниты");
        Add("unit:active_ability", "Активные способности", "Юниты");
        Add("unit:effect", "Эффекты", "Юниты");
        Add("unit:corpse", "Трупы", "Юниты");
        Add("unit:other", "Другое", "Юниты");
        Add("building:body", "Основное тело", "Здания");
        Add("building:tower", "Башня", "Здания");
        Add("building:weapon", "Оружие", "Здания");
        Add("building:ammunition", "Боеприпасы", "Здания");
        Add("building:active_ability", "Активные способности", "Здания");
        Add("building:effect", "Эффекты", "Здания");
        Add("building:other", "Прочее", "Здания");
        Add("map", "Карта", "Карты");
        Add("other", "Другое", "Не определено");
    }

    private void ResourceFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResourceFilterItem.IsSelected) && _loaded && !IsScanning) _ = DebouncedSearchAsync();
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public new event PropertyChangedEventHandler? PropertyChanged;
}
