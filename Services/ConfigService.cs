namespace RustedShpizhionStudio.Services;

public sealed class ConfigService
{
    public string AppRoot => AppContext.BaseDirectory;
    public string PrimaryConfigPath => Path.Combine(AppRoot, "config.json");
    public string BackupConfigPath => Path.Combine(AppRoot, "config.json.bak");
    public string IndexPath => Path.Combine(AppRoot, "index.db");
    public string CachePath => Path.Combine(AppRoot, "rwmod-cache");
    public string DocsDbPath => Path.Combine(AppRoot, "resources", "rusted_warfare_docs.db");

    public async Task<(AppConfig Config, string ActivePath)> LoadAsync()
    {
        foreach (var path in new[] { PrimaryConfigPath, BackupConfigPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = await File.ReadAllTextAsync(path);
                var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
                if (cfg is null)
                {
                    AppLog.Warn($"Config load returned null: {path}");
                    continue;
                }

                Normalize(cfg);
                if (!string.Equals(path, PrimaryConfigPath, StringComparison.OrdinalIgnoreCase))
                    AppLog.Warn($"Primary config is unavailable; restored settings from backup: {path}");
                else
                    AppLog.Info($"Config loaded from: {path}");
                return (cfg, path);
            }
            catch (JsonException ex)
            {
                AppLog.Warn($"Config JSON is invalid ({path}): {ex.Message}");
            }
            catch (IOException ex)
            {
                AppLog.Warn($"Config read failed ({path}): {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Warn($"Config access denied ({path}): {ex.Message}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Unexpected config load failure ({path})", ex);
            }
        }

        AppLog.Info("No valid portable config found — using defaults");
        return (new AppConfig(), PrimaryConfigPath);
    }

    public async Task<string> SaveAsync(AppConfig config)
    {
        Normalize(config);
        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
        Directory.CreateDirectory(AppRoot);

        // Файловые операции на Windows могут неожиданно зависнуть на несколько
        // секунд из-за Defender/индексатора/блокировки файла. Не удерживаем ими
        // UI-поток Avalonia.
        await Task.Run(() => SaveCore(json)).ConfigureAwait(true);
        return PrimaryConfigPath;
    }

    private string SaveCore(string json)
    {
        var tempPath = PrimaryConfigPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // Write beside the real config and replace only after the complete JSON
            // has reached disk. A process crash during serialization/write therefore
            // cannot leave config.json half-written.
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));

            if (File.Exists(PrimaryConfigPath))
            {
                try
                {
                    File.Replace(tempPath, PrimaryConfigPath, BackupConfigPath, true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempPath, PrimaryConfigPath, true);
                }
                catch (IOException)
                {
                    // File.Replace can fail on some filesystems while Move still
                    // succeeds. Keep the fallback local to the portable directory.
                    File.Move(tempPath, PrimaryConfigPath, true);
                }
            }
            else
            {
                File.Move(tempPath, PrimaryConfigPath);
            }

            AppLog.Info($"Config saved to portable path: {PrimaryConfigPath}");
            return PrimaryConfigPath;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Cannot save portable config: {PrimaryConfigPath}", ex);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }
        }
    }

    private static void Normalize(AppConfig config)
    {
        config.ConfigVersion = 15;
        var safeIndexDefault = Math.Max(1, Environment.ProcessorCount - 2);
        var safeComputeDefault = Math.Max(1, Environment.ProcessorCount - 1);
        config.IndexParallelism = Math.Clamp(config.IndexParallelism <= 0 ? safeIndexDefault : config.IndexParallelism, 1, Math.Max(1, Environment.ProcessorCount));
        config.ComputeParallelism = Math.Clamp(config.ComputeParallelism <= 0 ? safeComputeDefault : config.ComputeParallelism, 1, Math.Max(1, Environment.ProcessorCount));
        if (config.DangerousMode)
        {
            config.IndexParallelism = Math.Max(1, Environment.ProcessorCount);
            config.ComputeParallelism = Math.Max(1, Environment.ProcessorCount);
        }

        config.ImagePageSize = Math.Clamp(config.ImagePageSize, 50, 1000);
        config.CardLayout = config.CardLayout is "grid-2" or "grid-3" or "grid-4" or "grid-5" or "list" ? config.CardLayout : "grid-3";
        config.NormalizePadding = Math.Clamp(config.NormalizePadding, 2, 35);
        config.WindowMode = config.WindowMode is "windowed" or "borderless" or "fullscreen" ? config.WindowMode : "windowed";
        config.PhotoMemoryLimitMb = Math.Clamp(config.PhotoMemoryLimitMb <= 0 ? 1024 : config.PhotoMemoryLimitMb, 1024, 2048);
        config.CardMemoryLimitMb = Math.Clamp(config.CardMemoryLimitMb <= 0 ? 512 : config.CardMemoryLimitMb, 512, 1024);
        config.PredictionDepth = Math.Clamp(config.PredictionDepth <= 0 ? 2 : config.PredictionDepth, 1, 5);
    }
}
