using Zipark.Core;
using Zipark.Crypto;

namespace Zipark.Compression;

public interface IArchiveEngine
{
    string Name { get; }
    Task<ArchiveInfo> ListAsync(string archivePath, SecretPassword? password, CancellationToken ct = default);
    Task<CompressResult> CompressAsync(CompressOptions options, IProgress<JobProgress>? progress, CancellationToken ct = default);
    Task<ExtractResult> ExtractAsync(ExtractOptions options, IProgress<JobProgress>? progress, CancellationToken ct = default);
    Task<TestResult> TestAsync(string archivePath, SecretPassword? password, IProgress<JobProgress>? progress, CancellationToken ct = default);

    /// <summary>
    /// Streams the content of the given entries, concatenated in archive order, without touching the disk.
    /// Used by the virtual drive for lazy, per-block decoding.
    /// </summary>
    EntryDataStream OpenEntries(string archivePath, IReadOnlyCollection<string> entryPaths, SecretPassword? password);
}
