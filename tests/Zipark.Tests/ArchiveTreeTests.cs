using Zipark.ArchiveExplorer;
using Zipark.Core;

namespace Zipark.Tests;

public class ArchiveTreeTests
{
    private static ArchiveInfo Info(bool solid) => new()
    {
        ArchivePath = "x.7z",
        IsSolid = solid,
        Entries =
        [
            new ArchiveEntry { Index = 0, Path = @"docs\a.txt", Size = 10, PackedSize = 6 },
            new ArchiveEntry { Index = 1, Path = @"docs\sub\b.bin", Size = 20, PackedSize = 20 },
            new ArchiveEntry { Index = 2, Path = "readme.md", Size = 5, PackedSize = 4 },
            new ArchiveEntry { Index = 3, Path = "empty", IsDirectory = true },
        ],
    };

    [Fact]
    public void Build_SynthesizesFoldersAndAggregates()
    {
        var t = ArchiveTree.Build(Info(false));
        Assert.Equal(["docs", "empty", "readme.md"], t.Root.Children.Select(c => c.Name));
        var docs = t.Find("DOCS")!;
        Assert.True(docs.IsDirectory);
        Assert.Equal(30, docs.Size);
        Assert.Equal(26, docs.PackedSize);
        Assert.Equal(2, docs.FileCount);
        Assert.Equal(@"docs\sub\b.bin", t.Find("docs/sub/b.bin")!.FullPath);
        Assert.Null(t.Find("nope"));
    }

    [Fact]
    public void Solid_PackedSizeIsUnknown()
    {
        var t = ArchiveTree.Build(Info(true));
        Assert.Equal(-1, t.Find("docs")!.PackedSize);
        Assert.Equal(-1, t.Find(@"docs\a.txt")!.PackedSize);
        Assert.Equal("-", ByteSize.Format(-1));
    }

    [Fact]
    public void Search_SupportsSubstringAndWildcards()
    {
        var t = ArchiveTree.Build(Info(false));
        Assert.Single(t.Search("read"));
        Assert.Equal(["a.txt"], t.Search("*.txt").Select(n => n.Name));
        Assert.Equal([@"docs\sub\b.bin"], ArchiveTree.ToSelection(t.Search("b?bin")));
    }
}
