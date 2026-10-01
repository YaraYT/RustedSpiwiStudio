namespace RustedShpizhionStudio.Core;

public sealed partial class IndexerService
{
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> FallbackImageKeys = new(StringComparer.OrdinalIgnoreCase)
        { "image", "image_back", "image_wreak", "image_turret", "image_shadow", "chargeEffectImage" };

    private readonly IndexDatabase _db;
    private readonly string _docsDbPath;
    private HashSet<string>? _docsImageKeys;
    private static readonly HashSet<string> AnimationMarkerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "total_frames", "frame_width", "frame_height", "frameindex",
        "animateframestart", "animateframeend", "animateframespeed", "animateframelooping"
    };

    public IndexerService(IndexDatabase db, string docsDbPath)
    { _db = db; _docsDbPath = docsDbPath; }

    public async Task<ScanSummary> ScanAsync(IndexerOptions options)
    {
        using var priorityScope = DangerousPriorityScope.Enter(options.DangerousMode);
        if (string.IsNullOrWhiteSpace(options.ModsRoot)) throw new InvalidOperationException("Папка модов не выбрана.");
        var fullRoot = Path.GetFullPath(options.ModsRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(fullRoot) || !Path.GetFileName(fullRoot).Equals("units", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выбранная папка должна быть корнем mods/units.");
        Directory.CreateDirectory(options.CacheRoot);

        using var c = _db.Open(); c.Open();
        Configure(c, options.DangerousMode);
        var tx = c.BeginTransaction();
        var totals = new Totals();
        try
        {
            EnsureIndexVersion(c, tx);
            InsertScanRun(c, tx, out var runId);
            var folderMods = Directory.EnumerateDirectories(fullRoot).Select(p => new ModSpec(Path.GetFileName(p), p, Path.GetFileName(p), "folder", null, null)).ToList();
            var archives = Directory.EnumerateFiles(fullRoot, "*.rwmod", SearchOption.TopDirectoryOnly).ToList();
            var mods = new List<ModSpec>(folderMods);

            if (archives.Count > 0 && options.IncludeRwmod)
            {
                var unknown = new List<(string Path, string Hash)>();
                var known = new List<(string Path, string Hash, string Decision)>();
                foreach (var archive in archives)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    var hash = await ComputeSha256Async(archive, options.CancellationToken);
                    var d = GetRwmodDecision(c, tx, archive);
                    if (d is { } decision && decision.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)) known.Add((archive, hash, decision.Decision));
                    else unknown.Add((archive, hash));
                }
                if (unknown.Count > 0)
                {
                    var allow = await (options.AskRwmodAsync?.Invoke(unknown.Select(x => x.Path).ToList()) ?? Task.FromResult(false));
                    foreach (var x in unknown) SaveRwmodDecision(c, tx, x.Path, x.Hash, allow ? "allow" : "deny");
                    known.AddRange(unknown.Select(x => (x.Path, x.Hash, allow ? "allow" : "deny")));
                }
                foreach (var item in known.Where(x => x.Decision == "allow"))
                {
                    try
                    {
                        var extracted = await ExtractRwmodAsync(item.Path, item.Hash, options.CacheRoot, options.CancellationToken);
                        mods.Add(new(Path.GetFileNameWithoutExtension(item.Path), extracted, "@rwmod/" + Path.GetFileName(item.Path), "rwmod", item.Path, item.Hash));
                    }
                    catch (OperationCanceledException) when (options.CancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { options.Progress?.Invoke(new("warning", $"Не удалось распаковать {Path.GetFileName(item.Path)}: {ex.Message}")); }
                }
            }

            if (mods.Count == 0) throw new InvalidOperationException("В выбранной папке не найдено ни одной модификации.");
            options.Progress?.Invoke(new("mods-count", $"Найдено модов: {mods.Count}", 0, mods.Count));
            DeleteStaleMods(c, tx, fullRoot, mods.Select(m => m.RelativePath).ToArray());

            for (var i = 0; i < mods.Count; i++)
            {
                if (options.CancellationToken.IsCancellationRequested)
                    throw new ScanCancelledException(totals.ToSummary(i));
                options.Progress?.Invoke(new("mod-start", mods[i].Name, i + 1, mods.Count));
                var result = await ScanModAsync(c, tx, mods[i], i, mods.Count, options);
                totals.Files += result.Files; totals.Images += result.Images; totals.References += result.References; totals.Missing += result.Missing;
                totals.Mods = i + 1;
            }

            tx.Commit();
            UpdateScanRun(runId, totals.ToSummary());
            options.Progress?.Invoke(new("complete", "Сканирование завершено.", totals.Mods, mods.Count, totals.ToSummary()));
            return totals.ToSummary();
        }
        catch (OperationCanceledException) when (options.CancellationToken.IsCancellationRequested)
        {
            var summary = totals.ToSummary(totals.Mods);
            var save = await (options.AskCancelAsync?.Invoke(summary) ?? Task.FromResult(false));
            if (save)
            {
                try
                {
                    tx.Commit();
                }
                catch (Exception commitEx)
                {
                    AppLog.Error("Ошибка commit при сохранении частичного результата", commitEx);
                    try { tx.Rollback(); } catch (Exception rbEx) { AppLog.Warn($"Rollback после неудачного commit: {rbEx.Message}"); }
                    UpdateLastRunCancelled(summary, false);
                    options.Progress?.Invoke(new("cancelled", "Сканирование остановлено, но частичный результат не удалось сохранить.", 0, 0, summary with { Cancelled = true, Saved = false }));
                    return summary with { Cancelled = true, Saved = false };
                }
                UpdateLastRunCancelled(summary, true);
                options.Progress?.Invoke(new("cancelled", "Сканирование остановлено, частичный результат сохранён.", 0, 0, summary with { Cancelled = true, Saved = true }));
                return summary with { Cancelled = true, Saved = true };
            }
            try { tx.Rollback(); }
            catch (Exception rbEx) { AppLog.Warn($"Rollback после отмены: {rbEx.Message}"); }
            UpdateLastRunCancelled(summary, false);
            options.Progress?.Invoke(new("cancelled", "Сканирование отменено, изменения откатаны.", 0, 0, summary with { Cancelled = true, Saved = false }));
            return summary with { Cancelled = true, Saved = false };
        }
        catch (ScanCancelledException ex)
        {
            var save = await (options.AskCancelAsync?.Invoke(ex.Summary) ?? Task.FromResult(false));
            if (save)
            {
                try
                {
                    tx.Commit();
                }
                catch (Exception commitEx)
                {
                    AppLog.Error("Ошибка commit (ScanCancelled)", commitEx);
                    try { tx.Rollback(); } catch (Exception rbEx) { AppLog.Warn($"Rollback после неудачного commit (ScanCancelled): {rbEx.Message}"); }
                    UpdateLastRunCancelled(ex.Summary, false);
                    options.Progress?.Invoke(new("cancelled", "Сканирование остановлено, но частичный результат не удалось сохранить.", 0, 0, ex.Summary with { Cancelled = true, Saved = false }));
                    return ex.Summary with { Cancelled = true, Saved = false };
                }
                UpdateLastRunCancelled(ex.Summary, true);
                options.Progress?.Invoke(new("cancelled", "Сканирование остановлено, частичный результат сохранён.", 0, 0, ex.Summary with { Cancelled = true, Saved = true }));
                return ex.Summary with { Cancelled = true, Saved = true };
            }
            try { tx.Rollback(); }
            catch (Exception rbEx) { AppLog.Warn($"Rollback (ScanCancelled): {rbEx.Message}"); }
            UpdateLastRunCancelled(ex.Summary, false);
            options.Progress?.Invoke(new("cancelled", "Сканирование отменено, изменения откатаны.", 0, 0, ex.Summary with { Cancelled = true, Saved = false }));
            return ex.Summary with { Cancelled = true, Saved = false };
        }
        catch (Exception ex)
        {
            AppLog.Error("Критическая ошибка сканирования", ex);
            try { tx.Rollback(); }
            catch (Exception rbEx) { AppLog.Warn($"Rollback после ошибки: {rbEx.Message}"); }
            options.Progress?.Invoke(new("error", ex.Message));
            throw;
        }
    }

    private async Task<ModScanResult> ScanModAsync(SqliteConnection c, SqliteTransaction tx, ModSpec mod, int modIndex, int modCount, IndexerOptions options)
    {
        AppLog.Info($"Indexing mod started: {mod.Name} ({mod.RootPath})");
        var modId = UpsertMod(c, tx, mod);
        Execute(c, tx, "DELETE FROM inheritance_edges WHERE mod_id=$id; DELETE FROM image_references WHERE mod_id=$id; DELETE FROM units WHERE mod_id=$id;", ("$id", modId));

        var allFiles = new List<string>();
        var iniPaths = new List<string>();
        var imagePaths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(mod.RootPath, "*", SearchOption.AllDirectories))
        {
            allFiles.Add(path);
            if (IsIniLike(path)) iniPaths.Add(path);
            if (IsImage(path)) imagePaths.Add(path);
        }
        var files = new List<FileState>(iniPaths.Count);
        var currentFileRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var indexDegree = GetParallelism(options.IndexParallelism, options.EnableParallelism, options.DangerousMode);
        var computeDegree = GetParallelism(options.ComputeParallelism, options.EnableParallelism, options.DangerousMode);

        // ── Подготовка INI параллельно ───────────────────────────────────────
        // Сначала снимаем только старое состояние SQLite. Само чтение, парсинг
        // и при необходимости SHA-256 выполняются без записи в БД.
        var fileJobs = new ParsedFilePrepareItem[iniPaths.Count];
        for (var i = 0; i < iniPaths.Count; i++)
        {
            ThrowIfCancelled(options.CancellationToken, 0, imagePaths.Count, 0, 0, modIndex);
            var path = iniPaths[i];
            var rel = PathResolver.NormalizeRelative(mod.RootPath, path);
            var info = new FileInfo(path);
            var previous = QueryFile(c, tx, modId, rel);
            var sameAsExisting = previous is not null && previous.Size == info.Length && previous.MtimeMs == info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond;
            fileJobs[i] = new ParsedFilePrepareItem(
                path,
                rel,
                info.Length,
                info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond,
                previous,
                sameAsExisting);
        }

        var parseCompleted = 0;
        var parseProgress = new ProgressReporter(options.Progress, "file", mod.Name, iniPaths.Count);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, iniPaths.Count),
            new ParallelOptions { MaxDegreeOfParallelism = indexDegree, CancellationToken = options.CancellationToken },
            async (i, ct) =>
            {
                var job = fileJobs[i];
                try
                {
                    job.Parsed = await IniParser.ParseFileAsync(job.AbsolutePath, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    job.Parsed = new ParsedIni { AbsolutePath = job.AbsolutePath };
                    lock (job.Errors) job.Errors.Add(ex);
                }

                var done = Interlocked.Increment(ref parseCompleted);
                parseProgress.Report(done);
            });

        var fileComputeCompleted = 0;
        var fileComputeProgress = new ProgressReporter(options.Progress, "compute", mod.Name, iniPaths.Count);
        await Parallel.ForEachAsync(
            fileJobs,
            new ParallelOptions { MaxDegreeOfParallelism = computeDegree, CancellationToken = options.CancellationToken },
            async (job, ct) =>
            {
                if (!job.SameAsExisting || job.Previous?.Sha256 is null)
                    job.Sha256 = await ComputeSha256Async(job.AbsolutePath, ct).ConfigureAwait(false);
                else
                    job.Sha256 = job.Previous.Sha256;

                var done = Interlocked.Increment(ref fileComputeCompleted);
                fileComputeProgress.Report(done);
            });

        foreach (var job in fileJobs)
        {
            ThrowIfCancelled(options.CancellationToken, files.Count, imagePaths.Count, 0, 0, modIndex);
            currentFileRel.Add(job.RelativePath);
            if (job.Errors.Count > 0)
            {
                foreach (var error in job.Errors)
                {
                    AppLog.Warn($"Failed to parse {mod.Name}/{job.RelativePath}: {error.Message}");
                    options.Progress?.Invoke(new("warning", $"Не удалось разобрать {job.RelativePath}: {error.Message}"));
                }
            }

            var row = SavePreparedFile(c, tx, modId, job);
            var sections = SaveSections(c, tx, row, job.Parsed!);
            files.Add(new FileState(row, job.Parsed!, sections));
        }

        DeleteStaleFiles(c, tx, modId, currentFileRel);
        SyncUnits(c, tx, modId, files);

        // ── Подготовка изображений параллельно ────────────────────────────────
        // Сначала читаем старое состояние из SQLite, после чего хеши и размеры
        // считаются без удержания DB-операции на каждом файле.
        var imageJobs = new ImagePrepareItem[imagePaths.Count];
        for (var i = 0; i < imagePaths.Count; i++)
        {
            ThrowIfCancelled(options.CancellationToken, files.Count, i, 0, 0, modIndex);
            var path = imagePaths[i];
            var rel = PathResolver.NormalizeRelative(mod.RootPath, path);
            var info = new FileInfo(path);
            var old = QueryImage(c, tx, modId, rel);
            var same = old is not null && old.Size == info.Length && old.MtimeMs == info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond;
            imageJobs[i] = new ImagePrepareItem(
                path,
                rel,
                info.Length,
                info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond,
                old,
                same);
        }

        var imageCompleted = 0;
        var imageProgress = new ProgressReporter(options.Progress, "image", mod.Name, imagePaths.Count);
        await Parallel.ForEachAsync(
            imageJobs,
            new ParallelOptions { MaxDegreeOfParallelism = computeDegree, CancellationToken = options.CancellationToken },
            async (job, ct) =>
            {
                if (!job.SameAsExisting || job.Previous?.Sha256 is null)
                    job.Sha256 = await ComputeSha256Async(job.AbsolutePath, ct).ConfigureAwait(false);

                if (!job.SameAsExisting || job.Previous?.Width is null || job.Previous?.Height is null)
                {
                    try
                    {
                        await using var fs = File.Open(job.AbsolutePath, new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.Read,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                        });
                        var dims = ReadImageDimensions(fs, Path.GetExtension(job.AbsolutePath));
                        job.Width = dims.Width;
                        job.Height = dims.Height;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AppLog.Warn($"Не удалось получить размеры изображения {job.RelativePath}: {ex.Message}");
                    }
                }

                var done = Interlocked.Increment(ref imageCompleted);
                imageProgress.Report(done);
            });

        var imageRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in imageJobs)
        {
            ThrowIfCancelled(options.CancellationToken, files.Count, imageRels.Count, 0, 0, modIndex);
            imageRels.Add(job.RelativePath);
            SavePreparedImage(c, tx, modId, job);
        }
        DeleteStaleImages(c, tx, modId, imageRels);
        SyncMaps(c, tx, modId, mod.RootPath, imagePaths, options.CancellationToken);

        var imageKeys = _docsImageKeys ??= LoadDocsImageKeys();
        var sectionsByFile = BuildSectionMaps(files, c, tx, modId);
        var ownSectionsByFile = sectionsByFile.ToDictionary(k => k.Key, v => v.Value, comparer: EqualityComparer<long>.Default);
        BuildExpandedSectionMaps(mod, files, sectionsByFile, ownSectionsByFile, options.CancellationToken);
        var templateFile = files.FirstOrDefault(f => Path.GetFileName(f.Row.AbsolutePath).Equals("all-units.template", StringComparison.OrdinalIgnoreCase));
        var templateSections = BuildTemplateSections(templateFile);

        var memo = new Dictionary<(long FileId, string Section), ResolvedSection>();
        var visiting = new HashSet<(long, string)>();
        var refSeen = new HashSet<string>(StringComparer.Ordinal);
        var refs = 0;
        var missing = 0;

        foreach (var state in files)
        {
            // В старой версии этот словарь строился заново для каждого key.
            // Он зависит только от файла, поэтому переносим вычисление наружу.
            var localContextResolved = new Dictionary<string, ResolvedSection>(StringComparer.OrdinalIgnoreCase);
            foreach (var contextName in sectionsByFile[state.Row.Id].Keys)
                localContextResolved[contextName] = ResolveSection(state.Row.Id, contextName, files, sectionsByFile, templateFile?.Row.Id, templateSections, memo, visiting, c, tx, modId);

            foreach (var section in state.Parsed.Sections)
            {
                ThrowIfCancelled(options.CancellationToken, files.Count, imagePaths.Count, refs, missing, modIndex);
                var resolved = ResolveSection(state.Row.Id, section.Name, files, sectionsByFile, templateFile?.Row.Id, templateSections, memo, visiting, c, tx, modId);
                var resolvedKeys = new HashSet<string>(resolved.Values.Keys, StringComparer.OrdinalIgnoreCase);
                if (resolved.Cycle)
                {
                    options.Progress?.Invoke(new("warning", $"Обнаружен цикл наследования: {state.Row.RelativePath} → [{section.Name}]"));
                    continue;
                }

                var unitId = GetUnitId(c, tx, modId, state.Row.Id);
                foreach (var (keyName, rec) in resolved.Values)
                {
                    var expanded = ExpandVariables(rec.Value, section.Name, localContextResolved);
                    if (!ShouldIndexImage(keyName, expanded, imageKeys)) continue;
                    var value = IniParser.StripQuotes(expanded);
                    var upper = value.ToUpperInvariant();
                    if (IniParser.SpecialImageValues.Contains(upper)) continue;

                    var resolution = PathResolver.Resolve(mod.RootPath, state.Row.AbsolutePath, value);
                    string? rel = null;
                    long? imageId = null;
                    if (resolution.Type == "path" && resolution.Target is not null)
                    {
                        rel = PathResolver.NormalizeRelative(mod.RootPath, resolution.Target);
                        imageId = SelectNullableInt64(c, tx, "SELECT id FROM images WHERE mod_id=$m AND relative_path=$p", ("$m", modId), ("$p", rel));
                    }

                    var source = resolved.Sources.TryGetValue(keyName, out var src)
                        ? src
                        : new SourceRef(state.Row.Id, section.Id, section.Name, rec.LineNo, "direct");
                    var relation = source.Relation == "direct" && source.FileId == state.Row.Id && source.SectionId == section.Id ? "direct" : "inherited";
                    var inheritedFrom = relation == "inherited" ? $"{source.SectionName}:{source.LineNo}" : null;
                    var sig = $"{modId}|{state.Row.Id}|{section.Id}|{unitId}|{keyName}|{expanded}|{relation}|{rel}";
                    if (!refSeen.Add(sig)) continue;
                    var status = imageId.HasValue ? "ok" : resolution.Type == "outside-root" ? "outside-root" : "missing";
                    if (status != "ok") missing++;
                    var animation = DetectAnimationReference(section.Name, resolvedKeys, keyName);
                    InsertReference(c, tx, imageId, modId, state.Row.Id, section.Id, unitId, keyName, expanded, rel, relation, source, inheritedFrom, status, animation);
                    refs++;
                }
            }
        }

        ClassifyImages(c, tx, modId);
        Execute(c, tx, "UPDATE mods SET last_scan_at=$at WHERE id=$id", ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", modId));
        var result = new ModScanResult(files.Count + allFiles.Except(iniPaths).Count(), imagePaths.Count, refs, missing);
        AppLog.Info($"Indexing mod complete: {mod.Name} ({mod.RootPath}) (files={result.Files}, images={result.Images}, refs={result.References}, missing={result.Missing})");
        return result;
    }

    private static int GetParallelism(int requested, bool enabled, bool dangerous)
    {
        if (!enabled) return 1;
        var cores = Math.Max(1, Environment.ProcessorCount);
        if (dangerous) return cores;
        return Math.Clamp(requested, 1, Math.Max(1, cores));
    }

    private sealed class DangerousPriorityScope : IDisposable
    {
        private readonly Process? _process;
        private readonly ProcessPriorityClass _previous;

        private DangerousPriorityScope(Process? process, ProcessPriorityClass previous)
        {
            _process = process;
            _previous = previous;
        }

        public static IDisposable Enter(bool enabled)
        {
            if (!enabled) return Noop.Instance;
            try
            {
                var process = Process.GetCurrentProcess();
                var previous = process.PriorityClass;
                process.PriorityClass = ProcessPriorityClass.High;
                AppLog.Warn("Dangerous mode: process priority raised to High for the duration of indexing.");
                return new DangerousPriorityScope(process, previous);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Dangerous mode could not raise process priority: {ex.Message}");
                return Noop.Instance;
            }
        }

        public void Dispose()
        {
            if (_process is null) return;
            try { _process.PriorityClass = _previous; }
            catch (Exception ex) { AppLog.Warn($"Could not restore process priority after indexing: {ex.Message}"); }
            _process.Dispose();
        }

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class ProgressReporter
    {
        private readonly Action<ScanProgress>? _progress;
        private readonly string _kind;
        private readonly string _modName;
        private readonly int _total;
        private int _lastReported;
        private long _lastTick;

        public ProgressReporter(Action<ScanProgress>? progress, string kind, string modName, int total)
        {
            _progress = progress;
            _kind = kind;
            _modName = modName;
            _total = total;
        }

        public void Report(int current)
        {
            if (_progress is null) return;
            var now = Environment.TickCount64;
            var step = Math.Max(1, _total / 80);
            var shouldReport = current >= _total || current - Volatile.Read(ref _lastReported) >= step || now - Volatile.Read(ref _lastTick) >= 120;
            if (!shouldReport) return;
            Interlocked.Exchange(ref _lastReported, current);
            Interlocked.Exchange(ref _lastTick, now);
            _progress(new ScanProgress(_kind, $"{_modName}: {_kind switch { "file" => "разбор INI", "compute" => "вычисления", "image" => "подготовка изображений", _ => "обработка" }} {current}/{_total}", current, _total));
        }
    }

    private sealed class ParsedFilePrepareItem
    {
        public string AbsolutePath { get; }
        public string RelativePath { get; }
        public long Size { get; }
        public long MtimeMs { get; }
        public FileStateRow? Previous { get; }
        public bool SameAsExisting { get; }
        public ParsedIni? Parsed { get; set; }
        public string? Sha256 { get; set; }
        public List<Exception> Errors { get; } = [];

        public ParsedFilePrepareItem(string absolutePath, string relativePath, long size, long mtimeMs, FileStateRow? previous, bool sameAsExisting)
        {
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
            Size = size;
            MtimeMs = mtimeMs;
            Previous = previous;
            SameAsExisting = sameAsExisting;
        }
    }
    private sealed class ImagePrepareItem
    {
        public string AbsolutePath { get; }
        public string RelativePath { get; }
        public long Size { get; }
        public long MtimeMs { get; }
        public ImageStateRow? Previous { get; }
        public bool SameAsExisting { get; }
        public string? Sha256 { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }

        public ImagePrepareItem(string absolutePath, string relativePath, long size, long mtimeMs, ImageStateRow? previous, bool sameAsExisting)
        {
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
            Size = size;
            MtimeMs = mtimeMs;
            Previous = previous;
            SameAsExisting = sameAsExisting;
            Sha256 = sameAsExisting ? previous?.Sha256 : null;
            Width = sameAsExisting ? previous?.Width : null;
            Height = sameAsExisting ? previous?.Height : null;
        }
    }

    private static void Configure(SqliteConnection c, bool dangerousMode = false)
    {
        using var cmd=c.CreateCommand();
        cmd.CommandText = dangerousMode ? """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=30000;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-262144;
            PRAGMA mmap_size=1073741824;
            """ : """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=30000;
            """;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureIndexVersion(SqliteConnection c, SqliteTransaction tx)
    {
        var format = Scalar(c, tx, "SELECT value FROM meta WHERE key='index_format_version'"); var parser = Scalar(c, tx, "SELECT value FROM meta WHERE key='parser_version'");
        if (format == IndexDatabase.IndexFormatVersion.ToString(CultureInfo.InvariantCulture) && parser == IndexDatabase.ParserVersion.ToString(CultureInfo.InvariantCulture)) return;
        foreach (var table in new[]{"image_references","inheritance_edges","maps","units","keys","sections","images","files","mods"}) Execute(c,tx,$"DELETE FROM {table};");
        Execute(c,tx,"INSERT INTO meta(key,value) VALUES('index_format_version',$f) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",("$f",IndexDatabase.IndexFormatVersion.ToString()));
        Execute(c,tx,"INSERT INTO meta(key,value) VALUES('parser_version',$p) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",("$p",IndexDatabase.ParserVersion.ToString()));
    }

    private static long UpsertMod(SqliteConnection c, SqliteTransaction tx, ModSpec mod)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO mods(name,root_path,relative_path,source_type,package_path,package_hash,last_scan_at) VALUES($n,$r,$rel,$s,$pp,$ph,$d) ON CONFLICT(root_path,relative_path) DO UPDATE SET name=excluded.name,source_type=excluded.source_type,package_path=excluded.package_path,package_hash=excluded.package_hash,last_scan_at=excluded.last_scan_at RETURNING id";Add(cmd,("$n",mod.Name),("$r",mod.RootPath),("$rel",mod.RelativePath),("$s",mod.SourceType),("$pp",(object?)mod.PackagePath??DBNull.Value),("$ph",(object?)mod.PackageHash??DBNull.Value),("$d",DateTimeOffset.UtcNow.ToString("O")));return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
    }

    private static FileStateRow SavePreparedFile(SqliteConnection c, SqliteTransaction tx, long modId, ParsedFilePrepareItem file)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO files(mod_id,relative_path,absolute_path,kind,size,mtime_ms,sha256) VALUES($m,$r,$a,'ini',$s,$mt,$h) ON CONFLICT(mod_id,relative_path) DO UPDATE SET absolute_path=excluded.absolute_path,size=excluded.size,mtime_ms=excluded.mtime_ms,sha256=excluded.sha256 RETURNING id,mod_id,relative_path,absolute_path,kind,size,mtime_ms,sha256";
        Add(cmd,
            ("$m", modId),
            ("$r", file.RelativePath),
            ("$a", file.AbsolutePath),
            ("$s", file.Size),
            ("$mt", file.MtimeMs),
            ("$h", (object?)file.Sha256 ?? DBNull.Value));
        using var r = cmd.ExecuteReader();
        r.Read();
        return new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            r.GetInt64(5),
            r.GetInt64(6),
            r.IsDBNull(7) ? null : r.GetString(7));
    }

    private static List<SectionRow> SaveSections(SqliteConnection c,SqliteTransaction tx,FileStateRow file,ParsedIni parsed)
    {
        Execute(c,tx,"DELETE FROM sections WHERE file_id=$f",("$f",file.Id));
        var result=new List<SectionRow>();
        var ids=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        using var keyCmd=c.CreateCommand();
        keyCmd.Transaction=tx;
        keyCmd.CommandText="INSERT INTO keys(section_id,key_name,value,line_no,is_directive) VALUES($s,$k,$v,$l,$d)";
        keyCmd.Parameters.Add("$s",SqliteType.Integer);
        keyCmd.Parameters.Add("$k",SqliteType.Text);
        keyCmd.Parameters.Add("$v",SqliteType.Text);
        keyCmd.Parameters.Add("$l",SqliteType.Integer);
        keyCmd.Parameters.Add("$d",SqliteType.Integer);
        foreach(var s in parsed.Sections)
        {
            long id;
            if(ids.TryGetValue(s.Name,out id))
            {
                Execute(c,tx,"UPDATE sections SET line_start=$l,order_index=$o,is_core=$c,entity_name=NULL WHERE id=$s; DELETE FROM keys WHERE section_id=$s;",
                    ("$l",s.LineStart),("$o",s.OrderIndex),("$c",s.Name.Equals("core",StringComparison.OrdinalIgnoreCase)?1:0),("$s",id));
            }
            else
            {
                using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO sections(file_id,name,line_start,order_index,is_core,entity_name) VALUES($f,$n,$l,$o,$c,NULL) RETURNING id";
                Add(cmd,("$f",file.Id),("$n",s.Name),("$l",s.LineStart),("$o",s.OrderIndex),("$c",s.Name.Equals("core",StringComparison.OrdinalIgnoreCase)?1:0));
                id=Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
                ids[s.Name]=id;
            }
            s.Id=id;
            foreach(var k in s.Keys)
            {
                keyCmd.Parameters["$s"].Value=id;
                keyCmd.Parameters["$k"].Value=k.Key;
                keyCmd.Parameters["$v"].Value=k.Value;
                keyCmd.Parameters["$l"].Value=k.LineNo;
                keyCmd.Parameters["$d"].Value=k.Directive?1:0;
                keyCmd.ExecuteNonQuery();
            }
            result.Add(new(id,file.Id,s.Name,s.LineStart,s));
        }
        return result;
    }

    private static void SyncUnits(SqliteConnection c,SqliteTransaction tx,long modId,List<FileState> files)
    {
        Execute(c,tx,"DELETE FROM units WHERE mod_id=$m",("$m",modId));
        foreach(var file in files)
        {
            if(!Path.GetExtension(file.Row.AbsolutePath).Equals(".ini",StringComparison.OrdinalIgnoreCase))continue;
            if(Path.GetFileName(file.Row.AbsolutePath).Equals("all-units.ini",StringComparison.OrdinalIgnoreCase))continue;
            var core=file.Parsed.Sections.LastOrDefault(s=>s.Name.Equals("core",StringComparison.OrdinalIgnoreCase)); if(core is null)continue;
            var technical=KeyValue(core,"name") ?? Path.GetFileNameWithoutExtension(file.Row.AbsolutePath); var displayRu=KeyValue(core,"displaytext_ru");var display=displayRu ?? KeyValue(core,"displaytext") ?? technical;var source=displayRu is not null?"displaytext_ru":KeyValue(core,"displaytext") is not null?"displaytext":"technical";
            Execute(c,tx,"UPDATE sections SET entity_name=$n WHERE id=$s",("$n",IniParser.StripQuotes(technical)),("$s",core.Id));Execute(c,tx,"INSERT INTO units(mod_id,file_id,section_id,technical_name,display_name,display_name_source) VALUES($m,$f,$s,$t,$d,$src)",("$m",modId),("$f",file.Row.Id),("$s",core.Id),("$t",IniParser.StripQuotes(technical)),("$d",IniParser.StripQuotes(display)),("$src",source));
        }
    }

    private static void SavePreparedImage(SqliteConnection c, SqliteTransaction tx, long modId, ImagePrepareItem image)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO images(mod_id,relative_path,filename,extension,size,sha256,width,height,mtime_ms,resource_subtype) VALUES($m,$r,$f,$e,$s,$h,$w,$he,$mt,'other') ON CONFLICT(mod_id,relative_path) DO UPDATE SET filename=excluded.filename,extension=excluded.extension,size=excluded.size,sha256=excluded.sha256,width=excluded.width,height=excluded.height,mtime_ms=excluded.mtime_ms";
        Add(cmd,
            ("$m", modId),
            ("$r", image.RelativePath),
            ("$f", Path.GetFileName(image.AbsolutePath)),
            ("$e", Path.GetExtension(image.AbsolutePath).ToLowerInvariant()),
            ("$s", image.Size),
            ("$h", (object?)image.Sha256 ?? DBNull.Value),
            ("$w", (object?)image.Width ?? DBNull.Value),
            ("$he", (object?)image.Height ?? DBNull.Value),
            ("$mt", image.MtimeMs));
        cmd.ExecuteNonQuery();
    }

    private static void SyncMaps(SqliteConnection c, SqliteTransaction tx, long modId, string root, IReadOnlyList<string> imagePaths, CancellationToken ct)
    {
        Execute(c, tx, "DELETE FROM maps WHERE mod_id=$m;", ("$m", modId));
        foreach (var imagePath in imagePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsMapPreviewImage(imagePath)) continue;
            var tmx = FindMapTmx(imagePath);
            if (tmx is null) continue;
            var imageRel = PathResolver.NormalizeRelative(root, imagePath);
            var tmxRel = PathResolver.NormalizeRelative(root, tmx);
            var imageId = SelectNullableInt64(c, tx, "SELECT id FROM images WHERE mod_id=$m AND relative_path=$p", ("$m", modId), ("$p", imageRel));
            if (!imageId.HasValue) continue;
            Execute(c, tx, "INSERT OR REPLACE INTO maps(mod_id,image_id,image_relative_path,tmx_relative_path) VALUES($m,$i,$p,$t);", ("$m",modId), ("$i",imageId.GetValueOrDefault()), ("$p",imageRel), ("$t",tmxRel));
        }
    }

    private static bool IsMapPreviewImage(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path).Trim();
        if (stem.Equals("map", StringComparison.OrdinalIgnoreCase)) return true;
        return stem.EndsWith("_map", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("-map", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(" map", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindMapTmx(string imagePath)
    {
        var directory = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var baseStem = stem;
        foreach (var suffix in new[] { "_map", "-map", " map" })
        {
            if (baseStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                baseStem = baseStem[..^suffix.Length];
                break;
            }
        }
        var candidates = new[]
        {
            Path.Combine(directory, baseStem + ".tmx"),
            Path.Combine(directory, stem + ".tmx"),
            Path.Combine(directory, "map.tmx")
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static Dictionary<long,Dictionary<string,IniSection>> BuildSectionMaps(List<FileState> files,SqliteConnection c,SqliteTransaction tx,long modId)
    {
        var map=new Dictionary<long,Dictionary<string,IniSection>>();
        foreach(var f in files)
        {
            var sections=new Dictionary<string,IniSection>(StringComparer.OrdinalIgnoreCase);
            foreach(var section in f.Parsed.Sections) sections[section.Name]=section;
            map[f.Row.Id]=sections;
        }
        return map;
    }

    private static void BuildExpandedSectionMaps(ModSpec mod, List<FileState> files, Dictionary<long, Dictionary<string, IniSection>> expanded, Dictionary<long, Dictionary<string, IniSection>> own, CancellationToken ct)
    {
        var byPath = files.ToDictionary(f => Path.GetFullPath(f.Row.AbsolutePath), StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<long>();
        var done = new HashSet<long>();
        void Build(FileState state)
        {
            if (done.Contains(state.Row.Id)) return;
            if (!visiting.Add(state.Row.Id)) return;
            var merged = new Dictionary<string, IniSection>(StringComparer.OrdinalIgnoreCase);
            foreach (var refText in state.Parsed.CopyFrom.SelectMany(IniParser.SplitRefs))
            {
                ct.ThrowIfCancellationRequested();
                var p = PathResolver.Resolve(mod.RootPath, state.Row.AbsolutePath, refText);
                if (p.Type != "path" || p.Target is null) continue;
                if (!byPath.TryGetValue(Path.GetFullPath(p.Target), out var parent)) continue;
                Build(parent);
                foreach (var kv in expanded.GetValueOrDefault(parent.Row.Id) ?? new(StringComparer.OrdinalIgnoreCase))
                    if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
            }
            foreach (var kv in own[state.Row.Id]) merged[kv.Key] = kv.Value;
            expanded[state.Row.Id] = merged;
            visiting.Remove(state.Row.Id);
            done.Add(state.Row.Id);
        }
        foreach (var state in files) Build(state);
    }

    private static Dictionary<string,TemplateSection> BuildTemplateSections(FileState? template)
    {
        var result=new Dictionary<string,TemplateSection>(StringComparer.OrdinalIgnoreCase);if(template is null)return result;foreach(var s in template.Parsed.Sections){var vals=new Dictionary<string,IniKey>(StringComparer.OrdinalIgnoreCase);var defines=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var src=new Dictionary<string,SourceRef>(StringComparer.OrdinalIgnoreCase);foreach(var k in s.Keys){if(k.Key.Equals("@define",StringComparison.OrdinalIgnoreCase)){var i=k.Value.IndexOf(':');if(i>0)defines[k.Value[..i].Trim()]=k.Value[(i+1)..].Trim();}else if(!k.Key.StartsWith('@')){vals[k.Key]=k;src[k.Key]=new(template.Row.Id,s.Id,s.Name,k.LineNo,"template");}}result[s.Name]=new(s,vals,defines,src);}return result;
    }

    private ResolvedSection ResolveSection(long contextFileId,string name,List<FileState> files,Dictionary<long,Dictionary<string,IniSection>> sectionsByFile,long? templateFileId,Dictionary<string,TemplateSection> templateSections,Dictionary<(long,string),ResolvedSection> memo,HashSet<(long,string)> visiting,SqliteConnection c,SqliteTransaction tx,long modId)
    {
        var cacheKey=(contextFileId,name);
        if(memo.TryGetValue(cacheKey,out var cached)) return cached;
        if(!visiting.Add(cacheKey)) return new(new(StringComparer.OrdinalIgnoreCase),new(StringComparer.OrdinalIgnoreCase),new(StringComparer.OrdinalIgnoreCase),true,null);
        var state=files.FirstOrDefault(f=>f.Row.Id==contextFileId);
        var available=sectionsByFile.GetValueOrDefault(contextFileId);
        var local=state?.Parsed.Sections.LastOrDefault(s=>s.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
        var result=new Dictionary<string,IniKey>(StringComparer.OrdinalIgnoreCase);
        var defines=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var sources=new Dictionary<string,SourceRef>(StringComparer.OrdinalIgnoreCase);
        void AddParent(ResolvedSection parent,SourceRef fallback)
        {
            if(parent.Cycle) return;
            foreach(var kv in parent.Values) if(!result.ContainsKey(kv.Key)){result[kv.Key]=kv.Value;sources[kv.Key]=parent.Sources.GetValueOrDefault(kv.Key,fallback);}
            foreach(var d in parent.Defines) defines.TryAdd(d.Key,d.Value);
        }
        // If the section comes through file-level copyFrom, resolve it in the source file.
        if(local is null && available is not null && available.TryGetValue(name,out var availableSection))
        {
            var sourceFile=files.FirstOrDefault(f=>f.Parsed.Sections.Any(s=>s.Id==availableSection.Id));
            if(sourceFile is not null && sourceFile.Row.Id!=contextFileId)
                AddParent(ResolveSection(sourceFile.Row.Id,name,files,sectionsByFile,templateFileId,templateSections,memo,visiting,c,tx,modId),new(sourceFile.Row.Id,availableSection.Id,name,availableSection.LineStart,"inherited"));
        }
        var isUnit=state is not null && IsUnitFile(state.Parsed,state.Row.AbsolutePath);
        if(isUnit && templateFileId.HasValue && contextFileId!=templateFileId && templateSections.TryGetValue(name,out var tpl))
        {
            foreach(var kv in tpl.Values) if(!result.ContainsKey(kv.Key)){result[kv.Key]=kv.Value;sources[kv.Key]=tpl.Sources[kv.Key];}
            foreach(var d in tpl.Defines) defines.TryAdd(d.Key,d.Value);
        }
        if(local is not null)
        {
            foreach(var rec in local.Keys)
            {
                var lower=rec.Key.ToLowerInvariant();
                if(lower=="@copyfromsection")
                {
                    foreach(var rr in IniParser.SplitRefs(rec.Value))
                    {
                        var parentName=Path.GetFileName(rr.Trim());
                        var parent=ResolveSection(contextFileId,parentName,files,sectionsByFile,templateFileId,templateSections,memo,visiting,c,tx,modId);
                        AddParent(parent,new(contextFileId,local.Id,parentName,rec.LineNo,"inherited"));
                        Execute(c,tx,"INSERT OR IGNORE INTO inheritance_edges(mod_id,child_section_id,parent_section_id,relation_type,relation_text) VALUES($m,$c,$p,'copyFromSection',$r)",("$m",modId),("$c",local.Id),("$p",(object?)parent.Section?.Id??DBNull.Value),("$r",rr));
                    }
                    continue;
                }
                if(rec.Key.StartsWith('@'))
                {
                    if(lower=="@define"){var i=rec.Value.IndexOf(':');if(i>0)defines[rec.Value[..i].Trim()]=rec.Value[(i+1)..].Trim();}
                    continue;
                }
                result[rec.Key]=rec;sources[rec.Key]=new(contextFileId,local.Id,local.Name,rec.LineNo,"direct");
            }
        }
        visiting.Remove(cacheKey);
        var resolved=new ResolvedSection(result,defines,sources,false,local??available?.GetValueOrDefault(name));
        memo[cacheKey]=resolved;
        return resolved;
    }

    private static string ExpandVariables(string value,string section,Dictionary<string,ResolvedSection> localContext,int depth=0)
    {
        if(depth>12)return value;
        return RegexVariable().Replace(value,m=>{var e=m.Groups[1].Value.Trim();if(e.Contains('(')&&e.EndsWith(')')){var i=e.IndexOf('(');var inner=e[(i+1)..^1].Trim();return ExpandVariables(ResolveVariable(inner,section,localContext),section,localContext,depth+1);}return ExpandVariables(ResolveVariable(e,section,localContext),section,localContext,depth+1);});
    }
    private static string ResolveVariable(string expr,string section,Dictionary<string,ResolvedSection> localContext){if(!expr.Contains('.'))return localContext.GetValueOrDefault(section)?.Defines.GetValueOrDefault(expr)??"";var p=expr.Split('.',2);return localContext.GetValueOrDefault(p[0])?.Values.GetValueOrDefault(p[1])?.Value??"";}
    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex RegexVariable();

    private static bool ShouldIndexImage(string key,string value,HashSet<string> imageKeys)=>imageKeys.Contains(IniParser.NormalizeImageKeyName(key))||IniParser.IsImagePathLike(value);
    private static bool DetectAnimationReference(string section,ISet<string> keys,string key){var s=section.ToLowerInvariant();if(s.StartsWith("animation")||s.Contains("_animation")||s.Contains("animation_"))return true;var k=key.ToLowerInvariant();if(k.StartsWith("animation_")||k.StartsWith("animateframe"))return true;return AnimationMarkerKeys.Overlaps(keys);}

    private static void InsertReference(SqliteConnection c,SqliteTransaction tx,long? imageId,long modId,long fileId,long sectionId,long? unitId,string key,string raw,string? rel,string relation,SourceRef source,string? inherited,string status,bool animation)=>Execute(c,tx,"INSERT OR IGNORE INTO image_references(image_id,mod_id,file_id,section_id,unit_id,key_name,raw_value,resolved_path,relation_type,source_file_id,source_section_id,inherited_from,status,is_animation) VALUES($i,$m,$f,$s,$u,$k,$r,$p,$rel,$sf,$ss,$inh,$st,$a)",("$i",(object?)imageId??DBNull.Value),("$m",modId),("$f",fileId),("$s",sectionId),("$u",(object?)unitId??DBNull.Value),("$k",key),("$r",raw),("$p",(object?)rel??DBNull.Value),("$rel",relation),("$sf",source.FileId),("$ss",source.SectionId),("$inh",(object?)inherited??DBNull.Value),("$st",status),("$a",animation?1:0));
    private static void ClassifyImages(SqliteConnection c, SqliteTransaction tx, long modId)
    {
        // Основано на структуре resources/rusted_warfare_docs.db:
        // [graphics].image -> основное тело, image_wreak -> труп,
        // image_turret / [turret_NAME].image -> башня,
        // [projectile_NAME] -> снаряд/боеприпасы,
        // [action_NAME]/[hiddenAction_NAME] -> активная способность,
        // [effect_NAME]/[animation_NAME] -> эффект.
        // Отдельно учитываются building-пометки через [core]: isBuilding.
        Execute(c, tx, @"
WITH ref_flags AS (
    SELECT
        r.image_id,
        MAX(CASE WHEN lower(s.name) LIKE 'turret%' OR lower(r.key_name) = 'image_turret' THEN 1 ELSE 0 END) AS has_tower,
        MAX(CASE WHEN lower(s.name) LIKE 'projectile%' THEN 1 ELSE 0 END) AS has_projectile,
        MAX(CASE WHEN lower(s.name) LIKE 'action%' OR lower(s.name) LIKE 'hiddenaction%' THEN 1 ELSE 0 END) AS has_ability,
        MAX(CASE WHEN lower(s.name) LIKE 'effect%' OR lower(s.name) LIKE 'animation%' THEN 1 ELSE 0 END) AS has_effect,
        MAX(CASE WHEN lower(r.key_name) = 'image_wreak' OR lower(s.name) LIKE '%corpse%' OR lower(s.name) LIKE '%wreak%' OR lower(s.name) LIKE '%wreck%' THEN 1 ELSE 0 END) AS has_corpse,
        MAX(CASE WHEN lower(s.name) LIKE 'weapon%' OR lower(r.key_name) LIKE '%weapon%' THEN 1 ELSE 0 END) AS has_weapon,
        MAX(CASE WHEN lower(s.name) = 'graphics' AND lower(r.key_name) IN ('image', 'image_back') THEN 1 ELSE 0 END) AS has_main_body,
        MAX(CASE WHEN lower(s.name) LIKE 'leg_%' OR lower(s.name) LIKE 'arm_%' THEN 1 ELSE 0 END) AS has_mech_part,
        MAX(CASE WHEN r.unit_id IS NOT NULL THEN 1 ELSE 0 END) AS has_unit_ref
    FROM image_references r
    JOIN sections s ON s.id = r.section_id
    WHERE r.mod_id = $m
    GROUP BY r.image_id
), building_flags AS (
    SELECT
        r.image_id,
        MAX(CASE WHEN lower(k.key_name) = 'isbuilding' AND lower(trim(replace(k.value, '""', ''))) = 'true' THEN 1 ELSE 0 END) AS is_building,
        MAX(CASE WHEN r.unit_id IS NOT NULL THEN 1 ELSE 0 END) AS is_unit,
        MAX(CASE WHEN r.unit_id IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM image_references tr
                      JOIN sections ts ON ts.id = tr.section_id
                      WHERE tr.unit_id = r.unit_id AND lower(ts.name) LIKE 'turret%'
                  )
                  AND EXISTS (
                      SELECT 1 FROM sections gs
                      JOIN image_references gr ON gr.section_id = gs.id
                      WHERE gr.image_id = r.image_id
                        AND gr.unit_id = r.unit_id
                        AND lower(gs.name) = 'graphics'
                        AND lower(gr.key_name) IN ('image', 'image_back')
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM keys ak
                      JOIN sections ats ON ats.id = ak.section_id
                      JOIN units unit_ref ON unit_ref.id = r.unit_id
                      WHERE ats.file_id = unit_ref.file_id
                        AND lower(ats.name) = 'attack'
                        AND lower(ak.key_name) = 'canattack'
                        AND lower(trim(replace(ak.value, '""', ''))) = 'true'
                  ) THEN 1 ELSE 0 END) AS is_chassis
    FROM image_references r
    LEFT JOIN units u ON u.id = r.unit_id
    LEFT JOIN keys k ON k.section_id = u.section_id
    WHERE r.mod_id = $m
    GROUP BY r.image_id
), map_flags AS (
    SELECT image_id FROM maps WHERE mod_id = $m
)
UPDATE images
SET
    usage_count = (SELECT COUNT(*) FROM image_references r WHERE r.image_id = images.id AND r.status = 'ok'),
    missing_count = (SELECT COUNT(*) FROM image_references r WHERE r.image_id = images.id AND r.status = 'missing'),
    is_used = CASE WHEN EXISTS(SELECT 1 FROM image_references r WHERE r.image_id = images.id AND r.status = 'ok') OR EXISTS(SELECT 1 FROM maps mp WHERE mp.image_id = images.id) THEN 1 ELSE 0 END,
    is_animation = CASE WHEN EXISTS(SELECT 1 FROM image_references r WHERE r.image_id = images.id AND r.status = 'ok' AND r.is_animation = 1) THEN 1 ELSE 0 END,
    resource_type = CASE
        WHEN EXISTS(SELECT 1 FROM map_flags mf WHERE mf.image_id = images.id) THEN 'map'
        WHEN COALESCE((SELECT is_building FROM building_flags bf WHERE bf.image_id = images.id), 0) = 1 THEN 'building'
        WHEN COALESCE((SELECT is_unit FROM building_flags bf WHERE bf.image_id = images.id), 0) = 1 THEN 'unit'
        ELSE 'other'
    END,
    resource_subtype = CASE
        WHEN EXISTS(SELECT 1 FROM map_flags mf WHERE mf.image_id = images.id) THEN 'map'
        WHEN COALESCE((SELECT is_building FROM building_flags bf WHERE bf.image_id = images.id), 0) = 1 THEN
            CASE
                WHEN COALESCE((SELECT has_projectile FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'building:ammunition'
                WHEN COALESCE((SELECT has_ability FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'building:active_ability'
                WHEN COALESCE((SELECT has_effect FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'building:effect'
                WHEN COALESCE((SELECT has_tower FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'building:tower'
                WHEN COALESCE((SELECT has_weapon FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'building:weapon'
                WHEN COALESCE((SELECT has_main_body FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'building:body'
                ELSE 'building:other'
            END
        WHEN COALESCE((SELECT is_unit FROM building_flags bf WHERE bf.image_id = images.id), 0) = 1 THEN
            CASE
                WHEN COALESCE((SELECT has_corpse FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'unit:corpse'
                WHEN COALESCE((SELECT has_projectile FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'unit:projectile'
                WHEN COALESCE((SELECT has_ability FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'unit:active_ability'
                WHEN COALESCE((SELECT has_effect FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'unit:effect'
                WHEN COALESCE((SELECT has_tower FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'unit:tower'
                WHEN COALESCE((SELECT is_chassis FROM building_flags bf WHERE bf.image_id = images.id), 0) = 1 THEN 'unit:chassis'
                WHEN COALESCE((SELECT has_main_body FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'unit:body'
                WHEN COALESCE((SELECT has_mech_part FROM ref_flags rf WHERE rf.image_id = images.id), 0) = 1 THEN 'unit:other'
                ELSE 'unit:other'
            END
        ELSE 'other'
    END
WHERE mod_id = $m;", ("$m", modId));
    }

    private HashSet<string> LoadDocsImageKeys()
    {
        var result=new HashSet<string>(FallbackImageKeys,StringComparer.OrdinalIgnoreCase);if(!File.Exists(_docsDbPath))return result;using var c=new SqliteConnection($"Data Source={_docsDbPath};Mode=ReadOnly");c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT key_name FROM keys WHERE lower(value_type) LIKE '%image%'";using var r=cmd.ExecuteReader();while(r.Read())result.Add(IniParser.NormalizeImageKeyName(r.GetString(0)));return result;
    }

    public static async Task<string> ExtractRwmodAsync(string archivePath,string hash,string cacheRoot,CancellationToken ct)
    {
        var info=new FileInfo(archivePath);
        if(info.Length>MaxArchiveBytes)throw new InvalidOperationException(".rwmod превышает лимит 2 GiB.");
        var outRoot=Path.Combine(cacheRoot,hash);
        var marker=Path.Combine(outRoot,".extracted");
        if(File.Exists(marker))return outRoot;
        Directory.CreateDirectory(cacheRoot);
        var temp=outRoot+".tmp";
        try
        {
            if(Directory.Exists(temp))Directory.Delete(temp,true);
            Directory.CreateDirectory(temp);
            using var archive=new ZipArchive(File.OpenRead(archivePath),ZipArchiveMode.Read,false,Encoding.UTF8);
            long total=0;
            foreach(var e in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if(e.FullName.EndsWith('/'))continue;
                var normalized=e.FullName.Replace('\\','/');
                var dest=Path.GetFullPath(Path.Combine(temp,normalized.Replace('/',Path.DirectorySeparatorChar)));
                var baseRoot=Path.GetFullPath(temp).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!dest.StartsWith(baseRoot,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Небезопасный путь внутри .rwmod.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using var source=e.Open();
                await using var target=File.Create(dest);
                await source.CopyToAsync(target,ct);
                total+=e.Length;
                if(total>MaxArchiveBytes)throw new InvalidOperationException("Распакованный .rwmod превышает лимит 2 GiB.");
            }
            File.WriteAllText(Path.Combine(temp,".extracted"),hash);
            if(Directory.Exists(outRoot))Directory.Delete(outRoot,true);
            Directory.Move(temp,outRoot);
            return outRoot;
        }
        finally
        {
            if(Directory.Exists(temp))
            {
                try { Directory.Delete(temp,true); } catch(Exception ex) { AppLog.Warn($"Не удалось очистить временную папку .rwmod: {ex.Message}"); }
            }
        }
    }

    private static bool IsIniLike(string p)=>Path.GetExtension(p).Equals(".ini",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(p).Equals(".template",StringComparison.OrdinalIgnoreCase);
    private static bool IsImage(string p)=>IniParser.ImageExtensions.Contains(Path.GetExtension(p));
    private static bool IsUnitFile(ParsedIni p,string path)=>Path.GetExtension(path).Equals(".ini",StringComparison.OrdinalIgnoreCase)&&!Path.GetFileName(path).Equals("all-units.ini",StringComparison.OrdinalIgnoreCase)&&p.Sections.Any(s=>s.Name.Equals("core",StringComparison.OrdinalIgnoreCase));
    private static string? KeyValue(IniSection s,string key)=>s.Keys.LastOrDefault(x=>x.Key.Equals(key,StringComparison.OrdinalIgnoreCase))?.Value;
    private static IniSection CloneSection(IniSection source){var s=new IniSection{Name=source.Name,LineStart=source.LineStart,OrderIndex=source.OrderIndex};s.Keys.AddRange(source.Keys);return s;}
    private static (int? Width,int? Height) ReadImageDimensions(Stream stream,string ext){using var ms=new MemoryStream();stream.CopyTo(ms);var b=ms.ToArray();try{if(ext.Equals(".png",StringComparison.OrdinalIgnoreCase)&&b.Length>=24&&Encoding.ASCII.GetString(b,1,3)=="PNG")return (ReadBeInt32(b,16),ReadBeInt32(b,20));if(ext.Equals(".gif",StringComparison.OrdinalIgnoreCase)&&b.Length>=10&&Encoding.ASCII.GetString(b,0,3)=="GIF")return(BitConverter.ToUInt16(b,6),BitConverter.ToUInt16(b,8));if((ext.Equals(".jpg",StringComparison.OrdinalIgnoreCase)||ext.Equals(".jpeg",StringComparison.OrdinalIgnoreCase))&&b.Length>4&&b[0]==0xff&&b[1]==0xd8){var i=2;while(i+9<b.Length){if(b[i]!=0xff){i++;continue;}var marker=b[i+1];var len=(b[i+2]<<8)|b[i+3];if(marker>=0xc0&&marker<=0xc3)return((b[i+5]<<8)|b[i+6],(b[i+7]<<8)|b[i+8]);if(len==0)break;i+=2+len;}}if(ext.Equals(".webp",StringComparison.OrdinalIgnoreCase)&&b.Length>=30&&Encoding.ASCII.GetString(b,0,4)=="RIFF"&&Encoding.ASCII.GetString(b,8,4)=="WEBP"&&Encoding.ASCII.GetString(b,12,4)=="VP8X")return(1+b[24]+(b[25]<<8)+(b[26]<<16),1+b[27]+(b[28]<<8)+(b[29]<<16));}catch{}return(null,null);}
    private static int ReadBeInt32(byte[] b,int o)=>(b[o]<<24)|(b[o+1]<<16)|(b[o+2]<<8)|b[o+3];
    private static async Task<string> ComputeSha256Async(string p, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = File.Open(p, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    private static void ThrowIfCancelled(CancellationToken ct,int files,int images,int refs,int missing,int mods){if(ct.IsCancellationRequested)throw new ScanCancelledException(new ScanSummary(mods,files,images,refs,missing,true,false));}
    private static long? GetUnitId(SqliteConnection c,SqliteTransaction tx,long modId,long fileId)=>SelectNullableInt64(c,tx,"SELECT id FROM units WHERE mod_id=$m AND file_id=$f LIMIT 1",("$m",modId),("$f",fileId));

    private static void DeleteStaleMods(SqliteConnection c,SqliteTransaction tx,string root,string[] keep){if(keep.Length==0){Execute(c,tx,"DELETE FROM mods WHERE root_path=$r",("$r",root));return;}Execute(c,tx,$"DELETE FROM mods WHERE root_path=$r AND relative_path NOT IN ({string.Join(',',keep.Select((_,i)=>$"$k{i}"))})",new (string Key, object? Value)[]{("$r",root)}.Concat(keep.Select((x,i)=>("$k"+i,(object?)x))).ToArray());}
    private static void DeleteStaleFiles(SqliteConnection c,SqliteTransaction tx,long modId,HashSet<string> keep){if(keep.Count==0){Execute(c,tx,"DELETE FROM files WHERE mod_id=$m",("$m",modId));return;}Execute(c,tx,$"DELETE FROM files WHERE mod_id=$m AND relative_path NOT IN ({string.Join(',',keep.Select((_,i)=>$"$p{i}"))})",new (string Key, object? Value)[]{("$m",modId)}.Concat(keep.Select((x,i)=>("$p"+i,(object?)x))).ToArray());}
    private static void DeleteStaleImages(SqliteConnection c,SqliteTransaction tx,long modId,HashSet<string> keep){if(keep.Count==0){Execute(c,tx,"DELETE FROM images WHERE mod_id=$m",("$m",modId));return;}Execute(c,tx,$"DELETE FROM images WHERE mod_id=$m AND relative_path NOT IN ({string.Join(',',keep.Select((_,i)=>$"$p{i}"))})",new (string Key, object? Value)[]{("$m",modId)}.Concat(keep.Select((x,i)=>("$p"+i,(object?)x))).ToArray());}

    private static (string Decision,string Hash)? GetRwmodDecision(SqliteConnection c,SqliteTransaction tx,string path){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT decision,archive_hash FROM rwmod_decisions WHERE archive_path=$p";Add(cmd,("$p",path));using var r=cmd.ExecuteReader();return r.Read()?(r.GetString(0),r.GetString(1)):null;}
    private static void SaveRwmodDecision(SqliteConnection c,SqliteTransaction tx,string path,string hash,string decision)=>Execute(c,tx,"INSERT INTO rwmod_decisions(archive_path,archive_hash,decision,decided_at) VALUES($p,$h,$d,$at) ON CONFLICT(archive_path) DO UPDATE SET archive_hash=excluded.archive_hash,decision=excluded.decision,decided_at=excluded.decided_at",("$p",path),("$h",hash),("$d",decision),("$at",DateTimeOffset.UtcNow.ToString("O")));

    private static void InsertScanRun(SqliteConnection c,SqliteTransaction tx,out long id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO scan_runs(started_at,status) VALUES($d,'running') RETURNING id";Add(cmd,("$d",DateTimeOffset.UtcNow.ToString("O")));id=Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);}
    private void UpdateScanRun(long id,ScanSummary s){using var c=_db.Open();c.Open();Execute(c,null,"UPDATE scan_runs SET finished_at=$d,mods_count=$m,files_count=$f,images_count=$i,references_count=$r,missing_count=$x,status='done' WHERE id=$id",("$d",DateTimeOffset.UtcNow.ToString("O")),("$m",s.Mods),("$f",s.Files),("$i",s.Images),("$r",s.References),("$x",s.Missing),("$id",id));}
    private void UpdateLastRunCancelled(ScanSummary s,bool saved){using var c=_db.Open();c.Open();Execute(c,null,"UPDATE scan_runs SET finished_at=$d,mods_count=$m,files_count=$f,images_count=$i,references_count=$r,missing_count=$x,status=$st WHERE id=(SELECT MAX(id) FROM scan_runs)",("$d",DateTimeOffset.UtcNow.ToString("O")),("$m",s.Mods),("$f",s.Files),("$i",s.Images),("$r",s.References),("$x",s.Missing),("$st",saved?"cancelled-saved":"cancelled-discarded"));}
    private void MarkRunCancelled(ScanSummary s){UpdateLastRunCancelled(s,false);}

    private static string? Scalar(SqliteConnection c,SqliteTransaction? tx,string sql){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;return cmd.ExecuteScalar()?.ToString();}
    private static long? SelectNullableInt64(SqliteConnection c,SqliteTransaction tx,string sql,params (string Key,object? Value)[] args){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;Add(cmd,args);var v=cmd.ExecuteScalar();return v is null or DBNull?null:Convert.ToInt64(v,CultureInfo.InvariantCulture);}
    private static void Execute(SqliteConnection c,SqliteTransaction? tx,string sql,params (string Key,object? Value)[] args){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;Add(cmd,args);cmd.ExecuteNonQuery();}
    private static void Add(SqliteCommand cmd,params (string Key,object? Value)[] args){foreach(var (k,v) in args)cmd.Parameters.AddWithValue(k,v??DBNull.Value);}
    private static FileStateRow? QueryFile(SqliteConnection c,SqliteTransaction tx,long modId,string rel){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT id,mod_id,relative_path,absolute_path,size,mtime_ms,sha256 FROM files WHERE mod_id=$m AND relative_path=$r";Add(cmd,("$m",modId),("$r",rel));using var r=cmd.ExecuteReader();return !r.Read()?null:new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetInt64(5),r.IsDBNull(6)?null:r.GetString(6));}
    private static ImageStateRow? QueryImage(SqliteConnection c,SqliteTransaction tx,long modId,string rel){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT size,mtime_ms,sha256,width,height FROM images WHERE mod_id=$m AND relative_path=$r";Add(cmd,("$m",modId),("$r",rel));using var r=cmd.ExecuteReader();return !r.Read()?null:new(r.GetInt64(0),r.GetInt64(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetInt32(3),r.IsDBNull(4)?null:r.GetInt32(4));}

    private sealed record ModSpec(string Name,string RootPath,string RelativePath,string SourceType,string? PackagePath,string? PackageHash);
    private sealed record FileStateRow(long Id,long ModId,string RelativePath,string AbsolutePath,long Size,long MtimeMs,string? Sha256);
    private sealed record FileState(FileStateRow Row,ParsedIni Parsed,List<SectionRow> Sections);
    private sealed record SectionRow(long Id,long FileId,string Name,int LineStart,IniSection Parsed);
    private sealed record ImageStateRow(long Size,long MtimeMs,string? Sha256,int? Width,int? Height);
    private sealed record SourceRef(long FileId,long SectionId,string SectionName,int LineNo,string Relation);
    private sealed record TemplateSection(IniSection Section,Dictionary<string,IniKey> Values,Dictionary<string,string> Defines,Dictionary<string,SourceRef> Sources);
    private sealed record ResolvedSection(Dictionary<string,IniKey> Values,Dictionary<string,string> Defines,Dictionary<string,SourceRef> Sources,bool Cycle,IniSection? Section);
    private sealed class Totals { public int Mods,Files,Images,References,Missing; public ScanSummary ToSummary()=>new(Mods,Files,Images,References,Missing); public ScanSummary ToSummary(int mods)=>new(mods,Files,Images,References,Missing,true,false); }
    private sealed record ModScanResult(int Files,int Images,int References,int Missing);
}
