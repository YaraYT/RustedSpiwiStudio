namespace RustedShpizhionStudio.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly CheckboxSnapshot _initialCheckboxes;
    private bool _allowClose;
    public AppConfig? Result { get; private set; }
    public bool DeleteDatabaseRequested { get; private set; }

    private readonly record struct CheckboxSnapshot(bool Parallel, bool Dangerous, bool Normalize, bool Animations, bool Rwmod, bool HideBroken);

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _initialCheckboxes = new(config.ParallelIndexing, config.DangerousMode, config.NormalizeImages, config.ShowAnimations, config.RwmodExtraction, config.HideBroken);
        UiAnimationService.PrepareWindow(this, config.ShowAnimations);

        RootBox.Text = config.ModsRoot;
        IndexParallelismBox.Maximum = Math.Max(1, Environment.ProcessorCount);
        ComputeParallelismBox.Maximum = Math.Max(1, Environment.ProcessorCount);
        PageSizeBox.Value = config.ImagePageSize;
        WindowModeBox.SelectedItem = WindowModeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), config.WindowMode, StringComparison.OrdinalIgnoreCase)) ?? WindowModeBox.Items.Cast<ComboBoxItem>().First();
        LayoutBox.SelectedItem = LayoutBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), config.CardLayout, StringComparison.OrdinalIgnoreCase)) ?? LayoutBox.Items.Cast<ComboBoxItem>().ElementAt(1);
        NormalizeBox.IsChecked = config.NormalizeImages;
        PaddingBox.Value = config.NormalizePadding;
        PaddingText.Text = $"{config.NormalizePadding}%";
        RwmodBox.IsChecked = config.RwmodExtraction;
        ShowAnimationsBox.IsChecked = config.ShowAnimations;
        HideBrokenBox.IsChecked = config.HideBroken;
        ParallelIndexingBox.IsChecked = config.ParallelIndexing;
        IndexParallelismBox.Value = config.IndexParallelism;
        ComputeParallelismBox.Value = config.ComputeParallelism;
        DangerousModeBox.IsChecked = config.DangerousMode;
        UpdatePerformanceControls();

        ParallelIndexingBox.IsCheckedChanged += (_, _) => UpdatePerformanceControls();
        DangerousModeBox.IsCheckedChanged += (_, _) => UpdatePerformanceControls();
        PaddingBox.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") PaddingText.Text = $"{(int)Math.Round(PaddingBox.Value)}%"; };
        Closing += SettingsWindow_Closing;

        Opened += (_, _) =>
        {
            if (Screens.Primary is { } primary) Height = Math.Min(800, Math.Max(560, primary.WorkingArea.Height - 40));
        };
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "Выберите папку mods/units" });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is string path) RootBox.Text = path;
    }

    private async void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (!HasChangedCheckboxes()) { _allowClose = true; Close(); return; }
        var ok = await DialogWindow.AskAsync(this, "Некоторые переключатели были изменены. Выйти без сохранения?", "Выйти", "Остаться");
        if (!ok) return;
        _allowClose = true;
        Close();
    }

    private async void SettingsWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || !HasChangedCheckboxes()) return;
        e.Cancel = true;
        var ok = await DialogWindow.AskAsync(this, "Некоторые переключатели были изменены. Выйти без сохранения?", "Выйти", "Остаться");
        if (ok) { _allowClose = true; Close(); }
    }

    private bool HasChangedCheckboxes()
        => _initialCheckboxes.Parallel != (ParallelIndexingBox.IsChecked == true)
        || _initialCheckboxes.Dangerous != (DangerousModeBox.IsChecked == true)
        || _initialCheckboxes.Normalize != (NormalizeBox.IsChecked == true)
        || _initialCheckboxes.Animations != (ShowAnimationsBox.IsChecked == true)
        || _initialCheckboxes.Rwmod != (RwmodBox.IsChecked == true)
        || _initialCheckboxes.HideBroken != (HideBrokenBox.IsChecked == true);

    private void UpdatePerformanceControls()
    {
        var enabled = ParallelIndexingBox.IsChecked == true;
        var dangerous = DangerousModeBox.IsChecked == true;
        IndexParallelismBox.IsEnabled = enabled && !dangerous;
        ComputeParallelismBox.IsEnabled = enabled && !dangerous;
        if (dangerous)
        {
            var cores = Math.Max(1, Environment.ProcessorCount);
            IndexParallelismBox.Value = cores;
            ComputeParallelismBox.Value = cores;
        }
    }

    private async void DeleteDatabase_Click(object? sender, RoutedEventArgs e)
    {
        var ok = await DialogWindow.AskAsync(this, "Удалить текущую portable-базу индекса?\n\nВсе собранные индексированные данные будут удалены. Конфиг, исходные моды и программа останутся на месте.", "Удалить базу", "Отмена");
        if (!ok) return;
        DeleteDatabaseRequested = true;
        StatusText.Text = "База будет удалена после закрытия настроек.";
        _allowClose = true;
        Close();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(RootBox.Text))
        {
            var fullPath = Path.GetFullPath(RootBox.Text);
            if (!Directory.Exists(fullPath) || !Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)).Equals("units", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Путь должен указывать именно на папку mods/units.";
                return;
            }
            _config.ModsRoot = fullPath;
        }
        else _config.ModsRoot = string.Empty;

        _config.ImagePageSize = (int)Math.Round(PageSizeBox.Value ?? _config.ImagePageSize);
        _config.WindowMode = (WindowModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "windowed";
        _config.CardLayout = (LayoutBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "grid-3";
        _config.NormalizeImages = NormalizeBox.IsChecked == true;
        _config.NormalizePadding = (int)Math.Round(PaddingBox.Value);
        _config.RwmodExtraction = RwmodBox.IsChecked == true;
        _config.ShowAnimations = ShowAnimationsBox.IsChecked == true;
        _config.HideBroken = HideBrokenBox.IsChecked == true;
        _config.ParallelIndexing = ParallelIndexingBox.IsChecked == true || DangerousModeBox.IsChecked == true;
        _config.IndexParallelism = Math.Clamp((int)Math.Round(IndexParallelismBox.Value ?? _config.IndexParallelism), 1, Math.Max(1, Environment.ProcessorCount));
        _config.ComputeParallelism = Math.Clamp((int)Math.Round(ComputeParallelismBox.Value ?? _config.ComputeParallelism), 1, Math.Max(1, Environment.ProcessorCount));
        _config.DangerousMode = DangerousModeBox.IsChecked == true;

        UiAnimationService.SetEnabled(_config.ShowAnimations);
        Result = _config;
        _allowClose = true;
        Close(Result);
    }
}
