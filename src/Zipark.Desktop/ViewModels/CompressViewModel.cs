using System.Collections.ObjectModel;
using System.Windows.Input;
using Zipark.Application.Jobs;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Crypto;
using Zipark.Desktop.Infrastructure;
using Zipark.SplitVolumes;

namespace Zipark.Desktop.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class CompressViewModel : ObservableObject, INavigable
{
    private ArchiveFormat _format;
    private CompressionLevel _level;
    private string _output = "";
    private bool _outputEdited;
    private int _threads;
    private CompressionMethod _method;
    private int _dictionaryMb;
    private int _solidMode; // 0 default, 1 on, 2 off
    private bool _split;
    private long _splitPreset = 100L * 1024 * 1024;
    private string _customSplit = "";
    private bool _encrypt;
    private bool _encryptNames = true;
    private ZipEncryption _zipEncryption = ZipEncryption.Aes256;
    private bool _eachSeparately;
    private bool _verifyAfter;
    private string? _selectedSource;
    private SecretPassword? _presetPassword;

    public CompressViewModel()
    {
        var s = AppServices.Current.Settings;
        _format = ArchiveFormats.CanCreate(s.DefaultFormat) ? s.DefaultFormat : ArchiveFormat.Zip;
        _level = Enum.IsDefined(typeof(CompressionLevel), s.DefaultLevel) ? (CompressionLevel)s.DefaultLevel : CompressionLevel.Normal;
        _threads = s.Threads;
        _verifyAfter = s.VerifyAfterCompress;
        Sources.CollectionChanged += (_, _) => { SuggestOutput(); Raise(nameof(SourceSummary)); };

        AddFilesCommand = new RelayCommand(() => Add(Dialogs.OpenFiles(Loc.T("s.dlg.addFiles"))));
        AddFolderCommand = new RelayCommand(() => Add(Dialogs.PickFolders(Loc.T("s.dlg.addFolder"))));
        RemoveCommand = new RelayCommand(() => { if (_selectedSource is not null) Sources.Remove(_selectedSource); }, () => _selectedSource is not null);
        ClearCommand = new RelayCommand(() => Sources.Clear(), () => Sources.Count > 0);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        StartCommand = new RelayCommand(Start, () => Sources.Count > 0 && AppServices.Current.EnginePath is not null);
    }

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand StartCommand { get; }

    /// <summary>Set by the view (SecretInput controls).</summary>
    public IPasswordSource? PasswordInput { get; set; }
    public IPasswordSource? ConfirmInput { get; set; }

    public ObservableCollection<string> Sources { get; } = [];
    public string? SelectedSource { get => _selectedSource; set => Set(ref _selectedSource, value); }
    public string SourceSummary => Sources.Count == 0 ? Loc.T("s.compress.noSources") : Loc.F("s.compress.sourceCount", Sources.Count);

    public IReadOnlyList<Choice<ArchiveFormat>> Formats { get; } = ArchiveFormats.Creatable.Select(f => new Choice<ArchiveFormat>(f, f switch
    {
        ArchiveFormat.SevenZip => "7Z",
        ArchiveFormat.GZip => "GZIP (.gz)",
        ArchiveFormat.BZip2 => "BZIP2 (.bz2)",
        ArchiveFormat.Xz => "XZ (.xz)",
        _ => f.ToString().ToUpperInvariant(),
    })).ToList();

    public IReadOnlyList<Choice<CompressionLevel>> Levels => Enum.GetValues<CompressionLevel>().Select(l => new Choice<CompressionLevel>(l, Loc.T("s.level." + l))).ToList();
    public IReadOnlyList<Choice<int>> ThreadChoices { get; } = new[] { 0 }.Concat(Enumerable.Range(1, Environment.ProcessorCount)).Select(n => new Choice<int>(n, n == 0 ? "Auto" : n.ToString())).ToList();
    public IReadOnlyList<Choice<CompressionMethod>> Methods { get; } = Enum.GetValues<CompressionMethod>().Select(m => new Choice<CompressionMethod>(m, m == CompressionMethod.Default ? "Auto" : m.ToString().ToUpperInvariant())).ToList();
    public IReadOnlyList<Choice<int>> Dictionaries { get; } = new[] { 0, 1, 4, 16, 32, 64, 128, 256 }.Select(d => new Choice<int>(d, d == 0 ? "Auto" : d + " MB")).ToList();
    public IReadOnlyList<Choice<int>> SolidModes => [new(0, "Auto"), new(1, Loc.T("s.common.on")), new(2, Loc.T("s.common.off"))];
    public IReadOnlyList<Choice<long>> SplitPresets => FileSplitter.Presets.Select(p => new Choice<long>(p.Bytes, p.Label)).Append(new Choice<long>(0, Loc.T("s.split.custom"))).ToList();
    public IReadOnlyList<Choice<ZipEncryption>> ZipEncryptions => [new(ZipEncryption.Aes256, "AES-256"), new(ZipEncryption.ZipCrypto, "ZipCrypto (" + Loc.T("s.enc.weak") + ")")];

