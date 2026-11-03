using System.Globalization;
using Zipark.Core;

namespace Zipark.Compression;

/// <summary>Parses the technical listing produced by <c>7z l -slt</c>.</summary>
public static class SevenZipListParser
{
    public static ArchiveInfo Parse(string archivePath, string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        int i = 0;
        var archiveProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        // Archive section starts at "--" and ends at "----------".
        while (i < lines.Length && lines[i].Trim() != "--") i++;
        i++;
        for (; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.StartsWith("----------", StringComparison.Ordinal)) { i++; break; }
            if (TrySplit(l, out var k, out var v)) archiveProps[k] = v;
            else if (l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) || l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) warnings.Add(l.Trim());
        }

        var entries = new List<ArchiveEntry>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (; i <= lines.Length; i++)
        {
            var l = i < lines.Length ? lines[i] : "";
            if (string.IsNullOrWhiteSpace(l))
            {
                if (current.Count > 0 && current.ContainsKey("Path"))
                    entries.Add(ToEntry(current, entries.Count));
                current.Clear();
                continue;
            }
            if (TrySplit(l, out var k, out var v)) current[k] = v;
            else if (l.Contains("Warnings:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) warnings.Add(l.Trim());
        }

        var format = ArchiveFormats.FromSevenZipType(Get(archiveProps, "Type"));
        return new ArchiveInfo
        {
            ArchivePath = archivePath,
            Format = format,
            PhysicalSize = ParseLong(Get(archiveProps, "Physical Size")) ?? (File.Exists(archivePath) ? new FileInfo(archivePath).Length : 0),
            IsSolid = Get(archiveProps, "Solid") == "+",
            BlockCount = (int?)ParseLong(Get(archiveProps, "Blocks")),
            Method = Get(archiveProps, "Method"),
            VolumeCount = (int?)ParseLong(Get(archiveProps, "Volumes")) ?? 1,
            Entries = entries,
            Warnings = warnings,
        };
    }

    private static ArchiveEntry ToEntry(Dictionary<string, string> p, int index)
    {
        var attrs = Get(p, "Attributes") ?? "";
        var winAttrs = attrs.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => !t.StartsWith('-') && !t.StartsWith('d') && !t.StartsWith('l')) ?? "";
        var unixMode = attrs.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t.Length == 10 && (t[0] is '-' or 'd' or 'l'));
        bool isDir = Get(p, "Folder") == "+" || winAttrs.Contains('D') || unixMode?[0] == 'd';
        bool isLink = unixMode?[0] == 'l' || !string.IsNullOrEmpty(Get(p, "Symbolic Link")) || winAttrs.Contains('L');
        uint? crc = uint.TryParse(Get(p, "CRC"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c) ? c : null;
        return new ArchiveEntry
        {
            Index = index,
            Path = (Get(p, "Path") ?? "").Replace('/', '\\'),
            IsDirectory = isDir,
            Size = ParseLong(Get(p, "Size")) ?? 0,
            PackedSize = ParseLong(Get(p, "Packed Size")),
            Modified = ParseDate(Get(p, "Modified")),
            Created = ParseDate(Get(p, "Created")),
            Crc = crc,
            Encrypted = Get(p, "Encrypted") == "+",
            Method = Get(p, "Method"),
            Block = (int?)ParseLong(Get(p, "Block")),
            Attributes = attrs,
            IsSymbolicLink = isLink,
            LinkTarget = Get(p, "Symbolic Link"),
        };
    }

    private static bool TrySplit(string line, out string key, out string value)
    {
        var idx = line.IndexOf(" = ", StringComparison.Ordinal);
        if (idx <= 0)
        {
            // "Key =" with an empty value
            if (line.EndsWith(" =", StringComparison.Ordinal))
            {
                key = line[..^2].Trim();
                value = "";
                return key.Length > 0;
            }
            key = value = "";
            return false;
        }
        key = line[..idx].Trim();
        value = line[(idx + 3)..];
        return true;
    }

    private static string? Get(Dictionary<string, string> d, string k) =>
        d.TryGetValue(k, out var v) && v.Length > 0 ? v : null;

    private static long? ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        // "2026-11-02 18:27:56.1781735" (fraction length varies)
        var main = s.Length >= 19 ? s[..19] : s;
        return DateTime.TryParseExact(main, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
    }
}
