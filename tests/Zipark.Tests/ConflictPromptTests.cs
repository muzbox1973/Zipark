using Zipark.Application.Jobs;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Settings;

namespace Zipark.Tests;

public class ConflictPromptTests
{
    private readonly SevenZipEngine _engine = TestUtil.Engine();

    /// <summary>Archive with a.txt, b.txt, c.txt; destination already has all three with different content.</summary>
    private async Task<(string Archive, string Dest)> Prepare(TempDir t)
    {
        var src = t.Sub("src");
        Directory.CreateDirectory(src);
        foreach (var n in new[] { "a", "b", "c" }) File.WriteAllText(Path.Combine(src, n + ".txt"), "from archive " + n);
        var archive = t.Sub("x.zip");
        await _engine.CompressAsync(new CompressOptions { Sources = Directory.GetFiles(src), OutputPath = archive, Format = ArchiveFormat.Zip }, null);
        var dest = t.Sub("dest");
        Directory.CreateDirectory(dest);
        foreach (var n in new[] { "a", "b", "c" }) File.WriteAllText(Path.Combine(dest, n + ".txt"), "user " + n);
        return (archive, dest);
    }

    [Fact]
    public async Task Ask_EachConflictIsAskedWithDetails()
    {
        using var t = new TempDir("ask");
        var (archive, dest) = await Prepare(t);
        var asked = new List<ConflictInfo>();
        var answers = new Queue<ConflictChoice>([ConflictChoice.Overwrite, ConflictChoice.Skip, ConflictChoice.Rename]);
        var r = await _engine.ExtractAsync(new ExtractOptions
        {
            ArchivePath = archive,
            Destination = dest,
            Conflict = ConflictPolicy.Ask,
            ConflictPrompt = info => { asked.Add(info); return new ConflictDecision(answers.Dequeue(), false); },
        }, null);

        Assert.Equal(3, asked.Count);
        Assert.Equal([2, 1, 0], asked.Select(a => a.Remaining));
        Assert.All(asked, a => Assert.True(a.IncomingSize > 0 && a.ExistingSize > 0));
        Assert.EndsWith(" (2).txt", asked[0].RenameTo);
        var byName = asked.Select(a => Path.GetFileName(a.TargetPath)).ToList();
        var contents = byName.Select(n => File.ReadAllText(Path.Combine(dest, n))).ToList();
        Assert.StartsWith("from archive", contents[0]);   // overwritten
        Assert.StartsWith("user", contents[1]);           // skipped
        Assert.StartsWith("user", contents[2]);           // kept, new copy renamed
        Assert.True(File.Exists(Path.Combine(dest, Path.GetFileNameWithoutExtension(byName[2]) + " (2).txt")));
        Assert.Equal(1, r.Skipped);
        Assert.Equal(1, r.Renamed);
    }

    [Fact]
    public async Task Ask_ApplyToAllAsksOnce()
    {
        using var t = new TempDir("askall");
        var (archive, dest) = await Prepare(t);
        int calls = 0;
        await _engine.ExtractAsync(new ExtractOptions
        {
            ArchivePath = archive,
            Destination = dest,
            Conflict = ConflictPolicy.Ask,
            ConflictPrompt = _ => { calls++; return new ConflictDecision(ConflictChoice.Overwrite, true); },
        }, null);
        Assert.Equal(1, calls);
        Assert.All(new[] { "a", "b", "c" }, n => Assert.StartsWith("from archive", File.ReadAllText(Path.Combine(dest, n + ".txt"))));
    }

    [Fact]
    public async Task Ask_StopCancelsAndLeavesNoStaging()
    {
        using var t = new TempDir("askstop");
        var (archive, dest) = await Prepare(t);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.ExtractAsync(new ExtractOptions
        {
            ArchivePath = archive,
            Destination = dest,
            Conflict = ConflictPolicy.Ask,
            ConflictPrompt = _ => new ConflictDecision(ConflictChoice.Cancel, false),
        }, null));
        Assert.Empty(Directory.GetDirectories(dest, ".fxstage-*"));
        Assert.All(new[] { "a", "b", "c" }, n => Assert.StartsWith("user", File.ReadAllText(Path.Combine(dest, n + ".txt"))));
    }

    [Fact]
    public async Task Ask_WithoutPromptKeepsBoth()
    {
        using var t = new TempDir("asknone");
        var (archive, dest) = await Prepare(t);
        var r = await _engine.ExtractAsync(new ExtractOptions { ArchivePath = archive, Destination = dest, Conflict = ConflictPolicy.Ask }, null);
        Assert.Equal(3, r.Renamed);
        Assert.StartsWith("user", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task WorkerProcess_ConflictQuestionRoundTrip()
    {
        using var t = new TempDir("askworker");
        var (archive, dest) = await Prepare(t);
        int calls = 0;
        var exec = new WorkerProcessJobExecutor(Path.Combine(AppContext.BaseDirectory, "Zipark.Worker.exe"))
        {
            ConflictPrompt = async info =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(50); // a user taking a moment to answer
                return new ConflictDecision(info.Remaining == 2 ? ConflictChoice.Skip : ConflictChoice.Overwrite, info.Remaining == 1);
            },
        };
        var r = await exec.ExecuteAsync(new JobRequest { Kind = JobKind.Extract, Sources = [archive], Destination = dest, Conflict = ConflictPolicy.Ask }, null, null, default);
        Assert.True(r.Success, r.Message);
        Assert.Equal(2, calls); // third conflict answered by "apply to all" on the second
        var texts = new[] { "a", "b", "c" }.Select(n => File.ReadAllText(Path.Combine(dest, n + ".txt"))).ToList();
        Assert.Equal(1, texts.Count(x => x.StartsWith("user")));
        Assert.Equal(2, texts.Count(x => x.StartsWith("from archive")));
    }

    [Fact]
    public async Task WorkerProcess_StopAtConflictReportsCancelled()
    {
        using var t = new TempDir("askworkerstop");
        var (archive, dest) = await Prepare(t);
        var exec = new WorkerProcessJobExecutor(Path.Combine(AppContext.BaseDirectory, "Zipark.Worker.exe"))
        {
            ConflictPrompt = _ => Task.FromResult(new ConflictDecision(ConflictChoice.Cancel, false)),
        };
        var r = await exec.ExecuteAsync(new JobRequest { Kind = JobKind.Extract, Sources = [archive], Destination = dest, Conflict = ConflictPolicy.Ask }, null, null, default);
        Assert.False(r.Success);
        Assert.Equal(ErrorCode.Cancelled, r.Error);
        Assert.Empty(Directory.GetDirectories(dest, ".fxstage-*"));
    }

    [Fact]
    public void Settings_DefaultAndMigrationAskOnConflict()
    {
        Assert.Equal(ConflictPolicy.Ask, new AppSettings().Conflict);
        var old = new AppSettings { Version = 1, Conflict = ConflictPolicy.Rename };
        Assert.Equal(ConflictPolicy.Ask, SettingsStore.Migrate(old).Conflict);
        Assert.Equal(SettingsStore.CurrentVersion, old.Version);
        var chosen = new AppSettings { Version = 1, Conflict = ConflictPolicy.Overwrite };
        Assert.Equal(ConflictPolicy.Overwrite, SettingsStore.Migrate(chosen).Conflict); // a deliberate choice is kept
    }
}
