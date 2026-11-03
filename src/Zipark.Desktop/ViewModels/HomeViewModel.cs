using System.Collections.ObjectModel;
using System.Windows.Input;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;
using Zipark.VirtualFileSystem;

namespace Zipark.Desktop.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    public HomeViewModel()
    {
        var s = AppServices.Current;
        NewArchiveCommand = new RelayCommand(() => s.Navigate?.Invoke("compress", null));
        ExtractCommand = new RelayCommand(() => s.Navigate?.Invoke("extract", null));
        OpenCommand = new RelayCommand(() =>
        {
            var p = Dialogs.OpenArchive();
            if (p is not null) s.Navigate?.Invoke(ArchiveFormats.Detect(p) == ArchiveFormat.Iso ? "iso" : "explorer", p);
        });
        MountCommand = new RelayCommand(() => s.Navigate?.Invoke("drives", "mount"));
        VerifyCommand = new RelayCommand(() => s.Navigate?.Invoke("verify", null));
        OpenRecentCommand = new RelayCommand(p =>
        {
            if (p is string path && File.Exists(path)) s.Navigate?.Invoke(ArchiveFormats.Detect(path) == ArchiveFormat.Iso ? "iso" : "explorer", path);
            else if (p is string missing) { s.Settings.RecentArchives.Remove(missing); s.SaveSettings(); Refresh(); }
        });
        OpenManualCommand = new RelayCommand(() => AppServices.OpenDocument(AppInfo.ManualFile));
        OpenYouTubeCommand = new RelayCommand(() => AppServices.OpenUrl(AppInfo.YouTubeUrl));
        OpenWebsiteCommand = new RelayCommand(() => AppServices.OpenUrl(AppInfo.WebsiteUrl));
        Refresh();
    }

    public ICommand OpenManualCommand { get; }
    public ICommand OpenYouTubeCommand { get; }
    public ICommand OpenWebsiteCommand { get; }
    public ICommand NewArchiveCommand { get; }
    public ICommand ExtractCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand MountCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand OpenRecentCommand { get; }

    /// <summary>The home page has a fixed layout; only this many recent archives fit without scrolling.</summary>
    public const int MaxRecentShown = 6;

    public ObservableCollection<string> Recent { get; } = [];

    public bool EngineOk => AppServices.Current.EnginePath is not null;
    public string EngineText => EngineOk
        ? Loc.F("s.home.engineOk", AppServices.Current.EngineVersion, AppServices.Current.EnginePath)
        : Loc.T("s.home.engineMissing");
    public bool WinFspOk => WinFspRuntime.IsInstalled;
    public string WinFspText => WinFspOk ? Loc.F("s.home.winfspOk", WinFspRuntime.InstalledVersion) : Loc.T("s.home.winfspMissing");
    public string WorkerText => AppServices.Current.UsingWorkerProcess ? Loc.T("s.home.workerOn") : Loc.T("s.home.workerOff");

    public void Refresh()
    {
        Recent.Clear();
        foreach (var r in AppServices.Current.Settings.RecentArchives.Take(MaxRecentShown)) Recent.Add(r);
        Raise(nameof(EngineOk));
        Raise(nameof(EngineText));
        Raise(nameof(WinFspOk));
        Raise(nameof(WinFspText));
        Raise(nameof(WorkerText));
    }
}
