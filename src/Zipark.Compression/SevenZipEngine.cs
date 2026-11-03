using System.Text;
using Zipark.Core;
using Zipark.Crypto;

namespace Zipark.Compression;

/// <summary>
/// Archive engine backed by the 7-Zip console program running as a separate process
/// (LGPL-2.1-or-later; see THIRD_PARTY_NOTICES). The process boundary keeps native engine crashes
/// away from the UI and the virtual drive host.
/// </summary>
public sealed class SevenZipEngine : IArchiveEngine
{
    private readonly string _exe;

    public SevenZipEngine(string? sevenZipExe = null)
    {
        _exe = sevenZipExe ?? SevenZipLocator.Find();
    }

    public string Name => "7-Zip";
    public string ExecutablePath => _exe;

    private static readonly string[] CommonSwitches = ["-sccUTF-8", "-scsUTF-8", "-spd", "-y"];

    // ----------------------------------------------------------------- list

    public async Task<ArchiveInfo> ListAsync(string archivePath, SecretPassword? password, CancellationToken ct = default)
    {
        EnsureFile(archivePath);
        var args = new List<string> { "l", "-slt", "-bsp0", "-bse2" };
        args.AddRange(CommonSwitches);
        args.Add("--");
        args.Add(archivePath);
        var r = await SevenZipProcess.RunAsync(_exe, args, password, Path.GetDirectoryName(Path.GetFullPath(archivePath)), null, ct).ConfigureAwait(false);
        var error = SevenZipProcess.Classify(r, password is not null);
        if (error is not null) throw error;
        var info = SevenZipListParser.Parse(Path.GetFullPath(archivePath), r.StdOut);
        if (info.Format == ArchiveFormat.Unknown) info = info with { Format = ArchiveFormats.FromPath(archivePath) };
        return info;
    }

    /// <summary>
    /// Lists an archive, asking for a password only when the engine says one is needed.
    /// Sets <see cref="ArchiveInfo.HeadersEncrypted"/> when the file list itself was encrypted.
    /// </summary>
    public async Task<(ArchiveInfo Info, SecretPassword? Password)> OpenAsync(string archivePath, Func<bool, Task<SecretPassword?>> askPassword, CancellationToken ct = default)
    {
        try
        {
            var info = await ListAsync(archivePath, null, ct).ConfigureAwait(false);
            return (info, null);
        }
        catch (ZiparkException ex) when (ex.Code is ErrorCode.PasswordRequired or ErrorCode.WrongPassword)
        {
            bool retry = false;
            while (true)
            {
                var pw = await askPassword(retry).ConfigureAwait(false);
                if (pw is null) throw new ZiparkException(ErrorCode.PasswordRequired, "Password required.");
                try
                {
                    var info = await ListAsync(archivePath, pw, ct).ConfigureAwait(false);
                    return (info with { HeadersEncrypted = true }, pw);
                }
                catch (ZiparkException wrong) when (wrong.Code == ErrorCode.WrongPassword)
                {
                    pw.Dispose();
                    retry = true;
                }
            }
        }
    }

    // ------------------------------------------------------------- compress

