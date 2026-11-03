using Zipark.Application.Drives;
using Zipark.Application.Jobs;
using Zipark.Core;
using Zipark.Crypto;
using Zipark.Settings;
using Zipark.ShellIntegration;
using Microsoft.Win32;

namespace Zipark.Tests;

public class ApplicationTests
{
    private static string WorkerExe => Path.Combine(AppContext.BaseDirectory, "Zipark.Worker.exe");

    [Fact]
    public async Task WorkerProcess_CompressesAndExtractsEncrypted()
    {
        Assert.True(File.Exists(WorkerExe), "worker exe must be copied next to the tests");
        using var t = new TempDir("worker");
        var src = TestUtil.CreateSampleTree(t.Path);
        var exec = new WorkerProcessJobExecutor(WorkerExe);
        using var pw = TestUtil.Pw("worker-pass-9");
        var progressSeen = 0;
        var progress = new Progress<JobProgress>(_ => Interlocked.Increment(ref progressSeen));

        var c = await exec.ExecuteAsync(new JobRequest { Kind = JobKind.Compress, Sources = [src], Output = t.Sub("w.7z"), VerifyAfter = true }, pw, progress, default);
        Assert.True(c.Success, c.Message);
        Assert.Single(c.Outputs);

        var x = await exec.ExecuteAsync(new JobRequest { Kind = JobKind.Extract, Sources = [c.Outputs[0]], Destination = t.Sub("x") }, pw, null, default);
        Assert.True(x.Success, x.Message);
        TestUtil.AssertSameTree(src, t.Sub("x", "src"));

        using var wrong = TestUtil.Pw("nope");
        var bad = await exec.ExecuteAsync(new JobRequest { Kind = JobKind.Test, Sources = [c.Outputs[0]] }, wrong, null, default);
        Assert.False(bad.Success);
        Assert.Equal(ErrorCode.WrongPassword, bad.Error);
    }

    [Fact]
    public async Task WorkerProcess_CancelStopsAndCleansUp()
    {
        using var t = new TempDir("wcancel");
        var big = t.Sub("big.bin");
        await using (var fs = File.Create(big))
        {
            // Incompressible data so the job reliably outlives the cancel request.
            var chunk = new byte[1 << 20];
            for (int i = 0; i < 300; i++)
            {
                System.Security.Cryptography.RandomNumberGenerator.Fill(chunk);
                fs.Write(chunk);
            }
        }
        var exec = new WorkerProcessJobExecutor(WorkerExe);
        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double firedAt = -1;
        cts.Token.Register(() => firedAt = sw.Elapsed.TotalSeconds);
        // Cancel from a dedicated thread: a timer-based CancellationTokenSource depends on the thread pool,
        // which other tests running in parallel can saturate.
        new Thread(() => { Thread.Sleep(1500); cts.Cancel(); }) { IsBackground = true }.Start();
        var r = await exec.ExecuteAsync(new JobRequest { Kind = JobKind.Compress, Sources = [big], Output = t.Sub("big.7z"), Level = Compression.CompressionLevel.Ultra }, null, null, cts.Token);
        Assert.False(r.Success, $"cancelFiredAt={firedAt:F2}s elapsed={sw.Elapsed.TotalSeconds:F2}s worker={r.ElapsedSeconds:F2}s");
        Assert.Equal(ErrorCode.Cancelled, r.Error);
        await Task.Delay(500);
        Assert.False(File.Exists(t.Sub("big.7z")));
        Assert.Empty(Directory.GetFiles(t.Path, "*.fxtmp*"));
    }

