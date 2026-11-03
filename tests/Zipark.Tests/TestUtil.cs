using System.Security.Cryptography;
using Zipark.Compression;
using Zipark.Crypto;

namespace Zipark.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Zipark.Tests", (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

public static class TestUtil
{
    public static SevenZipEngine Engine() => new();

    public static SecretPassword Pw(string s) => SecretPassword.FromChars(s);

    /// <summary>Creates a small tree with text, random binary, empty file, nested and Korean names.</summary>
    public static string CreateSampleTree(string root, int randomBytes = 300_000)
    {
        var src = System.IO.Path.Combine(root, "src");
        Directory.CreateDirectory(System.IO.Path.Combine(src, "sub", "deeper"));
        Directory.CreateDirectory(System.IO.Path.Combine(src, "빈 폴더"));
        File.WriteAllText(System.IO.Path.Combine(src, "a.txt"), "hello Zipark\n");
        File.WriteAllText(System.IO.Path.Combine(src, "한글 문서.txt"), "가나다라마바사 테스트\n");
        File.WriteAllBytes(System.IO.Path.Combine(src, "empty.dat"), []);
        File.WriteAllBytes(System.IO.Path.Combine(src, "sub", "random.bin"), RandomNumberGenerator.GetBytes(randomBytes));
        File.WriteAllText(System.IO.Path.Combine(src, "sub", "deeper", "notes.md"), string.Concat(Enumerable.Repeat("# repeatable line\n", 2000)));
        return src;
    }

    /// <summary>relative path → SHA-256 for every file under root.</summary>
    public static Dictionary<string, string> HashTree(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var fs = File.OpenRead(f);
            map[System.IO.Path.GetRelativePath(root, f)] = Convert.ToHexString(SHA256.HashData(fs));
        }
        return map;
    }

    public static void AssertSameTree(string expectedRoot, string actualRoot)
    {
        var a = HashTree(expectedRoot);
        var b = HashTree(actualRoot);
        Assert.Equal(a.Keys.OrderBy(k => k), b.Keys.OrderBy(k => k));
        foreach (var (k, v) in a) Assert.Equal(v, b[k]);
    }
}