    public async Task<CompressResult> CompressAsync(CompressOptions o, IProgress<JobProgress>? progress, CancellationToken ct = default)
    {
        if (!ArchiveFormats.CanCreate(o.Format))
            throw new ZiparkException(ErrorCode.UnsupportedFormat, $"Creating {o.Format} archives is not supported.");
        if (o.Sources.Count == 0) throw new ZiparkException(ErrorCode.InvalidArgument, "Nothing to compress.");
        foreach (var s in o.Sources)
            if (!File.Exists(s) && !Directory.Exists(s))
                throw new ZiparkException(ErrorCode.FileNotFound, $"Source not found: {s}");
        if (ArchiveFormats.IsSingleStream(o.Format) && (o.Sources.Count != 1 || Directory.Exists(o.Sources[0])))
            throw new ZiparkException(ErrorCode.InvalidArgument, $"{o.Format} compresses exactly one file. Create a TAR first for folders.");
        bool encrypt = o.Password is { IsEmpty: false };
        if (encrypt && !ArchiveFormats.SupportsEncryption(o.Format))
            throw new ZiparkException(ErrorCode.InvalidArgument, $"{o.Format} does not support encryption.");
        if (o.VolumeSize is not null && !ArchiveFormats.SupportsVolumes(o.Format))
            throw new ZiparkException(ErrorCode.InvalidArgument, $"{o.Format} does not support split volumes.");
        if (o.VolumeSize is < 64 * 1024)
            throw new ZiparkException(ErrorCode.InvalidArgument, "Volume size must be at least 64 KB.");

        var output = Path.GetFullPath(o.OutputPath);
        var outDir = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(outDir);
        bool volumes = o.VolumeSize is not null;
        output = ResolveOutputConflict(output, volumes, o.Conflict);

        long inputBytes = o.Sources.Sum(MeasureSource);
        var tracker = new ProgressTracker(inputBytes);
        progress?.Report(tracker.FromPercent(0, null, JobStage.Scanning));

        var tmp = output + "." + Guid.NewGuid().ToString("N")[..8] + ".fxtmp";
        var args = new List<string> { "a", "-t" + ArchiveFormats.SevenZipType(o.Format), "-bsp1", "-bso0", "-bse2", "-ssw" };
        args.AddRange(CommonSwitches);
        if (o.Format != ArchiveFormat.Tar) args.Add("-mx=" + (int)o.Level);
        if (o.Format is ArchiveFormat.SevenZip or ArchiveFormat.Zip or ArchiveFormat.Xz or ArchiveFormat.BZip2)
            args.Add(o.Threads > 0 ? "-mmt=" + o.Threads : "-mmt=on");
        AddMethodSwitches(o, args);
        if (encrypt)
        {
            args.Add("-p");
            if (o.Format == ArchiveFormat.SevenZip && o.EncryptFileNames) args.Add("-mhe=on");
            if (o.Format == ArchiveFormat.Zip) args.Add(o.ZipEncryption == ZipEncryption.Aes256 ? "-mem=AES256" : "-mem=ZipCrypto");
        }
        if (volumes) args.Add($"-v{o.VolumeSize}b");
        args.Add("--");
        args.Add(tmp);
        args.AddRange(o.Sources.Select(Path.GetFullPath));

        try
        {
            var r = await SevenZipProcess.RunAsync(_exe, args, o.Password, outDir,
                (pct, item) => progress?.Report(tracker.FromPercent(pct, item)), ct).ConfigureAwait(false);
            var error = SevenZipProcess.Classify(r, encrypt);
            if (error is not null) throw error;

            progress?.Report(tracker.FromPercent(100, null, JobStage.Finalizing));
            var produced = new List<string>();
            if (volumes)
            {
                for (int i = 1; ; i++)
                {
                    var part = $"{tmp}.{i:D3}";
                    if (!File.Exists(part)) break;
                    var final = $"{output}.{i:D3}";
                    File.Move(part, final, o.Conflict == ConflictPolicy.Overwrite);
                    produced.Add(final);
                }
                if (produced.Count == 0) throw new ZiparkException(ErrorCode.EngineCrashed, "The engine produced no volumes.");
            }
            else
            {
                File.Move(tmp, output, o.Conflict == ConflictPolicy.Overwrite);
                produced.Add(output);
            }
            long outBytes = produced.Sum(p => new FileInfo(p).Length);
            Log.Info($"compress ok: {produced.Count} file(s), {inputBytes} -> {outBytes} bytes, {o.Format}");
            return new CompressResult(volumes ? produced[0] : output, produced, inputBytes, outBytes, tracker.Elapsed);
        }
        finally
        {
            // Remove partial output on failure or cancellation. Only our own temp names are touched.
            TryDelete(tmp);
            for (int i = 1; i < 100_000; i++)
            {
                var part = $"{tmp}.{i:D3}";
                if (!File.Exists(part)) break;
                TryDelete(part);
            }
        }
    }

