using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Zipark.Core;
using Zipark.Core.Native;
using Zipark.Crypto;
using Zipark.VirtualDisk.Iso;
using Zipark.VirtualFileSystem;

namespace Zipark.Application.Drives;

public enum DriveState { Mounting, Mounted, Unmounting, Unmounted, Failed }

public sealed class MountedDrive : INotifyPropertyChanged
{
    private DriveState _state = DriveState.Mounting;
    private long _cacheBytes;
    private string? _message;
    private string? _letter;

    internal MountedDrive(string source, ArchiveFormat format, bool encrypted)
    {
        SourcePath = source;
        Format = format;
        Encrypted = encrypted;
    }

    public string SourcePath { get; }
    public string SourceName => Path.GetFileName(SourcePath);
    public ArchiveFormat Format { get; }
    public bool Encrypted { get; internal set; }
    public DateTime MountedAt { get; } = DateTime.Now;
    internal Process? Host { get; set; }

    /// <summary>"X:"</summary>
    public string? Letter { get => _letter; internal set => Set(ref _letter, value); }
    public DriveState State { get => _state; internal set => Set(ref _state, value); }
    public long CacheBytes { get => _cacheBytes; internal set => Set(ref _cacheBytes, value); }
    public string? Message { get => _message; internal set => Set(ref _message, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record VirtualDriveOptions(int MemoryCacheMb = 256, int DiskCacheMb = 2048, char PreferredLetter = 'X', string? HostExecutable = null, string? SevenZipPath = null);

/// <summary>
/// Manages ISO drives (Windows virtual disk API) and ZIP/7Z drives (one Zipark.VfsHost process each,
/// backed by WinFsp). Archive drives are read-only; the source archive is locked against writes while
/// mounted and the drive disappears when Zipark exits.
/// </summary>
public sealed class VirtualDriveService : IDisposable
{
    private readonly object _gate = new();
    private readonly List<MountedDrive> _drives = [];
    private VirtualDriveOptions _options;

    public VirtualDriveService(VirtualDriveOptions? options = null)
    {
        _options = options ?? new VirtualDriveOptions();
    }

    public void UpdateOptions(VirtualDriveOptions options) => _options = options;

    public event Action<MountedDrive>? DriveAdded;
    public event Action<MountedDrive>? DriveRemoved;

    public IReadOnlyList<MountedDrive> Drives
    {
        get
        {
            lock (_gate) return _drives.ToList();
        }
    }

    public string HostExecutable => _options.HostExecutable ?? Path.Combine(AppContext.BaseDirectory, "Zipark.VfsHost.exe");

    public static bool WinFspInstalled => WinFspRuntime.IsInstalled;

    public MountedDrive? Find(string letterOrPath)
    {
        var key = letterOrPath.TrimEnd('\\');
        lock (_gate)
            return _drives.FirstOrDefault(d => d.State == DriveState.Mounted &&
                (string.Equals(d.Letter, key, StringComparison.OrdinalIgnoreCase) || string.Equals(d.SourcePath, Path.GetFullPath(letterOrPath), StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<MountedDrive> MountAsync(string path, SecretPassword? password, char? letter = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new ZiparkException(ErrorCode.FileNotFound, full);
        if (Find(full) is { } existing) return existing;
        var format = ArchiveFormats.Detect(full);
        if (format == ArchiveFormat.Iso) return await MountIsoAsync(full).ConfigureAwait(false);
        if (format is not (ArchiveFormat.Zip or ArchiveFormat.SevenZip))
            throw new ZiparkException(ErrorCode.UnsupportedFormat, "Only ISO, ZIP and 7Z can be mounted as a drive.");
        if (!WinFspRuntime.IsInstalled)
            throw new ZiparkException(ErrorCode.VirtualDriveUnavailable, "WinFsp is required for ZIP/7Z drives.");
        if (!File.Exists(HostExecutable))
            throw new ZiparkException(ErrorCode.VirtualDriveUnavailable, "Zipark.VfsHost.exe is missing.");

        var drive = new MountedDrive(full, format, password is not null);
        var chosen = letter is { } l ? $"{char.ToUpperInvariant(l)}:" : FindFreeLetter(_options.PreferredLetter);
        if (chosen is null) throw new ZiparkException(ErrorCode.DriveLetterInUse, "No free drive letter.");
        if (IsLetterUsed(chosen[0])) throw new ZiparkException(ErrorCode.DriveLetterInUse, $"{chosen} is already in use.");
        drive.Letter = chosen;
        Add(drive);

        var psi = new ProcessStartInfo(HostExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--archive", full, "--mount", chosen, "--memory-cache-mb", _options.MemoryCacheMb.ToString(), "--disk-cache-mb", _options.DiskCacheMb.ToString() })
            psi.ArgumentList.Add(a);
        if (password is not null) psi.ArgumentList.Add("--password-stdin");
        if (!string.IsNullOrEmpty(_options.SevenZipPath))
        {
            psi.ArgumentList.Add("--7z");
            psi.ArgumentList.Add(_options.SevenZipPath);
        }

        var p = Process.Start(psi) ?? throw new ZiparkException(ErrorCode.MountFailed, "Host did not start.");
        ProcessJob.Shared?.TryAdd(p);
        drive.Host = p;
        if (password is not null) password.WriteLineTo(p.StandardInput.BaseStream);
        _ = p.StandardError.ReadToEndAsync(CancellationToken.None);

        var mounted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? errorCode = null, errorMessage = null;
        _ = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var ev = root.GetProperty("event").GetString();
                    switch (ev)
                    {
                        case "mounted":
                            drive.State = DriveState.Mounted;
                            if (root.TryGetProperty("encrypted", out var enc)) drive.Encrypted = enc.GetBoolean();
                            mounted.TrySetResult(true);
                            break;
                        case "error":
                            errorCode = root.GetProperty("code").GetString();
                            errorMessage = root.GetProperty("message").GetString();
                            drive.Message = errorMessage;
                            mounted.TrySetResult(false);
                            break;
                        case "stats":
                            drive.CacheBytes = root.GetProperty("memoryBytes").GetInt64() + root.GetProperty("diskBytes").GetInt64();
                            break;
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    // ignore malformed lines
                }
            }
            mounted.TrySetResult(false);
            drive.State = drive.State == DriveState.Mounted || drive.State == DriveState.Unmounting ? DriveState.Unmounted : DriveState.Failed;
            Remove(drive);
        }, CancellationToken.None);

        var done = await Task.WhenAny(mounted.Task, Task.Delay(TimeSpan.FromMinutes(2), ct)).ConfigureAwait(false);
        if (done != mounted.Task || !mounted.Task.Result)
        {
            SevenZip_Kill(p);
            drive.State = DriveState.Failed;
            Remove(drive);
            var code = Enum.TryParse<ErrorCode>(errorCode, out var c) ? c : ErrorCode.MountFailed;
            throw new ZiparkException(code, errorMessage ?? "Mount failed.");
        }
        Log.Info($"drive {chosen} mounted for {full}");
        return drive;
    }

    private async Task<MountedDrive> MountIsoAsync(string full)
    {
        var drive = new MountedDrive(full, ArchiveFormat.Iso, false);
        Add(drive);
        try
        {
            var m = await Task.Run(() => IsoMounter.Mount(full)).ConfigureAwait(false);
            drive.Letter = m.DriveLetter;
            drive.State = DriveState.Mounted;
            return drive;
        }
        catch
        {
            drive.State = DriveState.Failed;
            Remove(drive);
            throw;
        }
    }

    /// <summary>Re-attaches tracking for an ISO that Windows already has mounted.</summary>
    public MountedDrive? TrackExistingIso(string isoPath)
    {
        var q = IsoMounter.Query(isoPath);
        if (q?.DriveLetter is null) return null;
        if (Find(q.ImagePath) is { } d) return d;
        var drive = new MountedDrive(q.ImagePath, ArchiveFormat.Iso, false) { Letter = q.DriveLetter, State = DriveState.Mounted };
        Add(drive);
        return drive;
    }

    public async Task UnmountAsync(MountedDrive drive)
    {
        drive.State = DriveState.Unmounting;
        if (drive.Format == ArchiveFormat.Iso)
        {
            await Task.Run(() => IsoMounter.Unmount(drive.SourcePath)).ConfigureAwait(false);
            drive.State = DriveState.Unmounted;
            Remove(drive);
            return;
        }
        var p = drive.Host;
        if (p is not null && !p.HasExited)
        {
            try
            {
                await p.StandardInput.WriteLineAsync("unmount").ConfigureAwait(false);
                await p.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // host already closing
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SevenZip_Kill(p);
            }
        }
        drive.State = DriveState.Unmounted;
        Remove(drive);
    }

    public void RequestStats()
    {
        foreach (var d in Drives.Where(d => d.Host is { HasExited: false }))
        {
            try
            {
                d.Host!.StandardInput.WriteLine("stats");
                d.Host.StandardInput.Flush();
            }
            catch (IOException)
            {
                // host gone
            }
        }
    }

    private void Add(MountedDrive d)
    {
        lock (_gate) _drives.Add(d);
        DriveAdded?.Invoke(d);
    }

    private void Remove(MountedDrive d)
    {
        bool removed;
        lock (_gate) removed = _drives.Remove(d);
        if (removed) DriveRemoved?.Invoke(d);
    }

    private static void SevenZip_Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // gone
        }
    }

    public static bool IsLetterUsed(char letter)
    {
        uint mask = GetLogicalDrives();
        int bit = char.ToUpperInvariant(letter) - 'A';
        return bit is < 0 or > 25 || (mask & (1u << bit)) != 0;
    }

    /// <summary>Searches downwards from the preferred letter (X:, W:, …, D:), then upwards (Y:, Z:).</summary>
    public static string? FindFreeLetter(char preferred = 'X')
    {
        preferred = char.ToUpperInvariant(preferred);
        if (preferred is < 'D' or > 'Z') preferred = 'X';
        for (char c = preferred; c >= 'D'; c--)
            if (!IsLetterUsed(c)) return $"{c}:";
        for (char c = (char)(preferred + 1); c <= 'Z'; c++)
            if (!IsLetterUsed(c)) return $"{c}:";
        return null;
    }

    public void Dispose()
    {
        foreach (var d in Drives.Where(d => d.Format != ArchiveFormat.Iso))
        {
            try { UnmountAsync(d).Wait(TimeSpan.FromSeconds(10)); } catch { /* shutting down */ }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();
}
