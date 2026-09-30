namespace RustedShpizhionStudio.Core;

public sealed record IniKey(string Key, string Value, int LineNo, bool Directive);
public sealed class IniSection
{
    public string Name { get; init; } = string.Empty;
    public int LineStart { get; init; }
    public int OrderIndex { get; init; }
    public List<IniKey> Keys { get; } = [];
    public long Id { get; set; }
}
public sealed class ParsedIni
{
    public string AbsolutePath { get; init; } = string.Empty;
    public List<IniSection> Sections { get; } = [];
    public List<string> CopyFrom { get; } = [];
}

public enum ResourceUsageFilter { All, Used, Unused }
public sealed class AppConfig
{
    [JsonPropertyName("configVersion")] public int ConfigVersion { get; set; } = 12;
    [JsonPropertyName("modsRoot")] public string ModsRoot { get; set; } = string.Empty;
    [JsonPropertyName("rwmodExtraction")] public bool RwmodExtraction { get; set; } = true;
    [JsonPropertyName("imagePageSize")] public int ImagePageSize { get; set; } = 250;
    [JsonPropertyName("hideBroken")] public bool HideBroken { get; set; }
    [JsonPropertyName("cardLayout")] public string CardLayout { get; set; } = "grid-3";
    [JsonPropertyName("normalizeImages")] public bool NormalizeImages { get; set; } = true;
    [JsonPropertyName("normalizePadding")] public int NormalizePadding { get; set; } = 12;
    [JsonPropertyName("showAnimations")] public bool ShowAnimations { get; set; } = true;
    [JsonPropertyName("windowMode")] public string WindowMode { get; set; } = "windowed";
    [JsonPropertyName("lastScanAt")] public string? LastScanAt { get; set; }
    [JsonPropertyName("parallelIndexing")] public bool ParallelIndexing { get; set; } = true;
    [JsonPropertyName("indexParallelism")] public int IndexParallelism { get; set; } = Math.Max(1, Environment.ProcessorCount - 2);
    [JsonPropertyName("computeParallelism")] public int ComputeParallelism { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
    [JsonPropertyName("dangerousMode")] public bool DangerousMode { get; set; }
}


public sealed record ModInfo(long Id, string Name, string RootPath, string RelativePath, string SourceType);
public sealed record ImageRow(
    long Id, long ModId, string ModName, string Filename, string RelativePath,
    long Size, int? Width, int? Height, string Sha256, string ResourceType,
    bool IsUsed, bool IsAnimation, string RootPath, int UsageCount)
{
    public string ResourceSubtype { get; init; } = "other";
}
public sealed record ReferenceRow(
    long Id, string KeyName, string RawValue, string? ResolvedPath,
    string RelationType, string? InheritedFrom, string Status, bool IsAnimation,
    long? UnitId, long? ImageId);
public sealed record ImageDetails(ImageRow Image, IReadOnlyList<ReferenceRow> References, IReadOnlyList<string> UnitNames, IReadOnlyList<MissingReference> MissingReferences);
public sealed record UnitResource(long RefId, string KeyName, string RawValue, string? ResolvedPath, long? ImageId, string Status, bool IsAnimation, string RelationType = "direct", string? InheritedFrom = null);
public sealed record UnitDetails(long Id, string TechnicalName, string DisplayName, string DisplayNameSource, string ModName, string RootPath, string IniPath, IReadOnlyList<UnitResource> Resources);
public sealed record MissingReference(long Id, string ModName, string RelativeFile, string SectionName, string KeyName, string RawValue, string? ResolvedPath, string Status);
public sealed record SearchResult(IReadOnlyList<ImageRow> Rows, bool HasMore);
public sealed record Stats(int Mods, int Files, int Images, int References, int Unused, int Missing, int Maps, bool HasIndexedData);
public sealed record MapInfo(long Id, long ModId, long ImageId, string ImageRelativePath, string TmxRelativePath);
public sealed record ScanSummary(int Mods, int Files, int Images, int References, int Missing, bool Cancelled = false, bool Saved = false);
public sealed record ScanProgress(string Kind, string Message, int Current = 0, int Total = 0, ScanSummary? Summary = null);

public sealed class IndexerOptions
{
    public string ModsRoot { get; init; } = string.Empty;
    public string CacheRoot { get; init; } = string.Empty;
    public bool IncludeRwmod { get; init; } = true;
    public Func<IReadOnlyList<string>, Task<bool>>? AskRwmodAsync { get; init; }
    public Func<ScanSummary, Task<bool>>? AskCancelAsync { get; init; }
    public Action<ScanProgress>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public bool EnableParallelism { get; init; } = true;
    public int IndexParallelism { get; init; } = 1;
    public int ComputeParallelism { get; init; } = 1;
    public bool DangerousMode { get; init; }
}

public sealed class ScanCancelledException(ScanSummary summary) : Exception("Сканирование отменено")
{
    public ScanSummary Summary { get; } = summary;
}

// ─── Changelog ───────────────────────────────────────────────────────────────

public sealed record ChangelogEntry(string Version, string Date, IReadOnlyList<string> Items);

public static class AppChangelog
{
    public static readonly IReadOnlyList<ChangelogEntry> Entries =
    [
        new("0.1.1", "30 сентября 2026",
        [
            "Добавлена расширенная диагностика запуска и критических ошибок.",
            "Критическая ошибка теперь показывается системным окном Windows с ID и текстом исключения.",
            "Добавлен аварийный перезапуск с защитой от бесконечного цикла после двух неудачных запусков.",
            "Лог автоматически использует %TEMP%, если рядом с программой нет доступа на запись.",
            "Добавлена предварительная проверка обязательных Windows native-библиотек."
        ]),
        new("0.1.0", "30 сентября 2026",
        [
            "История «Что нового» и данные обновления теперь загружаются из одного файла update.json в GitHub.",
            "Добавлена ручная проверка обновлений и безрамочное окно предложения новой версии.",
            "Добавлено обновление EXE с проверкой SHA-256 и сохранением пользовательских данных.",
            "Нижняя панель сканирования теперь появляется только во время работы.",
            "Основные элементы интерфейса выровнены по общей геометрии, а favicon.ico используется как иконка приложения."
        ])
    ];
}

// ─── GitHub Update Check ──────────────────────────────────────────────────────

public sealed class UpdateManifest
{
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("date")] public string Date { get; set; } = string.Empty;
    [JsonPropertyName("releaseUrl")] public string ReleaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = string.Empty;
    [JsonPropertyName("assetName")] public string AssetName { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("changes")] public List<string> Changes { get; set; } = [];
    [JsonPropertyName("history")] public List<UpdateHistoryEntry> History { get; set; } = [];
}

