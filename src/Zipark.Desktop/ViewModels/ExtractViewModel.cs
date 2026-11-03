using System.Collections.ObjectModel;
using System.Windows.Input;
using Zipark.Application.Jobs;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;
using Zipark.SplitVolumes;

namespace Zipark.Desktop.ViewModels;

public sealed class ExtractRow(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Status { get; set; } = "";
}

public sealed class ExtractViewModel : ObservableObject, INavigable
{
    private bool _toSameFolder = true;
    private string _destination = "";
    private bool _createSubfolder = true;
    private ConflictPolicy _conflict;
    private bool _openAfter;
    private ExtractRow? _selected;

    public ExtractViewModel()
    {
        var s = AppServices.Current.Settings;
        _conflict = s.Conflict;
        _openAfter = s.OpenFolderAfterExtract;
        AddCommand = new RelayCommand(() => Add(Dialogs.OpenArchives()));
        RemoveCommand = new RelayCommand(() => { if (_selected is not null) Archives.Remove(_selected); }, () => _selected is not null);
        ClearCommand = new RelayCommand(() => Archives.Clear(), () => Archives.Count > 0);
        BrowseCommand = new RelayCommand(() =>
        {
            var d = Dialogs.PickFolder(Loc.T("s.dlg.extractTo"));
            if (d is not null)
            {
                Destination = d;
                ToSameFolder = false;
            }
        });
        StartCommand = new RelayCommand(Start, () => Archives.Count > 0 && AppServices.Current.EnginePath is not null && (_toSameFolder || _destination.Length > 0));
    }

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand StartCommand { get; }
    public IPasswordSource? PasswordInput { get; set; }

    public ObservableCollection<ExtractRow> Archives { get; } = [];
    public ExtractRow? Selected { get => _selected; set => Set(ref _selected, value); }

    public bool ToSameFolder
    {
        get => _toSameFolder;
        set
        {
            if (Set(ref _toSameFolder, value)) Raise(nameof(ToCustomFolder));
        }
    }

    public bool ToCustomFolder
    {
        get => !_toSameFolder;
        set => ToSameFolder = !value;
    }

    public string Destination { get => _destination; set => Set(ref _destination, value); }
    public bool CreateSubfolder { get => _createSubfolder; set => Set(ref _createSubfolder, value); }
    public ConflictPolicy Conflict { get => _conflict; set => Set(ref _conflict, value); }
    public bool OpenAfter { get => _openAfter; set => Set(ref _openAfter, value); }
    public IReadOnlyList<Choice<ConflictPolicy>> ConflictChoices => Enum.GetValues<ConflictPolicy>().Select(c => new Choice<ConflictPolicy>(c, Loc.T("s.conflict." + c))).ToList();

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is IEnumerable<string> paths) Add(paths.ToList());
    }

    private void Add(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            if (Archives.Any(a => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var row = new ExtractRow(p);
            var set = VolumeSetDetector.Detect(p);
            if (set.IsMultiVolume)
            {
                // Selecting any volume adds the set once, through its first volume.
                if (Archives.Any(a => string.Equals(VolumeSetDetector.Detect(a.Path).FirstVolume, set.FirstVolume, StringComparison.OrdinalIgnoreCase))) continue;
                row = new ExtractRow(set.FirstVolume)
                {
                    Status = set.IsComplete ? Loc.F("s.extract.volumes", set.Volumes.Count) : Loc.F("s.extract.missing", string.Join(", ", set.Missing.Select(Path.GetFileName))),
                };
            }
            Archives.Add(row);
        }
    }

    /// <summary>Destination for one archive: its own folder or the chosen one, optionally in a new sub-folder named after it.</summary>
    public static string DestinationFor(string archive, string? custom, bool subfolder)
    {
        var baseDir = custom ?? Path.GetDirectoryName(Path.GetFullPath(archive))!;
        if (!subfolder) return baseDir;
        var name = Path.GetFileName(archive);
        foreach (var suffix in new[] { ".001", ".tar.gz", ".tar.xz", ".tar.bz2", ".7z", ".zip", ".rar", ".tar", ".gz", ".xz", ".bz2", ".tgz", ".txz", ".iso", ".cab", ".wim", ".zst" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        var partIdx = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (partIdx > 0) name = name[..partIdx];
        return ConflictResolver.GetUniquePath(Path.Combine(baseDir, PathSafety.SanitizeFileName(name)));
    }

    private void Start()
    {
        var s = AppServices.Current;
        using var pw = PasswordInput?.Take();
        var limits = s.Settings.ToBombLimits();
        foreach (var row in Archives.ToList())
        {
            var dest = DestinationFor(row.Path, _toSameFolder ? null : _destination, _createSubfolder);
            var job = s.Jobs.Enqueue(new JobRequest
            {
                Kind = JobKind.Extract,
                Sources = [row.Path],
                Destination = dest,
                Conflict = _conflict,
                BombMaxTotalBytes = limits.MaxTotalBytes,
                BombSuspiciousRatio = limits.SuspiciousRatio,
            }, pw);
            JobsViewModel.ReportWhenDone(job, dest, _openAfter);
        }
        s.Notify.Info(Loc.F("s.notice.extractBatch", Archives.Count));
        PasswordInput?.Clear();
        Archives.Clear();
        s.Navigate?.Invoke("jobs", null);
    }
}