    [Fact]
    public async Task Queue_RunsJobsInOrderAndReportsResults()
    {
        using var t = new TempDir("queue");
        var src = TestUtil.CreateSampleTree(t.Path, 10_000);
        using var queue = new JobQueue(new InProcessJobExecutor(TestUtil.Engine()), maxParallel: 1);
        var a = queue.Enqueue(new JobRequest { Kind = JobKind.Compress, Sources = [src], Output = t.Sub("a.zip"), Format = ArchiveFormat.Zip });
        var b = queue.Enqueue(new JobRequest { Kind = JobKind.Checksum, Sources = [t.Sub("a.zip")], Output = t.Sub("a.zip.sha256") });
        var c = queue.Enqueue(new JobRequest { Kind = JobKind.Extract, Sources = [t.Sub("missing.zip")], Destination = t.Sub("x") });
        var results = await Task.WhenAll(a.Completion, b.Completion, c.Completion);
        Assert.True(results[0].Success);
        Assert.True(results[1].Success);
        Assert.Equal(64, results[1].Checksums[0].Sha256.Length);
        Assert.StartsWith(results[1].Checksums[0].Sha256, File.ReadAllText(t.Sub("a.zip.sha256")));
        Assert.False(results[2].Success);
        Assert.Equal(JobStage.Failed, c.Stage);
        Assert.True(a.Finished <= b.Started);
    }

    [Fact]
    public async Task Queue_CancelQueuedJob()
    {
        using var t = new TempDir("qcancel");
        using var queue = new JobQueue(new InProcessJobExecutor(TestUtil.Engine()));
        var src = TestUtil.CreateSampleTree(t.Path, 10_000);
        var first = queue.Enqueue(new JobRequest { Kind = JobKind.Compress, Sources = [src], Output = t.Sub("1.7z") });
        var second = queue.Enqueue(new JobRequest { Kind = JobKind.Compress, Sources = [src], Output = t.Sub("2.7z") });
        second.Cancel();
        await first.Completion;
        var r = await second.Completion;
        Assert.Equal(ErrorCode.Cancelled, r.Error);
        Assert.False(File.Exists(t.Sub("2.7z")));
    }

    [Fact]
    public async Task SplitJob_ThenMergeJob()
    {
        using var t = new TempDir("splitjob");
        var file = t.Sub("f.bin");
        File.WriteAllBytes(file, System.Security.Cryptography.RandomNumberGenerator.GetBytes(250_000));
        var exec = new InProcessJobExecutor(TestUtil.Engine());
        var s = await exec.ExecuteAsync(new JobRequest { Kind = JobKind.SplitFile, Sources = [file], Destination = t.Sub("p"), PartSize = 100_000 }, null, null, default);
        Assert.True(s.Success, s.Message);
        Assert.Equal(3, s.Files);
        var m = await exec.ExecuteAsync(new JobRequest { Kind = JobKind.MergeFile, Sources = [s.Outputs[0]], Output = t.Sub("merged.bin") }, null, null, default);
        Assert.True(m.Success, m.Message);
        Assert.Equal(Checksums.Sha256OfFile(file), Checksums.Sha256OfFile(t.Sub("merged.bin")));
    }

    [Fact]
    public void Settings_DefaultFormatIsZip()
    {
        Assert.Equal(ArchiveFormat.Zip, new AppSettings().DefaultFormat);
        Assert.Equal(ArchiveFormat.Zip, ArchiveFormats.Creatable[0]);
    }

