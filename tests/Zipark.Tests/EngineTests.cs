using System.IO.Compression;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Crypto;
using CompressionLevel = Zipark.Compression.CompressionLevel;

namespace Zipark.Tests;

public class EngineTests
{
    private readonly SevenZipEngine _engine = TestUtil.Engine();

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    [InlineData(ArchiveFormat.Tar)]
    public async Task Roundtrip_PreservesSha256(ArchiveFormat format)
    {
        using var t = new TempDir("rt");
        var src = TestUtil.CreateSampleTree(t.Path);
        var archive = t.Sub("out" + ArchiveFormats.Extension(format));
        var r = await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Format = format, Level = CompressionLevel.Normal }, null);
        Assert.True(File.Exists(r.ArchivePath));

        var info = await _engine.ListAsync(archive, null);
        Assert.Equal(format, info.Format);
        Assert.Contains(info.Entries, e => e.Path == @"src\한글 문서.txt");

        var dest = t.Sub("x");
        var x = await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = dest }, null);
        Assert.Empty(x.Blocked);
        TestUtil.AssertSameTree(src, Path.Combine(dest, "src"));
        Assert.True(Directory.Exists(Path.Combine(dest, "src", "빈 폴더")));
        Assert.Empty(Directory.GetDirectories(dest, ".fxstage-*"));
    }

    [Theory]
    [InlineData(ArchiveFormat.GZip)]
    [InlineData(ArchiveFormat.Xz)]
    [InlineData(ArchiveFormat.BZip2)]
    public async Task SingleStreamFormats_Roundtrip(ArchiveFormat format)
    {
        using var t = new TempDir("ss");
        var src = TestUtil.CreateSampleTree(t.Path);
        var file = Path.Combine(src, "sub", "random.bin");
        var archive = t.Sub("random.bin" + ArchiveFormats.Extension(format));
        await _engine.CompressAsync(new CompressOptions { Sources = [file], OutputPath = archive, Format = format }, null);
        var dest = t.Sub("x");
        await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = dest }, null);
        Assert.Equal(Checksums.Sha256OfFile(file), Checksums.Sha256OfFile(Path.Combine(dest, "random.bin")));
    }

    [Fact]
    public async Task SingleStreamFormat_RejectsFolder()
    {
        using var t = new TempDir("ssf");
        var src = TestUtil.CreateSampleTree(t.Path);
        var ex = await Assert.ThrowsAsync<ZiparkException>(() =>
            _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = t.Sub("a.gz"), Format = ArchiveFormat.GZip }, null));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task SevenZip_Aes256_WithEncryptedNames()
    {
        using var t = new TempDir("aes7");
        var src = TestUtil.CreateSampleTree(t.Path);
        var archive = t.Sub("secret.7z");
        using (var pw = TestUtil.Pw("Corr3ct-Horse!한글"))
            await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Password = pw, EncryptFileNames = true }, null);

        // Header encryption: listing without a password must fail with PasswordRequired.
        var noPw = await Assert.ThrowsAsync<ZiparkException>(() => _engine.ListAsync(archive, null));
        Assert.Equal(ErrorCode.PasswordRequired, noPw.Code);

        using (var wrong = TestUtil.Pw("wrong"))
        {
            var bad = await Assert.ThrowsAsync<ZiparkException>(() => _engine.ListAsync(archive, wrong));
            Assert.Equal(ErrorCode.WrongPassword, bad.Code);
        }

        using var right = TestUtil.Pw("Corr3ct-Horse!한글");
        var info = await _engine.ListAsync(archive, right);
        Assert.Contains("7zAES", info.Method);
        Assert.All(info.Entries.Where(e => !e.IsDirectory && e.Size > 0), e => Assert.True(e.Encrypted));

        var dest = t.Sub("x");
        await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = dest, Password = right }, null);
        TestUtil.AssertSameTree(src, Path.Combine(dest, "src"));

        // The password must never appear in the archive bytes in plain text.
        var bytes = File.ReadAllBytes(archive);
        Assert.Equal(-1, bytes.AsSpan().IndexOf("Corr3ct-Horse"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("random.bin"u8));
    }

    [Fact]
    public async Task SevenZip_OpenAsync_PromptsAndFlagsEncryptedHeaders()
    {
        using var t = new TempDir("open");
        var src = TestUtil.CreateSampleTree(t.Path, 1000);
        var archive = t.Sub("s.7z");
        using (var pw = TestUtil.Pw("abc12345"))
            await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Password = pw }, null);
        int asked = 0;
        var (info, password) = await _engine.OpenAsync(archive, retry =>
        {
            asked++;
            return Task.FromResult<SecretPassword?>(TestUtil.Pw(retry ? "abc12345" : "nope"));
        });
        Assert.Equal(2, asked);
        Assert.True(info.HeadersEncrypted);
        Assert.NotNull(password);
        password!.Dispose();
    }

    [Fact]
    public async Task Zip_Aes256_Roundtrip_AndWrongPasswordDetected()
    {
        using var t = new TempDir("aesz");
        var src = TestUtil.CreateSampleTree(t.Path);
        var archive = t.Sub("secret.zip");
        using var pw = TestUtil.Pw("zip-Secret-2026");
        await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Format = ArchiveFormat.Zip, Password = pw }, null);
        var info = await _engine.ListAsync(archive, null); // zip names are not encrypted
        var enc = info.Entries.Where(e => !e.IsDirectory && e.Size > 0).ToList();
        Assert.All(enc, e => Assert.True(e.Encrypted));
        Assert.All(enc, e => Assert.Contains("AES-256", e.Method));

        using (var wrong = TestUtil.Pw("bad"))
        {
            var ex = await Assert.ThrowsAsync<ZiparkException>(() =>
                _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = t.Sub("bad"), Password = wrong }, null));
            Assert.Equal(ErrorCode.WrongPassword, ex.Code);
            // nothing half-written may remain
            Assert.Empty(Directory.EnumerateFiles(t.Sub("bad"), "*", SearchOption.AllDirectories));
        }

        var dest = t.Sub("x");
        await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = dest, Password = pw }, null);
        TestUtil.AssertSameTree(src, Path.Combine(dest, "src"));
    }

    [Theory]
    [InlineData(ArchiveFormat.SevenZip)]
    [InlineData(ArchiveFormat.Zip)]
    public async Task SplitVolumes_ExtractFromFirstVolume(ArchiveFormat format)
    {
        using var t = new TempDir("vol");
        var src = TestUtil.CreateSampleTree(t.Path, 900_000);
        var archive = t.Sub("split" + ArchiveFormats.Extension(format));
        var r = await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Format = format, VolumeSize = 200 * 1024 }, null);
        Assert.True(r.Volumes.Count >= 4, $"expected several volumes, got {r.Volumes.Count}");
        Assert.EndsWith(".001", r.ArchivePath);
        Assert.All(r.Volumes.SkipLast(1), v => Assert.Equal(200 * 1024, new FileInfo(v).Length));

        var dest = t.Sub("x");
        await _engine.ExtractAsync(new ExtractOptions { ArchivePath = r.ArchivePath, Destination = dest }, null);
        TestUtil.AssertSameTree(src, Path.Combine(dest, "src"));

        var test = await _engine.TestAsync(r.ArchivePath, null, null);
        Assert.True(test.Ok, string.Join("; ", test.Errors));
    }

    [Fact]
    public async Task SplitVolumes_MissingMiddleVolume_IsReported()
    {
        using var t = new TempDir("volmiss");
        var src = TestUtil.CreateSampleTree(t.Path, 900_000);
        var r = await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = t.Sub("m.7z"), VolumeSize = 200 * 1024, Level = CompressionLevel.Store }, null);
        File.Delete(r.Volumes[1]);
        var test = await _engine.TestAsync(r.ArchivePath, null, null);
        Assert.False(test.Ok);
    }

    [Fact]
    public async Task Test_DetectsCorruption()
    {
        using var t = new TempDir("corrupt");
        var src = TestUtil.CreateSampleTree(t.Path);
        var archive = t.Sub("c.7z");
        await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Level = CompressionLevel.Store }, null);
        Assert.True((await _engine.TestAsync(archive, null, null)).Ok);

        var bytes = File.ReadAllBytes(archive);
        for (int i = 2000; i < 2100; i++) bytes[i] ^= 0x5A; // flip data bytes inside the packed stream
        File.WriteAllBytes(archive, bytes);
        var result = await _engine.TestAsync(archive, null, null);
        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ZipSlip_EntriesAreNeverWrittenOutsideDestination()
    {
        using var t = new TempDir("slip");
        var archive = t.Sub("malicious.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
            Add("good/ok.txt", "fine");
            Add("../evil1.txt", "escaped");
            Add("..\\..\\evil2.txt", "escaped");
            Add("good/../../evil3.txt", "escaped");
            Add("C:/Windows/Temp/evil4.txt", "absolute");
            Add("good/ads.txt:stream", "ads");
            Add("good/CON.txt", "device");
        }
        var dest = t.Sub("dest", "inner");
        var r = await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = dest }, null);

        Assert.True(File.Exists(Path.Combine(dest, "good", "ok.txt")));
        Assert.Equal(1, r.FilesWritten);
        Assert.True(r.Blocked.Count >= 5);
        Assert.False(File.Exists(t.Sub("dest", "evil1.txt")));
        Assert.False(File.Exists(t.Sub("evil2.txt")));
        Assert.False(File.Exists(t.Sub("dest", "evil3.txt")));
        var escaped = Directory.EnumerateFiles(t.Path, "evil*", SearchOption.AllDirectories)
            .Where(f => !PathSafety.IsWithin(dest, f)).ToList();
        Assert.Empty(escaped);
        Assert.All(r.WrittenFiles, f => Assert.True(PathSafety.IsWithin(dest, f)));
        Assert.Empty(Directory.EnumerateFiles(t.Path, "*", SearchOption.AllDirectories).Where(f => f.Contains("evil")).Select(f => Path.GetRelativePath(t.Path, f)));
    }

    [Fact]
    public async Task ArchiveBomb_HighRatioIsBlocked()
    {
        using var t = new TempDir("bomb");
        var zeros = t.Sub("zeros.bin");
        await using (var fs = File.Create(zeros)) fs.SetLength(32L * 1024 * 1024);
        var archive = t.Sub("bomb.7z");
        await _engine.CompressAsync(new CompressOptions { Sources = [zeros], OutputPath = archive, Level = CompressionLevel.Ultra }, null);
        Assert.True(new FileInfo(archive).Length < 64 * 1024);

        var strict = new BombLimits { RatioCheckMinBytes = 1024 * 1024, SuspiciousRatio = 200 };
        var ex = await Assert.ThrowsAsync<ZiparkException>(() =>
            _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = t.Sub("x"), Limits = strict }, null));
        Assert.Equal(ErrorCode.ArchiveBomb, ex.Code);
        Assert.False(File.Exists(t.Sub("x", "zeros.bin")));

        // With explicit user override it extracts.
        var ok = await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = t.Sub("x"), Limits = strict, AllowLimitOverride = true }, null);
        Assert.Equal(1, ok.FilesWritten);
    }

    [Fact]
    public async Task Conflicts_RenameByDefault_SkipAndOverwriteOnRequest()
    {
        using var t = new TempDir("conf");
        var src = TestUtil.CreateSampleTree(t.Path, 1000);
        var archive = t.Sub("c.zip");
        await _engine.CompressAsync(new CompressOptions { Sources = [Path.Combine(src, "a.txt")], OutputPath = archive, Format = ArchiveFormat.Zip }, null);
        var dest = t.Sub("x");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "a.txt"), "user data");

        var r1 = await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = dest }, null);
        Assert.Equal(1, r1.Renamed);
        Assert.Equal("user data", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "a (2).txt")));

        var r2 = await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = dest, Conflict = ConflictPolicy.Skip }, null);
        Assert.Equal(1, r2.Skipped);
        Assert.Equal("user data", File.ReadAllText(Path.Combine(dest, "a.txt")));

        await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = dest, Conflict = ConflictPolicy.Overwrite }, null);
        Assert.Equal("hello Zipark\n", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task SelectiveExtraction_WithPrefixStrip()
    {
        using var t = new TempDir("sel");
        var src = TestUtil.CreateSampleTree(t.Path);
        var archive = t.Sub("s.7z");
        await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive }, null);
        var dest = t.Sub("x");
        var r = await _engine.ExtractAsync(new ExtractOptions
        {
            ArchivePath = archive,
            Destination = dest,
            Selection = [@"src\sub"],
            StripPrefix = "src",
        }, null);
        Assert.Equal(2, r.FilesWritten);
        Assert.True(File.Exists(Path.Combine(dest, "sub", "random.bin")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "deeper", "notes.md")));
        Assert.False(File.Exists(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task Cancellation_RemovesPartialOutput()
    {
        using var t = new TempDir("cancel");
        var big = t.Sub("big.bin");
        await using (var fs = File.Create(big))
        {
            var chunk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1 << 20);
            for (int i = 0; i < 400; i++) fs.Write(chunk); // 400 MB, near-incompressible repeats are still slow at Ultra
        }
        var archive = t.Sub("big.7z");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _engine.CompressAsync(new CompressOptions { Sources = [big], OutputPath = archive, Level = CompressionLevel.Ultra }, null, cts.Token));
        await Task.Delay(300);
        Assert.False(File.Exists(archive));
        Assert.Empty(Directory.GetFiles(t.Path, "*.fxtmp*"));
        Assert.True(File.Exists(big)); // the source is never touched
    }

    [Fact]
    public async Task OpenEntries_StreamsInArchiveOrder()
    {
        using var t = new TempDir("so");
        var src = TestUtil.CreateSampleTree(t.Path, 5000);
        var archive = t.Sub("s.7z");
        using var pw = TestUtil.Pw("stream-pw");
        await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Password = pw }, null);
        var info = await _engine.ListAsync(archive, pw);
        var files = info.Entries.Where(e => !e.IsDirectory).ToList();
        await using var s = _engine.OpenEntries(archive, files.Select(f => f.Path).Reverse().ToList(), pw);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        Assert.Equal(files.Sum(f => f.Size), ms.Length);
        // First bytes belong to the first file in archive order.
        var first = files.First(f => f.Size > 0);
        var expected = File.ReadAllBytes(Path.Combine(t.Path, first.Path));
        Assert.Equal(expected, ms.ToArray().AsSpan((int)files.TakeWhile(f => f != first).Sum(f => f.Size), expected.Length).ToArray());
    }

    [Fact]
    public async Task OpenEntries_WrongPasswordThrowsAtEnd()
    {
        using var t = new TempDir("sow");
        var src = TestUtil.CreateSampleTree(t.Path, 5000);
        var archive = t.Sub("s.zip");
        using var pw = TestUtil.Pw("right");
        await _engine.CompressAsync(new CompressOptions { Sources = [src], OutputPath = archive, Format = ArchiveFormat.Zip, Password = pw }, null);
        using var wrong = TestUtil.Pw("wrong");
        await using var s = _engine.OpenEntries(archive, [@"src\a.txt"], wrong);
        var ex = await Assert.ThrowsAsync<ZiparkException>(async () => await s.CopyToAsync(Stream.Null));
        Assert.Equal(ErrorCode.WrongPassword, ex.Code);
    }
}
