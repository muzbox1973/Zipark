using System.Text.Json;
using System.Text.Json.Serialization;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Crypto;

namespace Zipark.Application.Jobs;

public enum JobKind { Compress, Extract, Test, Checksum, SplitFile, MergeFile }

/// <summary>
/// Serializable description of one unit of work. It travels to the worker process as JSON; the password
/// is sent separately on the worker's stdin and is never part of this object.
/// </summary>
public sealed class JobRequest
{
    public JobKind Kind { get; set; }
    public List<string> Sources { get; set; } = [];
    public string? Output { get; set; }
    public string? Destination { get; set; }
    public bool HasPassword { get; set; }

    // compress
    public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZip;
    public CompressionLevel Level { get; set; } = CompressionLevel.Normal;
    public int Threads { get; set; }
    public bool EncryptFileNames { get; set; } = true;
    public ZipEncryption ZipEncryption { get; set; } = ZipEncryption.Aes256;
    public long? VolumeSize { get; set; }
    public bool? Solid { get; set; }
    public CompressionMethod Method { get; set; }
    public int? DictionaryMb { get; set; }
    public bool VerifyAfter { get; set; }

    // extract
    public List<string>? Selection { get; set; }
    public string? StripPrefix { get; set; }
    public bool FlattenPaths { get; set; }
    public bool AllowLimitOverride { get; set; }
    public long BombMaxTotalBytes { get; set; } = new BombLimits().MaxTotalBytes;
    public double BombSuspiciousRatio { get; set; } = new BombLimits().SuspiciousRatio;

    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;

    // split
    public long PartSize { get; set; }

    public string Describe() => Kind switch
    {
        JobKind.Compress => $"{Path.GetFileName(Output)}",
        JobKind.Extract => $"{Path.GetFileName(Sources.FirstOrDefault())} → {Destination}",
        JobKind.SplitFile => $"{Path.GetFileName(Sources.FirstOrDefault())} ({ByteSize.Format(PartSize)})",
        _ => Path.GetFileName(Sources.FirstOrDefault()) ?? "",
    };

    public CompressOptions ToCompressOptions(SecretPassword? pw) => new()
    {
        Sources = Sources,
        OutputPath = Output ?? throw new ZiparkException(ErrorCode.InvalidArgument, "Output path missing."),
        Format = Format,
        Level = Level,
        Threads = Threads,
        Password = pw,
        EncryptFileNames = EncryptFileNames,
        ZipEncryption = ZipEncryption,
        VolumeSize = VolumeSize,
        Solid = Solid,
        Method = Method,
        DictionaryMb = DictionaryMb,
        Conflict = Conflict,
    };

    public ExtractOptions ToExtractOptions(SecretPassword? pw, Func<ConflictInfo, ConflictDecision>? conflictPrompt = null) => new()
    {
        ArchivePath = Sources.FirstOrDefault() ?? throw new ZiparkException(ErrorCode.InvalidArgument, "Archive path missing."),
        Destination = Destination ?? throw new ZiparkException(ErrorCode.InvalidArgument, "Destination missing."),
        Password = pw,
        Selection = Selection,
        StripPrefix = StripPrefix,
        FlattenPaths = FlattenPaths,
        AllowLimitOverride = AllowLimitOverride,
        Limits = new BombLimits { MaxTotalBytes = BombMaxTotalBytes, SuspiciousRatio = BombSuspiciousRatio },
        Conflict = Conflict,
        ConflictPrompt = conflictPrompt,
    };
}

public sealed class JobResult
{
    public bool Success { get; set; }
    public ErrorCode Error { get; set; }
    public string? Message { get; set; }
    public List<string> Outputs { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<ChecksumResult> Checksums { get; set; } = [];
    public long InputBytes { get; set; }
    public long OutputBytes { get; set; }
    public double ElapsedSeconds { get; set; }
    public int Files { get; set; }

    public static JobResult Fail(ZiparkException ex) => new() { Success = false, Error = ex.Code, Message = ex.Message };
}

/// <summary>Line-delimited JSON messages between the UI and Zipark.Worker.</summary>
public sealed class WorkerMessage
{
    public string Type { get; set; } = "";   // "progress" | "result"
    public JobProgress? Progress { get; set; }
    public JobResult? Result { get; set; }
    /// <summary>Type "conflict": the worker waits for a <see cref="ConflictAnswer"/> line on stdin.</summary>
    public ConflictInfo? Conflict { get; set; }
}

[JsonSerializable(typeof(JobRequest))]
[JsonSerializable(typeof(JobResult))]
[JsonSerializable(typeof(WorkerMessage))]
[JsonSerializable(typeof(ConflictAnswer))]
[JsonSourceGenerationOptions(UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class JobJsonContext : JsonSerializerContext;

/// <summary>UI → worker reply to a conflict question (one JSON line on the worker's stdin).</summary>
public sealed class ConflictAnswer
{
    public ConflictChoice Choice { get; set; }
    public bool ApplyToAll { get; set; }
}

public static class JobJson
{
    public static string Serialize(ConflictAnswer a) => JsonSerializer.Serialize(a, JobJsonContext.Default.ConflictAnswer);
    public static ConflictAnswer? DeserializeAnswer(string s) => JsonSerializer.Deserialize(s, JobJsonContext.Default.ConflictAnswer);
    public static string Serialize(JobRequest r) => JsonSerializer.Serialize(r, JobJsonContext.Default.JobRequest);
    public static JobRequest? DeserializeRequest(string s) => JsonSerializer.Deserialize(s, JobJsonContext.Default.JobRequest);
    public static string Serialize(WorkerMessage m) => JsonSerializer.Serialize(m, JobJsonContext.Default.WorkerMessage);
    public static WorkerMessage? DeserializeMessage(string s) => JsonSerializer.Deserialize(s, JobJsonContext.Default.WorkerMessage);
}
