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
    /// <summary>Display order of conflict policies (asking first).</summary>
    public static readonly ConflictPolicy[] ConflictOrder = [ConflictPolicy.Ask, ConflictPolicy.Rename, ConflictPolicy.Skip, ConflictPolicy.Overwrite];

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
    public IReadOnlyList<Choice<ConflictPolicy>> ConflictChoices => ConflictOrder.Select(c => new Choice<ConflictPolicy>(c, Loc.T("s.conflict." + c))).ToList();

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

    private void Start()
    {
        var s = AppServices.Current;
        using var pw = PasswordInput?.Take();
        var limits = s.Settings.ToBombLimits();
        foreach (var row in Archives.ToList())
        {
            // With "new folder" the job chooses the folder itself (reusing it if it exists, never "name (2)").
            var job = s.Jobs.Enqueue(new JobRequest
            {
                Kind = JobKind.Extract,
                Sources = [row.Path],
                Destination = _toSameFolder ? Path.GetDirectoryName(Path.GetFullPath(row.Path))! : _destination,
                IntoArchiveFolder = _createSubfolder,
                Conflict = _conflict,
                BombMaxTotalBytes = limits.MaxTotalBytes,
                BombSuspiciousRatio = limits.SuspiciousRatio,
            }, pw);
            JobsViewModel.ReportWhenDone(job, null, _openAfter);
        }
        s.Notify.Info(Loc.F("s.notice.extractBatch", Archives.Count));
        PasswordInput?.Clear();
        Archives.Clear();
        s.Navigate?.Invoke("jobs", null);
    }
}
