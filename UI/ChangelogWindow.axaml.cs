namespace RustedShpizhionStudio.UI;

public sealed class ChangelogEntryViewModel
{
    public string Version { get; }
    public string Date { get; }
    public string ReleaseTitle { get; }
    public string HtmlUrl { get; }
    public bool CanOpenRelease => !string.IsNullOrWhiteSpace(HtmlUrl);
    public IReadOnlyList<string> Items { get; }

    public ChangelogEntryViewModel(ChangelogEntry e)
    {
        Version = e.Version;
        Date = e.Date;
        ReleaseTitle = e.Version;
        HtmlUrl = string.Empty;
        Items = e.Items;
    }

    public ChangelogEntryViewModel(GitHubRelease e)
    {
        Version = string.IsNullOrWhiteSpace(e.TagName) ? e.Name : e.TagName;
        Date = FormatDate(e.PublishedAt);
        ReleaseTitle = string.IsNullOrWhiteSpace(e.Name) ? e.TagName : e.Name;
        HtmlUrl = e.HtmlUrl;
        Items = ParseReleaseBody(e.Body);
    }

    private static IReadOnlyList<string> ParseReleaseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ["Описание изменений в GitHub для этого релиза отсутствует."];
        var result = new List<string>();
        foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            while (line.StartsWith("#", StringComparison.Ordinal)) line = line[1..].TrimStart();
            if (line.Length == 0) continue;
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal))
                line = line[2..].TrimStart();
            if (Regex.IsMatch(line, @"^\d+[.)]\s+"))
                line = Regex.Replace(line, @"^\d+[.)]\s+", string.Empty);
            result.Add(line);
        }
        return result.Count == 0 ? ["Описание изменений в GitHub для этого релиза отсутствует."] : result;
    }

    private static readonly string[] RussianMonths =
    [
        "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря"
    ];

    private static string FormatDate(string publishedAt)
    {
        if (!DateTimeOffset.TryParse(publishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return publishedAt;
        date = date.ToLocalTime();
        return date.Day + " " + RussianMonths[date.Month - 1] + " " + date.Year;
    }
}

public partial class ChangelogWindow : Window, INotifyPropertyChanged
{
    private string _updateBannerText = "Загружаем историю версий из update.json…";
    private string _updateBannerForeground = "#8899AA";
    private bool _updateBannerVisible = true;
    private UpdateInfo? _knownUpdate;

    public ObservableCollection<ChangelogEntryViewModel> Entries { get; } = [];
    public bool UpdateBannerVisible { get => _updateBannerVisible; private set { _updateBannerVisible = value; OnPropertyChanged(); } }
    public string UpdateBannerText { get => _updateBannerText; private set { _updateBannerText = value; OnPropertyChanged(); } }
    public string UpdateBannerForeground { get => _updateBannerForeground; private set { _updateBannerForeground = value; OnPropertyChanged(); } }
    public string FooterText => $"Текущая версия: {UpdateService.CurrentVersion}   ·   история: update.json   ·   app.log: {Path.GetFileName(AppLog.LogFilePath)}";

    public ChangelogWindow(UpdateInfo? knownUpdate = null)
    {
        _knownUpdate = knownUpdate;
        InitializeComponent();
        DataContext = this;
        UiAnimationService.PrepareWindow(this);
        if (knownUpdate?.IsNewer == true)
        {
            UpdateBannerText = knownUpdate.CanInstall
                ? $"Доступна новая версия {knownUpdate.TagName}."
                : $"Доступна новая версия {knownUpdate.TagName}, но автоматическая установка сейчас недоступна.";
            UpdateBannerForeground = knownUpdate.CanInstall ? "#6DCC88" : "#8899AA";
        }
        _ = LoadReleasesAsync();
    }

    private async Task LoadReleasesAsync()
    {
        try
        {
            var releases = await UpdateService.GetReleasesAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Entries.Clear();
                if (releases is { Count: > 0 })
                {
                    foreach (var release in releases)
                        Entries.Add(new ChangelogEntryViewModel(release));
                    UpdateBannerText = _knownUpdate?.IsNewer == true
                        ? $"Доступна новая версия {_knownUpdate.TagName}."
                        : $"GitHub: загружено {Entries.Count} версий из update.json.";
                    UpdateBannerForeground = _knownUpdate?.IsNewer == true ? "#6DCC88" : "#7AA3CC";
                    UpdateBannerVisible = true;
                    return;
                }

                LoadLocalFallback();
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"ChangelogWindow: GitHub release history failed: {ex.Message}");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(LoadLocalFallback);
        }
    }

    private void LoadLocalFallback()
    {
        Entries.Clear();
        foreach (var e in AppChangelog.Entries)
            Entries.Add(new ChangelogEntryViewModel(e));
        UpdateBannerText = "update.json сейчас недоступен. Показана встроенная история версий.";
        UpdateBannerForeground = "#8899AA";
        UpdateBannerVisible = true;
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var info = await UpdateService.CheckAsync();
            _knownUpdate = info;
            if (info?.IsNewer == true)
            {
                if (!info.CanInstall)
                {
                    UpdateBannerText = $"Доступна версия {info.TagName}, но GitHub не предоставил подходящий EXE с SHA-256.";
                    UpdateBannerForeground = "#8899AA";
                    UpdateBannerVisible = true;
                    return;
                }
                var prompt = new UpdatePromptWindow(info);
                var accepted = await prompt.ShowAndWaitAsync(this);
                if (accepted && info.CanInstall && Owner is MainWindow owner)
                    owner.CloseForUpdate();
                else if (accepted && info.CanInstall)
                    Close();
                return;
            }

            UpdateBannerText = "Установлена актуальная версия.";
            UpdateBannerForeground = "#7AA3CC";
            UpdateBannerVisible = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("ChangelogWindow update check failed", ex);
            UpdateBannerText = "Не удалось проверить GitHub. Работа программы не затронута.";
            UpdateBannerForeground = "#8899AA";
            UpdateBannerVisible = true;
        }
    }

    private void OpenRelease_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrWhiteSpace(url))
            OpenUrl(url);
    }

    private void OpenLog_Click(object? sender, RoutedEventArgs e) => AppLog.OpenLogFile();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
            BeginMoveDrag(e);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn($"ChangelogWindow: failed to open URL: {ex.Message}"); }
    }


    public new event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
