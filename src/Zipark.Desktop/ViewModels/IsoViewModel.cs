using System.Windows.Input;
using Zipark.Application.Drives;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;

namespace Zipark.Desktop.ViewModels;

/// <summary>ISO explorer: browse/extract like any archive, plus Windows virtual DVD mounting.</summary>
public sealed class IsoViewModel : ArchiveBrowserViewModel
{
    private MountedDrive? _drive;

    public IsoViewModel()
    {
        OpenIsoCommand = new AsyncCommand(async () =>
        {
            var p = Dialogs.OpenFiles(Loc.T("s.dlg.openIso"), "ISO|*.iso", false).FirstOrDefault();
            if (p is not null) await LoadAsync(p);
        });
        MountCommand = new AsyncCommand(MountAsync, () => HasArchive && _drive is null);
        UnmountCommand = new AsyncCommand(UnmountAsync, () => _drive is not null);
        OpenDriveCommand = new RelayCommand(() => AppServices.OpenInExplorer(_drive!.Letter + "\\"), () => _drive?.Letter is not null);
    }

    public ICommand OpenIsoCommand { get; }
    public ICommand MountCommand { get; }
    public ICommand UnmountCommand { get; }
    public ICommand OpenDriveCommand { get; }

    public string DriveText => _drive?.Letter is { } l ? Loc.F("s.iso.mountedAt", l) : Loc.T("s.iso.notMounted");
    public bool IsMounted => _drive is not null;

    protected override void OnLoaded()
    {
        _drive = Info?.Format == ArchiveFormat.Iso && ArchivePath is not null ? AppServices.Current.Drives.TrackExistingIso(ArchivePath) : null;
        RaiseDrive();
    }

    private async Task MountAsync()
    {
        if (Info?.Format != ArchiveFormat.Iso)
        {
            AppServices.Current.Notify.Warn(Loc.T("s.iso.onlyIso"));
            return;
        }
        _drive = await AppServices.Current.Drives.MountAsync(ArchivePath!, null);
        RaiseDrive();
        AppServices.Current.Notify.Success(Loc.F("s.notice.mounted", _drive.Letter), ArchiveName, Loc.T("s.action.openExplorer"),
            () => AppServices.OpenInExplorer(_drive?.Letter + "\\"));
    }

    private async Task UnmountAsync()
    {
        if (_drive is null) return;
        await AppServices.Current.Drives.UnmountAsync(_drive);
        _drive = null;
        RaiseDrive();
        AppServices.Current.Notify.Info(Loc.T("s.notice.unmounted"), ArchiveName);
    }

    private void RaiseDrive()
    {
        Raise(nameof(DriveText));
        Raise(nameof(IsMounted));
        CommandManager.InvalidateRequerySuggested();
    }
}