    [Fact]
    public void Settings_RoundTripWithoutSecrets()
    {
        using var t = new TempDir("settings");
        var store = new SettingsStore(t.Sub("s.json"));
        var s = store.Load();
        s.Language = "en";
        s.Theme = ThemeMode.Dark;
        s.AddRecent(@"C:\a.7z");
        store.Save(s);
        var back = new SettingsStore(t.Sub("s.json")).Load();
        Assert.Equal("en", back.Language);
        Assert.Equal(ThemeMode.Dark, back.Theme);
        Assert.Equal(@"C:\a.7z", back.RecentArchives[0]);
        Assert.DoesNotContain("password", File.ReadAllText(t.Sub("s.json")), StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(t.Sub("s.json"), "{ not json");
        Assert.NotNull(new SettingsStore(t.Sub("s.json")).Load()); // corrupt settings fall back to defaults
    }

    [Fact]
    public void ShellRegistrar_RegistersAndRemovesEverything()
    {
        const string scratch = @"Software\Zipark.Tests.Shell";
        Registry.CurrentUser.DeleteSubKeyTree(scratch, false);
        using (var root = Registry.CurrentUser.CreateSubKey(scratch))
        {
            var reg = new ShellRegistrar(root);
            var exe = @"C:\Program Files\Zipark\Zipark.exe";
            reg.Register(exe, new ShellLabels("Zipark", "열기", "ZIP", "7Z", "고급", "여기에", "새 폴더", "검사", "탑재", "해제"));
            Assert.True(reg.IsRegistered);
            Assert.Equal(exe, reg.RegisteredExecutable);
            using (var k = root.OpenSubKey(@"SystemFileAssociations\.7z\shell\Zipark\shell"))
            {
                Assert.NotNull(k);
                Assert.Contains(k!.GetSubKeyNames(), n => n.EndsWith(ShellVerbs.Mount));
            }
            using (var cmd = root.OpenSubKey(@"Directory\shell\Zipark\shell\01-compress-7z\command"))
                Assert.Equal($"\"{exe}\" --verb compress-7z \"%1\"", cmd!.GetValue(""));
            using (var noMount = root.OpenSubKey(@"SystemFileAssociations\.rar\shell\Zipark\shell"))
                Assert.DoesNotContain(noMount!.GetSubKeyNames(), n => n.EndsWith(ShellVerbs.Mount));

            reg.Unregister();
            Assert.False(reg.IsRegistered);
            Assert.Null(root.OpenSubKey(@"SystemFileAssociations\.zip\shell\Zipark"));
            using var owp = root.OpenSubKey(@".zip\OpenWithProgids");
            Assert.DoesNotContain(ShellRegistrar.ProgId, owp?.GetValueNames() ?? []);
        }
        Registry.CurrentUser.DeleteSubKeyTree(scratch, false);
    }

    [Fact]
    public void ShellInvocation_ParsesExplorerArguments()
    {
        var inv = ShellInvocation.Parse(["--verb", "extract-here", @"C:\a b\x.7z"]);
        Assert.Equal(ShellVerbs.ExtractHere, inv!.Verb);
        Assert.Equal(@"C:\a b\x.7z", inv.Paths[0]);
        Assert.Equal(ShellVerbs.Open, ShellInvocation.Parse([@"C:\x.zip"])!.Verb);
        Assert.Null(ShellInvocation.Parse(["--verb", "format-disk", "C:"]));
    }

    [Fact]
    public void ShellVerbs_BackgroundVerbsNeverNeedTheMainWindow()
    {
        foreach (var v in new[] { ShellVerbs.CompressZip, ShellVerbs.Compress7z, ShellVerbs.ExtractHere, ShellVerbs.ExtractToFolder, ShellVerbs.Test, ShellVerbs.Mount, ShellVerbs.Unmount })
            Assert.True(ShellVerbs.RunsInBackground(v), v);
        Assert.False(ShellVerbs.RunsInBackground(ShellVerbs.Open));
        Assert.False(ShellVerbs.RunsInBackground(ShellVerbs.CompressAdvanced));
    }

    [Fact]
    public async Task DriveService_ReportsMissingWinFspSafely()
    {
        if (VirtualDriveService.WinFspInstalled) return; // covered by the end-to-end mount test instead
        using var t = new TempDir("nofsp");
        var src = TestUtil.CreateSampleTree(t.Path, 1000);
        await TestUtil.Engine().CompressAsync(new Compression.CompressOptions { Sources = [src], OutputPath = t.Sub("a.zip"), Format = ArchiveFormat.Zip }, null);
        using var svc = new VirtualDriveService();
        var ex = await Assert.ThrowsAsync<ZiparkException>(() => svc.MountAsync(t.Sub("a.zip"), null));
        Assert.Equal(ErrorCode.VirtualDriveUnavailable, ex.Code);
        Assert.Empty(svc.Drives);
        Assert.NotNull(VirtualDriveService.FindFreeLetter());
    }
}

public class SecurityUnitTests
{
    [Theory]
    [InlineData("a/b/c.txt", PathSafety.Verdict.Safe)]
    [InlineData("./a/./b.txt", PathSafety.Verdict.Safe)]
    [InlineData("한글/파일.txt", PathSafety.Verdict.Safe)]
    [InlineData("../x", PathSafety.Verdict.Traversal)]
    [InlineData("a/../../x", PathSafety.Verdict.Traversal)]
    [InlineData("a/.../x", PathSafety.Verdict.Traversal)]
    [InlineData("/etc/passwd", PathSafety.Verdict.Absolute)]
    [InlineData("\\\\server\\share\\x", PathSafety.Verdict.Absolute)]
    [InlineData("C:/Windows/x", PathSafety.Verdict.Absolute)]
    [InlineData("a/b.txt:evil", PathSafety.Verdict.AlternateStream)]
    [InlineData("a/NUL", PathSafety.Verdict.ReservedName)]
    [InlineData("a/com1.txt", PathSafety.Verdict.ReservedName)]
    [InlineData("a/b?.txt", PathSafety.Verdict.InvalidCharacters)]
    [InlineData("a/trailing.", PathSafety.Verdict.InvalidCharacters)]
    [InlineData("", PathSafety.Verdict.Empty)]
    public void PathSafety_Classifies(string path, PathSafety.Verdict expected) =>
        Assert.Equal(expected, PathSafety.Check(path, out _));

