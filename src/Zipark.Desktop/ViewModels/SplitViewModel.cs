using System.Collections.ObjectModel;
using System.Windows.Input;
using Zipark.Application.Jobs;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;
using Zipark.SplitVolumes;

namespace Zipark.Desktop.ViewModels;

public sealed class SplitViewModel : ObservableObject
{
    private long _preset = 100L * 1024 * 1024;
    private string _custom = "";
    private ArchiveFormat _format = ArchiveFormat.Zip;
    private string _rawFile = "";
    private string _rawDest = "";
    private string _mergeSource = "";
    private string _inspectPath = "";
    private string _inspectSummary = "";

    public SplitViewModel()
    {
        AddArchiveSourcesCommand = new RelayCommand(() => { foreach (var f in Dialogs.OpenFiles(Loc.T("s.dlg.addFiles"))) ArchiveSources.Add(f); });
        AddArchiveFolderCommand = new RelayCommand(() => { foreach (var f in Dialogs.PickFolders()) ArchiveSources.Add(f); });
        ClearArchiveSourcesCommand = new RelayCommand(() => ArchiveSources.Clear());
        CreateSplitArchiveCommand = new RelayCommand(CreateSplitArchive, () => ArchiveSources.Count > 0);
        PickRawCommand = new RelayCommand(() => { var f = Dialogs.OpenFiles(Loc.T("s.dlg.pickFile"), "", false).FirstOrDefault(); if (f is not null) RawFile = f; });
        PickRawDestCommand = new RelayCommand(() => { var d = Dialogs.PickFolder(); if (d is not null) RawDestination = d; });
        SplitRawCommand = new RelayCommand(SplitRaw, () => File.Exists(_rawFile));
        PickMergeCommand = new RelayCommand(() =>
        {
            var f = Dialogs.OpenFiles(Loc.T("s.dlg.pickFirstPart"), $"{Loc.T("s.split.parts")}|*.001;*{SplitManifest.Extension}|{Loc.T("s.dlg.allFiles")}|*.*", false).FirstOrDefault();
            if (f is not null) MergeSource = f;
        });
        MergeCommand = new RelayCommand(Merge, () => File.Exists(_mergeSource));
        InspectCommand = new RelayCommand(() =>
        {
            var f = Dialogs.OpenFiles(Loc.T("s.dlg.pickVolume"), "", false).FirstOrDefault();
            if (f is not null) Inspect(f);
        });
    }

    public ObservableCollection<string> ArchiveSources { get; } = [];
    public ICommand AddArchiveSourcesCommand { get; }
    public ICommand AddArchiveFolderCommand { get; }
    public ICommand ClearArchiveSourcesCommand { get; }
    public ICommand CreateSplitArchiveCommand { get; }
    public ICommand PickRawCommand { get; }
    public ICommand PickRawDestCommand { get; }
    public ICommand SplitRawCommand { get; }
    public ICommand PickMergeCommand { get; }
    public ICommand MergeCommand { get; }
    public ICommand InspectCommand { get; }

    public IReadOnlyList<Choice<long>> Presets => FileSplitter.Presets.Select(p => new Choice<long>(p.Bytes, p.Label)).Append(new Choice<long>(0, Loc.T("s.split.custom"))).ToList();
    public IReadOnlyList<Choice<ArchiveFormat>> Formats { get; } = [new(ArchiveFormat.Zip, "ZIP"), new(ArchiveFormat.SevenZip, "7Z")];

    public long Preset
    {
        get => _preset;
        set
        {
            if (Set(ref _preset, value)) Raise(nameof(IsCustom));
        }
    }
    public bool IsCustom => _preset == 0;
    public string Custom { get => _custom; set => Set(ref _custom, value); }
    public ArchiveFormat Format { get => _format; set => Set(ref _format, value); }
    public string RawFile { get => _rawFile; set => Set(ref _rawFile, value); }
    public string RawDestination { get => _rawDest; set => Set(ref _rawDest, value); }
    public string MergeSource { get => _mergeSource; set => Set(ref _mergeSource, value); }
    public string InspectPath { get => _inspectPath; set => Set(ref _inspectPath, value); }
    public string InspectSummary { get => _inspectSummary; set => Set(ref _inspectSummary, value); }
    public ObservableCollection<string> InspectMissing { get; } = [];
    public ObservableCollection<string> InspectVolumes { get; } = [];

    private long? PartSize()
    {
        if (_preset > 0) return _preset;
        if (ByteSize.TryParse(_custom, out var b) && b >= 64 * 1024) return b;
        AppServices.Current.Notify.Warn(Loc.T("s.split.invalidSize"));
        return null;
    }

    private void CreateSplitArchive()
    {
        if (PartSize() is not { } size) return;
        // Same engine path as "new archive": hand over with split preset so password/level options stay in one place.
        AppServices.Current.Navigate?.Invoke("compress", new CompressPreset(ArchiveSources.ToList(), _format, null, size));
        ArchiveSources.Clear();
    }

    private void SplitRaw()
    {
        if (PartSize() is not { } size) return;
        var s = AppServices.Current;
        var job = s.Jobs.Enqueue(new JobRequest
        {
            Kind = JobKind.SplitFile,
            Sources = [_rawFile],
            Destination = string.IsNullOrWhiteSpace(_rawDest) ? null : _rawDest,
            PartSize = size,
        });
        JobsViewModel.ReportWhenDone(job, string.IsNullOrWhiteSpace(_rawDest) ? Path.GetDirectoryName(_rawFile) : _rawDest);
        s.Notify.Info(Loc.T("s.notice.splitQueued"), Path.GetFileName(_rawFile));
    }

    private void Merge()
    {
        var s = AppServices.Current;
        var job = s.Jobs.Enqueue(new JobRequest { Kind = JobKind.MergeFile, Sources = [_mergeSource] });
        JobsViewModel.ReportWhenDone(job, Path.GetDirectoryName(_mergeSource));
        s.Notify.Info(Loc.T("s.notice.mergeQueued"), Path.GetFileName(_mergeSource));
    }

    public void Inspect(string path)
    {
        InspectPath = path;
        var set = VolumeSetDetector.Detect(path);
        InspectVolumes.Clear();
        InspectMissing.Clear();
        foreach (var v in set.Volumes) InspectVolumes.Add(Path.GetFileName(v));
        foreach (var m in set.Missing) InspectMissing.Add(Path.GetFileName(m));
        InspectSummary = Loc.F("s.split.inspectSummary", Loc.T("s.scheme." + set.Scheme), set.Volumes.Count, ByteSize.Format(set.TotalBytes),
            set.IsComplete ? Loc.T("s.split.complete") : Loc.F("s.split.incomplete", set.Missing.Count));
    }
}
