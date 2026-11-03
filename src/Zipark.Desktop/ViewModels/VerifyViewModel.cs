using System.Collections.ObjectModel;
using System.Windows.Input;
using Zipark.Application.Jobs;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;

namespace Zipark.Desktop.ViewModels;

public sealed class VerifyRow : ObservableObject
{
    private string _status = "";
    private bool? _ok;
    private string _crc = "";
    private string _sha = "";
    private long _size;

    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool? Ok { get => _ok; set => Set(ref _ok, value); }
    public string Crc32 { get => _crc; set => Set(ref _crc, value); }
    public string Sha256 { get => _sha; set => Set(ref _sha, value); }
    public long Size { get => _size; set => Set(ref _size, value); }
}

/// <summary>Archive integrity tests and CRC32 / SHA-256 checksums (create and verify manifests).</summary>
public sealed class VerifyViewModel : ObservableObject, INavigable
{
    private int _tab;

    public VerifyViewModel()
    {
        AddArchivesCommand = new RelayCommand(() => AddArchives(Dialogs.OpenArchives()));
        TestCommand = new AsyncCommand(TestAsync, () => Archives.Count > 0 && AppServices.Current.EnginePath is not null);
        ClearArchivesCommand = new RelayCommand(() => Archives.Clear());
        AddFilesCommand = new RelayCommand(() => AddFiles(Dialogs.OpenFiles(Loc.T("s.dlg.addFiles"))));
        ComputeCommand = new AsyncCommand(ComputeAsync, () => Files.Count > 0);
        SaveManifestCommand = new RelayCommand(SaveManifest, () => Files.Any(f => f.Sha256.Length == 64));
        VerifyManifestCommand = new AsyncCommand(VerifyManifestAsync);
        ClearFilesCommand = new RelayCommand(() => Files.Clear());
        CopyCommand = new RelayCommand(p => { if (p is string s && s.Length > 0) System.Windows.Clipboard.SetText(s); });
    }

    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public ObservableCollection<VerifyRow> Archives { get; } = [];
    public ObservableCollection<VerifyRow> Files { get; } = [];
    public ICommand AddArchivesCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand ClearArchivesCommand { get; }
    public ICommand AddFilesCommand { get; }
    public ICommand ComputeCommand { get; }
    public ICommand SaveManifestCommand { get; }
    public ICommand VerifyManifestCommand { get; }
    public ICommand ClearFilesCommand { get; }
    public ICommand CopyCommand { get; }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is not IEnumerable<string> paths) return;
        var list = paths.ToList();
        var archives = list.Where(p => File.Exists(p) && ArchiveFormats.Detect(p) != ArchiveFormat.Unknown).ToList();
        if (archives.Count == list.Count)
        {
            Tab = 0;
            AddArchives(archives);
        }
        else
        {
            Tab = 1;
            AddFiles(list);
        }
    }

    public void AddArchives(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!Archives.Any(a => a.Path == p)) Archives.Add(new VerifyRow { Path = p, Size = new FileInfo(p).Length });
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories) : [p]))
            if (File.Exists(p) && !Files.Any(a => a.Path == p)) Files.Add(new VerifyRow { Path = p, Size = new FileInfo(p).Length });
    }

    private async Task TestAsync()
    {
        var s = AppServices.Current;
        var tasks = new List<Task>();
        foreach (var row in Archives)
        {
            row.Status = Loc.T("s.stage.Queued");
            row.Ok = null;
            var r = row;
            var job = s.Jobs.Enqueue(new JobRequest { Kind = JobKind.Test, Sources = [row.Path] });
            tasks.Add(job.Completion.ContinueWith(async t =>
            {
                var res = t.Result;
                if (res.Error is ErrorCode.PasswordRequired or ErrorCode.WrongPassword)
                {
                    // Ask once, then test again with the password.
                    using var pw = Dialogs.AskPassword(r.Name, res.Error == ErrorCode.WrongPassword);
                    if (pw is not null) res = await s.Jobs.Enqueue(new JobRequest { Kind = JobKind.Test, Sources = [r.Path] }, pw).Completion;
                }
                Ui.OnUi(() =>
                {
                    r.Ok = res.Success;
                    r.Status = res.Success ? Loc.F("s.verify.ok", res.Files) : Loc.Error(res.Error) + (res.Message is null ? "" : " — " + res.Message.Split('\n')[0]);
                });
            }, TaskScheduler.Default).Unwrap());
        }
        await Task.WhenAll(tasks);
    }

    private async Task ComputeAsync()
    {
        var s = AppServices.Current;
        var job = s.Jobs.Enqueue(new JobRequest { Kind = JobKind.Checksum, Sources = Files.Select(f => f.Path).ToList() });
        foreach (var f in Files) f.Status = Loc.T("s.stage.Running");
        var r = await job.Completion;
        if (!r.Success)
        {
            s.Notify.Show(NoticeKind.Error, Loc.Error(r.Error), r.Message);
            return;
        }
        foreach (var c in r.Checksums)
        {
            var row = Files.FirstOrDefault(f => string.Equals(f.Path, c.Path, StringComparison.OrdinalIgnoreCase));
            if (row is null) continue;
            row.Crc32 = c.Crc32;
            row.Sha256 = c.Sha256;
            row.Status = "";
            row.Ok = null;
        }
    }

    private void SaveManifest()
    {
        var first = Files.First();
        var target = Dialogs.SaveFile(Loc.T("s.verify.saveManifest"), Path.GetFileName(first.Path) + ".sha256", "SHA-256 (*.sha256)|*.sha256");
        if (target is null) return;
        var baseDir = Path.GetDirectoryName(target)!;
        Checksums.WriteSha256Manifest(target, Files.Where(f => f.Sha256.Length == 64).Select(f => new ChecksumResult(f.Path, f.Size, f.Crc32, f.Sha256)), baseDir);
        AppServices.Current.Notify.Success(Loc.T("s.verify.manifestSaved"), target, Loc.T("s.action.openFolder"), () => AppServices.OpenInExplorer(target));
    }

    private async Task VerifyManifestAsync()
    {
        var manifest = Dialogs.OpenFiles(Loc.T("s.verify.pickManifest"), "SHA-256|*.sha256;*.sha256sum;*.txt|*.*|*.*", false).FirstOrDefault();
        if (manifest is null) return;
        var map = Checksums.ReadSha256Manifest(manifest);
        var baseDir = Path.GetDirectoryName(manifest)!;
        Files.Clear();
        foreach (var (rel, hash) in map)
        {
            var full = Path.GetFullPath(Path.Combine(baseDir, rel));
            var row = new VerifyRow { Path = full };
            Files.Add(row);
            if (!PathSafety.IsWithin(baseDir, full) || !File.Exists(full))
            {
                row.Ok = false;
                row.Status = Loc.T("s.verify.missing");
                continue;
            }
            row.Size = new FileInfo(full).Length;
            row.Status = Loc.T("s.stage.Running");
            var c = await Checksums.ComputeAsync(full);
            row.Sha256 = c.Sha256;
            row.Crc32 = c.Crc32;
            row.Ok = string.Equals(c.Sha256, hash, StringComparison.OrdinalIgnoreCase);
            row.Status = row.Ok == true ? Loc.T("s.verify.match") : Loc.T("s.verify.mismatch");
        }
        var bad = Files.Count(f => f.Ok != true);
        if (bad == 0) AppServices.Current.Notify.Success(Loc.F("s.verify.allMatch", Files.Count));
        else AppServices.Current.Notify.Warn(Loc.F("s.verify.someBad", bad, Files.Count));
    }
}
