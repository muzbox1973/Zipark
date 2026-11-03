namespace Zipark.Core;

/// <summary>One item inside an archive, as reported by the engine listing.</summary>
public sealed record ArchiveEntry
{
    /// <summary>Position of the entry in the archive's own order (0-based).</summary>
    public int Index { get; init; }
    /// <summary>Path inside the archive using '\' separators, exactly as stored.</summary>
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public long? PackedSize { get; init; }
    public DateTime? Modified { get; init; }
    public DateTime? Created { get; init; }
    public uint? Crc { get; init; }
    public bool Encrypted { get; init; }
    public string? Method { get; init; }
    /// <summary>7z solid block (folder) number; null when the entry has no data or the format has no blocks.</summary>
    public int? Block { get; init; }
    /// <summary>Raw attribute string from the engine, e.g. "A", "D", "RHA", "-rwxr-xr-x".</summary>
    public string? Attributes { get; init; }
    public bool IsSymbolicLink { get; init; }
    public string? LinkTarget { get; init; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
}

public sealed record ArchiveInfo
{
    public required string ArchivePath { get; init; }
    public ArchiveFormat Format { get; init; }
    public long PhysicalSize { get; init; }
    public bool IsSolid { get; init; }
    public int? BlockCount { get; init; }
    public bool HeadersEncrypted { get; init; }
    public string? Method { get; init; }
    public int VolumeCount { get; init; } = 1;
    public IReadOnlyList<ArchiveEntry> Entries { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool HasEncryptedEntries => HeadersEncrypted || Entries.Any(e => e.Encrypted);
    public long TotalUncompressedSize => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
    public int FileCount => Entries.Count(e => !e.IsDirectory);
}

public sealed record TestResult(bool Ok, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, int FilesTested);
