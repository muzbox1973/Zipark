using System.IO.Compression;
using System.Security.Cryptography;
using Zipark.Cache;
using Zipark.Compression;
using Zipark.Core;
using Zipark.VirtualFileSystem;
using CompressionLevel = Zipark.Compression.CompressionLevel;

namespace Zipark.Tests;

public class VirtualFileSystemTests
{
    private readonly SevenZipEngine _engine = TestUtil.Engine();

    private static byte[] ReadAll(ReadOnlyArchiveFileSystem fs, VfsNode node, int bufferSize = 70_000)
    {
        var result = new byte[node.Size];
        long off = 0;
        var buf = new byte[bufferSize];
        while (off < node.Size)
        {
            Assert.Equal(VfsStatus.Success, fs.Read(node, off, buf, out var n));
            Assert.True(n > 0);
            buf.AsSpan(0, n).CopyTo(result.AsSpan((int)off));
            off += n;
        }
        return result;
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip, false)]
    [InlineData(ArchiveFormat.Zip, true)]
    [InlineData(ArchiveFormat.SevenZip, false)]
    [InlineData(ArchiveFormat.SevenZip, true)]
    public async Task MountedArchive_FilesReadBackIdentical(ArchiveFormat format, bool encrypted)
    {
        using var t = new TempDir("vfs");
        var src = TestUtil.CreateSampleTree(t.Path, 3_500_000);
        var archive = t.Sub("m" + ArchiveFormats.Extension(format));
        using var pw = encrypted ? TestUtil.Pw("mount-Pw-1") : null;
        await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Format = format, Password = pw }, null);
        var before = Checksums.Sha256OfFile(archive);

        using (var fs = await ReadOnlyArchiveFileSystem.OpenAsync(archive, _engine, pw, new CacheOptions { DiskRoot = t.Sub("cache") }))
        {
            Assert.Equal(encrypted, fs.Encrypted);
            Assert.Equal(VfsStatus.Success, fs.Open(@"\src\sub\random.bin", out var node));
            Assert.Equal(3_500_000, node!.Size);
            Assert.Equal(File.ReadAllBytes(Path.Combine(src, "sub", "random.bin")), ReadAll(fs, node));

            // Random access in the middle of a file (crosses a chunk boundary).
            var mid = new byte[10];
            Assert.Equal(VfsStatus.Success, fs.Read(node, (1 << 20) - 5, mid, out var n));
            Assert.Equal(10, n);
            Assert.Equal(File.ReadAllBytes(Path.Combine(src, "sub", "random.bin")).AsSpan((1 << 20) - 5, 10).ToArray(), mid);

            Assert.Equal(VfsStatus.Success, fs.Open(@"\SRC\한글 문서.TXT", out var ko)); // case-insensitive
            Assert.Equal(File.ReadAllBytes(Path.Combine(src, "한글 문서.txt")), ReadAll(fs, ko!));
            Assert.Equal(VfsStatus.Success, fs.Open(@"\src\빈 폴더", out var emptyDir));
            Assert.True(emptyDir!.IsDirectory);
            Assert.Equal(VfsStatus.ObjectNotFound, fs.Open(@"\src\nope.txt", out _));
            Assert.Equal(VfsStatus.PathNotFound, fs.Open(@"\nope\x.txt", out _));
            Assert.Equal(VfsStatus.WriteProtected, ReadOnlyArchiveFileSystem.DenyModification());
            Assert.True((node.Attributes & VfsNode.AttrReadOnly) != 0);

            // The archive cannot be modified while mounted.
            Assert.ThrowsAny<IOException>(() => File.OpenWrite(archive).Dispose());
        }
        Assert.Equal(before, Checksums.Sha256OfFile(archive));
    }

    [Fact]
    public async Task SolidBlock_DecodedOnceForNeighbours()
    {
        using var t = new TempDir("solid");
        var src = t.Sub("src");
        Directory.CreateDirectory(src);
        for (int i = 0; i < 20; i++) File.WriteAllBytes(Path.Combine(src, $"f{i:D2}.bin"), RandomNumberGenerator.GetBytes(50_000));
        var archive = t.Sub("solid.7z");
        await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Solid = true, Level = CompressionLevel.Fast }, null);
        using var fs = await ReadOnlyArchiveFileSystem.OpenAsync(archive, _engine, null, new CacheOptions { DiskRoot = t.Sub("cache") });
        Assert.Equal(1, fs.Content.Stats.SolidGroups);

        foreach (var i in new[] { 15, 3, 19, 0, 7 })
        {
            fs.Open($@"\src\f{i:D2}.bin", out var node);
            Assert.Equal(File.ReadAllBytes(Path.Combine(src, $"f{i:D2}.bin")), ReadAll(fs, node!));
        }
        // One engine run decoded the whole block; every later read was a cache hit.
        Assert.Equal(1, fs.Content.Stats.DecodeRuns);
    }

    [Fact]
    public async Task ParallelReaders_GetConsistentData()
    {
        using var t = new TempDir("par");
        var src = t.Sub("src");
        Directory.CreateDirectory(src);
        for (int i = 0; i < 8; i++) File.WriteAllBytes(Path.Combine(src, $"p{i}.bin"), RandomNumberGenerator.GetBytes(400_000));
        var archive = t.Sub("p.7z");
        await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Solid = false }, null);
        using var fs = await ReadOnlyArchiveFileSystem.OpenAsync(archive, _engine, null, new CacheOptions { DiskRoot = t.Sub("cache") });
        // WinFsp calls Read on its own threads; emulate that with dedicated threads instead of the shared pool.
        await Parallel.ForEachAsync(Enumerable.Range(0, 32), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
        {
            await Task.Yield();
            var name = $"p{i % 8}.bin";
            fs.Open(@"\src\" + name, out var node);
            Assert.Equal(File.ReadAllBytes(Path.Combine(src, name)), ReadAll(fs, node!, 33_333));
        });
    }

    [Fact]
    public async Task SmallCache_StillServesLargeFile()
    {
        using var t = new TempDir("small");
        var src = TestUtil.CreateSampleTree(t.Path, 6_000_000);
        var archive = t.Sub("s.7z");
        await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive }, null);
        using var fs = await ReadOnlyArchiveFileSystem.OpenAsync(archive, _engine, null,
            new CacheOptions { MemoryLimitBytes = 2 << 20, DiskLimitBytes = 0, EnableDisk = false });
        fs.Open(@"\src\sub\random.bin", out var node);
        Assert.Equal(File.ReadAllBytes(Path.Combine(src, "sub", "random.bin")), ReadAll(fs, node!, 1 << 20));
        Assert.True(fs.CacheStats.MemoryBytes <= 2 << 20);
    }

    [Fact]
    public async Task WrongPassword_IsRejectedAtMount()
    {
        using var t = new TempDir("vfspw");
        var src = TestUtil.CreateSampleTree(t.Path, 1000);
        foreach (var format in new[] { ArchiveFormat.Zip, ArchiveFormat.SevenZip })
        {
            var archive = t.Sub("w" + ArchiveFormats.Extension(format));
            using (var pw = TestUtil.Pw("right-one"))
                await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Format = format, Password = pw }, null);
            using var wrong = TestUtil.Pw("wrong-one");
            var ex = await Assert.ThrowsAsync<ZiparkException>(() => ReadOnlyArchiveFileSystem.OpenAsync(archive, _engine, wrong));
            Assert.Equal(ErrorCode.WrongPassword, ex.Code);
            var none = await Assert.ThrowsAsync<ZiparkException>(() => ReadOnlyArchiveFileSystem.OpenAsync(archive, _engine, null));
            Assert.Equal(ErrorCode.PasswordRequired, none.Code);
        }
    }

    [Fact]
    public async Task UnsafeEntryNames_AreNotExposed()
    {
        using var t = new TempDir("vfsunsafe");
        var archive = t.Sub("u.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("ok/file.txt").Open())) w.Write("ok");
            using (var w = new StreamWriter(zip.CreateEntry("../../escape.txt").Open())) w.Write("bad");
        }
        using var fs = await ReadOnlyArchiveFileSystem.OpenAsync(archive, _engine, null, new CacheOptions { DiskRoot = t.Sub("cache") });
        Assert.Equal(VfsStatus.Success, fs.Open(@"\ok\file.txt", out var node));
        Assert.Equal("ok"u8.ToArray(), ReadAll(fs, node!));
        Assert.Single(fs.Index.Root.Children);
        Assert.True(fs.Content.Stats.ManagedZipReads >= 1); // unencrypted Deflate uses the in-process fast path
    }

    [Fact]
    public void DirectoryEnumeration_IsSortedAndMarkerAware()
    {
        var info = new ArchiveInfo
        {
            ArchivePath = "x.zip",
            Format = ArchiveFormat.Zip,
            Entries = new[] { "b.txt", "A.txt", "c\\d.txt", "C\\e.txt", "a2.txt" }
                .Select((p, i) => new ArchiveEntry { Index = i, Path = p, Size = 1 }).ToList(),
        };
        var index = new ArchiveIndex(info);
        Assert.Equal(["A.txt", "a2.txt", "b.txt", "c"], index.Root.Children.Select(c => c.Name));
        Assert.Equal(["b.txt", "c"], index.Root.ChildrenAfter("a2.txt").Select(c => c.Name));
        Assert.Equal(2, index.Lookup(@"\C")!.Children.Count);
    }
}
