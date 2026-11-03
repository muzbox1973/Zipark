using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Zipark.Application.Drives;
using Zipark.Core;
using Zipark.Crypto;
using Zipark.Desktop.Infrastructure;
using Zipark.VirtualFileSystem;

namespace Zipark.Desktop.ViewModels;

public sealed class DrivesViewModel : ObservableObject, INavigable
{
    private string _letter = "";
    private readonly DispatcherTimer _timer;

    public DrivesViewModel()
    {
        var svc = AppServices.Current.Drives;
        svc.DriveAdded += d => Ui.OnUi(() => { if (!Drives.Contains(d)) Drives.Add(d); });
        svc.DriveRemoved += d => Ui.OnUi(() => Drives.Remove(d));
        MountCommand = new AsyncCommand(() => MountAsync(null));
        UnmountCommand = new AsyncCommand(async p =>
        {
            if (p is MountedDrive d)
            {
                await svc.UnmountAsync(d);
                AppServices.Current.Notify.Info(Loc.T("s.notice.unmounted"), d.SourceName);
            }
        });
        OpenCommand = new RelayCommand(p => { if (p is MountedDrive { Letter: { } l }) AppServices.OpenInExplorer(l + "\\"); });
        GetWinFspCommand = new RelayCommand(() =>
        {
            // Opens the official download page in the user's browser; nothing is downloaded or installed by Zipark.
            Process.Start(new ProcessStartInfo(WinFspRuntime.DownloadUrl) { UseShellExecute = true });
        });
        RefreshCommand = new RelayCommand(Refresh);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => svc.RequestStats();
        _timer.Start();
        Refresh();
    }

    public ObservableCollection<MountedDrive> Drives { get; } = [];
    public ObservableCollection<string> Letters { get; } = [];
    public ICommand MountCommand { get; }
    public ICommand UnmountCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand GetWinFspCommand { get; }
    public ICommand RefreshCommand { get; }

    public string Letter { get => _letter; set => Set(ref _letter, value); }
    public bool WinFspInstalled => WinFspRuntime.IsInstalled;
    public string WinFspStatus => WinFspInstalled ? Loc.F("s.drives.winfspOk", WinFspRuntime.InstalledVersion) : Loc.T("s.drives.winfspMissing");

    public void Refresh()
    {
        var keep = _letter;
        Letters.Clear();
        Letters.Add(Loc.T("s.drives.auto"));
        for (char c = 'Z'; c >= 'D'; c--)
            if (!VirtualDriveService.IsLetterUsed(c)) Letters.Add($"{c}:");
        Letter = Letters.Contains(keep) ? keep : Letters[0];
        Raise(nameof(WinFspInstalled));
        Raise(nameof(WinFspStatus));
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is "mount") _ = SafeMount(null);
        else if (parameter is string path && File.Exists(path)) _ = SafeMount(path);
    }

    private async Task SafeMount(string? path)
    {
        try
        {
            await MountAsync(path);
        }
        catch (Exception ex)
        {
            AppServices.Current.Notify.Error(ex);
        }
    }

    public async Task MountAsync(string? path)
    {
        var s = AppServices.Current;
        path ??= Dialogs.OpenFiles(Loc.T("s.dlg.pickMount"), $"{Loc.T("s.drives.mountable")}|*.zip;*.7z;*.iso;*.001", false).FirstOrDefault();
        if (path is null) return;
        var format = ArchiveFormats.Detect(path);
        if (format is ArchiveFormat.Zip or ArchiveFormat.SevenZip && !WinFspRuntime.IsInstalled)
        {
            s.Notify.Show(NoticeKind.Warning, Loc.T("s.drives.needWinFsp"), Loc.T("s.drives.needWinFspDetail"), Loc.T("s.drives.getWinFsp"),
                () => GetWinFspCommand.Execute(null));
            return;
        }

        SecretPassword? pw = null;
        try
        {
            if (format is ArchiveFormat.Zip or ArchiveFormat.SevenZip)
            {
                // Find out whether a password is needed before starting the host.
                var name = Path.GetFileName(path);
                var (info, listPw) = await s.Engine.OpenAsync(path, retry => Task.FromResult(Dialogs.AskPassword(name, retry)));
                pw = listPw;
                if (pw is null && info.HasEncryptedEntries) pw = Dialogs.AskPassword(name, false);
                if (info.HasEncryptedEntries && pw is null) return;
            }
            char? letter = _letter.Length == 2 && _letter[1] == ':' ? _letter[0] : null;
            var drive = await s.Drives.MountAsync(path, pw, letter);
            Refresh();
            s.Notify.Success(Loc.F("s.notice.mounted", drive.Letter), drive.SourceName, Loc.T("s.action.openExplorer"),
                () => AppServices.OpenInExplorer(drive.Letter + "\\"));
        }
        finally
        {
            pw?.Dispose();
        }
    }
}
