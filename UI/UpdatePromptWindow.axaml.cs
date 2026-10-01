namespace RustedShpizhionStudio.UI;

public partial class UpdatePromptWindow : Window, INotifyPropertyChanged
{
    private readonly UpdateInfo _info;
    private bool _buttonsEnabled = true;
    private bool _updateButtonEnabled = true;
    private string _statusText = "Обновление проверено и готово к установке.";
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string VersionText => _info.TagName;
    public string ReleaseTitle => _info.Title;
    public string AssetText => _info.Asset is null
        ? "ZIP-пакет обновления не найден."
        : $"Пакет: {_info.Asset.Name} · {FormatSize(_info.Asset.Size)} · SHA-256 проверяется перед заменой набора файлов";
    public string ReleaseNotes => string.IsNullOrWhiteSpace(_info.Body)
        ? "В этом релизе нет отдельного описания изменений."
        : NormalizeReleaseNotes(_info.Body);
    public bool ButtonsEnabled { get => _buttonsEnabled; private set { _buttonsEnabled = value; OnPropertyChanged(); } }
    public bool UpdateButtonEnabled { get => _updateButtonEnabled; private set { _updateButtonEnabled = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }

    public UpdatePromptWindow(UpdateInfo info)
    {
        _info = info;
        _updateButtonEnabled = info.CanInstall;
        InitializeComponent();
        DataContext = this;
        UiAnimationService.PrepareWindow(this);
    }

    public async Task<bool> ShowAndWaitAsync(Window owner)
    {
        await ShowDialog(owner);
        return await _result.Task;
    }

    private async void Update_Click(object? sender, RoutedEventArgs e)
    {
        if (!_info.CanInstall) return;
        ButtonsEnabled = false;
        UpdateButtonEnabled = false;
        StatusText = "Скачивание обновления…";
        OnPropertyChanged(nameof(StatusText));
        try
        {
            StatusText = "Проверка SHA-256…";
            OnPropertyChanged(nameof(StatusText));
            var started = await UpdateService.InstallUpdateAsync(_info);
            if (!started) throw new InvalidOperationException("Не удалось запустить установщик обновления.");

            StatusText = "Обновление подготовлено. Программа сейчас закроется…";
            OnPropertyChanged(nameof(StatusText));
            _result.TrySetResult(true);
            Close(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Update installation failed", ex);
            StatusText = $"Не удалось установить обновление: {ex.Message}";
            OnPropertyChanged(nameof(StatusText));
            ButtonsEnabled = true;
            UpdateButtonEnabled = true;
        }
    }

    private void Later_Click(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(false);
        Close(false);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
            BeginMoveDrag(e);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string NormalizeReleaseNotes(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cleaned = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { cleaned.Add(string.Empty); continue; }
            while (line.StartsWith("#", StringComparison.Ordinal)) line = line[1..].TrimStart();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal))
                line = "• " + line[2..].TrimStart();
            cleaned.Add(line);
        }
        return string.Join(Environment.NewLine, cleaned).Trim();
    }

    private static string FormatSize(long bytes)
        => bytes switch
        {
            >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.##} MiB",
            >= 1024 => $"{bytes / 1024d:0.##} KiB",
            _ => $"{bytes} B"
        };
}
