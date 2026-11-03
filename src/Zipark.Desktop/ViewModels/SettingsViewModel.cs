using System.Diagnostics;
using System.Windows.Input;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;
using Zipark.Settings;
using Zipark.ShellIntegration;

namespace Zipark.Desktop.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private AppSettings S => AppServices.Current.Settings;
    private readonly ShellRegistrar _shell = new();

    public SettingsViewModel()
    {
        RegisterShellCommand = new RelayCommand(RegisterShell);
        UnregisterShellCommand = new RelayCommand(() =>
        {
            _shell.Unregister();
            Raise(nameof(ShellStatus));
            AppServices.Current.Notify.Info(Loc.T("s.settings.shellRemoved"));
        });
        BrowseEngineCommand = new RelayCommand(() =>
        {
            var f = Dialogs.OpenFiles("7z.exe", "7z.exe|7z.exe", false).FirstOrDefault();
            if (f is null) return;
            SevenZipPath = f;
        });
        OpenLogsCommand = new RelayCommand(() => AppServices.OpenInExplorer(AppPaths.LogDir));
        OpenLicensesCommand = new RelayCommand(() =>
        {
            var f = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
            if (File.Exists(f)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{f}\"") { UseShellExecute = false });
        });
        OpenYouTubeCommand = new RelayCommand(() => AppServices.OpenUrl(AppInfo.YouTubeUrl));
        OpenWebsiteCommand = new RelayCommand(() => AppServices.OpenUrl(AppInfo.WebsiteUrl));
        OpenRepositoryCommand = new RelayCommand(() => AppServices.OpenUrl(AppInfo.RepositoryUrl));
        OpenManualCommand = new RelayCommand(() => AppServices.OpenDocument(AppInfo.ManualFile));
        OpenHistoryCommand = new RelayCommand(() => AppServices.OpenDocument(AppInfo.HistoryFile));
    }

    public ICommand OpenYouTubeCommand { get; }
    public ICommand OpenWebsiteCommand { get; }
    public ICommand OpenRepositoryCommand { get; }
    public ICommand OpenManualCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public string Developer => AppInfo.Developer;
    public string YouTubeUrl => AppInfo.YouTubeUrl;
    public string WebsiteUrl => AppInfo.WebsiteUrl;

    public ICommand RegisterShellCommand { get; }
    public ICommand UnregisterShellCommand { get; }
    public ICommand BrowseEngineCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenLicensesCommand { get; }

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        AppServices.Current.SaveSettings();
        Raise(name);
    }

    public IReadOnlyList<Choice<string>> Languages { get; } = [new("ko", "한국어"), new("en", "English")];
    public IReadOnlyList<Choice<ThemeMode>> Themes => Enum.GetValues<ThemeMode>().Select(t => new Choice<ThemeMode>(t, Loc.T("s.theme." + t))).ToList();
    public IReadOnlyList<Choice<ArchiveFormat>> Formats { get; } = [new(ArchiveFormat.Zip, "ZIP"), new(ArchiveFormat.SevenZip, "7Z"), new(ArchiveFormat.Tar, "TAR")];
    public IReadOnlyList<Choice<int>> Levels => Enum.GetValues<CompressionLevel>().Select(l => new Choice<int>((int)l, Loc.T("s.level." + l))).ToList();
    public IReadOnlyList<Choice<ConflictPolicy>> Conflicts => ExtractViewModel.ConflictOrder.Select(c => new Choice<ConflictPolicy>(c, Loc.T("s.conflict." + c))).ToList();
    public IReadOnlyList<char> DriveLetters { get; } = Enumerable.Range('D', 23).Select(c => (char)c).ToList();

    public string Language
    {
        get => S.Language;
        set
        {
            if (S.Language == value) return;
            S.Language = value;
            Loc.Apply(value);
            Save();
            Raise(nameof(Themes));
            Raise(nameof(Levels));
            Raise(nameof(Conflicts));
        }
    }

    public ThemeMode Theme
    {
        get => S.Theme;
        set
        {
            S.Theme = value;
            ThemeManager.Apply(value);
            Save();
        }
    }

    public bool ExpertMode
    {
        get => !S.SimpleMode;
        set
        {
            S.SimpleMode = !value;
            UiState.Instance.Expert = value;
            Save();
        }
    }

    public ArchiveFormat DefaultFormat { get => S.DefaultFormat; set { S.DefaultFormat = value; Save(); } }
    public int DefaultLevel { get => S.DefaultLevel; set { S.DefaultLevel = value; Save(); } }
    public ConflictPolicy Conflict { get => S.Conflict; set { S.Conflict = value; Save(); } }
    public bool OpenFolderAfterExtract { get => S.OpenFolderAfterExtract; set { S.OpenFolderAfterExtract = value; Save(); } }
    public bool VerifyAfterCompress { get => S.VerifyAfterCompress; set { S.VerifyAfterCompress = value; Save(); } }
    public bool UseWorkerProcess { get => S.UseWorkerProcess; set { S.UseWorkerProcess = value; Save(); } }

    public int MemoryCacheMb
    {
        get => S.MemoryCacheMb;
        set
        {
            S.MemoryCacheMb = Math.Clamp(value, 16, 16384);
            AppServices.Current.Drives.UpdateOptions(AppServices.Current.DriveOptions());
            Save();
        }
    }

    public int DiskCacheMb
    {
        get => S.DiskCacheMb;
        set
        {
            S.DiskCacheMb = Math.Clamp(value, 0, 1 << 20);
            AppServices.Current.Drives.UpdateOptions(AppServices.Current.DriveOptions());
            Save();
        }
    }

    public char PreferredDriveLetter
    {
        get => S.PreferredDriveLetter;
        set
        {
            S.PreferredDriveLetter = value;
            AppServices.Current.Drives.UpdateOptions(AppServices.Current.DriveOptions());
            Save();
        }
    }

    public long BombMaxTotalGb { get => S.BombMaxTotalGb; set { S.BombMaxTotalGb = Math.Max(1, value); Save(); } }
    public int BombSuspiciousRatio { get => S.BombSuspiciousRatio; set { S.BombSuspiciousRatio = Math.Max(10, value); Save(); } }

    public string SevenZipPath
    {
        get => S.SevenZipPath;
        set
        {
            S.SevenZipPath = value ?? "";
            SevenZipLocator.OverridePath = string.IsNullOrWhiteSpace(value) ? null : value;
            AppServices.Current.RefreshEngine();
            Save();
            Raise(nameof(EngineStatus));
        }
    }

    public string EngineStatus => AppServices.Current.EnginePath is { } p
        ? Loc.F("s.home.engineOk", AppServices.Current.EngineVersion, p)
        : Loc.T("s.home.engineMissing");

    public string ShellStatus => _shell.IsRegistered ? Loc.F("s.settings.shellOn", _shell.RegisteredExecutable) : Loc.T("s.settings.shellOff");

    private void RegisterShell()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Zipark.exe");
        _shell.Register(exe, ShellLabelsFactory.Create());
        Raise(nameof(ShellStatus));
        AppServices.Current.Notify.Success(Loc.T("s.settings.shellAdded"), Loc.T("s.settings.shellWin11"));
    }

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}

public static class ShellLabelsFactory
{
    public static ShellLabels Create() => new(
        "Zipark",
        Loc.T("s.shell.open"), Loc.T("s.shell.zip"), Loc.T("s.shell.7z"), Loc.T("s.shell.advanced"),
        Loc.T("s.shell.extractHere"), Loc.T("s.shell.extractFolder"), Loc.T("s.shell.test"),
        Loc.T("s.shell.mount"), Loc.T("s.shell.unmount"));
}
