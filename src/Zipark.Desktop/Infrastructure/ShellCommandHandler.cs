using Zipark.Application.Jobs;
using Zipark.Core;
using Zipark.Crypto;
using Zipark.Desktop.ViewModels;
using Zipark.ShellIntegration;
using Zipark.SplitVolumes;

namespace Zipark.Desktop.Infrastructure;

/// <summary>
/// Executes Explorer context-menu verbs. Invocations that arrive within a short window are batched,
/// so selecting 20 files and choosing "Compress to ZIP" produces one archive instead of 20.
/// </summary>
public sealed class ShellCommandHandler
{
    private readonly MainViewModel _main;
    private readonly List<ShellInvocation> _pending = [];
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private int _inFlight;

    public ShellCommandHandler(MainViewModel main) => _main = main;

    /// <summary>Context-menu requests received but not yet turned into queued jobs (password prompts, listing).</summary>
    public int Pending => Volatile.Read(ref _inFlight);

    public void Submit(string[] args)
    {
        var inv = ShellInvocation.Parse(args);
        if (inv is null || inv.Paths.Count == 0) return;
        Interlocked.Increment(ref _inFlight);
        lock (_gate)
        {
            _pending.Add(inv);
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ => Flush(), null, 450, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        List<ShellInvocation> batch;
        lock (_gate)
        {
            batch = _pending.ToList();
            _pending.Clear();
        }
        foreach (var group in batch.GroupBy(b => b.Verb))
        {
            var paths = group.SelectMany(g => g.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var count = group.Count();
            Ui.OnUi(async () =>
            {
                try
                {
                    await RunAsync(group.Key, paths);
                }
                finally
                {
                    Interlocked.Add(ref _inFlight, -count);
                }
            });
        }
    }

    private async Task RunAsync(string verb, List<string> paths)
    {
        var s = AppServices.Current;
        try
        {
            switch (verb)
            {
                case ShellVerbs.Open:
                    _main.Navigate(ArchiveFormats.Detect(paths[0]) == ArchiveFormat.Iso ? "iso" : "explorer", paths[0]);
                    break;
                case ShellVerbs.CompressZip:
                case ShellVerbs.Compress7z:
                    QuickCompress(paths, verb == ShellVerbs.CompressZip ? ArchiveFormat.Zip : ArchiveFormat.SevenZip);
                    break;
                case ShellVerbs.CompressAdvanced:
                    _main.Navigate("compress", paths.ToArray());
                    break;
                case ShellVerbs.ExtractHere:
                case ShellVerbs.ExtractToFolder:
                    foreach (var p in UniqueArchives(paths))
                        await QuickExtractAsync(p, verb == ShellVerbs.ExtractToFolder);
                    break;
                case ShellVerbs.Test:
                    foreach (var p in UniqueArchives(paths))
                        await QuickTestAsync(p);
                    break;
                case ShellVerbs.Mount:
                    await _main.Drives.MountAsync(paths[0]);
                    break;
                case ShellVerbs.Unmount:
                    var drive = s.Drives.Find(paths[0].TrimEnd('\\'));
                    if (drive is null)
                    {
                        s.Notify.Warn(Loc.T("s.notice.notOurDrive"), paths[0]);
                    }
                    else
                    {
                        await s.Drives.UnmountAsync(drive);
                        s.Notify.Success(Loc.T("s.notice.unmounted"), drive.SourceName);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            s.Notify.Error(ex);
        }
    }

    private static IEnumerable<string> UniqueArchives(IEnumerable<string> paths) =>
        paths.Select(p => VolumeSetDetector.Detect(p)).GroupBy(v => v.FirstVolume, StringComparer.OrdinalIgnoreCase).Select(g => g.First().IsMultiVolume ? g.First().EnginePath : g.Key);

    private void QuickCompress(List<string> paths, ArchiveFormat format)
    {
        var s = AppServices.Current;
        var first = paths[0].TrimEnd('\\');
        var dir = Path.GetDirectoryName(first) ?? first;
        var name = paths.Count == 1 ? Path.GetFileName(first) : new DirectoryInfo(dir).Name;
        if (paths.Count == 1 && File.Exists(first)) name = Path.GetFileNameWithoutExtension(first);
        var output = ConflictResolver.GetUniquePath(Path.Combine(dir, PathSafety.SanitizeFileName(name) + ArchiveFormats.Extension(format)));
        var job = s.Jobs.Enqueue(new JobRequest
        {
            Kind = JobKind.Compress,
            Sources = paths,
            Output = output,
            Format = format,
            Level = (Compression.CompressionLevel)s.Settings.DefaultLevel,
            Threads = s.Settings.Threads,
            VerifyAfter = s.Settings.VerifyAfterCompress,
        });
        JobsViewModel.ReportWhenDone(job, output);
    }

    /// <summary>Lists the archive and asks for a password only if it is needed. False when the user cancelled.</summary>
    private static async Task<(bool Ok, SecretPassword? Password)> PasswordFor(string archive)
    {
        var s = AppServices.Current;
        var name = Path.GetFileName(archive);
        var (info, pw) = await s.Engine.OpenAsync(archive, retry => Task.FromResult(Dialogs.AskPassword(name, retry)));
        if (pw is null && info.HasEncryptedEntries)
        {
            pw = Dialogs.AskPassword(name, false);
            if (pw is null) return (false, null);
        }
        return (true, pw);
    }

    private static async Task QuickTestAsync(string archive)
    {
        var s = AppServices.Current;
        var (ok, pw) = await PasswordFor(archive);
        if (!ok) return;
        using (pw)
        {
            var job = s.Jobs.Enqueue(new JobRequest { Kind = JobKind.Test, Sources = [archive] }, pw);
            JobsViewModel.ReportWhenDone(job, null);
        }
    }

    private static async Task QuickExtractAsync(string archive, bool toFolder)
    {
        var s = AppServices.Current;
        SecretPassword? pw = null;
        try
        {
            var (ok, listPw) = await PasswordFor(archive);
            pw = listPw;
            if (!ok) return;
            var limits = s.Settings.ToBombLimits();
            var job = s.Jobs.Enqueue(new JobRequest
            {
                Kind = JobKind.Extract,
                Sources = [archive],
                Destination = Path.GetDirectoryName(Path.GetFullPath(archive))!,
                IntoArchiveFolder = toFolder,
                Conflict = s.Settings.Conflict,
                BombMaxTotalBytes = limits.MaxTotalBytes,
                BombSuspiciousRatio = limits.SuspiciousRatio,
            }, pw);
            JobsViewModel.ReportWhenDone(job, null, s.Settings.OpenFolderAfterExtract); // job reports the real folder
        }
        finally
        {
            pw?.Dispose();
        }
    }
}
