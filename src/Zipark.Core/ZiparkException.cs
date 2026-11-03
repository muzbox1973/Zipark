namespace Zipark.Core;

public enum ErrorCode
{
    Unknown = 0,
    Cancelled,
    EngineNotFound,
    EngineCrashed,
    WrongPassword,
    PasswordRequired,
    CorruptArchive,
    MissingVolume,
    DamagedVolume,
    UnsupportedFormat,
    UnsafePath,
    ArchiveBomb,
    DiskFull,
    AccessDenied,
    FileNotFound,
    InvalidArgument,
    VirtualDriveUnavailable,
    DriveLetterInUse,
    MountFailed,
    ChecksumMismatch,
}

/// <summary>Application error with a stable code the UI can localize.</summary>
public class ZiparkException : Exception
{
    public ErrorCode Code { get; }

    public ZiparkException(ErrorCode code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }

    public static ZiparkException Wrap(Exception ex) => ex switch
    {
        ZiparkException f => f,
        OperationCanceledException => new ZiparkException(ErrorCode.Cancelled, "Operation cancelled.", ex),
        UnauthorizedAccessException => new ZiparkException(ErrorCode.AccessDenied, ex.Message, ex),
        FileNotFoundException or DirectoryNotFoundException => new ZiparkException(ErrorCode.FileNotFound, ex.Message, ex),
        IOException io when DiskSpace.IsDiskFull(io) => new ZiparkException(ErrorCode.DiskFull, ex.Message, ex),
        _ => new ZiparkException(ErrorCode.Unknown, ex.Message, ex),
    };
}
