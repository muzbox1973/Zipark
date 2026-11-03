using System.Text.RegularExpressions;

namespace Zipark.SplitVolumes;

public enum VolumeScheme
{
    /// <summary>Not a multi-volume set.</summary>
    Single,
    /// <summary>name.7z.001, name.zip.001, name.tar.001 … (7-Zip style numbered volumes).</summary>
    Numbered,
    /// <summary>name.z01, name.z02 … name.zip (PKZIP/WinZip spanned archive; .zip is the last part).</summary>
    ZipSpanned,
    /// <summary>name.part1.rar, name.part2.rar …</summary>
    RarParts,
    /// <summary>name.rar, name.r00, name.r01 … (old RAR naming).</summary>
    RarOld,
}

public sealed record VolumeSet(
    VolumeScheme Scheme,
    string FirstVolume,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<string> Missing,
    long TotalBytes)
{
    public bool IsMultiVolume => Scheme != VolumeScheme.Single;
    public bool IsComplete => Missing.Count == 0;
    /// <summary>Path to hand to the engine (first volume for numbered/rar, the .zip for spanned zip).</summary>
    public string EnginePath => Scheme == VolumeScheme.ZipSpanned ? Volumes[^1] : FirstVolume;
}

/// <summary>
/// Recognises multi-volume naming schemes from any volume and finds all siblings, reporting gaps.
/// The final volume of a set cannot always be known from names alone, so "missing" means gaps
/// before the highest number found (plus the closing .zip for spanned zips).
/// </summary>
public static partial class VolumeSetDetector
{
    [GeneratedRegex(@"^(?<base>.+)\.(?<num>\d{3,})$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedRx();

    [GeneratedRegex(@"^(?<base>.+)\.z(?<num>\d{2,})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipSpanRx();

    [GeneratedRegex(@"^(?<base>.+)\.part(?<num>\d+)\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex RarPartRx();

    [GeneratedRegex(@"^(?<base>.+)\.r(?<num>\d{2,})$", RegexOptions.IgnoreCase)]
    private static partial Regex RarOldRx();

    public static VolumeSet Detect(string anyVolume)
    {
        var full = Path.GetFullPath(anyVolume);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);

        Match m;
        if ((m = RarPartRx().Match(name)).Success)
        {
            var b = m.Groups["base"].Value;
            int width = m.Groups["num"].Value.Length;
            return Collect(VolumeScheme.RarParts, dir, i => $"{b}.part{i.ToString().PadLeft(width, '0')}.rar", 1, null, b + ".part*.rar");
        }
        if ((m = NumberedRx().Match(name)).Success)
        {
            var b = m.Groups["base"].Value;
            int width = m.Groups["num"].Value.Length;
            return Collect(VolumeScheme.Numbered, dir, i => $"{b}.{i.ToString().PadLeft(width, '0')}", 1, null, b + ".*");
        }
        if ((m = ZipSpanRx().Match(name)).Success || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && HasSibling(dir, Path.GetFileNameWithoutExtension(name), ".z"))
        {
            var b = m.Success ? m.Groups["base"].Value : Path.GetFileNameWithoutExtension(name);
            return Collect(VolumeScheme.ZipSpanned, dir, i => $"{b}.z{i:D2}", 1, b + ".zip", b + ".z*");
        }
        if ((m = RarOldRx().Match(name)).Success || name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) && HasSibling(dir, Path.GetFileNameWithoutExtension(name), ".r"))
        {
            var b = m.Success ? m.Groups["base"].Value : Path.GetFileNameWithoutExtension(name);
            var first = Path.Combine(dir, b + ".rar");
            var rest = Collect(VolumeScheme.RarOld, dir, i => $"{b}.r{i - 1:D2}", 1, null, b + ".r*");
            var vols = new List<string> { first };
            vols.AddRange(rest.Volumes);
            var missing = new List<string>(rest.Missing);
            if (!File.Exists(first)) missing.Insert(0, first);
            return new VolumeSet(VolumeScheme.RarOld, first, vols.Where(File.Exists).ToList(), missing, vols.Where(File.Exists).Sum(v => new FileInfo(v).Length));
        }
        return new VolumeSet(VolumeScheme.Single, full, [full], File.Exists(full) ? [] : [full], File.Exists(full) ? new FileInfo(full).Length : 0);
    }

    /// <summary>True when "base.z01"-style siblings (prefix + two or more digits) exist, even if the first one is missing.</summary>
    private static bool HasSibling(string dir, string baseName, string extPrefix) =>
        Directory.EnumerateFiles(dir, baseName + extPrefix + "*").Any(f =>
        {
            var ext = Path.GetExtension(f);
            return ext.Length >= 4 && ext[2..].All(char.IsAsciiDigit);
        });

    private static VolumeSet Collect(VolumeScheme scheme, string dir, Func<int, string> nameOf, int start, string? closing, string globPattern)
    {
        // Find the highest-numbered sibling that exists (by regenerating its expected name), then report every gap below it.
        int highest = 0;
        foreach (var f in Directory.EnumerateFiles(dir, globPattern))
        {
            var digits = new string(Path.GetFileName(f).Reverse().SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
            if (!int.TryParse(digits, out var n)) continue;
            foreach (var i in new[] { n, n + 1 }) // RarOld maps .r00 to volume 1
            {
                if (i >= start && string.Equals(nameOf(i), Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))
                    highest = Math.Max(highest, i);
            }
        }
        var volumes = new List<string>();
        var missing = new List<string>();
        for (int i = start; i <= Math.Max(highest, start); i++)
        {
            var p = Path.Combine(dir, nameOf(i));
            if (File.Exists(p)) volumes.Add(p); else missing.Add(p);
        }
        if (closing is not null)
        {
            var c = Path.Combine(dir, closing);
            if (File.Exists(c)) volumes.Add(c); else missing.Add(c);
        }
        var first = Path.Combine(dir, nameOf(start));
        return new VolumeSet(scheme, first, volumes, missing, volumes.Sum(v => new FileInfo(v).Length));
    }
}
