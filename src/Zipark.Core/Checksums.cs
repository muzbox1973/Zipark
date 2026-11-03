using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;

namespace Zipark.Core;

public enum ChecksumAlgorithm { Crc32, Sha256 }

public sealed record ChecksumResult(string Path, long Length, string Crc32, string Sha256);

/// <summary>Streaming CRC32 + SHA-256 over files (one pass, both digests).</summary>
public static class Checksums
{
    private const int BufferSize = 1 << 20;

    public static async Task<ChecksumResult> ComputeAsync(string path, IProgress<long>? bytesRead = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var r = await ComputeAsync(fs, bytesRead, ct).ConfigureAwait(false);
        return r with { Path = path };
    }

    public static async Task<ChecksumResult> ComputeAsync(Stream stream, IProgress<long>? bytesRead = null, CancellationToken ct = default)
    {
        var crc = new Crc32();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                crc.Append(buf.AsSpan(0, n));
                sha.AppendData(buf, 0, n);
                total += n;
                bytesRead?.Report(total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        var crcValue = crc.GetCurrentHashAsUInt32();
        return new ChecksumResult("", total, crcValue.ToString("X8"), Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    public static string Sha256OfFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>Writes a sha256sum-compatible manifest ("hash *relative/path").</summary>
    public static void WriteSha256Manifest(string manifestPath, IEnumerable<ChecksumResult> results, string baseDir)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
            sb.Append(r.Sha256).Append(" *").Append(Path.GetRelativePath(baseDir, r.Path).Replace('\\', '/')).Append('\n');
        File.WriteAllText(manifestPath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Parses a sha256sum-style manifest into (relativePath → hash).</summary>
    public static Dictionary<string, string> ReadSha256Manifest(string manifestPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(manifestPath))
        {
            var line = raw.Trim();
            if (line.Length < 66 || line.StartsWith('#')) continue;
            var hash = line[..64];
            var rest = line[64..].TrimStart();
            if (rest.StartsWith('*')) rest = rest[1..];
            map[rest.Replace('/', '\\')] = hash.ToLowerInvariant();
        }
        return map;
    }
}
