namespace RustedShpizhionStudio.Services;

/// <summary>
/// GitHub-backed release discovery and single-file application updater.
/// The public repository is intentionally read anonymously: no token is needed.
/// </summary>
public static class UpdateService
{
    private const string Owner = "YaraYT";
    private const string Repo = "RustedSpiwiStudio";
    private const string ApiVersion = "2026-03-10";

    public const string CurrentVersion = "0.1.1";

    private static readonly string ReleasesApiUrl =
        $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=100";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"RustedSpiwiStudio/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
        return client;
    }

    public static async Task<IReadOnlyList<GitHubRelease>?> GetReleasesAsync(CancellationToken ct = default)
    {
        try
        {
            var all = new List<GitHubRelease>();
            for (var page = 1; page <= 20; page++)
            {
                var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=100&page={page}";
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Warn($"UpdateService releases request returned {(int)response.StatusCode} {response.ReasonPhrase}.");
                    return all.Count == 0 ? null : all.ToArray();
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var releases = await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.ListGitHubRelease, ct) ?? [];
                var valid = releases.Where(x => !x.Draft && TryParseVersion(x.TagName, out _)).ToArray();
                all.AddRange(valid);
                if (releases.Count < 100) break;
            }
            return all;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"UpdateService releases check failed: {ex.Message}");
            return null;
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var releases = await GetReleasesAsync(ct);
        if (releases is null || releases.Count == 0)
            return null;

        var current = ParseVersionOrNull(CurrentVersion);
        if (current is null)
            return null;

        GitHubRelease? newest = null;
        SemVersion newestVersion = default;
        foreach (var release in releases)
        {
            if (!TryParseVersion(release.TagName, out var version))
                continue;

            if (newest is null || version.CompareTo(newestVersion) > 0)
            {
                newest = release;
                newestVersion = version;
            }
        }

        if (newest is null)
            return null;

        var isNewer = newestVersion.CompareTo(current.Value) > 0;
        var asset = isNewer ? FindInstallerAsset(newest) : null;
        AppLog.Info($"UpdateService: latest={newest.TagName} current={CurrentVersion} newer={isNewer} asset={(asset?.Name ?? "none")}");
        return new UpdateInfo(newest, asset, isNewer);
    }

    public static async Task<bool> InstallUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (!info.CanInstall || info.Asset is null)
            throw new InvalidOperationException("У релиза нет доступного EXE с SHA-256 для безопасной установки.");

        var asset = info.Asset;
        var expectedDigest = ExtractSha256(asset.Digest!);
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Автообновление доступно только для опубликованного EXE-файла.");

        var appDirectory = Path.GetDirectoryName(processPath)!;
        if (!Directory.Exists(appDirectory))
            throw new DirectoryNotFoundException(appDirectory);

        var downloadPath = Path.Combine(Path.GetTempPath(), $"RustedSpiwiStudio-{Guid.NewGuid():N}.exe");
        var stagedPath = Path.Combine(appDirectory, $".RustedSpiwiStudio-update-{Guid.NewGuid():N}.exe");
        var batchPath = Path.Combine(Path.GetTempPath(), $"RustedSpiwiStudio-update-{Guid.NewGuid():N}.bat");

        try
        {
            AppLog.Info($"UpdateService: downloading {asset.Name} from {asset.BrowserDownloadUrl}");
            using (var response = await Http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                await source.CopyToAsync(destination, ct);
            }

            ct.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(downloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
            {
                var actual = await SHA256.HashDataAsync(stream, ct);
                var actualHex = Convert.ToHexString(actual).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(actualHex),
                        Encoding.ASCII.GetBytes(expectedDigest.ToLowerInvariant())))
                {
                    throw new InvalidDataException($"Проверка SHA-256 не пройдена. Ожидалось {expectedDigest}, получено {actualHex}.");
                }
            }

            // Single-file release contains everything needed by the application.
            // Only the EXE is replaced, so portable user data stays untouched.
            File.Copy(downloadPath, stagedPath, overwrite: false);

            var pid = Environment.ProcessId;
            var batch = BuildUpdaterBatch(processPath, stagedPath, pid);
            await File.WriteAllTextAsync(batchPath, batch, new UTF8Encoding(false), ct);

            AppLog.Info($"UpdateService: SHA-256 verified. Staged update at {stagedPath}; updater={batchPath}");
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/C \"\"{batchPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = appDirectory
            });
            return true;
        }
        catch
        {
            TryDelete(downloadPath);
            TryDelete(stagedPath);
            TryDelete(batchPath);
            throw;
        }
        finally
        {
            // Once the updater has been launched, it owns the staged file.
            // The temporary download can always be removed here.
            TryDelete(downloadPath);
        }
    }

    private static GitHubReleaseAsset? FindInstallerAsset(GitHubRelease release)
        => release.Assets
            .Where(x => string.Equals(x.State, "uploaded", StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace(x.BrowserDownloadUrl))
            .Where(x => !string.IsNullOrWhiteSpace(x.Digest))
            .OrderByDescending(x => x.Name.Contains("Rusted", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Size)
            .FirstOrDefault();

    private static string ExtractSha256(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || digest.Length != prefix.Length + 64)
            throw new InvalidDataException("GitHub asset не содержит корректный SHA-256 digest.");
        return digest[prefix.Length..];
    }

    private static string BuildUpdaterBatch(string appPath, string stagedPath, int pid)
    {
        static string Quote(string value) => value.Replace("%", "%%").Replace("\"", "\"\"");

        var app = Quote(appPath);
        var staged = Quote(stagedPath);
        return $"@echo off\r\n" +
               "setlocal EnableExtensions\r\n" +
               $"set \"APP={app}\"\r\n" +
               $"set \"NEW={staged}\"\r\n" +
               $"set \"PID={pid}\"\r\n" +
               ":wait_for_app\r\n" +
               "tasklist /FI \"PID eq %PID%\" /NH | find \"%PID%\" >nul\r\n" +
               "if not errorlevel 1 (\r\n" +
               "  timeout /t 1 /nobreak >nul\r\n" +
               "  goto wait_for_app\r\n" +
               ")\r\n" +
               ":replace\r\n" +
               "move /Y \"%NEW%\" \"%APP%\" >nul 2>&1\r\n" +
               "if errorlevel 1 (\r\n" +
               "  timeout /t 1 /nobreak >nul\r\n" +
               "  goto replace\r\n" +
               ")\r\n" +
               "start \"\" \"%APP%\"\r\n" +
               "del \"%~f0\" >nul 2>&1\r\n" +
               "exit /b 0\r\n";
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private readonly record struct SemVersion(int Major, int Minor, int Patch, int Revision, string[] PreRelease)
        : IComparable<SemVersion>
    {
        public int CompareTo(SemVersion other)
        {
            var numeric = Major.CompareTo(other.Major);
            if (numeric != 0) return numeric;
            numeric = Minor.CompareTo(other.Minor);
            if (numeric != 0) return numeric;
            numeric = Patch.CompareTo(other.Patch);
            if (numeric != 0) return numeric;
            numeric = Revision.CompareTo(other.Revision);
            if (numeric != 0) return numeric;

            var thisHasPre = PreRelease.Length > 0;
            var otherHasPre = other.PreRelease.Length > 0;
            if (!thisHasPre && !otherHasPre) return 0;
            if (!thisHasPre) return 1;
            if (!otherHasPre) return -1;

            var count = Math.Min(PreRelease.Length, other.PreRelease.Length);
            for (var i = 0; i < count; i++)
            {
                var left = PreRelease[i];
                var right = other.PreRelease[i];
                var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
                var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
                if (leftNumeric && rightNumeric)
                {
                    numeric = leftNumber.CompareTo(rightNumber);
                    if (numeric != 0) return numeric;
                }
                else if (leftNumeric != rightNumeric)
                {
                    return leftNumeric ? -1 : 1;
                }
                else
                {
                    numeric = string.CompareOrdinal(left, right);
                    if (numeric != 0) return numeric;
                }
            }
            return PreRelease.Length.CompareTo(other.PreRelease.Length);
        }
    }

    private static SemVersion? ParseVersionOrNull(string value)
        => TryParseVersion(value, out var version) ? version : null;

    private static bool TryParseVersion(string raw, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.StartsWith('v')) text = text[1..];
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        var dash = text.IndexOf('-');
        var numericPart = dash >= 0 ? text[..dash] : text;
        var prerelease = dash >= 0 ? text[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : [];
        var numbers = numericPart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (numbers.Length is < 1 or > 4) return false;
        var parsed = new int[4];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (!int.TryParse(numbers[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i])) return false;
        }
        version = new SemVersion(parsed[0], numbers.Length > 1 ? parsed[1] : 0, numbers.Length > 2 ? parsed[2] : 0, numbers.Length > 3 ? parsed[3] : 0, prerelease);
        return true;
    }
}
