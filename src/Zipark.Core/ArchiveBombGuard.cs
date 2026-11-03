namespace Zipark.Core;

public sealed record BombLimits
{
    /// <summary>Total uncompressed bytes above which extraction is blocked unless the user overrides.</summary>
    public long MaxTotalBytes { get; init; } = 256L * 1024 * 1024 * 1024;
    /// <summary>Compression ratio (unpacked / packed) considered suspicious.</summary>
    public double SuspiciousRatio { get; init; } = 1000;
    /// <summary>The ratio check only applies when the unpacked total is at least this large.</summary>
    public long RatioCheckMinBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxEntries { get; init; } = 2_000_000;
    /// <summary>Depth of nested folders considered abusive.</summary>
    public int MaxDepth { get; init; } = 256;
}

public enum GuardLevel { Ok, Warning, Blocked }

public sealed record GuardReport(GuardLevel Level, IReadOnlyList<string> Findings, IReadOnlyList<ArchiveEntry> UnsafeEntries)
{
    public bool CanProceed => Level != GuardLevel.Blocked;
}

/// <summary>
/// Inspects an archive listing before anything is written to disk: archive bombs, unsafe paths,
/// symbolic links, duplicate names and missing disk space.
/// </summary>
public static class ArchiveBombGuard
{
    public static GuardReport Evaluate(ArchiveInfo info, BombLimits limits, string? destination = null, IEnumerable<ArchiveEntry>? selection = null)
    {
        var entries = (selection ?? info.Entries).ToList();
        var findings = new List<string>();
        var unsafeEntries = new List<ArchiveEntry>();
        var level = GuardLevel.Ok;

        void Raise(GuardLevel l, string msg)
        {
            findings.Add(msg);
            if (l > level) level = l;
        }

        if (entries.Count > limits.MaxEntries)
            Raise(GuardLevel.Blocked, $"entries:{entries.Count}>{limits.MaxEntries}");

        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!e.IsDirectory) total = checked(total + Math.Max(0, e.Size));
            var verdict = PathSafety.Check(e.Path, out var norm);
            if (verdict != PathSafety.Verdict.Safe)
            {
                unsafeEntries.Add(e);
                Raise(GuardLevel.Blocked, $"unsafe-path:{verdict}:{e.Path}");
                continue;
            }
            if (e.IsSymbolicLink)
            {
                unsafeEntries.Add(e);
                Raise(GuardLevel.Warning, $"symlink:{e.Path}");
            }
            if (norm.Count(c => c == '\\') + 1 > limits.MaxDepth)
                Raise(GuardLevel.Blocked, $"depth:{e.Path}");
            if (!e.IsDirectory && !seen.Add(norm))
                Raise(GuardLevel.Warning, $"duplicate:{e.Path}");
        }

        if (total > limits.MaxTotalBytes)
            Raise(GuardLevel.Blocked, $"total-size:{total}>{limits.MaxTotalBytes}");

        if (info.PhysicalSize > 0 && total >= limits.RatioCheckMinBytes)
        {
            var ratio = (double)total / info.PhysicalSize;
            if (ratio > limits.SuspiciousRatio)
                Raise(GuardLevel.Blocked, $"ratio:{ratio:F0}");
        }

        if (destination is not null)
        {
            var free = DiskSpace.GetFreeBytes(destination);
            if (free >= 0 && total > free)
                Raise(GuardLevel.Blocked, $"disk-space:{total}>{free}");
        }

        return new GuardReport(level, findings, unsafeEntries);
    }
}
