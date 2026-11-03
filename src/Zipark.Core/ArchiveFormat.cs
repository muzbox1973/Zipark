namespace Zipark.Core;

/// <summary>Archive container formats Zipark understands.</summary>
public enum ArchiveFormat
{
    Unknown = 0,
    Zip,
    SevenZip,
    Tar,
    GZip,
    BZip2,
    Xz,
    Zstd,
    Rar,
    Iso,
    Cab,
    Wim,
}

public static class ArchiveFormats
{
    /// <summary>Formats Zipark can create (RAR creation is intentionally not supported).</summary>
    public static readonly IReadOnlyList<ArchiveFormat> Creatable =
        [ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Tar, ArchiveFormat.GZip, ArchiveFormat.BZip2, ArchiveFormat.Xz];

    public static bool CanCreate(ArchiveFormat format) => Creatable.Contains(format);

    /// <summary>Formats that hold exactly one stream (need a single input file).</summary>
    public static bool IsSingleStream(ArchiveFormat format) =>
        format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz or ArchiveFormat.Zstd;

    public static bool SupportsEncryption(ArchiveFormat format) => format is ArchiveFormat.SevenZip or ArchiveFormat.Zip;

    public static bool SupportsFileNameEncryption(ArchiveFormat format) => format == ArchiveFormat.SevenZip;

    public static bool SupportsVolumes(ArchiveFormat format) => format is ArchiveFormat.SevenZip or ArchiveFormat.Zip or ArchiveFormat.Tar;

    public static bool CanMountAsDrive(ArchiveFormat format) => format is ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Iso;

    public static string Extension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => ".zip",
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.Tar => ".tar",
        ArchiveFormat.GZip => ".gz",
        ArchiveFormat.BZip2 => ".bz2",
        ArchiveFormat.Xz => ".xz",
        ArchiveFormat.Zstd => ".zst",
        ArchiveFormat.Rar => ".rar",
        ArchiveFormat.Iso => ".iso",
        ArchiveFormat.Cab => ".cab",
        ArchiveFormat.Wim => ".wim",
        _ => "",
    };

    /// <summary>Value for 7-Zip's -t switch.</summary>
    public static string SevenZipType(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "zip",
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Tar => "tar",
        ArchiveFormat.GZip => "gzip",
        ArchiveFormat.BZip2 => "bzip2",
        ArchiveFormat.Xz => "xz",
        ArchiveFormat.Zstd => "zstd",
        ArchiveFormat.Rar => "rar",
        ArchiveFormat.Iso => "iso",
        ArchiveFormat.Cab => "cab",
        ArchiveFormat.Wim => "wim",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static ArchiveFormat FromSevenZipType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "zip" => ArchiveFormat.Zip,
        "7z" => ArchiveFormat.SevenZip,
        "tar" => ArchiveFormat.Tar,
        "gzip" or "gz" => ArchiveFormat.GZip,
        "bzip2" or "bz2" => ArchiveFormat.BZip2,
        "xz" => ArchiveFormat.Xz,
        "zstd" => ArchiveFormat.Zstd,
        "rar" or "rar5" => ArchiveFormat.Rar,
        "iso" or "udf" => ArchiveFormat.Iso,
        "cab" => ArchiveFormat.Cab,
        "wim" => ArchiveFormat.Wim,
        _ => ArchiveFormat.Unknown,
    };

    /// <summary>Guess format from the file name (volume suffixes such as .001 are looked through).</summary>
    public static ArchiveFormat FromPath(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        // name.7z.001 -> name.7z
        if (name.Length > 4 && name[^4] == '.' && name[^3..].All(char.IsAsciiDigit))
            name = name[..^4];
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz")) return ArchiveFormat.GZip;
        if (name.EndsWith(".tar.xz") || name.EndsWith(".txz")) return ArchiveFormat.Xz;
        if (name.EndsWith(".tar.bz2") || name.EndsWith(".tbz2")) return ArchiveFormat.BZip2;
        var ext = Path.GetExtension(name);
        if (ext.Length == 4 && ext[1] == 'z' && char.IsAsciiDigit(ext[2]) && char.IsAsciiDigit(ext[3])) return ArchiveFormat.Zip; // .z01 spanned zip
        if (ext.Length == 4 && ext[1] == 'r' && char.IsAsciiDigit(ext[2]) && char.IsAsciiDigit(ext[3])) return ArchiveFormat.Rar; // .r00 old rar volumes
        return ext switch
        {
            ".zip" or ".jar" or ".docx" or ".xlsx" or ".pptx" or ".apk" => ArchiveFormat.Zip,
            ".7z" => ArchiveFormat.SevenZip,
            ".tar" => ArchiveFormat.Tar,
            ".gz" => ArchiveFormat.GZip,
            ".bz2" => ArchiveFormat.BZip2,
            ".xz" => ArchiveFormat.Xz,
            ".zst" => ArchiveFormat.Zstd,
            ".rar" => ArchiveFormat.Rar,
            ".iso" => ArchiveFormat.Iso,
            ".cab" => ArchiveFormat.Cab,
            ".wim" => ArchiveFormat.Wim,
            _ => ArchiveFormat.Unknown,
        };
    }

    /// <summary>Detect format from the first bytes of a file (signature sniffing).</summary>
    public static ArchiveFormat Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C) return ArchiveFormat.SevenZip;
        if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && (head[2] is 0x03 or 0x05 or 0x07) && (head[3] is 0x04 or 0x06 or 0x08)) return ArchiveFormat.Zip;
        if (head.Length >= 7 && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21 && head[4] == 0x1A && head[5] == 0x07) return ArchiveFormat.Rar;
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B) return ArchiveFormat.GZip;
        if (head.Length >= 3 && head[0] == 0x42 && head[1] == 0x5A && head[2] == 0x68) return ArchiveFormat.BZip2;
        if (head.Length >= 6 && head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A && head[3] == 0x58 && head[4] == 0x5A && head[5] == 0x00) return ArchiveFormat.Xz;
        if (head.Length >= 4 && head[0] == 0x28 && head[1] == 0xB5 && head[2] == 0x2F && head[3] == 0xFD) return ArchiveFormat.Zstd;
        if (head.Length >= 4 && head[0] == 0x4D && head[1] == 0x53 && head[2] == 0x43 && head[3] == 0x46) return ArchiveFormat.Cab;
        if (head.Length >= 0x8006 && head[0x8001] == 'C' && head[0x8002] == 'D' && head[0x8003] == '0' && head[0x8004] == '0' && head[0x8005] == '1') return ArchiveFormat.Iso;
        if (head.Length >= 262 && head[257] == 'u' && head[258] == 's' && head[259] == 't' && head[260] == 'a' && head[261] == 'r') return ArchiveFormat.Tar;
        return ArchiveFormat.Unknown;
    }

    public static ArchiveFormat Detect(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[0x8800];
            int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            var sniffed = Sniff(buf.AsSpan(0, n));
            if (sniffed != ArchiveFormat.Unknown) return sniffed;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return FromPath(path);
    }
}