    public ArchiveFormat Format
    {
        get => _format;
        set
        {
            if (!Set(ref _format, value)) return;
            SuggestOutput();
            Raise(nameof(SupportsEncryption));
            Raise(nameof(IsSevenZip));
            Raise(nameof(IsZip));
            Raise(nameof(SupportsSplit));
            UpdateWarnings();
        }
    }

    public CompressionLevel Level { get => _level; set => Set(ref _level, value); }
    public int Threads { get => _threads; set => Set(ref _threads, value); }
    public CompressionMethod Method { get => _method; set => Set(ref _method, value); }
    public int DictionaryMb { get => _dictionaryMb; set => Set(ref _dictionaryMb, value); }
    public int SolidMode { get => _solidMode; set => Set(ref _solidMode, value); }

    public bool SupportsEncryption => ArchiveFormats.SupportsEncryption(_format);
    public bool SupportsSplit => ArchiveFormats.SupportsVolumes(_format);
    public bool IsSevenZip => _format == ArchiveFormat.SevenZip;
    public bool IsZip => _format == ArchiveFormat.Zip;

    public string Output
    {
        get => _output;
        set
        {
            if (Set(ref _output, value)) _outputEdited = true;
        }
    }

    public bool Split { get => _split; set => Set(ref _split, value); }
    public long SplitPreset
    {
        get => _splitPreset;
        set
        {
            if (Set(ref _splitPreset, value)) Raise(nameof(IsCustomSplit));
        }
    }
    public bool IsCustomSplit => _splitPreset == 0;
    public string CustomSplit { get => _customSplit; set => Set(ref _customSplit, value); }

    public bool Encrypt
    {
        get => _encrypt;
        set
        {
            if (Set(ref _encrypt, value)) UpdateWarnings();
        }
    }
    public bool EncryptNames
    {
        get => _encryptNames;
        set
        {
            if (Set(ref _encryptNames, value)) UpdateWarnings();
        }
    }
    public ZipEncryption ZipEncryption
    {
        get => _zipEncryption;
        set
        {
            if (Set(ref _zipEncryption, value)) UpdateWarnings();
        }
    }

    public bool EachSeparately { get => _eachSeparately; set => Set(ref _eachSeparately, value); }
    public bool VerifyAfter { get => _verifyAfter; set => Set(ref _verifyAfter, value); }

    public ObservableCollection<string> Warnings { get; } = [];

    public void UpdateWarnings()
    {
        Warnings.Clear();
        if (!_encrypt) return;
        var strength = _presetPassword is not null ? PasswordStrength.Evaluate(_presetPassword.Span).Level : PasswordInput?.Strength ?? StrengthLevel.VeryWeak;
        foreach (var w in EncryptionAdvisor.Review(_format, _zipEncryption, _encryptNames, strength)) Warnings.Add(Loc.T("s." + w));
    }

    public void OnNavigatedTo(object? parameter)
    {
        switch (parameter)
        {
            case string[] paths:
                Add(paths);
                break;
            case IEnumerable<string> list:
                Add(list.ToList());
                break;
            case CompressPreset preset:
                Add(preset.Sources);
                if (preset.Format is { } f) Format = f;
                if (preset.Password is not null)
                {
                    _presetPassword?.Dispose();
                    _presetPassword = preset.Password;
                    Encrypt = true;
                    Raise(nameof(HasPresetPassword));
                }
                if (preset.SplitBytes is { } sb)
                {
                    Split = true;
                    var match = FileSplitter.Presets.FirstOrDefault(p => p.Bytes == sb);
                    SplitPreset = match.Bytes;
                    if (match.Bytes == 0) CustomSplit = ByteSize.Format(sb);
                }
                break;
        }
    }

    public bool HasPresetPassword => _presetPassword is not null;

    private void Add(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
            if (!Sources.Contains(p, StringComparer.OrdinalIgnoreCase)) Sources.Add(p);
    }

