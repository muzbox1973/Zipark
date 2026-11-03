using Zipark.Compression;
using Zipark.Core;
using Zipark.Crypto;
using Zipark.SplitVolumes;

namespace Zipark.Application.Jobs;

/// <summary>Executes a <see cref="JobRequest"/>. Used inside Zipark.Worker and as in-process fallback.</summary>
public sealed class JobRunner
{
    private readonly IArchiveEngine _engine;
    private readonly Func<ConflictInfo, ConflictDecision>? _conflictPrompt;

    /// <param name="conflictPrompt">Answers name conflicts for <see cref="ConflictPolicy.Ask"/> (may block).</param>
    public JobRunner(IArchiveEngine engine, Func<ConflictInfo, ConflictDecision>? conflictPrompt = null)
    {
        _engine = engine;
        _conflictPrompt = conflictPrompt;
    }

    public async Task<JobResult> RunAsync(JobRequest r, SecretPassword? pw, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = r.Kind switch
            {
                JobKind.Compress => await CompressAsync(r, pw, progress, ct).ConfigureAwait(false),
                JobKind.Extract => await ExtractAsync(r, pw, progress, ct).ConfigureAwait(false),
                JobKind.Test => await TestAsync(r, pw, progress, ct).ConfigureAwait(false),
                JobKind.Checksum => await ChecksumAsync(r, progress, ct).ConfigureAwait(false),
                JobKind.SplitFile => await SplitAsync(r, progress, ct).ConfigureAwait(false),
                JobKind.MergeFile => await MergeAsync(r, progress, ct).ConfigureAwait(false),
                _ => throw new ZiparkException(ErrorCode.InvalidArgument, "Unknown job."),
            };
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return result;
        }
        catch (Exception ex)
        {
            var fx = ZiparkException.Wrap(ex);
            if (fx.Code != ErrorCode.Cancelled) Log.Error($"job {r.Kind} failed: {fx.Code}", ex);
            var fail = JobResult.Fail(fx);
            fail.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return fail;
        }
    }

    private async Task<JobResult> CompressAsync(JobRequest r, SecretPassword? pw, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var res = await _engine.CompressAsync(r.ToCompressOptions(pw), progress, ct).ConfigureAwait(false);
        var result = new JobResult { Success = true, Outputs = res.Volumes.ToList(), InputBytes = res.InputBytes, OutputBytes = res.OutputBytes };
        if (r.VerifyAfter)
        {
            var test = await _engine.TestAsync(res.ArchivePath, pw, progress, ct).ConfigureAwait(false);
            if (!test.Ok)
            {
                result.Success = false;
                result.Error = ErrorCode.CorruptArchive;
                result.Message = string.Join("\n", test.Errors);
            }
        }
        return result;
    }

    private async Task<JobResult> ExtractAsync(JobRequest r, SecretPassword? pw, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var set = VolumeSetDetector.Detect(r.Sources[0]);
        if (set.IsMultiVolume)
        {
            if (!set.IsComplete)
                throw new ZiparkException(ErrorCode.MissingVolume, "Missing volume: " + string.Join(", ", set.Missing.Select(Path.GetFileName)));
            r.Sources[0] = set.EnginePath;
        }
        var options = r.ToExtractOptions(pw, _conflictPrompt);
        var resultFolder = options.Destination;
        if (r.IntoArchiveFolder)
        {
            var listing = await _engine.ListAsync(options.ArchivePath, pw, ct).ConfigureAwait(false);
            var (dest, shown) = ExtractTarget.ForNewFolder(listing, r.Sources[0], options.Destination);
            options = new ExtractOptions
            {
                ArchivePath = options.ArchivePath,
                Destination = dest,
                Password = options.Password,
                Selection = options.Selection,
                StripPrefix = options.StripPrefix,
                FlattenPaths = options.FlattenPaths,
                AllowLimitOverride = options.AllowLimitOverride,
                Limits = options.Limits,
                Conflict = options.Conflict,
                ConflictPrompt = options.ConflictPrompt,
                Listing = listing,
            };
            resultFolder = shown;
        }
        var res = await _engine.ExtractAsync(options, progress, ct).ConfigureAwait(false);
        var result = new JobResult { Success = true, Outputs = [resultFolder], Files = res.FilesWritten };
        if (res.Blocked.Count > 0) result.Warnings.Add($"blocked:{res.Blocked.Count}");
        if (res.Renamed > 0) result.Warnings.Add($"renamed:{res.Renamed}");
        if (res.Skipped > 0) result.Warnings.Add($"skipped:{res.Skipped}");
        result.OutputBytes = res.WrittenFiles.Sum(f => new FileInfo(f).Length);
        return result;
    }

    private async Task<JobResult> TestAsync(JobRequest r, SecretPassword? pw, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var set = VolumeSetDetector.Detect(r.Sources[0]);
        if (set.IsMultiVolume && !set.IsComplete)
            return new JobResult { Success = false, Error = ErrorCode.MissingVolume, Message = "Missing volume: " + string.Join(", ", set.Missing.Select(Path.GetFileName)) };
        var t = await _engine.TestAsync(set.IsMultiVolume ? set.EnginePath : r.Sources[0], pw, progress, ct).ConfigureAwait(false);
        return new JobResult
        {
            Success = t.Ok,
            Error = t.Ok ? ErrorCode.Unknown : ErrorCode.CorruptArchive,
            Message = t.Ok ? null : string.Join("\n", t.Errors),
            Warnings = t.Warnings.ToList(),
            Files = t.FilesTested,
        };
    }

    private static async Task<JobResult> ChecksumAsync(JobRequest r, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var files = r.Sources.SelectMany(s => Directory.Exists(s) ? Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories) : [s]).ToList();
        long total = files.Sum(f => new FileInfo(f).Length);
        var tracker = new ProgressTracker(total);
        long before = 0;
        var result = new JobResult { Success = true, InputBytes = total, Files = files.Count };
        foreach (var f in files)
        {
            var baseline = before;
            var c = await Checksums.ComputeAsync(f, new Progress<long>(n => progress?.Report(tracker.FromBytes(baseline + n, Path.GetFileName(f)))), ct).ConfigureAwait(false);
            before += c.Length;
            result.Checksums.Add(c);
        }
        if (r.Output is not null)
        {
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(r.Output))!;
            Checksums.WriteSha256Manifest(r.Output, result.Checksums, baseDir);
            result.Outputs.Add(r.Output);
        }
        return result;
    }

    private static async Task<JobResult> SplitAsync(JobRequest r, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var dest = r.Destination ?? Path.GetDirectoryName(Path.GetFullPath(r.Sources[0]))!;
        var m = await FileSplitter.SplitAsync(r.Sources[0], dest, r.PartSize, progress, ct).ConfigureAwait(false);
        return new JobResult
        {
            Success = true,
            Outputs = m.Parts.Select(p => Path.Combine(dest, p.Name)).ToList(),
            InputBytes = m.OriginalSize,
            OutputBytes = m.OriginalSize,
            Files = m.Parts.Count,
        };
    }

    private static async Task<JobResult> MergeAsync(JobRequest r, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var m = await FileSplitter.MergeAsync(r.Sources[0], r.Output, progress, ct).ConfigureAwait(false);
        var res = new JobResult { Success = true, Outputs = [m.OutputPath], OutputBytes = m.Bytes, Files = 1 };
        if (!m.Verified) res.Warnings.Add("unverified");
        return res;
    }
}
