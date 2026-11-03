using System.Security.Cryptography;
using Zipark.Cache;
using Zipark.Core;
using Zipark.SplitVolumes;

namespace Zipark.Tests;

public class SplitVolumeTests
{
    [Fact]
    public async Task RawSplit_ThenMerge_IsBitExact()
    {
        using var t = new TempDir("split");
        var file = t.Sub("movie.mkv");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(2_500_000));
        var outDir = t.Sub("parts");
        var m = await FileSplitter.SplitAsync(file, outDir, 1_000_000);
        Assert.Equal(3, m.Parts.Count);
        Assert.Equal(500_000, m.Parts[2].Size);
        Assert.True(File.Exists(Path.Combine(outDir, "movie.mkv.001")));

        var merged = await FileSplitter.MergeAsync(Path.Combine(outDir, "movie.mkv.001"), t.Sub("merged.mkv"));
        Assert.True(merged.Verified);
        Assert.Equal(Checksums.Sha256OfFile(file), Checksums.Sha256OfFile(merged.OutputPath));
        Assert.Equal(m.OriginalSha256, merged.Sha256);
    }

    [Fact]
    public async Task RawMerge_DetectsMissingAndDamagedParts()
    {
        using var t = new TempDir("splitbad");
        var file = t.Sub("data.bin");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(300_000));
        var outDir = t.Sub("parts");
        await FileSplitter.SplitAsync(file, outDir, 100_000);

        var p2 = Path.Combine(outDir, "data.bin.002");
        var bytes = File.ReadAllBytes(p2);
        bytes[123] ^= 0xFF;
        File.WriteAllBytes(p2, bytes);
        var damaged = await Assert.ThrowsAsync<ZiparkException>(() => FileSplitter.MergeAsync(Path.Combine(outDir, "data.bin.001"), t.Sub("m1.bin")));
        Assert.Equal(ErrorCode.DamagedVolume, damaged.Code);
        Assert.False(File.Exists(t.Sub("m1.bin")));

        File.Delete(p2);
        var missing = await Assert.ThrowsAsync<ZiparkException>(() => FileSplitter.MergeAsync(Path.Combine(outDir, "data.bin.001"), t.Sub("m2.bin")));
        Assert.Equal(ErrorCode.MissingVolume, missing.Code);
    }

    [Fact]
    public void Detector_FindsNumberedVolumesAndGaps()
    {
        using var t = new TempDir("det");
        foreach (var n in new[] { "a.7z.001", "a.7z.002", "a.7z.004", "other.7z.001" }) File.WriteAllText(t.Sub(n), "x");
        var set = VolumeSetDetector.Detect(t.Sub("a.7z.002"));
        Assert.Equal(VolumeScheme.Numbered, set.Scheme);
        Assert.Equal(t.Sub("a.7z.001"), set.FirstVolume);
        Assert.Equal(3, set.Volumes.Count);
        Assert.Single(set.Missing);
        Assert.EndsWith("a.7z.003", set.Missing[0]);
    }

    [Fact]
    public void Detector_RecognisesZipSpannedAndRarSchemes()
    {
        using var t = new TempDir("det2");
        foreach (var n in new[] { "s.z01", "s.z02", "s.zip", "r.part1.rar", "r.part2.rar", "o.rar", "o.r00", "o.r01", "single.zip" })
            File.WriteAllText(t.Sub(n), "x");

        var zip = VolumeSetDetector.Detect(t.Sub("s.zip"));
        Assert.Equal(VolumeScheme.ZipSpanned, zip.Scheme);
        Assert.Equal(3, zip.Volumes.Count);
        Assert.Equal(t.Sub("s.zip"), zip.EnginePath);
        Assert.True(zip.IsComplete);

        var rar = VolumeSetDetector.Detect(t.Sub("r.part2.rar"));
        Assert.Equal(VolumeScheme.RarParts, rar.Scheme);
        Assert.Equal(t.Sub("r.part1.rar"), rar.FirstVolume);
        Assert.Equal(2, rar.Volumes.Count);

        var old = VolumeSetDetector.Detect(t.Sub("o.r01"));
        Assert.Equal(VolumeScheme.RarOld, old.Scheme);
        Assert.Equal(t.Sub("o.rar"), old.FirstVolume);
        Assert.Equal(3, old.Volumes.Count);

        Assert.Equal(VolumeScheme.Single, VolumeSetDetector.Detect(t.Sub("single.zip")).Scheme);
        File.Delete(t.Sub("s.z01"));
        Assert.False(VolumeSetDetector.Detect(t.Sub("s.zip")).IsComplete);
    }
}

public class CacheTests
{
    [Fact]
    public void Memory_LruEvictsAndSpillsToEncryptedDisk()
    {
        using var t = new TempDir("cache");
        using var cache = new HybridChunkCache(new CacheOptions { MemoryLimitBytes = 3000, DiskLimitBytes = 1_000_000, DiskRoot = t.Path });
        var secret = "TOP-SECRET-PLAINTEXT"u8.ToArray();
        var chunk0 = secret.Concat(new byte[980]).ToArray();
        cache.Put(new ChunkKey(1, 0), chunk0);
        cache.Put(new ChunkKey(1, 1), new byte[1000]);
        cache.Put(new ChunkKey(1, 2), new byte[1000]);
        cache.Put(new ChunkKey(1, 3), new byte[1000]); // pushes chunk 0 out of RAM

        var s = cache.Stats;
        Assert.True(s.MemoryBytes <= 3000);
        Assert.True(s.DiskChunks >= 1);

        // The spilled chunk must not be readable in plaintext on disk.
        foreach (var f in Directory.EnumerateFiles(cache.DiskDirectory!))
            Assert.Equal(-1, File.ReadAllBytes(f).AsSpan().IndexOf(secret));

        Assert.True(cache.TryGet(new ChunkKey(1, 0), out var back));
        Assert.Equal(chunk0, back);
    }

    [Fact]
    public void Disk_LimitIsHardCap_AndDisposeRemovesFiles()
    {
        using var t = new TempDir("cache2");
        string dir;
        using (var cache = new HybridChunkCache(new CacheOptions { MemoryLimitBytes = 1000, DiskLimitBytes = 5000, DiskRoot = t.Path }))
        {
            dir = cache.DiskDirectory!;
            for (int i = 0; i < 20; i++) cache.Put(new ChunkKey(7, i), new byte[900]);
            Assert.True(cache.Stats.DiskBytes <= 5000);
            Assert.True(cache.Stats.Evictions > 0);
            Assert.False(cache.TryGet(new ChunkKey(7, 0), out _)); // oldest is gone from both tiers
            Assert.True(cache.TryGet(new ChunkKey(7, 19), out _));
        }
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void TamperedDiskChunk_IsRejected()
    {
        using var t = new TempDir("cache3");
        using var cache = new HybridChunkCache(new CacheOptions { MemoryLimitBytes = 1000, DiskLimitBytes = 100_000, DiskRoot = t.Path });
        cache.Put(new ChunkKey(1, 0), new byte[900]);
        cache.Put(new ChunkKey(1, 1), new byte[900]); // chunk 0 spilled
        var file = Directory.EnumerateFiles(cache.DiskDirectory!).First();
        var raw = File.ReadAllBytes(file);
        raw[^1] ^= 1;
        File.WriteAllBytes(file, raw);
        Assert.False(cache.TryGet(new ChunkKey(1, 0), out _));
    }
}
