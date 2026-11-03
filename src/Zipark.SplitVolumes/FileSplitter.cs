using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zipark.Core;

namespace Zipark.SplitVolumes;

/// <summary>Manifest written next to raw split parts so a merge can detect missing or damaged parts.</summary>
public sealed class SplitManifest
{
    public const string Extension = ".fxsplit.json";

    public int Version { get; set; } = 1;
    public string OriginalName { get; set; } = "";
    public long OriginalSize { get; set; }
    public string OriginalSha256 { get; set; } = "";
    public long PartSize { get; set; }
    public List<SplitPart> Parts { get; set; } = [];
}

public sealed class SplitPart
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

[JsonSerializable(typeof(SplitManifest))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class SplitJsonContext : JsonSerializerContext;

public sealed record MergeResult(string OutputPath, long Bytes, bool Verified, string Sha256);

/// <summary>
/// Splits any file into fixed-size parts (name.ext.001 …) without compressing it, and merges them back.
/// Every part and the whole file are hashed with SHA-256; the original file is never modified.
/// </summary>
public static class FileSplitter
{
    public static readonly IReadOnlyList<(string Label, long Bytes)> Presets =
    [
        ("100 MB", 100L * 1024 * 1024),
        ("700 MB", 700L * 1024 * 1024),
        ("1 GB", 1L << 30),
        ("2 GB", 2L << 30),
        ("4 GB", 4L << 30),
    ];

    private const int BufferSize = 1 << 20;

    public static async Task<SplitManifest> SplitAsync(string input, string outputDir, long partSize, IProgress<JobProgress>? progress = null, CancellationToken ct = default)
    {
        if (partSize < 1024) throw new ZiparkException(ErrorCode.InvalidArgument, "Part size must be at least 1 KB.");
        var src = new FileInfo(input);
        if (!src.Exists) throw new ZiparkException(ErrorCode.FileNotFound, input);
        Directory.CreateDirectory(outputDir);
        DiskSpace.EnsureAvailable(outputDir, src.Length);

        var manifest = new SplitManifest { OriginalName = src.Name, OriginalSize = src.Length, PartSize = partSize };
        var tracker = new ProgressTracker(src.Length);
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        var written = new List<string>();
        try
        {
            await using var fs = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
            long done = 0;
            int index = 1;
            do
            {
                var partName = $"{src.Name}.{index:D3}";
                var partPath = Path.Combine(outputDir, partName);
                if (File.Exists(partPath)) throw new ZiparkException(ErrorCode.InvalidArgument, $"Part already exists: {partPath}");
                using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long partBytes = 0;
                written.Add(partPath);
                await using (var outFs = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
                {
                    while (partBytes < partSize)
                    {
                        int want = (int)Math.Min(BufferSize, partSize - partBytes);
                        int n = await fs.ReadAsync(buf.AsMemory(0, want), ct).ConfigureAwait(false);
                        if (n == 0) break;
                        await outFs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        partHash.AppendData(buf, 0, n);
                        whole.AppendData(buf, 0, n);
                        partBytes += n;
                        done += n;
                        progress?.Report(tracker.FromBytes(done, partName));
                    }
                }
                manifest.Parts.Add(new SplitPart { Name = partName, Size = partBytes, Sha256 = Convert.ToHexStringLower(partHash.GetHashAndReset()) });
                index++;
            } while (done < src.Length);

            manifest.OriginalSha256 = Convert.ToHexStringLower(whole.GetHashAndReset());
            var manifestPath = Path.Combine(outputDir, src.Name + SplitManifest.Extension);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, SplitJsonContext.Default.SplitManifest), ct).ConfigureAwait(false);
            return manifest;
        }
        catch
        {
            foreach (var w in written)
            {
                try { File.Delete(w); } catch { /* best effort */ }
            }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// Merges parts. Accepts the manifest, or any part (name.001): with a manifest every part is verified
    /// before writing, otherwise parts are concatenated and the result is reported as unverified.
    /// </summary>
    public static async Task<MergeResult> MergeAsync(string manifestOrPart, string? outputPath = null, IProgress<JobProgress>? progress = null, CancellationToken ct = default)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(manifestOrPart))!;
        SplitManifest? manifest = null;
        List<string> parts;
        string originalName;

        var manifestPath = manifestOrPart.EndsWith(SplitManifest.Extension, StringComparison.OrdinalIgnoreCase)
            ? manifestOrPart
            : FindManifest(manifestOrPart);
        if (manifestPath is not null && File.Exists(manifestPath))
        {
            manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), SplitJsonContext.Default.SplitManifest)
                ?? throw new ZiparkException(ErrorCode.CorruptArchive, "Invalid split manifest.");
            originalName = PathSafety.SanitizeFileName(Path.GetFileName(manifest.OriginalName));
            parts = [];
            foreach (var p in manifest.Parts)
            {
                var partPath = Path.Combine(dir, PathSafety.SanitizeFileName(Path.GetFileName(p.Name)));
                if (!File.Exists(partPath)) throw new ZiparkException(ErrorCode.MissingVolume, $"Missing part: {p.Name}");
                if (new FileInfo(partPath).Length != p.Size) throw new ZiparkException(ErrorCode.DamagedVolume, $"Part has wrong size: {p.Name}");
                parts.Add(partPath);
            }
        }
        else
        {
            var set = VolumeSetDetector.Detect(manifestOrPart);
            if (set.Scheme != VolumeScheme.Numbered) throw new ZiparkException(ErrorCode.InvalidArgument, "Select the first part (.001) or the .fxsplit.json manifest.");
            if (!set.IsComplete) throw new ZiparkException(ErrorCode.MissingVolume, "Missing part: " + Path.GetFileName(set.Missing[0]));
            parts = set.Volumes.ToList();
            originalName = Path.GetFileNameWithoutExtension(set.FirstVolume);
        }

        var output = outputPath ?? ConflictResolver.GetUniquePath(Path.Combine(dir, originalName));
        long total = parts.Sum(p => new FileInfo(p).Length);
        DiskSpace.EnsureAvailable(Path.GetDirectoryName(Path.GetFullPath(output))!, total);
        var tracker = new ProgressTracker(total);
        var tmp = output + ".fxtmp";
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            await using (var outFs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using var inFs = new FileStream(parts[i], FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    int n;
                    while ((n = await inFs.ReadAsync(buf.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                    {
                        await outFs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        partHash.AppendData(buf, 0, n);
                        whole.AppendData(buf, 0, n);
                        done += n;
                        progress?.Report(tracker.FromBytes(done, Path.GetFileName(parts[i])));
                    }
                    if (manifest is not null)
                    {
                        var h = Convert.ToHexStringLower(partHash.GetHashAndReset());
                        if (!string.Equals(h, manifest.Parts[i].Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new ZiparkException(ErrorCode.DamagedVolume, $"Part is damaged (SHA-256 mismatch): {manifest.Parts[i].Name}");
                    }
                }
            }
            var sha = Convert.ToHexStringLower(whole.GetHashAndReset());
            if (manifest is not null && !string.Equals(sha, manifest.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new ZiparkException(ErrorCode.ChecksumMismatch, "Merged file does not match the original SHA-256.");
            File.Move(tmp, output, overwrite: outputPath is not null);
            return new MergeResult(output, done, manifest is not null, sha);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static string? FindManifest(string part)
    {
        var name = Path.GetFileName(part);
        var dot = name.LastIndexOf('.');
        if (dot <= 0) return null;
        var original = name[..dot];
        var candidate = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(part))!, original + SplitManifest.Extension);
        return File.Exists(candidate) ? candidate : null;
    }
}