    private void SuggestOutput()
    {
        if (_outputEdited && _output.Length > 0)
        {
            // keep the user's folder/name, only swap the extension
            var ext = ArchiveFormats.Extension(_format);
            var cur = _output;
            foreach (var f in ArchiveFormats.Creatable)
                if (cur.EndsWith(ArchiveFormats.Extension(f), StringComparison.OrdinalIgnoreCase)) { cur = cur[..^ArchiveFormats.Extension(f).Length]; break; }
            _output = cur + ext;
            Raise(nameof(Output));
            return;
        }
        if (Sources.Count == 0)
        {
            _output = "";
        }
        else
        {
            var first = Sources[0].TrimEnd('\\');
            var dir = Path.GetDirectoryName(first) ?? first;
            var name = Sources.Count == 1 ? Path.GetFileName(first) : new DirectoryInfo(dir).Name;
            if (string.IsNullOrEmpty(name)) name = "archive";
            _output = Path.Combine(dir, name + ArchiveFormats.Extension(_format));
        }
        Raise(nameof(Output));
    }

    private void BrowseOutput()
    {
        var ext = ArchiveFormats.Extension(_format);
        var p = Dialogs.SaveFile(Loc.T("s.dlg.saveArchive"), Path.GetFileName(_output), $"{_format} (*{ext})|*{ext}");
        if (p is not null) Output = p;
    }

    private void Start()
    {
        var s = AppServices.Current;
        long? volume = null;
        if (_split && SupportsSplit)
        {
            if (_splitPreset > 0) volume = _splitPreset;
            else if (ByteSize.TryParse(_customSplit, out var custom) && custom >= 64 * 1024) volume = custom;
            else
            {
                s.Notify.Warn(Loc.T("s.split.invalidSize"));
                return;
            }
        }

        SecretPassword? pw = null;
        if (_encrypt && SupportsEncryption)
        {
            if (_presetPassword is not null) pw = _presetPassword.Clone();
            else
            {
                pw = PasswordInput?.Take();
                using var confirm = ConfirmInput?.Take();
                if (pw is null)
                {
                    s.Notify.Warn(Loc.T("s.pw.empty"));
                    return;
                }
                if (!pw.ContentEquals(confirm))
                {
                    pw.Dispose();
                    s.Notify.Warn(Loc.T("s.pw.mismatch"));
                    return;
                }
            }
        }

        try
        {
            var outputs = new List<(List<string> Sources, string Output)>();
            if (_eachSeparately && Sources.Count > 1)
            {
                var dir = Path.GetDirectoryName(_output) ?? Path.GetDirectoryName(Sources[0])!;
                foreach (var src in Sources)
                    outputs.Add(([src], Path.Combine(dir, Path.GetFileName(src.TrimEnd('\\')) + ArchiveFormats.Extension(_format))));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_output))
                {
                    s.Notify.Warn(Loc.T("s.compress.noOutput"));
                    return;
                }
                outputs.Add((Sources.ToList(), _output));
            }

            foreach (var (src, output) in outputs)
            {
                var req = new JobRequest
                {
                    Kind = JobKind.Compress,
                    Sources = src,
                    Output = output,
                    Format = _format,
                    Level = _level,
                    Threads = _threads,
                    Method = UiState.Instance.Expert ? _method : CompressionMethod.Default,
                    DictionaryMb = UiState.Instance.Expert && _dictionaryMb > 0 ? _dictionaryMb : null,
                    Solid = UiState.Instance.Expert ? _solidMode switch { 1 => true, 2 => false, _ => null } : null,
                    EncryptFileNames = _encryptNames,
                    ZipEncryption = _zipEncryption,
                    VolumeSize = volume,
                    VerifyAfter = _verifyAfter,
                    Conflict = ConflictPolicy.Rename,
                };
                var job = s.Jobs.Enqueue(req, pw);
                JobsViewModel.ReportWhenDone(job, output);
            }
            s.Notify.Info(Loc.F("s.notice.compressQueued", outputs.Count));
            PasswordInput?.Clear();
            ConfirmInput?.Clear();
            _presetPassword?.Dispose();
            _presetPassword = null;
            Raise(nameof(HasPresetPassword));
            Sources.Clear();
            _outputEdited = false;
            s.Navigate?.Invoke("jobs", null);
        }
        finally
        {
            pw?.Dispose(); // the queue holds its own copy
        }
    }
}

/// <summary>Hand-off from other screens (password screen, split screen) into "new archive".</summary>
public sealed record CompressPreset(IReadOnlyList<string> Sources, ArchiveFormat? Format = null, SecretPassword? Password = null, long? SplitBytes = null);
