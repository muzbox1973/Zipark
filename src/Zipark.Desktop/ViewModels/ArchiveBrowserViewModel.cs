using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Zipark.Application.Jobs;
using Zipark.ArchiveExplorer;
using Zipark.Core;
using Zipark.Crypto;
using Zipark.Desktop.Infrastructure;
using Zipark.SplitVolumes;

namespace Zipark.Desktop.ViewModels;

/// <summary>Shared archive browsing (archive explorer and ISO explorer).</summary>
public abstract class ArchiveBrowserViewModel : ObservableObject, INavigable, IDisposable
{
    private ArchiveTree? _tree;
    private ArchiveNode? _folder;
    private string _search = "";
    private bool _busy;
    private SecretPassword? _password;
    private IList<ArchiveNode> _selection = [];
    private static readonly string PreviewRoot = Path.Combine(AppPaths.TempRoot, "preview-" + Environment.ProcessId);

    protected ArchiveBrowserViewModel()
    {
        OpenCommand = new AsyncCommand(async () =>
        {
            var p = Dialogs.OpenArchive();
            if (p is not null) await LoadAsync(p);
        });
        UpCommand = new RelayCommand(() => { if (_folder?.Parent is { } parent) Folder = parent; }, () => _folder?.Parent is not null);
        OpenItemCommand = new AsyncCommand(p => OpenItemAsync(p as ArchiveNode));
        ExtractSelectedCommand = new RelayCommand(() => Extract(selectedOnly: true), () => HasArchive && _selection.Count > 0);
        ExtractAllCommand = new RelayCommand(() => Extract(selectedOnly: false), () => HasArchive);
        TestCommand = new RelayCommand(() => Enqueue(new JobRequest { Kind = JobKind.Test, Sources = [ArchivePath!] }, Loc.T("s.notice.testQueued")), () => HasArchive);
        CloseCommand = new RelayCommand(Close, () => HasArchive);
        ClearSearchCommand = new RelayCommand(() => Search = "");
    }

    public ICommand OpenCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand OpenItemCommand { get; }
    public ICommand ExtractSelectedCommand { get; }
    public ICommand ExtractAllCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public string? ArchivePath { get; private set; }
    public string ArchiveName => ArchivePath is null ? "" : Path.GetFileName(ArchivePath);
    public ArchiveInfo? Info => _tree?.Info;
    public bool HasArchive => _tree is not null;
    public bool Busy { get => _busy; protected set => Set(ref _busy, value); }
    protected SecretPassword? Password => _password;

    public ObservableCollection<ArchiveNode> Items { get; } = [];

    public ArchiveNode? Folder
    {
        get => _folder;
        set
        {
            if (!Set(ref _folder, value)) return;
            _search = "";
            Raise(nameof(Search));
            Fill();
        }
    }

