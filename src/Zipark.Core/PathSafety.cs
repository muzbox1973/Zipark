namespace Zipark.Core;

/// <summary>
/// Defends extraction against Zip Slip / path traversal, absolute paths, alternate data streams,
/// reserved device names and reparse-point (symlink/junction) redirection.
/// </summary>
public static class PathSafety
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly char[] InvalidNameChars = ['<', '>', '"', '|', '?', '*', ':'];

    public enum Verdict { Safe, Traversal, Absolute, AlternateStream, ReservedName, InvalidCharacters, Empty, TooLong }

    /// <summary>Checks one archive entry path and returns the normalized relative path when it is safe.</summary>
    public static Verdict Check(string? entryPath, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(entryPath)) return Verdict.Empty;
        var p = entryPath.Replace('/', '\\');
        if (p.StartsWith('\\')) return Verdict.Absolute;
        if (p.Length >= 2 && p[1] == ':') return Verdict.Absolute;

        var parts = new List<string>();
        foreach (var seg in p.Split('\\'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            // "..", "...", ". ." all collapse to parent/current references once Windows strips dots and spaces.
            if (seg.Trim(' ', '.').Length == 0) return Verdict.Traversal;
            if (seg.Contains(':')) return Verdict.AlternateStream;
            if (seg.IndexOfAny(InvalidNameChars) >= 0 || seg.Any(c => c < 32)) return Verdict.InvalidCharacters;
            var stem = seg.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(stem)) return Verdict.ReservedName;
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return Verdict.InvalidCharacters;
            parts.Add(seg);
        }
        if (parts.Count == 0) return Verdict.Empty;
        normalized = string.Join('\\', parts);
        if (normalized.Length > 32000) return Verdict.TooLong;
        return Verdict.Safe;
    }

    public static bool IsSafe(string? entryPath) => Check(entryPath, out _) == Verdict.Safe;

    /// <summary>Resolves an entry path under <paramref name="root"/>; throws when it would escape the root.</summary>
    public static string ResolveUnder(string root, string entryPath)
    {
        var verdict = Check(entryPath, out var normalized);
        if (verdict != Verdict.Safe)
            throw new ZiparkException(ErrorCode.UnsafePath, $"Unsafe entry path ({verdict}): {entryPath}");
        var rootFull = EnsureTrailingSeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (!IsWithin(rootFull, full))
            throw new ZiparkException(ErrorCode.UnsafePath, $"Entry escapes destination: {entryPath}");
        return full;
    }

    public static bool IsWithin(string root, string candidate)
    {
        var r = EnsureTrailingSeparator(Path.GetFullPath(root));
        var c = Path.GetFullPath(candidate);
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase) || string.Equals(EnsureTrailingSeparator(c), r, StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    public static bool IsReparsePoint(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>True when any directory between root (exclusive) and path (inclusive) is a reparse point.</summary>
    public static bool HasReparsePointInChain(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\');
        var current = Path.GetFullPath(path);
        while (current.Length > rootFull.Length)
        {
            FileSystemInfo fi = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (fi.Exists && IsReparsePoint(fi)) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            current = parent;
        }
        return false;
    }

    /// <summary>Replaces characters that are invalid in Windows file names.</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).TrimEnd('.', ' ');
        if (s.Length == 0) s = "_";
        if (ReservedNames.Contains(s.Split('.')[0])) s = "_" + s;
        return s;
    }
}
