using Zipark.Application.Jobs;
using Zipark.Compression;
using Zipark.Core;

namespace Zipark.Tests;

public class ExtractTargetTests
{
    private readonly SevenZipEngine _engine = TestUtil.Engine();

    private static ArchiveInfo Listing(params string[] paths) => new()
    {
        ArchivePath = "x.zip",
        Entries = paths.Select((p, i) => new ArchiveEntry { Index = i, Path = p, IsDirectory = !p.Contains('.') }).ToList(),
    };

    [Theory]
    [InlineData("retro.zip", "retro")]
    [InlineData("backup.7z.001", "backup")]
    [InlineData("data.part1.rar", "data")]
    [InlineData("site.tar.gz", "site")]
    public void FolderName_StripsArchiveSuffixes(string file, string expected) =>
        Assert.Equal(expected, ExtractTarget.FolderName(file));

    [Fact]
    public void SingleTopFolder_Detection()
    {
        Assert.Equal("retro", ExtractTarget.SingleTopFolder(Listing("retro", @"retro\a.txt", @"retro\sub", @"retro\sub\b.txt")));
        Assert.Null(ExtractTarget.SingleTopFolder(Listing("a.txt", "b.txt")));                 // files at the root
        Assert.Null(ExtractTarget.SingleTopFolder(Listing(@"one\a.txt", @"two\b.txt")));        // several roots
        Assert.Null(ExtractTarget.SingleTopFolder(Listing("readme.txt")));                       // a single file
    }

    [Fact]
    public void ForNewFolder_NeverInventsNames()
    {
        var baseDir = @"C:\work";
        Assert.Equal((baseDir, @"C:\work\retro"), ExtractTarget.ForNewFolder(Listing("retro", @"retro\a.txt"), @"C:\work\retro.zip", baseDir));
        Assert.Equal((@"C:\work\retro", @"C:\work\retro"), ExtractTarget.ForNewFolder(Listing("a.txt"), @"C:\work\retro.zip", baseDir));
        Assert.Equal((@"C:\work\retro", @"C:\work\retro"), ExtractTarget.ForNewFolder(Listing("game", @"game\a.txt"), @"C:\work\retro.zip", baseDir));
    }

    /// <summary>The reported bug: "extract to new folder" next to an existing "retro" folder made "retro (2)" without asking.</summary>
    [Fact]
    public async Task NewFolder_ExistingFolder_AsksPerFileInsteadOfMakingCopyFolder()
    {
        using var t = new TempDir("retro");
        var retro = t.Sub("retro");
        Directory.CreateDirectory(Path.Combine(retro, "sub"));
        foreach (var i in new[] { 1, 2, 3 }) File.WriteAllText(Path.Combine(retro, $"game{i}.txt"), $"v{i}");
        File.WriteAllText(Path.Combine(retro, "sub", "save.dat"), "s");
        var zip = t.Sub("retro.zip");
        await _engine.CompressAsync(new CompressOptions { Sources = [retro], OutputPath = zip, Format = ArchiveFormat.Zip }, null);

        var asked = new List<string>();
        var runner = new JobRunner(_engine, c => { asked.Add(Path.GetRelativePath(t.Path, c.TargetPath)); return new ConflictDecision(ConflictChoice.Skip, false); });
        var r = await runner.RunAsync(new JobRequest
        {
            Kind = JobKind.Extract,
            Sources = [zip],
            Destination = t.Path,
            IntoArchiveFolder = true,
            Conflict = ConflictPolicy.Ask,
        }, null, null, default);

        Assert.True(r.Success, r.Message);
        Assert.Equal(4, asked.Count);
        Assert.Contains(@"retro\sub\save.dat", asked);
        Assert.False(Directory.Exists(t.Sub("retro (2)")));
        Assert.False(Directory.Exists(t.Sub("retro", "retro")));
        Assert.Equal(retro, r.Outputs[0]);
    }

    [Fact]
    public async Task NewFolder_RootFilesGoIntoArchiveNamedFolder_AndReuseIt()
    {
        using var t = new TempDir("rootfiles");
        var src = t.Sub("src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        var zip = t.Sub("pack.zip");
        await _engine.CompressAsync(new CompressOptions { Sources = [Path.Combine(src, "a.txt")], OutputPath = zip, Format = ArchiveFormat.Zip }, null);

        int asked = 0;
        var runner = new JobRunner(_engine, _ => { asked++; return new ConflictDecision(ConflictChoice.Overwrite, false); });
        JobRequest Req() => new() { Kind = JobKind.Extract, Sources = [zip], Destination = t.Path, IntoArchiveFolder = true, Conflict = ConflictPolicy.Ask };
        Assert.True((await runner.RunAsync(Req(), null, null, default)).Success);
        Assert.True(File.Exists(t.Sub("pack", "a.txt")));
        Assert.Equal(0, asked);

        Assert.True((await runner.RunAsync(Req(), null, null, default)).Success); // second time: same folder, asked once
        Assert.Equal(1, asked);
        Assert.False(Directory.Exists(t.Sub("pack (2)")));
    }
}
