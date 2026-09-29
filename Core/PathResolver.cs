namespace RustedShpizhionStudio.Core;

public sealed record PathResolution(string Type, string? Target);

public static class PathResolver
{
    public static PathResolution Resolve(string modRoot, string sourceFile, string rawValue)
    {
        var value = IniParser.StripQuotes(rawValue).Trim();
        if (string.IsNullOrWhiteSpace(value)) return new("empty", null);
        var upper = value.ToUpperInvariant();
        if (upper.StartsWith("HTTP://") || upper.StartsWith("HTTPS://")) return new("external", null);
        if (upper.StartsWith("ROOT:"))
        {
            var rel = value[5..].TrimStart('\\', '/');
            return Build(modRoot, rel);
        }
        var sourceDir = Path.GetDirectoryName(sourceFile) ?? modRoot;
        var relative = value.TrimStart('\\', '/');
        return Build(modRoot, Path.Combine(sourceDir, relative));
    }

    private static PathResolution Build(string root, string target)
    {
        var full = Path.GetFullPath(target);
        var baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase))
            return new("outside-root", full);
        return new("path", full);
    }

    public static string NormalizeRelative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
