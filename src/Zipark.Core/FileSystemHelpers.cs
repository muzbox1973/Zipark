using System.Globalization;
using System.Runtime.InteropServices;

namespace Zipark.Core;

public enum ConflictPolicy
{
    /// <summary>Keep both: the new file gets a " (2)" style suffix. Default — never destroys user data.</summary>
    Rename = 0,
    Skip,
    Overwrite,
}

public static class ConflictResolver
{
    /// <summary>Returns a path that does not exist yet by adding " (n)" before the extension.</summary>
    public static string GetUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 100_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not find a free file name for " + path);
    }
}

public static class DiskSpace
{
    /// <summary>Free bytes for the volume holding <paramref name="path"/>, or -1 when unknown.</summary>
    public static long GetFreeBytes(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var probe = full;
            while (!Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (parent is null) return -1;
                probe = parent;
            }
            return GetDiskFreeSpaceEx(probe, out var avail, out _, out _) ? (long)avail : -1;
        }
        catch
        {
            return -1;
        }
    }

    public static void EnsureAvailable(string path, long requiredBytes)
    {
        var free = GetFreeBytes(path);
        if (free >= 0 && requiredBytes > free)
            throw new ZiparkException(ErrorCode.DiskFull,
                $"Not enough disk space: need {ByteSize.Format(requiredBytes)}, free {ByteSize.Format(free)}.");
    }

    public static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is 112 or 39; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
}

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0) return "-";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "F0" : v >= 10 ? "F1" : "F2", CultureInfo.InvariantCulture) + " " + Units[u];
    }

    /// <summary>Parses "100MB", "4 GB", "700m", "1024" (bytes).</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToUpperInvariant().Replace(" ", "");
        long mul = 1;
        string[] suffixes = ["TB", "GB", "MB", "KB", "T", "G", "M", "K", "B"];
        foreach (var s in suffixes)
        {
            if (!t.EndsWith(s)) continue;
            mul = s[0] switch { 'T' => 1L << 40, 'G' => 1L << 30, 'M' => 1L << 20, 'K' => 1L << 10, _ => 1 };
            t = t[..^s.Length];
            break;
        }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) return false;
        bytes = (long)(v * mul);
        return bytes > 0;
    }
}

public static class AppPaths
{
    public static string RoamingDir => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zipark"));
    public static string LocalDir => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zipark"));
    public static string LogDir => Ensure(Path.Combine(LocalDir, "logs"));
    public static string CacheRoot => Ensure(Path.Combine(LocalDir, "cache"));
    public static string TempRoot => Ensure(Path.Combine(Path.GetTempPath(), "Zipark"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