    [Fact]
    public void PathSafety_ResolveUnderRejectsEscapes()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.Equal(Path.Combine(root, "a", "b.txt"), PathSafety.ResolveUnder(root, "a/b.txt"));
        Assert.Throws<ZiparkException>(() => PathSafety.ResolveUnder(root, "../b.txt"));
        Assert.False(PathSafety.IsWithin(root, root + "-sibling\\x"));
    }

    [Fact]
    public void BombGuard_FlagsRatioCountAndDuplicates()
    {
        var info = new ArchiveInfo
        {
            ArchivePath = "x.7z",
            PhysicalSize = 1000,
            Entries = [
                new ArchiveEntry { Index = 0, Path = "a.bin", Size = 10L * 1024 * 1024 * 1024 },
                new ArchiveEntry { Index = 1, Path = "A.BIN", Size = 1 },
                new ArchiveEntry { Index = 2, Path = "link", IsSymbolicLink = true },
            ],
        };
        var r = ArchiveBombGuard.Evaluate(info, new BombLimits());
        Assert.Equal(GuardLevel.Blocked, r.Level);
        Assert.Contains(r.Findings, f => f.StartsWith("ratio:"));
        Assert.Contains(r.Findings, f => f.StartsWith("duplicate:"));
        Assert.Contains(r.Findings, f => f.StartsWith("symlink:"));
    }

    [Theory]
    [InlineData("", StrengthLevel.VeryWeak)]
    [InlineData("password", StrengthLevel.VeryWeak)]
    [InlineData("abc", StrengthLevel.VeryWeak)]
    [InlineData("Summer2026", StrengthLevel.Fair)]
    [InlineData("k7#Qm9!vR2$wLp4@", StrengthLevel.VeryStrong)]
    public void PasswordStrength_Levels(string pw, StrengthLevel min)
    {
        var r = PasswordStrength.Evaluate(pw);
        if (min <= StrengthLevel.Weak) Assert.True(r.Level <= StrengthLevel.Weak, $"{pw}: {r.Level}");
        else Assert.True(r.Level >= min, $"{pw}: {r.Level} ({r.EntropyBits:F0} bits)");
    }

    [Fact]
    public void SecretPassword_IsWipedOnDispose()
    {
        var pw = SecretPassword.FromChars("hunter2");
        Assert.Equal("********", pw.ToString());
        pw.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pw.Span.Length);
    }

    [Fact]
    public void EncryptionAdvisor_WarnsAboutWeakChoices()
    {
        var w = EncryptionAdvisor.Review(ArchiveFormat.Zip, ZipEncryption.ZipCrypto, false, StrengthLevel.Weak);
        Assert.Contains("enc.warn.zipcrypto", w);
        Assert.Contains("enc.warn.zipnames", w);
        Assert.Contains("enc.warn.weakpw", w);
        Assert.Empty(EncryptionAdvisor.Review(ArchiveFormat.SevenZip, ZipEncryption.Aes256, true, StrengthLevel.Strong));
    }

    [Fact]
    public void ByteSize_ParsesAndFormats()
    {
        Assert.True(ByteSize.TryParse("700MB", out var b));
        Assert.Equal(700L * 1024 * 1024, b);
        Assert.True(ByteSize.TryParse("4 gb", out b));
        Assert.Equal(4L << 30, b);
        Assert.False(ByteSize.TryParse("-5", out _));
        Assert.Equal("1.50 KB", ByteSize.Format(1536));
    }
}