public sealed class UpdateHistoryEntry
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("date")] public string Date { get; set; } = string.Empty;
    [JsonPropertyName("releaseUrl")] public string ReleaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = string.Empty;
    [JsonPropertyName("assetName")] public string AssetName { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("changes")] public List<string> Changes { get; set; } = [];
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;
    [JsonPropertyName("published_at")] public string PublishedAt { get; set; } = string.Empty;
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GitHubReleaseAsset> Assets { get; set; } = [];
}

public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
    [JsonPropertyName("content_type")] public string ContentType { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
}

public sealed record UpdateInfo(
    GitHubRelease Release,
    GitHubReleaseAsset? Asset,
    bool IsNewer)
{
    public string TagName => Release.TagName;
    public string Title => string.IsNullOrWhiteSpace(Release.Name) ? Release.TagName : Release.Name;
    public string HtmlUrl => Release.HtmlUrl;
    public string Body => Release.Body;
    public bool CanInstall => IsNewer
                              && Asset is not null
                              && !string.IsNullOrWhiteSpace(Asset.BrowserDownloadUrl)
                              && IsSha256Digest(Asset.Digest);

    private static bool IsSha256Digest(string? digest)
        => !string.IsNullOrWhiteSpace(digest)
           && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
           && digest.Length == "sha256:".Length + 64;
}