    public string Location => _folder is null ? "" : "\\" + _folder.FullPath;

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            Fill();
        }
    }

    public void SetSelection(IList<ArchiveNode> nodes)
    {
        _selection = nodes;
        Raise(nameof(SelectionText));
    }

    public string SelectionText => _selection.Count == 0 ? "" : Loc.F("s.explorer.selected", _selection.Count, ByteSize.Format(_selection.Sum(n => n.Size)));

    public string Summary
    {
        get
        {
            if (Info is not { } i) return "";
            var ratio = i.TotalUncompressedSize > 0 ? 100.0 * i.PhysicalSize / i.TotalUncompressedSize : 100;
            var parts = new List<string>
            {
                ArchiveFormats.Extension(i.Format).TrimStart('.').ToUpperInvariant(),
                Loc.F("s.explorer.files", i.FileCount),
                ByteSize.Format(i.TotalUncompressedSize) + " → " + ByteSize.Format(i.PhysicalSize) + $" ({ratio:F0}%)",
            };
            if (!string.IsNullOrEmpty(i.Method)) parts.Add(i.Method!);
            if (i.IsSolid) parts.Add(Loc.F("s.explorer.solid", i.BlockCount));
            if (i.HasEncryptedEntries) parts.Add(Loc.T(i.HeadersEncrypted ? "s.explorer.encNames" : "s.explorer.enc"));
            if (i.VolumeCount > 1) parts.Add(Loc.F("s.explorer.volumes", i.VolumeCount));
            return string.Join("  ·  ", parts);
        }
    }

    public virtual void OnNavigatedTo(object? parameter)
    {
        if (parameter is string path) _ = LoadSafeAsync(path);
    }

    private async Task LoadSafeAsync(string path)
    {
        try
        {
            await LoadAsync(path);
        }
        catch (Exception ex)
        {
            AppServices.Current.Notify.Error(ex);
        }
    }

    public async Task LoadAsync(string path)
    {
        var s = AppServices.Current;
        var set = VolumeSetDetector.Detect(path);
        if (set.IsMultiVolume && !set.IsComplete)
            s.Notify.Warn(Loc.T("s.err.MissingVolume"), string.Join(", ", set.Missing.Select(Path.GetFileName)));
        var enginePath = set.IsMultiVolume ? set.EnginePath : Path.GetFullPath(path);

        Busy = true;
        try
        {
            var name = Path.GetFileName(path);
            var (info, pw) = await s.Engine.OpenAsync(enginePath, retry => Task.FromResult(Dialogs.AskPassword(name, retry)));
            Close();
            _password = pw;
            ArchivePath = enginePath;
            _tree = ArchiveTree.Build(info);
            if (info.HasEncryptedEntries && _password is null)
            {
                // ZIP / 7z without header encryption: names are visible, data is encrypted. Ask now so extraction just works.
                _password = Dialogs.AskPassword(name, false);
            }
            s.Settings.AddRecent(enginePath);
            s.SaveSettings();
            Folder = _tree.Root;
            RaiseAll();
            OnLoaded();
            if (info.Warnings.Count > 0) s.Notify.Warn(Loc.T("s.notice.archiveWarnings"), string.Join("\n", info.Warnings.Take(5)));
        }
        finally
        {
            Busy = false;
        }
    }

    protected virtual void OnLoaded() { }

    private void Fill()
    {
        Items.Clear();
        if (_tree is null || _folder is null) return;
        IEnumerable<ArchiveNode> src = string.IsNullOrWhiteSpace(_search) ? _folder.Children : _tree.Search(_search).Take(5000);
        foreach (var n in src) Items.Add(n);
        Raise(nameof(Location));
    }

    private async Task OpenItemAsync(ArchiveNode? node)
    {
        if (node is null) return;
        if (node.IsDirectory)
        {
            Folder = node;
            return;
        }
        // Preview: extract that one file into a private temp folder (removed on exit) and open it.
        var dest = Path.Combine(PreviewRoot, Guid.NewGuid().ToString("N")[..8]);
        var job = AppServices.Current.Jobs.Enqueue(new JobRequest
        {
            Kind = JobKind.Extract,
            Sources = [ArchivePath!],
            Destination = dest,
            Selection = [node.FullPath],
            FlattenPaths = true,
        }, _password);
        var r = await job.Completion;
        if (!r.Success)
        {
            AppServices.Current.Notify.Show(NoticeKind.Error, Loc.Error(r.Error), r.Message);
            return;
        }
        var file = Path.Combine(dest, node.Name);
        if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private void Extract(bool selectedOnly)
    {
        var dest = Dialogs.PickFolder(Loc.T("s.dlg.extractTo"), Path.GetDirectoryName(ArchivePath));
        if (dest is null) return;
        var req = new JobRequest
        {
            Kind = JobKind.Extract,
            Sources = [ArchivePath!],
            Destination = dest,
            Conflict = AppServices.Current.Settings.Conflict,
        };
        var limits = AppServices.Current.Settings.ToBombLimits();
        req.BombMaxTotalBytes = limits.MaxTotalBytes;
        req.BombSuspiciousRatio = limits.SuspiciousRatio;
        if (selectedOnly)
        {
            req.Selection = ArchiveTree.ToSelection(_selection).ToList();
            if (_folder is { FullPath.Length: > 0 } && string.IsNullOrWhiteSpace(_search)) req.StripPrefix = _folder.FullPath;
        }
        Enqueue(req, Loc.T("s.notice.extractQueued"), dest);
    }

    protected void Enqueue(JobRequest req, string queuedMessage, string? openOnSuccess = null)
    {
        var s = AppServices.Current;
        var job = s.Jobs.Enqueue(req, _password);
        s.Notify.Info(queuedMessage, job.Title);
        JobsViewModel.ReportWhenDone(job, openOnSuccess);
    }

    public void Close()
    {
        _tree = null;
        _folder = null;
        ArchivePath = null;
        _password?.Dispose();
        _password = null;
        Items.Clear();
        _selection = [];
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(nameof(ArchivePath));
        Raise(nameof(ArchiveName));
        Raise(nameof(Info));
        Raise(nameof(HasArchive));
        Raise(nameof(Folder));
        Raise(nameof(Location));
        Raise(nameof(Summary));
        Raise(nameof(SelectionText));
    }

    public static void CleanupPreviews()
    {
        try
        {
            if (Directory.Exists(PreviewRoot)) Directory.Delete(PreviewRoot, true);
        }
        catch
        {
            // files may still be open in another program
        }
    }

    public void Dispose() => Close();
}

public sealed class ExplorerViewModel : ArchiveBrowserViewModel
{
    public ExplorerViewModel()
    {
        MountCommand = new RelayCommand(() => AppServices.Current.Navigate?.Invoke("drives", ArchivePath),
            () => HasArchive && Info is { Format: ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Iso });
    }

    public ICommand MountCommand { get; }
}
