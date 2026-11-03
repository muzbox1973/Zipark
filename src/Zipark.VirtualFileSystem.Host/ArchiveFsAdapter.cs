using System.Security.AccessControl;
using Fsp;
using Zipark.Core;
using Zipark.VirtualFileSystem;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FspFileInfo = Fsp.Interop.FileInfo;

namespace Zipark.VirtualFileSystem.Host;

/// <summary>
/// WinFsp binding for <see cref="ReadOnlyArchiveFileSystem"/>. Every modifying callback returns
/// STATUS_MEDIA_WRITE_PROTECTED, and the security descriptor only grants read/execute, so the drive
/// is read-only at both the file-system and the access-check level.
/// </summary>
internal sealed class ArchiveFsAdapter : FileSystemBase
{
    private readonly ReadOnlyArchiveFileSystem _fs;
    private readonly byte[] _securityDescriptor;
    private const int AllocationUnit = 4096;

    public ArchiveFsAdapter(ReadOnlyArchiveFileSystem fs)
    {
        _fs = fs;
        // Owner/Group: Administrators. DACL (protected): SYSTEM, Administrators, Everyone → read + execute only.
        var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FRFX;;;SY)(A;;FRFX;;;BA)(A;;FRFX;;;WD)");
        _securityDescriptor = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_securityDescriptor, 0);
    }

    public override int Init(object hostObject)
    {
        var host = (FileSystemHost)hostObject;
        host.SectorSize = AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 60_000;       // content never changes while mounted
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = true;
        host.PassQueryDirectoryPattern = false;
        host.FlushAndPurgeOnCleanup = false;
        host.NamedStreams = false;
        host.ReparsePoints = false;
        host.VolumeCreationTime = _fs.Index.Root.CreationTime;
        host.VolumeSerialNumber = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(_fs.ArchivePath);
        host.FileSystemName = "Zipark";
        return STATUS_SUCCESS;
    }

    public override int ExceptionHandler(Exception ex)
    {
        Log.Error("winfsp callback failed", ex);
        return STATUS_UNEXPECTED_IO_ERROR;
    }

    private static int ToNtStatus(VfsStatus s) => s switch
    {
        VfsStatus.Success => STATUS_SUCCESS,
        VfsStatus.ObjectNotFound => STATUS_OBJECT_NAME_NOT_FOUND,
        VfsStatus.PathNotFound => STATUS_OBJECT_PATH_NOT_FOUND,
        VfsStatus.NotADirectory => STATUS_NOT_A_DIRECTORY,
        VfsStatus.IsADirectory => STATUS_FILE_IS_A_DIRECTORY,
        VfsStatus.WriteProtected => STATUS_MEDIA_WRITE_PROTECTED,
        VfsStatus.WrongPassword => STATUS_WRONG_PASSWORD,
        VfsStatus.DataError => STATUS_DATA_ERROR,
        _ => STATUS_UNEXPECTED_IO_ERROR,
    };

    private static void Fill(VfsNode node, out FspFileInfo fi)
    {
        fi = default;
        fi.FileAttributes = node.Attributes;
        fi.ReparseTag = 0;
        fi.FileSize = (ulong)node.Size;
        fi.AllocationSize = (ulong)((node.Size + AllocationUnit - 1) / AllocationUnit * AllocationUnit);
        fi.CreationTime = node.CreationTime;
        fi.LastAccessTime = node.LastWriteTime;
        fi.LastWriteTime = node.LastWriteTime;
        fi.ChangeTime = node.LastWriteTime;
        fi.IndexNumber = (ulong)node.Id;
        fi.HardLinks = 0;
    }

    // ---------------------------------------------------------------- volume

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = (ulong)Math.Max(_fs.Index.TotalFileBytes, AllocationUnit);
        volumeInfo.FreeSize = 0;
        volumeInfo.SetVolumeLabel(_fs.VolumeLabel);
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        GetVolumeInfo(out volumeInfo);
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    // --------------------------------------------------------------- lookups

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var status = _fs.Open(fileName, out var node);
        if (status != VfsStatus.Success || node is null)
        {
            fileAttributes = 0;
            return ToNtStatus(status);
        }
        fileAttributes = node.Attributes;
        if (securityDescriptor is not null) securityDescriptor = _securityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FspFileInfo fileInfo, out string normalizedName)
    {
        fileDesc = null!;
        var status = _fs.Open(fileName, out var node);
        if (status != VfsStatus.Success || node is null)
        {
            fileNode = null!;
            fileInfo = default;
            normalizedName = null!;
            return ToNtStatus(status);
        }
        if ((createOptions & FILE_DIRECTORY_FILE) != 0 && !node.IsDirectory)
        {
            fileNode = null!;
            fileInfo = default;
            normalizedName = null!;
            return STATUS_NOT_A_DIRECTORY;
        }
        fileNode = node;
        Fill(node, out fileInfo);
        normalizedName = node.Path;
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FspFileInfo fileInfo)
    {
        Fill((VfsNode)fileNode, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _securityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int GetDirInfoByName(object parentNode, object fileDesc, string fileName, out string normalizedName, out FspFileInfo fileInfo)
    {
        var child = ((VfsNode)parentNode).Child(fileName);
        if (child is null)
        {
            normalizedName = null!;
            fileInfo = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        normalizedName = child.Name;
        Fill(child, out fileInfo);
        return STATUS_SUCCESS;
    }

    // ------------------------------------------------------------------ read

    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        var node = (VfsNode)fileNode;
        bytesTransferred = 0;
        if (node.IsDirectory) return STATUS_INVALID_DEVICE_REQUEST;
        if (offset >= (ulong)node.Size) return STATUS_END_OF_FILE;
        var span = new Span<byte>((void*)buffer, (int)Math.Min(length, int.MaxValue));
        var status = _fs.Read(node, (long)offset, span, out var n);
        bytesTransferred = (uint)n;
        return ToNtStatus(status);
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FspFileInfo fileInfo)
    {
        var dir = (VfsNode)fileNode;
        if (context is null)
        {
            var list = new List<(string Name, VfsNode Node)>();
            if (dir.Parent is not null)
            {
                if (marker is null) list.Add((".", dir));
                if (marker is null || marker == ".") list.Add(("..", dir.Parent));
            }
            var after = marker is "." or ".." ? null : marker;
            foreach (var c in dir.ChildrenAfter(after)) list.Add((c.Name, c));
            context = list.GetEnumerator();
        }
        var e = (List<(string Name, VfsNode Node)>.Enumerator)context;
        if (e.MoveNext())
        {
            context = e; // struct enumerator: store the advanced copy
            fileName = e.Current.Name;
            Fill(e.Current.Node, out fileInfo);
            return true;
        }
        context = e;
        fileName = null!;
        fileInfo = default;
        return false;
    }

    // -------------------------------------------- everything below is denied

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor,
        ulong allocationSize, out object fileNode, out object fileDesc, out FspFileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out FspFileInfo fileInfo)
    {
        Fill((VfsNode)fileNode, out fileInfo);
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile,
        bool constrainedIo, out uint bytesTransferred, out FspFileInfo fileInfo)
    {
        bytesTransferred = 0;
        Fill((VfsNode)fileNode, out fileInfo);
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime, out FspFileInfo fileInfo)
    {
        Fill((VfsNode)fileNode, out fileInfo);
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FspFileInfo fileInfo)
    {
        Fill((VfsNode)fileNode, out fileInfo);
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) => STATUS_MEDIA_WRITE_PROTECTED;

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) => STATUS_MEDIA_WRITE_PROTECTED;

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor) => STATUS_MEDIA_WRITE_PROTECTED;

    public override int Flush(object fileNode, object fileDesc, out FspFileInfo fileInfo)
    {
        if (fileNode is VfsNode n) Fill(n, out fileInfo); else fileInfo = default;
        return STATUS_SUCCESS;
    }
}
