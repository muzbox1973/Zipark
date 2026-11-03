using System.Text;
using System.Text.Json;
using Fsp;
using Zipark.Cache;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Crypto;
using Zipark.VirtualFileSystem;
using Zipark.VirtualFileSystem.Host;

// Zipark.VfsHost — one process per mounted ZIP/7Z archive, so a crash in one mount never takes
// down the UI or other drives.
//
//   Zipark.VfsHost.exe --archive <path> --mount X: [--memory-cache-mb N] [--disk-cache-mb N] [--password-stdin]
//
// stdin : first line = password when --password-stdin; then commands "stats" | "unmount".
//         EOF (parent exited) unmounts as well.
// stdout: one JSON object per line: {"event":"mounted"|"stats"|"error"|"unmounted", ...}
// exit  : 0 ok, 2 error, 3 WinFsp missing, 4 password problem, 5 mount failed.

Log.Initialize("vfshost");
var opts = HostOptions.Parse(args);
if (opts is null)
{
    Emit(new { @event = "error", code = nameof(ErrorCode.InvalidArgument), message = "usage: --archive <path> --mount X: [--password-stdin]" });
    return 2;
}
if (!WinFspRuntime.IsInstalled)
{
    Emit(new { @event = "error", code = nameof(ErrorCode.VirtualDriveUnavailable), message = "WinFsp runtime is not installed.", url = WinFspRuntime.DownloadUrl });
    return 3;
}

var stdin = Console.OpenStandardInput();
SecretPassword? password = opts.PasswordFromStdin ? ReadSecretLine(stdin) : null;

ReadOnlyArchiveFileSystem fs;
try
{
    var engine = new SevenZipEngine(opts.SevenZipPath);
    var cache = new CacheOptions
    {
        MemoryLimitBytes = opts.MemoryCacheMb * 1024L * 1024,
        DiskLimitBytes = opts.DiskCacheMb * 1024L * 1024,
        EnableDisk = opts.DiskCacheMb > 0,
        EncryptDisk = true,
    };
    HybridChunkCache.CleanupStaleSessions();
    fs = await ReadOnlyArchiveFileSystem.OpenAsync(opts.Archive, engine, password, cache);
}
catch (ZiparkException ex)
{
    Emit(new { @event = "error", code = ex.Code.ToString(), message = ex.Message });
    return ex.Code is ErrorCode.WrongPassword or ErrorCode.PasswordRequired ? 4 : 2;
}
catch (Exception ex)
{
    Log.Error("open failed", ex);
    Emit(new { @event = "error", code = nameof(ErrorCode.Unknown), message = ex.Message });
    return 2;
}
finally
{
    password?.Dispose(); // the content provider keeps its own copy
}

FileSystemHost? host = null;
try
{
    host = new FileSystemHost(new ArchiveFsAdapter(fs));
    int status = host.Mount(opts.MountPoint, null, false, 0);
    if (status < 0)
    {
        Emit(new { @event = "error", code = nameof(ErrorCode.MountFailed), message = $"WinFsp mount failed (NTSTATUS 0x{status:X8}).", ntstatus = status });
        return 5;
    }
    Emit(new { @event = "mounted", mountPoint = host.MountPoint(), archive = fs.ArchivePath, encrypted = fs.Encrypted, files = fs.Index.NodeCount, bytes = fs.Index.TotalFileBytes });
    Log.Info($"mounted {fs.ArchivePath} at {host.MountPoint()}");

    // Command loop. Returns when "unmount" arrives or stdin closes.
    using var reader = new StreamReader(stdin, Encoding.UTF8);
    while (await reader.ReadLineAsync() is { } line)
    {
        var cmd = line.Trim().ToLowerInvariant();
        if (cmd == "unmount") break;
        if (cmd == "stats")
        {
            var s = fs.Content.Stats;
            Emit(new { @event = "stats", memoryBytes = s.Cache.MemoryBytes, diskBytes = s.Cache.DiskBytes, hits = s.Cache.Hits, misses = s.Cache.Misses, decodeRuns = s.DecodeRuns, bytesDecoded = s.BytesDecoded });
        }
    }
    return 0;
}
catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
{
    Emit(new { @event = "error", code = nameof(ErrorCode.VirtualDriveUnavailable), message = "WinFsp could not be loaded: " + ex.Message, url = WinFspRuntime.DownloadUrl });
    return 3;
}
catch (Exception ex)
{
    Log.Error("host failed", ex);
    Emit(new { @event = "error", code = nameof(ErrorCode.MountFailed), message = ex.Message });
    return 5;
}
finally
{
    try
    {
        host?.Unmount();
        host?.Dispose();
    }
    catch (Exception ex)
    {
        Log.Warn("unmount: " + ex.Message);
    }
    fs.Dispose();
    Emit(new { @event = "unmounted" });
}

static void Emit(object payload)
{
    lock (typeof(HostOptions))
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }
}

static SecretPassword ReadSecretLine(Stream input)
{
    // Read raw UTF-8 bytes up to '\n' so the password never becomes a managed string.
    var bytes = new List<byte>(64);
    int b;
    while ((b = input.ReadByte()) >= 0 && b != '\n') bytes.Add((byte)b);
    if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
    // Some callers (e.g. Windows PowerShell) prefix the stream with a UTF-8 BOM; it is not part of the password.
    if (bytes.Count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes.RemoveRange(0, 3);
    var raw = bytes.ToArray();
    var chars = new char[Encoding.UTF8.GetCharCount(raw)];
    Encoding.UTF8.GetChars(raw, chars);
    var pw = SecretPassword.FromChars(chars);
    Array.Clear(raw);
    Array.Clear(chars);
    return pw;
}

internal sealed record HostOptions(string Archive, string MountPoint, bool PasswordFromStdin, int MemoryCacheMb, int DiskCacheMb, string? SevenZipPath)
{
    public static HostOptions? Parse(string[] args)
    {
        string? archive = null, mount = null, sevenZip = null;
        bool pw = false;
        int mem = 256, disk = 2048;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--archive" when i + 1 < args.Length: archive = args[++i]; break;
                case "--mount" when i + 1 < args.Length: mount = args[++i]; break;
                case "--password-stdin": pw = true; break;
                case "--memory-cache-mb" when i + 1 < args.Length && int.TryParse(args[i + 1], out var m): mem = m; i++; break;
                case "--disk-cache-mb" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d): disk = d; i++; break;
                case "--7z" when i + 1 < args.Length: sevenZip = args[++i]; break;
            }
        }
        if (archive is null || mount is null) return null;
        return new HostOptions(archive, mount, pw, Math.Clamp(mem, 16, 16384), Math.Clamp(disk, 0, 1 << 20), sevenZip);
    }
}
