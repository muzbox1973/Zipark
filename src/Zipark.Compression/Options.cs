using Zipark.Core;
using Zipark.Crypto;

namespace Zipark.Compression;

public enum CompressionLevel
{
    Store = 0,
    Fastest = 1,
    Fast = 3,
    Normal = 5,
    Maximum = 7,
    Ultra = 9,
}

public enum CompressionMethod
{
    Default = 0,
    Lzma2,
    Lzma,
    Ppmd,
    BZip2,
    Deflate,
    Deflate64,
}

public sealed class CompressOptions
{
    public required IReadOnlyList<string> Sources { get; init; }
    public required string OutputPath { get; init; }
    public ArchiveFormat Format { get; init; } = ArchiveFormat.SevenZip;
    public CompressionLevel Level { get; init; } = CompressionLevel.Normal;
    /// <summary>0 = let the engine use all cores.</summary>
    public int Threads { get; init; }
    public SecretPassword? Password { get; init; }
    /// <summary>7z only: encrypt the file list (header encryption).</summary>
    public bool EncryptFileNames { get; init; } = true;
    public ZipEncryption ZipEncryption { get; init; } = ZipEncryption.Aes256;
    /// <summary>Bytes per volume; null = single file.</summary>
    public long? VolumeSize { get; init; }
    /// <summary>7z only; null = engine default (solid).</summary>
    public bool? Solid { get; init; }
    public CompressionMethod Method { get; init; } = CompressionMethod.Default;
    /// <summary>Dictionary size in MiB; null = engine default for the level.</summary>
    public int? DictionaryMb { get; init; }
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.Rename;
}

public sealed class ExtractOptions
{
    public required string ArchivePath { get; init; }
    public required string Destination { get; init; }
    public SecretPassword? Password { get; init; }
    /// <summary>Archive paths to extract (files or folders). Null/empty = everything.</summary>
    public IReadOnlyCollection<string>? Selection { get; init; }
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.Rename;
    /// <summary>
    /// Called for each name conflict when <see cref="Conflict"/> is <see cref="ConflictPolicy.Ask"/>.
    /// Runs on the extraction thread and may block until the user answers.
    /// </summary>
    public Func<ConflictInfo, ConflictDecision>? ConflictPrompt { get; init; }
    /// <summary>Drop folder structure and put every file directly into the destination.</summary>
    public bool FlattenPaths { get; init; }
    /// <summary>Strip this archive folder prefix from extracted paths (used when extracting a sub-folder).</summary>
    public string? StripPrefix { get; init; }
    public BombLimits Limits { get; init; } = new();
    /// <summary>User explicitly accepted size/ratio warnings. Unsafe paths are still never extracted.</summary>
    public bool AllowLimitOverride { get; init; }
    /// <summary>Pre-fetched listing (avoids listing twice).</summary>
    public ArchiveInfo? Listing { get; init; }
}

public sealed record ExtractResult(
    string Destination,
    int FilesWritten,
    int Skipped,
    int Renamed,
    IReadOnlyList<string> Blocked,
    IReadOnlyList<string> WrittenFiles);

public sealed record CompressResult(string ArchivePath, IReadOnlyList<string> Volumes, long InputBytes, long OutputBytes, TimeSpan Elapsed);