    private static void AddMethodSwitches(CompressOptions o, List<string> args)
    {
        if (o.Format == ArchiveFormat.SevenZip)
        {
            var m = o.Method switch
            {
                CompressionMethod.Lzma2 => "LZMA2",
                CompressionMethod.Lzma => "LZMA",
                CompressionMethod.Ppmd => "PPMd",
                CompressionMethod.BZip2 => "BZip2",
                CompressionMethod.Deflate => "Deflate",
                CompressionMethod.Deflate64 => "Deflate64",
                _ => null,
            };
            if (m is not null) args.Add("-m0=" + m);
            if (o.DictionaryMb is > 0) args.Add($"-md={o.DictionaryMb}m");
            if (o.Solid is not null) args.Add(o.Solid.Value ? "-ms=on" : "-ms=off");
        }
        else if (o.Format == ArchiveFormat.Zip)
        {
            var m = o.Method switch
            {
                CompressionMethod.Deflate => "Deflate",
                CompressionMethod.Deflate64 => "Deflate64",
                CompressionMethod.BZip2 => "BZip2",
                CompressionMethod.Lzma or CompressionMethod.Lzma2 => "LZMA",
                CompressionMethod.Ppmd => "PPMd",
                _ => null,
            };
            if (m is not null) args.Add("-mm=" + m);
        }
    }

    private static string ResolveOutputConflict(string output, bool volumes, ConflictPolicy policy)
    {
        var probe = volumes ? output + ".001" : output;
        if (!File.Exists(probe)) return output;
        switch (policy)
        {
            case ConflictPolicy.Overwrite:
                return output;
            case ConflictPolicy.Skip:
                throw new ZiparkException(ErrorCode.InvalidArgument, $"Output already exists: {probe}");
            default:
                if (!volumes) return ConflictResolver.GetUniquePath(output);
                var dir = Path.GetDirectoryName(output)!;
                var stem = Path.GetFileNameWithoutExtension(output);
                var ext = Path.GetExtension(output);
                for (int i = 2; ; i++)
                {
                    var c = Path.Combine(dir, $"{stem} ({i}){ext}");
                    if (!File.Exists(c + ".001")) return c;
                }
        }
    }

