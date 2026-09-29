namespace RustedShpizhionStudio.Services;

public sealed class TranslationService(IndexDatabase db)
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru,en;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://translate.google.com/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/153.0.0.0 Safari/537.36");
        return client;
    }

    public async Task<string> TranslateAsync(string text, string targetLang = "ru", CancellationToken ct = default)
    {
        var value = text.Trim();
        if (value.Length == 0) return string.Empty;
        var cached = db.GetTranslation(value, targetLang);
        if (!string.IsNullOrWhiteSpace(cached)) return cached;
        var attempts = new (string Url, bool Dictionary)[]
        {
            ($"https://translate.googleapis.com/translate_a/single?client=dict-chrome-ex&dt=t&sl=auto&tl={Uri.EscapeDataString(targetLang)}&q={Uri.EscapeDataString(value)}", false),
            ($"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl={Uri.EscapeDataString(targetLang)}&q={Uri.EscapeDataString(value)}", true),
            ($"https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl=auto&tl={Uri.EscapeDataString(targetLang)}&q={Uri.EscapeDataString(value)}", false)
        };
        var errors = new List<string>();
        foreach (var attempt in attempts)
        {
            try
            {
                using var response = await Http.GetAsync(attempt.Url, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Translation HTTP {(int)response.StatusCode}");
                using var doc = JsonDocument.Parse(json);
                var translated = attempt.Dictionary ? ParseDictionary(doc.RootElement) : ParsePrimary(doc.RootElement);
                db.SaveTranslation(value, targetLang, translated);
                return translated;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { errors.Add(ex.Message); }
        }
        throw new InvalidOperationException($"Переводчик недоступен: {string.Join(" → ", errors)}");
    }

    private static string ParsePrimary(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Неожиданный ответ Google Translate.");
        var sb = new StringBuilder();
        foreach (var part in root[0].EnumerateArray()) if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0) sb.Append(part[0].GetString());
        var s = sb.ToString().Trim(); if (s.Length == 0) throw new InvalidOperationException("Переводчик вернул пустой результат."); return s;
    }
    private static string ParseDictionary(JsonElement root)
    {
        if (!root.TryGetProperty("sentences", out var sentences) || sentences.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Неожиданный ответ резервного переводчика.");
        var sb = new StringBuilder(); foreach (var part in sentences.EnumerateArray()) if (part.TryGetProperty("trans", out var t)) sb.Append(t.GetString());
        var s=sb.ToString().Trim(); if(s.Length==0)throw new InvalidOperationException("Резервный переводчик вернул пустой результат.");return s;
    }
}
