using Zipark.Cache;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Crypto;

namespace Zipark.VirtualFileSystem;

public enum VfsStatus
{
    Success,
    ObjectNotFound,
    PathNotFound,
    NotADirectory,
    IsADirectory,
    /// <summary>Every modifying operation: the drive is read-only by design.</summary>
    WriteProtected,
    WrongPassword,
    DataError,
    IoError,
}

/// <summary>
/// File-system semantics for a mounted archive, independent of WinFsp so they can be unit tested.
/// The host translates <see cref="VfsStatus"/> to NTSTATUS codes. The source archive is held open with
/// FileShare.Read for the whole session, so nobody (including Zipark) can modify it while mounted.
/// </summary>
public sealed class ReadOnlyArchiveFileSystem : IDisposable
{
    private readonly FileStream _archiveLock;
    private readonly HybridChunkCache _cache;

    public ArchiveIndex Index { get; }
    public ArchiveContentProvider Content { get; }
    public string ArchivePath { get; }
    public string VolumeLabel { get; }
    public bool Encrypted { get; }

    private ReadOnlyArchiveFileSystem(string archivePath, ArchiveInfo info, IArchiveEngine engine, SecretPassword? password, CacheOptions cacheOptions, FileStream archiveLock)
    {
        ArchivePath = archivePath;
        _archiveLock = archiveLock;
        Index = new ArchiveIndex(info);
        _cache = new HybridChunkCache(cacheOptions with { EncryptDisk = cacheOptions.EncryptDisk || info.HasEncryptedEntries });
        Content = new ArchiveContentProvider(archivePath, info, engine, password, _cache);
        Encrypted = info.HasEncryptedEntries;
        var label = Path.GetFileNameWithoutExtension(archivePath);
        VolumeLabel = label.Length > 32 ? label[..32] : label;
    }

    /// <summary>Lists the archive (verifying the password) and prepares the read-only file system.</summary>
    public static async Task<ReadOnlyArchiveFileSystem> OpenAsync(string archivePath, IArchiveEngine engine, SecretPassword? password, CacheOptions? cacheOptions = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(archivePath);
        // Deny writers for the lifetime of the mount (readers, including the engine, are allowed).
        var archiveLock = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            ArchiveInfo info;
            try
            {
                info = await engine.ListAsync(full, null, ct).ConfigureAwait(false);
            }
            catch (ZiparkException ex) when (ex.Code == ErrorCode.PasswordRequired && password is not null)
            {
                // Encrypted file list: a successful listing with the password proves the password.
                info = await engine.ListAsync(full, password, ct).ConfigureAwait(false) with { HeadersEncrypted = true };
            }
            if (!ArchiveFormats.CanMountAsDrive(info.Format) || info.Format == ArchiveFormat.Iso)
                throw new ZiparkException(ErrorCode.UnsupportedFormat, $"{info.Format} archives are not mounted through the archive file system.");
            if (info.Entries.Any(e => e.Encrypted))
            {
                if (password is null) throw new ZiparkException(ErrorCode.PasswordRequired, "The archive contains encrypted files. A password is required.");
                if (!info.HeadersEncrypted) await VerifyPasswordAsync(full, info, engine, password, ct).ConfigureAwait(false);
            }
            return new ReadOnlyArchiveFileSystem(full, info, engine, password, cacheOptions ?? new CacheOptions(), archiveLock);
        }
        catch
        {
            archiveLock.Dispose();
            throw;
        }
    }

    /// <summary>ZIP keeps names unencrypted, so a wrong password is only noticed when data is decoded: probe the smallest file.</summary>
    private static async Task VerifyPasswordAsync(string path, ArchiveInfo info, IArchiveEngine engine, SecretPassword password, CancellationToken ct)
    {
        var probe = info.Entries.Where(e => e.Encrypted && !e.IsDirectory && e.Size > 0).OrderBy(e => e.Size).First();
        await using var s = engine.OpenEntries(path, [probe.Path], password);
        var buf = new byte[64 * 1024];
        while (await s.ReadAsync(buf, ct).ConfigureAwait(false) > 0) { }
    }

    public VfsStatus Open(string path, out VfsNode? node)
    {
        node = Index.Lookup(path);
        if (node is not null) return VfsStatus.Success;
        var parent = Path.GetDirectoryName(path.Replace('/', '\\'));
        return parent is null || Index.Lookup(parent) is { IsDirectory: true } ? VfsStatus.ObjectNotFound : VfsStatus.PathNotFound;
    }

    public VfsStatus Read(VfsNode node, long offset, Span<byte> buffer, out int bytesRead)
    {
        bytesRead = 0;
        if (node.IsDirectory) return VfsStatus.IsADirectory;
        if (node.Entry is null || offset >= node.Size) return VfsStatus.Success; // EOF handled by caller
        try
        {
            bytesRead = Content.Read(node.Entry, offset, buffer);
            return VfsStatus.Success;
        }
        catch (ZiparkException ex) when (ex.Code == ErrorCode.WrongPassword)
        {
            return VfsStatus.WrongPassword;
        }
        catch (ZiparkException ex) when (ex.Code is ErrorCode.CorruptArchive or ErrorCode.MissingVolume or ErrorCode.DamagedVolume)
        {
            return VfsStatus.DataError;
        }
        catch (Exception ex)
        {
            Log.Error("vfs read failed: " + node.Path, ex);
            return VfsStatus.IoError;
        }
    }

    /// <summary>Create / overwrite / write / rename / delete / set-info / set-security all end here.</summary>
    public static VfsStatus DenyModification() => VfsStatus.WriteProtected;

    public CacheStats CacheStats => _cache.Stats;

    public void Dispose()
    {
        Content.Dispose();
        _cache.Dispose();
        _archiveLock.Dispose();
    }
}
