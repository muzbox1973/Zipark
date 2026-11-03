using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Zipark.Core;
using Microsoft.Win32.SafeHandles;

namespace Zipark.VirtualDisk.Iso;

public sealed record IsoMount(string ImagePath, string? DriveLetter, string PhysicalPath);

/// <summary>
/// Mounts ISO images with the Windows virtual disk API (virtdisk.dll, the same mechanism Explorer's
/// "Mount" uses). Images are attached read-only; no driver is installed by Zipark.
/// </summary>
public static class IsoMounter
{
    public static IsoMount Mount(string isoPath, TimeSpan? driveLetterTimeout = null)
    {
        var full = Path.GetFullPath(isoPath);
        if (!File.Exists(full)) throw new ZiparkException(ErrorCode.FileNotFound, full);
        using var handle = Open(full);
        var parameters = new ATTACH_VIRTUAL_DISK_PARAMETERS { Version = 1 };
        int err = AttachVirtualDisk(handle, IntPtr.Zero,
            ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY | ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME, 0, ref parameters, IntPtr.Zero);
        if (err == ERROR_ACCESS_DENIED || err == ERROR_PRIVILEGE_NOT_HELD)
            throw new ZiparkException(ErrorCode.AccessDenied, "Windows denied mounting the image: " + new Win32Exception(err).Message);
        if (err != 0)
            throw new ZiparkException(ErrorCode.MountFailed, "ISO mount failed: " + new Win32Exception(err).Message);

        var physical = GetPhysicalPath(handle) ?? throw new ZiparkException(ErrorCode.MountFailed, "Mounted image has no device path.");
        var letter = WaitForDriveLetter(physical, driveLetterTimeout ?? TimeSpan.FromSeconds(8));
        Log.Info($"iso mounted {full} -> {physical} {letter}");
        return new IsoMount(full, letter, physical);
    }

    public static void Unmount(string isoPath)
    {
        var full = Path.GetFullPath(isoPath);
        using var handle = Open(full);
        int err = DetachVirtualDisk(handle, 0, 0);
        if (err != 0 && err != ERROR_NOT_FOUND && err != ERROR_VIRTDISK_NOT_ATTACHED)
            throw new ZiparkException(ErrorCode.MountFailed, "ISO unmount failed: " + new Win32Exception(err).Message);
        Log.Info("iso unmounted " + full);
    }

    /// <summary>Returns mount details when the image is currently attached, otherwise null.</summary>
    public static IsoMount? Query(string isoPath)
    {
        var full = Path.GetFullPath(isoPath);
        if (!File.Exists(full)) return null;
        try
        {
            using var handle = Open(full);
            var physical = GetPhysicalPath(handle);
            return physical is null ? null : new IsoMount(full, FindDriveLetter(physical), physical);
        }
        catch (ZiparkException)
        {
            return null;
        }
    }

    private static SafeFileHandle Open(string path)
    {
        var type = new VIRTUAL_STORAGE_TYPE { DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_ISO, VendorId = VendorMicrosoft };
        var p = new OPEN_VIRTUAL_DISK_PARAMETERS { Version = 1, RWDepth = 1 };
        int err = OpenVirtualDisk(ref type, path, VIRTUAL_DISK_ACCESS_READ, 0, ref p, out var handle);
        if (err != 0)
        {
            handle.Dispose();
            throw new ZiparkException(err == ERROR_FILE_NOT_FOUND ? ErrorCode.FileNotFound : ErrorCode.MountFailed,
                "Cannot open image: " + new Win32Exception(err).Message);
        }
        return handle;
    }

    private static string? GetPhysicalPath(SafeFileHandle handle)
    {
        int size = 1024;
        var sb = new StringBuilder(size / 2);
        int err = GetVirtualDiskPhysicalPath(handle, ref size, sb);
        return err == 0 && sb.Length > 0 ? sb.ToString() : null;
    }

    private static string? WaitForDriveLetter(string physicalPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var letter = FindDriveLetter(physicalPath);
            if (letter is not null) return letter;
            Thread.Sleep(200);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    /// <summary>Maps "\\.\CDROM1" to the drive letter whose DOS device is "\Device\CdRom1".</summary>
    public static string? FindDriveLetter(string physicalPath)
    {
        var device = physicalPath.Replace(@"\\.\", "", StringComparison.Ordinal);
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.CDRom) continue;
            var name = d.Name.TrimEnd('\\');
            var target = new StringBuilder(512);
            if (QueryDosDevice(name, target, target.Capacity) == 0) continue;
            var t = target.ToString();
            if (t.EndsWith("\\" + device, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    // ------------------------------------------------------------- interop

    private const uint VIRTUAL_STORAGE_TYPE_DEVICE_ISO = 1;
    private static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47e9-901F-71415A66345B");
    private const uint VIRTUAL_DISK_ACCESS_READ = 0x000d0000;
    private const uint ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY = 0x1;
    private const uint ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME = 0x4;
    private const int ERROR_FILE_NOT_FOUND = 2, ERROR_ACCESS_DENIED = 5, ERROR_NOT_FOUND = 1168, ERROR_PRIVILEGE_NOT_HELD = 1314;
    private const int ERROR_VIRTDISK_NOT_ATTACHED = unchecked((int)0xC03A0015);

    [StructLayout(LayoutKind.Sequential)]
    private struct VIRTUAL_STORAGE_TYPE
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OPEN_VIRTUAL_DISK_PARAMETERS
    {
        public int Version;
        public uint RWDepth;
        // Version 2 fields follow in the native union; version 1 only reads RWDepth.
        private int _getInfoOnly;
        private int _readOnly;
        private Guid _resiliencyGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ATTACH_VIRTUAL_DISK_PARAMETERS
    {
        public int Version;
        private ulong _reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern int OpenVirtualDisk(ref VIRTUAL_STORAGE_TYPE type, string path, uint access, uint flags, ref OPEN_VIRTUAL_DISK_PARAMETERS parameters, out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern int AttachVirtualDisk(SafeFileHandle handle, IntPtr securityDescriptor, uint flags, uint providerSpecificFlags, ref ATTACH_VIRTUAL_DISK_PARAMETERS parameters, IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    private static extern int DetachVirtualDisk(SafeFileHandle handle, uint flags, uint providerSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern int GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref int diskPathSizeInBytes, StringBuilder diskPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
}
