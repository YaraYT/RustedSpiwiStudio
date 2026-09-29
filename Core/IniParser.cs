namespace RustedShpizhionStudio.Core;

public static class IniParser
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif" };
    public static readonly HashSet<string> SpecialImageValues = new(StringComparer.OrdinalIgnoreCase)
        { "NONE", "AUTO", "AUTO_ANIMATED" };

    public static ParsedIni ParseText(string text, string absolutePath)
    {
        var result = new ParsedIni { AbsolutePath = absolutePath };
        var lines = text.TrimStart('\uFEFF').Split('\n');
        IniSection? current = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//") || trimmed.StartsWith(';')) continue;
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = new IniSection { Name = trimmed[1..^1].Trim(), LineStart = i + 1, OrderIndex = result.Sections.Count };
                result.Sections.Add(current);
                continue;
            }
            var colon = trimmed.IndexOf(':');
            if (colon <= 0 || current is null) continue;
            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            var rec = new IniKey(key, value, i + 1, key.StartsWith('@'));
            current.Keys.Add(rec);
            if (current.Name.Equals("core", StringComparison.OrdinalIgnoreCase) && key.Equals("copyFrom", StringComparison.OrdinalIgnoreCase))
                result.CopyFrom.Add(value);
        }
        return result;
    }

    public static async Task<ParsedIni> ParseFileAsync(string path, CancellationToken ct = default)
        => ParseText(await File.ReadAllTextAsync(path, ct), path);

    public static string StripQuotes(string value)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')) ? v[1..^1] : v;
    }

    public static string NormalizeImageKeyName(string key)
    {
        var i = key.IndexOf(':');
        return (i >= 0 ? key[..i] : key).Trim();
    }

    public static bool IsImagePathLike(string value)
    {
        var clean = StripQuotes(value).Split('?', '#')[0].Replace('\\', '/');
        return ImageExtensions.Any(ext => clean.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeSectionName(string value) => StripQuotes(value).Trim();

    public static List<string> SplitRefs(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