    private static long MeasureSource(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        long total = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts)) total += f.Length;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    // -------------------------------------------------------------- extract

    public async Task<ExtractResult> ExtractAsync(ExtractOptions o, IProgress<JobProgress>? progress, CancellationToken ct = default)
    {
        EnsureFile(o.ArchivePath);
        var dest = Path.GetFullPath(o.Destination);
        Directory.CreateDirectory(dest);
        bool pwSupplied = o.Password is { IsEmpty: false };

        progress?.Report(new JobProgress { Stage = JobStage.Scanning });
        var info = o.Listing ?? await ListAsync(o.ArchivePath, o.Password, ct).ConfigureAwait(false);
        var selected = SelectEntries(info, o.Selection);

        // 1) Pre-flight: bomb / traversal / symlink / disk space checks on the listing.
        var report = ArchiveBombGuard.Evaluate(info, o.Limits, dest, selected);
        var blocked = report.UnsafeEntries.Select(e => e.Path).ToList();
        if (report.Level == GuardLevel.Blocked)
        {
            var hardBlock = report.Findings.Any(f => f.StartsWith("ratio:") || f.StartsWith("total-size:") || f.StartsWith("entries:") || f.StartsWith("disk-space:") || f.StartsWith("depth:"));
            if (hardBlock && !o.AllowLimitOverride)
                throw new ZiparkException(ErrorCode.ArchiveBomb, "Extraction blocked by safety limits: " + string.Join(", ", report.Findings.Take(5)));
            if (report.Findings.Any(f => f.StartsWith("disk-space:")))
                throw new ZiparkException(ErrorCode.DiskFull, "Not enough disk space for extraction.");
        }
        var unsafeSet = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);
        var safe = selected.Where(e => !unsafeSet.Contains(e.Path)).ToList();
        bool needList = o.Selection is { Count: > 0 } || unsafeSet.Count > 0;
        long total = safe.Where(e => !e.IsDirectory).Sum(e => e.Size);
        var tracker = new ProgressTracker(total);

        // 2) Extract into a private staging folder on the same volume.
        var staging = Path.Combine(dest, ".fxstage-" + Guid.NewGuid().ToString("N")[..10]);
        var stagingDir = Directory.CreateDirectory(staging);
        stagingDir.Attributes |= FileAttributes.Hidden;
        string? listFile = null;
        try
        {
            var args = new List<string> { "x", "-bsp1", "-bso0", "-bse2", "-aoa", "-o" + staging };
            args.AddRange(CommonSwitches);
            if (needList)
            {
                if (safe.Count == 0) return new ExtractResult(dest, 0, 0, 0, blocked, []);
                listFile = WriteListFile(safe.Select(e => e.Path));
                args.Add("-i@" + listFile);
            }
            args.Add("--");
            args.Add(o.ArchivePath);
            var r = await SevenZipProcess.RunAsync(_exe, args, o.Password, dest,
                (pct, item) => progress?.Report(tracker.FromPercent(pct, item)), ct).ConfigureAwait(false);
            var error = SevenZipProcess.Classify(r, pwSupplied);
            if (error is not null) throw error;

            // 3) Post-validate what actually landed and move it into place.
            progress?.Report(tracker.FromPercent(100, null, JobStage.Finalizing));
            return MoveIntoPlace(staging, dest, o, blocked, ct);
        }
        finally
        {
            if (listFile is not null) TryDelete(listFile);
            TryDeleteTree(staging);
        }
    }

    internal static List<ArchiveEntry> SelectEntries(ArchiveInfo info, IReadOnlyCollection<string>? selection)
    {
        if (selection is null || selection.Count == 0) return info.Entries.ToList();
        var keys = selection.Select(s => s.Replace('/', '\\').Trim('\\')).Where(s => s.Length > 0).ToList();
        return info.Entries.Where(e =>
        {
            var p = e.Path.Trim('\\');
            return keys.Any(k => p.Equals(k, StringComparison.OrdinalIgnoreCase) || p.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    private static ExtractResult MoveIntoPlace(string staging, string dest, ExtractOptions o, List<string> blocked, CancellationToken ct)
    {
        int written = 0, skipped = 0, renamed = 0;
        var files = new List<string>();
        var strip = o.StripPrefix?.Replace('/', '\\').Trim('\\');

        foreach (var (full, isDir) in WalkNoReparse(staging, blocked))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(staging, full);
            if (!string.IsNullOrEmpty(strip))
            {
                if (rel.Equals(strip, StringComparison.OrdinalIgnoreCase)) continue;
                if (!rel.StartsWith(strip + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                rel = rel[(strip.Length + 1)..];
            }
            if (o.FlattenPaths)
            {
                if (isDir) continue;
                rel = Path.GetFileName(rel);
            }

            string target;
            try
            {
                target = PathSafety.ResolveUnder(dest, rel);
            }
            catch (ZiparkException)
            {
                blocked.Add(rel);
                continue;
            }
            var parent = Path.GetDirectoryName(target)!;
            if (PathSafety.HasReparsePointInChain(dest, parent))
            {
                // An existing junction/symlink in the destination would redirect the write elsewhere.
                blocked.Add(rel);
                continue;
            }

            if (isDir)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(parent);
            if (File.Exists(target) || Directory.Exists(target))
            {
                switch (o.Conflict)
                {
                    case ConflictPolicy.Skip:
                        skipped++;
                        continue;
                    case ConflictPolicy.Overwrite when File.Exists(target):
                        if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                        break;
                    default:
                        target = ConflictResolver.GetUniquePath(target);
                        renamed++;
                        break;
                }
            }
            File.Move(full, target, overwrite: o.Conflict == ConflictPolicy.Overwrite);
            files.Add(target);
            written++;
        }
        return new ExtractResult(dest, written, skipped, renamed, blocked, files);
    }

    /// <summary>Depth-first walk that never descends into reparse points; links found are removed (the link only).</summary>
    private static IEnumerable<(string Path, bool IsDir)> WalkNoReparse(string root, List<string> blocked)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in new DirectoryInfo(dir).GetFileSystemInfos())
            {
                if (PathSafety.IsReparsePoint(entry))
                {
                    blocked.Add(Path.GetRelativePath(root, entry.FullName));
                    try
                    {
                        if (entry is DirectoryInfo d) d.Delete(false); else entry.Delete();
                    }
                    catch
                    {
                        // left inside staging; staging cleanup retries
                    }
                    continue;
                }
                if (entry is DirectoryInfo sub)
                {
                    yield return (sub.FullName, true);
                    stack.Push(sub.FullName);
                }
                else
                {
                    yield return (entry.FullName, false);
                }
            }
        }
    }

    // ----------------------------------------------------------------- test

    public async Task<TestResult> TestAsync(string archivePath, SecretPassword? password, IProgress<JobProgress>? progress, CancellationToken ct = default)
    {
        EnsureFile(archivePath);
        var tracker = new ProgressTracker(new FileInfo(archivePath).Length);
        var args = new List<string> { "t", "-bsp1", "-bso1", "-bse2" };
        args.AddRange(CommonSwitches);
        args.Add("--");
        args.Add(archivePath);
        var r = await SevenZipProcess.RunAsync(_exe, args, password, Path.GetDirectoryName(Path.GetFullPath(archivePath)),
            (pct, item) => progress?.Report(tracker.FromPercent(pct, item, JobStage.Verifying)), ct).ConfigureAwait(false);

        var errors = (r.StdErr + "\n" + r.StdOut).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("CRC Failed") || l.Contains("Data Error") || l.Contains("Wrong password") || l.Contains("Unexpected end") || l.Contains("Missing volume"))
            .Distinct().ToList();
        var warnings = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)).ToList();
        int files = 0;
        foreach (var l in r.StdOut.Split('\n'))
        {
            var t = l.Trim();
            if (t.StartsWith("Files:") && int.TryParse(t[6..].Trim(), out var f)) files = f;
            else if (t.StartsWith("Size:") && files == 0 && !r.StdOut.Contains("Files:")) files = 1; // single-stream formats
        }
        var error = SevenZipProcess.Classify(r, password is not null);
        if (error is { Code: ErrorCode.PasswordRequired or ErrorCode.WrongPassword or ErrorCode.Cancelled }) throw error;
        if (error is not null && errors.Count == 0) errors.Add(error.Message);
        return new TestResult(error is null && errors.Count == 0, errors, warnings, files);
    }

    // --------------------------------------------------------------- stream

    public EntryDataStream OpenEntries(string archivePath, IReadOnlyCollection<string> entryPaths, SecretPassword? password)
    {
        EnsureFile(archivePath);
        if (entryPaths.Count == 0) throw new ArgumentException("No entries requested.", nameof(entryPaths));
        var listFile = WriteListFile(entryPaths);
        var args = new List<string> { "x", "-so", "-bsp0", "-bse2", "-i@" + listFile };
        args.AddRange(CommonSwitches);
        args.Add("--");
        args.Add(archivePath);
        var psi = SevenZipProcess.CreateStartInfo(_exe, args, Path.GetDirectoryName(Path.GetFullPath(archivePath)));
        var p = SevenZipProcess.Start(psi, password);
        return new EntryDataStream(p, listFile, password is not null);
    }

    // -------------------------------------------------------------- helpers

    private static string WriteListFile(IEnumerable<string> paths)
    {
        var file = Path.Combine(AppPaths.TempRoot, "list-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, string.Join("\n", paths) + "\n", new UTF8Encoding(false));
        return file;
    }

    private static void EnsureFile(string path)
    {
        if (!File.Exists(path)) throw new ZiparkException(ErrorCode.FileNotFound, $"Archive not found: {path}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            // Delete only inside our own staging folder; links are removed without following them.
            foreach (var (p, isDir) in WalkNoReparse(dir, []).Reverse().ToList())
            {
                if (isDir) Directory.Delete(p, false);
                else
                {
                    File.SetAttributes(p, FileAttributes.Normal);
                    File.Delete(p);
                }
            }
            Directory.Delete(dir, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"staging cleanup failed: {dir}: {ex.Message}");
        }
    }
}
